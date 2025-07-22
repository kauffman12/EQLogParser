using log4net;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundTouch.Net.NAudioSupport;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.Media.SpeechSynthesis;

namespace EQLogParser
{
  internal partial class AudioManager : IDisposable
  {
    internal event Action<bool> DeviceListChanged;
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly Lazy<AudioManager> Lazy = new(() => new AudioManager());
    internal static AudioManager Instance => Lazy.Value;
    private const int LATENCY = 72;
    private readonly ConcurrentDictionary<string, PlayerAudio> _playerAudios = new();
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly AudioDeviceNotificationClient _notificationClient = new();
    private readonly DispatcherTimer _updateTimer;
    private readonly object _deviceLock = new();
    private MMDeviceEnumerator _deviceEnumerator;
    private Guid _selectedDeviceGuid = Guid.Empty;
    private bool _disposed;
    private readonly bool _usePiper;
    private volatile float _appVolume = 1.0f;
    private volatile bool _initialized;

    private AudioManager()
    {
      _updateTimer = new DispatcherTimer(DispatcherPriority.Loaded);
      _updateTimer.Tick += DoUpdateDeviceList;
      _updateTimer.Interval = new TimeSpan(0, 0, 0, 0, 500);
      InitAudio();

      if (PiperTts.Initialize())
      {
        Log.Info("Using piper-tts");
        _usePiper = true;
      }
    }

    internal int GetVolume() => (int)(_appVolume * 100.0f);
    internal void SetVolume(int volume) => _appVolume = volume / 100.0f;

    internal List<string> GetVoiceList()
    {
      if (_usePiper) return PiperTts.GetVoiceList();

      var list = new List<string>();

      try
      {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
        {
          foreach (var voice in SpeechSynthesizer.AllVoices)
          {
            if (voice is not null && voice.DisplayName is string name)
            {
              list.Add(name);
            }
          }
        }

        using var sapi = new System.Speech.Synthesis.SpeechSynthesizer();
#pragma warning disable CA1304 // If culture info is used then not all voices are returned
        foreach (var voice in sapi.GetInstalledVoices())
        {
          if (voice is not null && voice.VoiceInfo is System.Speech.Synthesis.VoiceInfo info && !string.IsNullOrEmpty(info.Name))
          {
            list.Add("(Legacy) " + info.Name);
          }
        }
#pragma warning restore CA1304 // Specify CultureInfo
      }
      catch (Exception)
      {
        Log.Error("Unable to read Voices from Windows SpeechSynthesizer.");
      }

      return list;
    }

    internal string GetDefaultVoice()
    {
      if (_usePiper) return PiperTts.GetDefaultVoice();

      if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240) && GetVoiceInfo(null) is VoiceInformation { } voiceInfo)
      {
        return voiceInfo.DisplayName;
      }

      return string.Empty;
    }

    internal static (List<string> idList, List<string> nameList) GetDeviceList()
    {
      List<string> idList = [Guid.Empty.ToString()];
      List<string> nameList = ["Default Audio"];

      foreach (var device in DirectSoundOut.Devices.ToList())
      {
        if (device.Guid != Guid.Empty)
        {
          idList.Add(device.Guid.ToString());
          nameList.Add(device.Description);
        }
      }

      return (idList, nameList);
    }

    internal void SelectDevice(string id)
    {
      var device = GetDeviceOrDefault(id);
      lock (_deviceLock)
      {
        _selectedDeviceGuid = device;
      }
    }

    internal void SetVoice(string id, string voice)
    {
      if (!string.IsNullOrEmpty(voice) && _playerAudios.TryGetValue(id, out var audio))
      {
        lock (audio)
        {
          LoadVoice(id, voice, audio);
        }
      }
    }

    internal void Add(string id, string voice)
    {
      var audio = new PlayerAudio();
      LoadVoice(id, voice, audio);
      _playerAudios.TryAdd(id, audio);

    }

    internal void Start(string id)
    {
      if (_playerAudios.TryGetValue(id, out var audio))
      {
        _ = ProcessAsync(audio);
      }
    }

    internal void Stop(string id, bool remove = false)
    {
      if (!string.IsNullOrEmpty(id) && _playerAudios.TryGetValue(id, out var playerAudio))
      {
        lock (playerAudio)
        {
          playerAudio.CurrentEvent = null;
          playerAudio.Events.Clear();
          playerAudio.CurrentPlayback?.Stop();

          if (remove)
          {
            _playerAudios.TryRemove(id, out _);
            try
            {
              playerAudio.ProcessingToken.Cancel();

              if (_usePiper)
              {
                PiperTts.RemoveVoice(id);
              }
              else
              {
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
                {
                  playerAudio.Synth?.Dispose();
                  playerAudio.Synth = null;
                }

                playerAudio.SapiSynth?.Dispose();
                playerAudio.SapiSynth = null;
              }
            }
            catch (Exception)
            {
              // ignore
            }
          }
        }
      }
    }

    internal async void TestSpeakFileAsync(string filePath, int adjustedVolume = 4)
    {
      var reader = new AudioFileReader(filePath);
      if (!string.IsNullOrEmpty(filePath) && await ReadFileToByteArrayAsync(reader) is { Length: > 0 } data)
      {
        if (!PlayAudioData(data, reader.WaveFormat, GetDevice(), _appVolume, 0, adjustedVolume))
        {
          new MessageWindow("Unable to Play sound. No audio device?", Resource.AUDIO_ERROR).ShowDialog();
        }
      }
    }

    internal async void TestSpeakTtsAsync(string tts, string voice = null, int rate = 0, int adjustedVolume = 4, int customVolume = -1)
    {
      if (!string.IsNullOrEmpty(tts))
      {
        byte[] audio = null;
        var sample = 16000;

        if (_usePiper)
        {
          const string testSpeaker = "testSpeaker";
          if (PiperTts.LoadVoice(testSpeaker, voice, out var voiceData))
          {
            audio = PiperTts.SynthesizeText(testSpeaker, tts);
            sample = voiceData.Sample;
            PiperTts.RemoveVoice(testSpeaker);
          }
        }
        else
        {
          if (IsLegacyVoice(voice))
          {
            if (CreateSapiSpeechSynthesizer(voice) is { } synth && SynthesizeTextToByteArray(tts, synth, out sample) is { Length: > 0 } data)
            {
              audio = data;
              synth.Dispose();
            }
          }
          else
          {
            if (CreateSpeechSynthesizer(voice) is { } synth && await SynthesizeTextToByteArrayAsync(tts, synth) is { Length: > 0 } data)
            {
              audio = data;
              if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) synth.Dispose();
            }
          }
        }

        if (audio?.Length > 0)
        {
          var waveFormat = new WaveFormat(sample, 16, 1);
          var realVolume = customVolume > -1 ? customVolume / 100.0f : _appVolume;
          if (!PlayAudioData(audio, waveFormat, GetDevice(), realVolume, rate, adjustedVolume))
          {
            new MessageWindow("Unable to Play sound. No audio device?", Resource.AUDIO_ERROR).ShowDialog();
          }
        }
      }
    }

    internal async void SpeakOrSaveTtsAsync(string tts, string voice, string id, float realVolume, int rate, string fileName = null)
    {
      if (!string.IsNullOrEmpty(tts))
      {
        byte[] audio = null;
        var sample = 16000;

        if (_usePiper)
        {
          const string testSpeaker = "testSpeaker";
          if (PiperTts.LoadVoice(testSpeaker, voice, out var voiceData))
          {
            audio = PiperTts.SynthesizeText(testSpeaker, tts);
            sample = voiceData.Sample;
            PiperTts.RemoveVoice(testSpeaker);
          }
        }
        else
        {
          if (IsLegacyVoice(voice))
          {
            if (CreateSapiSpeechSynthesizer(voice) is { } synth && SynthesizeTextToByteArray(tts, synth, out sample) is { Length: > 0 } data)
            {
              audio = data;
              synth.Dispose();
            }
          }
          else
          {
            if (CreateSpeechSynthesizer(voice) is { } synth && await SynthesizeTextToByteArrayAsync(tts, synth) is { Length: > 0 } data)
            {
              audio = data;
              if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) synth.Dispose();
            }
          }
        }

        if (audio?.Length > 0)
        {
          var waveFormat = new WaveFormat(sample, 16, 1);

          if (string.IsNullOrEmpty(fileName))
          {
            var device = GetDeviceOrDefault(id);
            if (!PlayAudioData(audio, waveFormat, device, realVolume, rate))
            {
              new MessageWindow("Unable to Play sound. No audio device?", Resource.AUDIO_ERROR).ShowDialog();
            }
          }
          else
          {
            WaveFileWriter writer = null;
            try
            {
              var stream = new RawSourceWaveStream(audio, 0, audio.Length, waveFormat);
              var provider = CreateVolumeProvider(realVolume, stream, rate, 4).ToWaveProvider16();

              // Write directly to a .wav file
              writer = new WaveFileWriter(fileName, provider.WaveFormat);
              var buffer = new byte[provider.WaveFormat.AverageBytesPerSecond];
              int bytesRead;

              // Read from the WaveProvider and write to the file
              while ((bytesRead = provider.Read(buffer, 0, buffer.Length)) > 0)
              {
                writer.Write(buffer, 0, bytesRead);
              }
            }
            catch (Exception ex)
            {
              Log.Error("Error Exporting WAV", ex);
              new MessageWindow("Failed to Export WAV file. Check the Error Log for Details.", Resource.EXPORT_ERROR).ShowDialog();
            }
            finally
            {
              writer?.Dispose();
            }
          }
        }
      }
    }

    internal async void SpeakFileAsync(string id, string filePath, int customVolume, Trigger trigger)
    {
      if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(filePath) && File.Exists(filePath))
      {
        try
        {
          var reader = new AudioFileReader(filePath);
          if (await ReadFileToByteArrayAsync(reader) is { Length: > 0 } data)
          {
            SpeakAsync(id, data, reader.WaveFormat, 0, customVolume, trigger.Priority, trigger.Volume, reader.TotalTime.TotalSeconds);
          }
        }
        catch (Exception ex)
        {
          Log.Debug($"Error while playing wav file: {filePath}", ex);
        }
      }
    }

    internal async void SpeakTtsAsync(string id, string tts, int rate, int customVolume, Trigger trigger)
    {
      if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(tts))
      {
        byte[] audio = null;
        var sample = 16000;
        await _semaphore.WaitAsync();

        try
        {
          if (_playerAudios.TryGetValue(id, out var playerAudio))
          {
            if (_usePiper)
            {
              audio = PiperTts.SynthesizeText(id, tts);
              sample = playerAudio.PiperSampleRate;
            }
            else
            {
              SpeechSynthesizer synth = null;
              System.Speech.Synthesis.SpeechSynthesizer sapiSynth = null;
              lock (playerAudio)
              {
                synth = playerAudio.Synth;
                sapiSynth = playerAudio.SapiSynth;
              }

              if (synth != null)
              {
                audio = await SynthesizeTextToByteArrayAsync(tts, synth);
              }
              else if (sapiSynth != null)
              {
                audio = SynthesizeTextToByteArray(tts, sapiSynth, out sample);
              }
            }
          }
        }
        catch (Exception ex)
        {
          Log.Debug("Error synthesizing text.", ex);
        }
        finally
        {
          _semaphore.Release();
        }

        if (audio is { Length: > 0 })
        {
          var waveFormat = new WaveFormat(sample, 16, 1);
          SpeakAsync(id, audio, waveFormat, rate, customVolume, trigger.Priority, trigger.Volume);
        }
      }
    }

    private void DoUpdateDeviceList(object sender, EventArgs e)
    {
      _updateTimer.Stop();

      Guid selected;
      lock (_deviceLock)
      {
        selected = _selectedDeviceGuid;
      }

      var found = false;
      foreach (var device in DirectSoundOut.Devices.ToList())
      {
        if (device.Guid == selected)
        {
          found = true;
          break;
        }
      }

      if (!found)
      {
        lock (_deviceLock)
        {
          _selectedDeviceGuid = Guid.Empty;
        }
      }

      if (!_initialized)
      {
        InitAudio();
      }

      DeviceListChanged?.Invoke(true);
    }

    protected void UpdateDeviceList()
    {
      _updateTimer.Stop();
      _updateTimer.Start();
    }

    private void InitAudio()
    {
      try
      {
        var silentWav = new byte[]
        {
            0x52, 0x49, 0x46, 0x46, 0x24, 0x08, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45, 0x66, 0x6D, 0x74, 0x20,
            0x10, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x44, 0xAC, 0x00, 0x00, 0x88, 0x58, 0x01, 0x00,
            0x02, 0x00, 0x10, 0x00, 0x64, 0x61, 0x74, 0x61, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00
        };

        // play something to register the audio session with windows
        var memStream = new MemoryStream(silentWav);
        var reader = new WaveFileReader(memStream);
        var output = new DirectSoundOut(GetDevice(), 100);
        output.Init(reader);

        output.PlaybackStopped += async (_, _) =>
        {
          await memStream.DisposeAsync();
          await reader.DisposeAsync();
          output.Dispose();
        };

        output.Play();
        output.Stop();
        _initialized = true;
      }
      catch (Exception ex)
      {
        Log.Warn($"Error Initializing Playback Device: {ex.Message}");
      }

      try
      {
        _deviceEnumerator?.UnregisterEndpointNotificationCallback(_notificationClient);
        _deviceEnumerator?.Dispose();
        _deviceEnumerator = new MMDeviceEnumerator();
        _deviceEnumerator.RegisterEndpointNotificationCallback(_notificationClient);
      }
      catch (Exception)
      {
        // not supported
      }
    }

    private void SpeakAsync(string id, byte[] audioData, WaveFormat waveFormat, int rate = 0, int customVolume = -1,
      long priority = 5, int adjustedVolume = 4, double seconds = -1)
    {
      if (_playerAudios.TryGetValue(id, out var playerAudio))
      {
        lock (playerAudio)
        {
          playerAudio.Events = [.. playerAudio.Events.Where(pa => pa.Priority <= priority)];
          playerAudio.Events.Add(new PlaybackEvent
          {
            AudioData = audioData,
            WaveFormat = waveFormat,
            Priority = priority,
            Rate = rate,
            AdjustedVolume = adjustedVolume,
            RealVolume = customVolume > -1 ? customVolume / 100.0f : _appVolume,
            Seconds = seconds
          });
        }
      }
    }

    private static bool PlayAudioData(byte[] data, WaveFormat waveFormat, Guid device, float appVolume, int rate = 0, int adjustedVolume = 4)
    {
      try
      {
        var stream = new RawSourceWaveStream(data, 0, data.Length, waveFormat);
        var output = CreateDirectSoundOut(device, appVolume, stream, rate, adjustedVolume);
        output.Play();
        output.PlaybackStopped += async (_, _) =>
        {
          await stream.DisposeAsync();
          output.Dispose();
        };
      }
      catch (Exception ex)
      {
        Log.Error("Error playing audio.", ex);
        return false;
      }

      return true;
    }

    private static VolumeSampleProvider CreateVolumeProvider(float appVolume, RawSourceWaveStream stream, int rate, int volume)
    {
      try
      {
        var soundTouchProvider = stream.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat ? new SoundTouchWaveProvider(stream)
          : new SoundTouchWaveProvider(stream.ToSampleProvider().ToWaveProvider());

        // only TTS will specify a custom rate
        if (rate > 0)
        {
          soundTouchProvider.OptimizeForSpeech();
          soundTouchProvider.Tempo = 1.0f + (rate / 10.0f * 1.6f);
        }

        var volumeProvider = new VolumeSampleProvider(soundTouchProvider.ToSampleProvider())
        {
          Volume = ConvertVolume(appVolume, volume)
        };

        return volumeProvider;
      }
      catch (Exception)
      {
        // not supported
      }

      return null;
    }

    // load voice. note not synchronized
    private void LoadVoice(string id, string voice, PlayerAudio playerAudio)
    {
      if (_usePiper)
      {
        if (PiperTts.LoadVoice(id, voice, out var piperVoice))
        {
          playerAudio.PiperSampleRate = piperVoice.Sample;
        }
      }
      else
      {
        if (IsLegacyVoice(voice))
        {
          playerAudio.SapiSynth?.Dispose();
          playerAudio.SapiSynth = CreateSapiSpeechSynthesizer(voice);
          if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
          {
            playerAudio.Synth?.Dispose();
            playerAudio.Synth = null;
          }
        }
        else
        {
          if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
          {
            playerAudio.Synth?.Dispose();
            playerAudio.Synth = CreateSpeechSynthesizer(voice);
            playerAudio.SapiSynth?.Dispose();
            playerAudio.SapiSynth = null;
          }
        }
      }
    }

    private async Task ProcessAsync(PlayerAudio audio)
    {
      var cancellationTokenSource = new CancellationTokenSource();
      audio.ProcessingToken = cancellationTokenSource;

      await Task.Run(async () =>
      {
        RawSourceWaveStream stream = null;
        DirectSoundOut output = null;

        try
        {
          while (!cancellationTokenSource.Token.IsCancellationRequested)
          {
            lock (audio)
            {
              if (output != null)
              {
                try
                {
                  if (output.PlaybackState != PlaybackState.Stopped)
                  {
                    foreach (var item in audio.Events)
                    {
                      if (audio.CurrentEvent != item && audio.CurrentEvent?.Priority > item.Priority)
                      {
                        output.Stop();
                        break;
                      }
                    }
                  }

                  // skip through short sound files if there's audio pending
                  if (output.PlaybackState == PlaybackState.Playing &&
                      audio.CurrentEvent?.Seconds is > -1 and < 1.0 && audio.Events.Count > 0)
                  {
                    output.Stop();
                  }
                }
                catch (Exception)
                {
                  // ignore stop errors
                }
              }

              if (output == null || output.PlaybackState == PlaybackState.Stopped)
              {
                try
                {
                  if (stream != null)
                  {
                    stream.Dispose();
                    stream = null;
                    output?.Dispose();
                    audio.CurrentPlayback = null;
                  }

                  if (audio.Events.Count > 0)
                  {
                    audio.CurrentEvent = audio.Events[0];
                    audio.Events.RemoveAt(0);

                    // remove header
                    var data = audio.CurrentEvent.AudioData;
                    if (data?.Length > 0)
                    {
                      stream = new RawSourceWaveStream(data, 0, data.Length, audio.CurrentEvent.WaveFormat);

                      // make sure audio is still valid
                      try
                      {
                        output = CreateDirectSoundOut(GetDevice(), audio.CurrentEvent.RealVolume, stream,
                          audio.CurrentEvent.Rate, audio.CurrentEvent.AdjustedVolume);
                        audio.CurrentPlayback = output;
                        output.Play();
                      }
                      catch (Exception)
                      {
                        output?.Dispose();
                        output = null;
                        audio.CurrentPlayback = null;
                      }
                    }
                  }
                }
                catch (Exception ex)
                {
                  Log.Error("Error Playing Audio", ex);
                }
              }
            }

            await Task.Delay(50);
          }
        }
        catch (Exception)
        {
          // ignore cancel event. the rest should have it's own try/catch
        }
        finally
        {
          if (output != null)
          {
            output.Stop();
            output.Dispose();
          }

          stream?.Dispose();
          cancellationTokenSource.Dispose();
        }
      }, cancellationTokenSource.Token);
    }

    private Guid GetDevice()
    {
      lock (_deviceLock)
      {
        return _selectedDeviceGuid;
      }
    }

    private static SpeechSynthesizer CreateSpeechSynthesizer(string voice)
    {
      SpeechSynthesizer synth = null;

      try
      {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
        {
          synth = new SpeechSynthesizer();
          if (GetVoiceInfo(voice) is { } voiceInfo)
          {
            synth.Voice = voiceInfo;
          }
        }
      }
      catch (Exception)
      {
        // not supported
      }

      return synth;
    }

    private static System.Speech.Synthesis.SpeechSynthesizer CreateSapiSpeechSynthesizer(string voice)
    {
      System.Speech.Synthesis.SpeechSynthesizer synth = null;

      try
      {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
        {
          synth = new System.Speech.Synthesis.SpeechSynthesizer();
          if (GetSapiVoiceInfo(voice) is { } voiceInfo)
          {
            synth.SelectVoice(voiceInfo.Name);
          }
        }
      }
      catch (Exception)
      {
        // not supported
      }

      return synth;
    }

    private static DirectSoundOut CreateDirectSoundOut(Guid device, float appVolume, RawSourceWaveStream stream, int rate, int adjustedVolume)
    {
      // short sounds need a shorter latency but don't go below 10 as it may break entirely
      var latencyCalc = (int)Math.Min(Math.Max(stream.TotalTime.TotalMilliseconds - 5, 30), LATENCY);
      var output = new DirectSoundOut(device, latencyCalc);

      var provider = CreateVolumeProvider(appVolume, stream, rate, adjustedVolume);
      if (provider != null)
      {
        output.Init(provider);
      }
      else
      {
        output.Init(stream);
      }

      return output;
    }

    private static Guid GetDeviceOrDefault(string id)
    {
      var foundGuid = Guid.Empty;
      if (!string.IsNullOrEmpty(id) && Guid.TryParse(id, out var result))
      {
        foreach (var device in DirectSoundOut.Devices.ToList())
        {
          if (device.Guid == result)
          {
            foundGuid = device.Guid;
            break;
          }
        }
      }

      return foundGuid;
    }

    private static VoiceInformation GetVoiceInfo(string name)
    {
      if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
      {
        return null;
      }

      VoiceInformation voiceInfo = null;

      try
      {
        voiceInfo = SpeechSynthesizer.DefaultVoice;
        if (!string.IsNullOrEmpty(name))
        {
          foreach (var voice in SpeechSynthesizer.AllVoices)
          {
            if (voice.DisplayName == name || name.StartsWith(voice.DisplayName, StringComparison.OrdinalIgnoreCase))
            {
              voiceInfo = voice;
              break;
            }
          }
        }
      }
      catch (Exception)
      {
        // not supported
      }

      return voiceInfo;
    }

    private static System.Speech.Synthesis.VoiceInfo GetSapiVoiceInfo(string name)
    {
      System.Speech.Synthesis.VoiceInfo voiceInfo = null;

      try
      {
        var synth = new System.Speech.Synthesis.SpeechSynthesizer();
        voiceInfo = synth.Voice;
        if (!string.IsNullOrEmpty(name))
        {
          foreach (var voice in synth.GetInstalledVoices())
          {
            if (!string.IsNullOrEmpty(name) && name.Contains(voice.VoiceInfo.Name, StringComparison.OrdinalIgnoreCase))
            {
              voiceInfo = voice.VoiceInfo;
              break;
            }
          }
        }
      }
      catch (Exception)
      {
        // not supported
      }

      return voiceInfo;
    }

    private static async Task<byte[]> ReadFileToByteArrayAsync(AudioFileReader reader)
    {
      try
      {
        var memStream = new MemoryStream();
        await reader.CopyToAsync(memStream);
        return memStream.ToArray();
      }
      catch (Exception ex)
      {
        Log.Debug($"Error reading file to byte array: {reader.FileName}", ex);
        return null;
      }
    }

    private static async Task<byte[]> SynthesizeTextToByteArrayAsync(string tts, SpeechSynthesizer synth)
    {
      if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
      {
        return null;
      }

      try
      {
        var stream = await synth.SynthesizeTextToStreamAsync(tts);
        var memStream = new MemoryStream();
        await stream.AsStream().CopyToAsync(memStream);
        stream.Dispose();
        var data = memStream.ToArray();
        await memStream.DisposeAsync();
        // return without wav header
        return data[44..];
      }
      catch (Exception ex)
      {
        Log.Debug("Error synthesizing text to byte array.", ex);
        return null;
      }
    }

    private static byte[] SynthesizeTextToByteArray(string tts, System.Speech.Synthesis.SpeechSynthesizer synth, out int sample)
    {
      sample = 16000; // default sample rate

      try
      {
        var memStream = new MemoryStream();
        synth.SetOutputToWaveStream(memStream);
        synth.Speak(tts);
        var data = memStream.ToArray();
        // Use NAudio to read WAV format
        memStream.Position = 0;
        using var reader = new WaveFileReader(memStream);
        var format = reader.WaveFormat;
        sample = format.SampleRate;
        memStream.Dispose();
        // return without wav header
        return data[44..];
      }
      catch (Exception ex)
      {
        Log.Debug("Error synthesizing text to byte array.", ex);
        return null;
      }
    }

    private static float ConvertVolume(float current, int increase)
    {
      var floatIncrease = increase switch
      {
        0 => 1.8f,
        1 => 1.6f,
        2 => 1.4f,
        3 => 1.2f,
        5 => 0.8f,
        6 => 0.6f,
        7 => 0.4f,
        8 => 0.2f,
        _ => 1.0f
      };

      if (current < 0)
      {
        current = 1.0f; // reset to default if negative
      }

      return current * floatIncrease;
    }

    private static bool IsLegacyVoice(string voice)
    {
      return !string.IsNullOrEmpty(voice) && voice.StartsWith("(Legacy) ", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
      Dispose(true);
      GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
      if (_disposed) return;

      if (disposing)
      {
        _semaphore?.Dispose();
        _deviceEnumerator?.UnregisterEndpointNotificationCallback(_notificationClient);
        _deviceEnumerator?.Dispose();

        if (_usePiper)
        {
          PiperTts.Release();
        }
      }

      _disposed = true;
    }

    private class PlayerAudio
    {
      internal List<PlaybackEvent> Events { get; set; } = [];
      internal PlaybackEvent CurrentEvent { get; set; }
      internal DirectSoundOut CurrentPlayback { get; set; }
      internal CancellationTokenSource ProcessingToken { get; set; }
      internal SpeechSynthesizer Synth { get; set; }
      internal System.Speech.Synthesis.SpeechSynthesizer SapiSynth { get; set; }
      internal int PiperSampleRate { get; set; }
    }

    private class PlaybackEvent
    {
      internal long Priority { get; init; } = -1;
      internal int Rate { get; init; }
      internal int AdjustedVolume { get; init; } = 4;
      internal float RealVolume { get; init; } = -1;
      internal byte[] AudioData { get; init; }
      internal WaveFormat WaveFormat { get; init; }
      internal double Seconds { get; init; }
    }

    private class AudioDeviceNotificationClient : IMMNotificationClient
    {
      public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Instance.UpdateDeviceList();
      public void OnDeviceAdded(string deviceId) => Instance.UpdateDeviceList();
      public void OnDeviceRemoved(string deviceId) => Instance.UpdateDeviceList();
      public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { }
      public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }
    }
  }
}