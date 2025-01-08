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
    private const int LATENCY = 75;
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
      _updateTimer = new DispatcherTimer();
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
      }
      catch (Exception)
      {
        Log.Error("Unable to read Voices from Windows.Media.SpeechSynthesizer.");
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

      foreach (var device in DirectSoundOut.Devices)
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
      var foundGuid = Guid.Empty;
      if (!string.IsNullOrEmpty(id) && Guid.TryParse(id, out var result))
      {
        foreach (var device in DirectSoundOut.Devices)
        {
          if (device.Guid == result)
          {
            foundGuid = device.Guid;
            break;
          }
        }
      }

      lock (_deviceLock)
      {
        _selectedDeviceGuid = foundGuid;
      }
    }

    internal void SetVoice(string id, string voice)
    {
      if (!string.IsNullOrEmpty(voice) && _playerAudios.TryGetValue(id, out var playerAudio))
      {
        lock (playerAudio)
        {
          if (_usePiper)
          {
            if (PiperTts.LoadVoice(id, voice, out var piperVoice))
            {
              playerAudio.PiperSampleRate = piperVoice.Sample;
            }
          }
          else if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240) && playerAudio?.Synth != null
            && GetVoiceInfo(voice) is { } voiceInfo)
          {
            playerAudio.Synth.Voice = voiceInfo;
          }
        }
      }
    }

    internal void Add(string id, string voice)
    {
      var audio = new PlayerAudio();

      if (_usePiper)
      {
        if (PiperTts.LoadVoice(id, voice, out var voiceData))
        {
          audio.PiperSampleRate = voiceData.Sample;
        }
      }
      else
      {
        audio.Synth = CreateSpeechSynthesizer(voice);
      }

      _playerAudios.TryAdd(id, audio);
      ProcessAsync(audio);
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
              else if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
              {
                playerAudio.Synth?.Dispose();
                playerAudio.Synth = null;
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

    internal async void TestSpeakFileAsync(string filePath, int volume = 4)
    {
      var reader = new AudioFileReader(filePath);
      if (!string.IsNullOrEmpty(filePath) && await ReadFileToByteArrayAsync(reader) is { Length: > 0 } data)
      {
        if (!PlayAudioData(data, reader.WaveFormat, 0, volume))
        {
          new MessageWindow("Unable to Play sound. No audio device?", Resource.AUDIO_ERROR).ShowDialog();
        }
      }
    }

    internal async void TestSpeakTtsAsync(string tts, string voice = null, int rate = 0, int volume = 4)
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
        else if (CreateSpeechSynthesizer(voice) is { } synth)
        {
          if (await SynthesizeTextToByteArrayAsync(tts, synth) is { Length: > 0 } data)
          {
            audio = data;
          }

          if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) synth.Dispose();
        }

        if (audio?.Length > 0)
        {
          var waveFormat = new WaveFormat(sample, 16, 1);
          if (!PlayAudioData(audio, waveFormat, rate, volume))
          {
            new MessageWindow("Unable to Play sound. No audio device?", Resource.AUDIO_ERROR).ShowDialog();
          }
        }
      }
    }

    internal async void SpeakFileAsync(string id, string filePath, Trigger trigger)
    {
      if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(filePath) && File.Exists(filePath))
      {
        try
        {
          var reader = new AudioFileReader(filePath);
          if (await ReadFileToByteArrayAsync(reader) is { Length: > 0 } data)
          {
            SpeakAsync(id, data, reader.WaveFormat, 0, trigger.Priority, trigger.Volume, reader.TotalTime.TotalSeconds);
          }
        }
        catch (Exception ex)
        {
          Log.Debug($"Error while playing wav file: {filePath}", ex);
        }
      }
    }

    internal async void SpeakTtsAsync(string id, string tts, int rate, Trigger trigger)
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
              lock (playerAudio)
              {
                synth = playerAudio.Synth;
              }

              audio = await SynthesizeTextToByteArrayAsync(tts, synth);
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
          SpeakAsync(id, audio, waveFormat, rate, trigger.Priority, trigger.Volume);
        }
      }
    }

    private void DoUpdateDeviceList(object sender, EventArgs e)
    {
      _updateTimer.Stop();

      lock (_deviceLock)
      {
        var found = false;
        foreach (var device in DirectSoundOut.Devices)
        {
          if (device.Guid == _selectedDeviceGuid)
          {
            found = true;
            break;
          }
        }

        if (!found)
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
        var output = new DirectSoundOut(GetDevice(), LATENCY);
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

    private void SpeakAsync(string id, byte[] audioData, WaveFormat waveFormat, int rate = 0, long priority = 5, int volume = 4, double seconds = -1)
    {
      if (_playerAudios.TryGetValue(id, out var playerAudio))
      {
        lock (playerAudio)
        {
          playerAudio.Events = playerAudio.Events.Where(pa => pa.Priority <= priority).ToList();
          playerAudio.Events.Add(new PlaybackEvent
          {
            AudioData = audioData,
            WaveFormat = waveFormat,
            Priority = priority,
            Rate = rate,
            Volume = volume,
            Seconds = seconds
          });
        }
      }
    }

    private bool PlayAudioData(byte[] data, WaveFormat waveFormat, int rate = 0, int volume = 4)
    {
      // remove header
      data = data[44..];

      try
      {
        var stream = new RawSourceWaveStream(data, 0, data.Length, waveFormat);
        var output = CreateDirectSoundOut(_appVolume, GetDevice(), stream, rate, volume);
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

    private static VolumeSampleProvider CreateVolumeProvider(float appVolume, RawSourceWaveStream stream, DirectSoundOut output, int rate, int volume)
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

    private void ProcessAsync(PlayerAudio audio)
    {
      var cancellationTokenSource = new CancellationTokenSource();
      audio.ProcessingToken = cancellationTokenSource;

      _ = Task.Run(async () =>
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
                  var data = audio.CurrentEvent.AudioData[44..];
                  stream = new RawSourceWaveStream(data, 0, data.Length, audio.CurrentEvent.WaveFormat);

                  // make sure audio is still valid
                  try
                  {
                    output = CreateDirectSoundOut(_appVolume, GetDevice(), stream, audio.CurrentEvent.Rate, audio.CurrentEvent.Volume);
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

            await Task.Delay(50);
          }
        }
        catch (OperationCanceledException)
        {
          // ignore cancel event
        }
        catch (Exception ex)
        {
          Log.Debug("Error during playback.", ex);
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

    private static DirectSoundOut CreateDirectSoundOut(float appVolume, Guid device, RawSourceWaveStream stream, int rate, int volume)
    {
      var output = new DirectSoundOut(device, LATENCY);
      var provider = CreateVolumeProvider(appVolume, stream, output, rate, volume);
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
        return data;
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

      return current * floatIncrease;
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
      internal int PiperSampleRate { get; set; }
    }

    private class PlaybackEvent
    {
      internal long Priority { get; init; } = -1;
      internal int Rate { get; init; }
      internal int Volume { get; init; } = 4;
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