using log4net;
using Microsoft.Extensions.Caching.Memory;
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
using Windows.Storage.Streams;

namespace EQLogParser
{
  internal partial class AudioManager : IDisposable
  {
    public const string AudioCacheKey = "audio-cache:";
    internal event Action<bool> DeviceListChanged;
    internal static AudioManager Instance => Lazy.Value;

    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly Lazy<AudioManager> Lazy = new(() => new AudioManager());
    private const int LATENCY = 72;
    private readonly ConcurrentDictionary<string, PlayerAudio> _playerAudios = [];
    private readonly ConcurrentDictionary<string, bool> _isRenderDevice = new();
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly AudioDeviceNotificationClient _notificationClient = new();
    private readonly DispatcherTimer _updateTimer;
    private readonly object _deviceLock = new();
    private readonly bool _usePiper;
    private readonly List<VoiceInformation> _validVoices = [];
    private MMDeviceEnumerator _deviceEnumerator;
    private Guid _selectedDeviceGuid = Guid.Empty;
    private bool _disposed;
    private volatile float _appVolume = 1.0f;
    private volatile bool _initialized;

    private AudioManager()
    {
      _updateTimer = new DispatcherTimer(DispatcherPriority.Loaded);
      _updateTimer.Tick += DoUpdateDeviceList;
      _updateTimer.Interval = new TimeSpan(0, 0, 0, 1, 500);
      _ = InitAudio();

      if (PiperTts.Initialize())
      {
        Log.Info("Using piper-tts");
        _usePiper = true;
      }
    }

    internal int GetVolume() => (int)(_appVolume * 100.0f);
    internal void SetVolume(int volume) => _appVolume = volume / 100.0f;

    internal async Task LoadValidVoicesAsync()
    {
      if (!PiperTts.Initialize() && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240) && _validVoices.Count == 0)
      {
        SpeechSynthesizer synth = null;
        IReadOnlyList<VoiceInformation> voices;

        try
        {
          synth = new SpeechSynthesizer();
          voices = SpeechSynthesizer.AllVoices; // this can also throw on some machines
        }
        catch
        {
          synth?.Dispose();
          return;
        }

        try
        {
          foreach (var voice in voices)
          {
            try
            {
              synth.Voice = voice;
              using IRandomAccessStream stream = await synth.SynthesizeTextToStreamAsync("test");
            }
            catch
            {
              continue;
            }

            // prefer default first
            if (SpeechSynthesizer.DefaultVoice?.Id == voice?.Id)
            {
              _validVoices.Insert(0, voice);
            }
            else
            {
              _validVoices.Add(voice);
            }
          }
        }
        finally
        {
          synth.Dispose();
        }
      }
    }

    internal List<string> GetVoiceList()
    {
      if (_usePiper) return PiperTts.GetVoiceList();
      var list = new List<string>();

      try
      {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
        {
          foreach (var voice in _validVoices)
          {
            if (voice?.DisplayName is string name)
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
      if (!string.IsNullOrEmpty(voice) && _playerAudios.TryGetValue(id, out var playerAudio))
      {
        lock (playerAudio)
        {
          LoadVoice(id, voice, playerAudio);
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
      if (_playerAudios.TryGetValue(id, out var playerAudio))
      {
        CancellationTokenSource cancellationTokenSource = null;
        lock (playerAudio)
        {
          if (playerAudio.ProcessingToken == null)
          {
            cancellationTokenSource = new CancellationTokenSource();
            playerAudio.ProcessingToken = cancellationTokenSource;
          }
        }

        if (cancellationTokenSource != null)
        {
          _ = ProcessAsync(playerAudio, cancellationTokenSource);
        }
      }
    }

    internal void Stop(string id, bool remove = false)
    {
      if (!string.IsNullOrEmpty(id) && _playerAudios.TryGetValue(id, out var playerAudio))
      {
        SpeechSynthesizer cleanupSynth = null;
        System.Speech.Synthesis.SpeechSynthesizer cleanupSapi = null;
        CancellationTokenSource cts = null;

        lock (playerAudio)
        {
          playerAudio.CurrentEvent = null;
          playerAudio.Events.Clear();
          playerAudio.PlayerRequestStop = true;

          if (remove)
          {
            cts = playerAudio.ProcessingToken;
            _playerAudios.TryRemove(id, out _);

            try
            {
              if (_usePiper)
              {
                PiperTts.RemoveVoice(id);
              }
              else
              {
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
                {
                  cleanupSynth = playerAudio.Synth;
                  playerAudio.Synth = null;
                }

                cleanupSapi = playerAudio.SapiSynth;
                playerAudio.SapiSynth = null;
              }
            }
            catch (Exception)
            {
              // ignore
            }
          }
        }

        try
        {
          cts?.Cancel();
        }
        catch (Exception)
        {
          // ignore
        }

        // dispose outside of locking
        try
        {
          if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240) && cleanupSynth != null)
          {
            cleanupSynth?.Dispose();
          }

          cleanupSapi?.Dispose();
        }
        catch (Exception)
        {
          // ignore
        }
      }
    }

    internal async void TestSpeakFileAsync(string filePath, int adjustedVolume = 4)
    {
      await using var reader = new AudioFileReader(filePath);
      if (!string.IsNullOrEmpty(filePath) && await ReadFileToByteArrayAsync(reader) is { Length: > 0 } data)
      {
        var volume = ConvertVolume(_appVolume, adjustedVolume);
        if (!PlayAudioData(data, reader.WaveFormat, GetDevice(), volume, 0))
        {
          new MessageWindow("Unable to Play sound. No audio device?", Resource.AUDIO_ERROR).ShowDialog();
        }
      }
    }

    internal async void TestSpeakTtsAsync(string tts, string voice = null, int rate = 0, int playerVolume = -1, int adjustedVolume = 4)
    {
      if (!string.IsNullOrEmpty(tts))
      {
        (var audio, var sample) = await SynthesizeTextAsync(voice, tts);

        if (audio?.Length > 0)
        {
          var waveFormat = new WaveFormat(sample, 16, 1);
          var appVolume = playerVolume > -1 ? playerVolume / 100.0f : _appVolume;
          var volume = ConvertVolume(appVolume, adjustedVolume);
          if (!PlayAudioData(audio, waveFormat, GetDevice(), volume, rate))
          {
            new MessageWindow("Unable to Play sound. No audio device?", Resource.AUDIO_ERROR).ShowDialog();
          }
        }
      }
    }

    internal async void SpeakOrSaveTtsAsync(string tts, string voice, string id, float specificVolume, int rate, string fileName = null)
    {
      if (!string.IsNullOrEmpty(tts))
      {
        (var audio, var sample) = await SynthesizeTextAsync(voice, tts);

        if (audio?.Length > 0)
        {
          var waveFormat = new WaveFormat(sample, 16, 1);

          if (string.IsNullOrEmpty(fileName))
          {
            var device = GetDeviceOrDefault(id);
            if (!PlayAudioData(audio, waveFormat, device, specificVolume, rate))
            {
              new MessageWindow("Unable to Play sound. No audio device?", Resource.AUDIO_ERROR).ShowDialog();
            }
          }
          else
          {
            WaveFileWriter writer = null;
            RawSourceWaveStream stream = null;
            try
            {
              stream = new RawSourceWaveStream(audio, 0, audio.Length, waveFormat);
              var volume = ConvertVolume(specificVolume, 4);
              var volumeProvider = CreateVolumeProvider(volume, stream, rate);
              if (volumeProvider != null)
              {
                var provider = volumeProvider.ToWaveProvider16();

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
            }
            catch (Exception ex)
            {
              Log.Error("Error Exporting WAV", ex);
              new MessageWindow("Failed to Export wav file. Check the Error Log for Details.", Resource.EXPORT_ERROR).ShowDialog();
            }
            finally
            {
              try
              {
                writer?.Dispose();
                stream?.Dispose();
              }
              catch (Exception)
              {
                // ignore
              }
            }
          }
        }
      }
    }

    internal async void SpeakFileAsync(string id, string filePath, long priority, int playerVolume, int adjustedVolume = 4)
    {
      if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(filePath) && File.Exists(filePath))
      {
        try
        {
          var cacheKey = $"{AudioCacheKey}{Path.GetFullPath(filePath).ToLowerInvariant()}";
          var cachedAudio = await App.AppCache.GetOrCreateAsync(cacheKey, async entry =>
          {
            await using var reader = new AudioFileReader(filePath);
            if (await ReadFileToByteArrayAsync(reader) is { Length: > 0 } data)
            {
              entry.SetSlidingExpiration(TimeSpan.FromMinutes(60));
              entry.SetSize(data.Length);
              return new CachedAudio
              {
                Data = data,
                Seconds = reader.TotalTime.TotalSeconds,
                WaveFormat = reader.WaveFormat
              };
            }
            entry.AbsoluteExpiration = DateTimeOffset.MinValue;
            return null;
          });

          if (cachedAudio != null)
          {
            SpeakAsync(id, cachedAudio.Data, cachedAudio.WaveFormat, 0, priority, playerVolume, adjustedVolume, cachedAudio.Seconds);
          }
        }
        catch (Exception ex)
        {
          Log.Debug($"Error while playing file: {filePath}", ex);
        }
      }
    }

    internal async void SpeakTtsAsync(string id, string tts, long priority, int rate, int playerVolume, int adjustedVolume)
    {
      if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(tts))
      {
        byte[] audio = null;
        var sample = 0;
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
                (audio, sample) = await SynthesizeTextToByteArrayAsync(tts, synth);
              }
              else if (sapiSynth != null)
              {
                (audio, sample) = await SynthesizeTextToByteArrayAsync(tts, sapiSynth);
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
          SpeakAsync(id, audio, waveFormat, rate, priority, playerVolume, adjustedVolume);
        }
      }
    }

    internal static (List<string> idList, List<string> nameList) GetDeviceList()
    {
      List<string> idList = [Guid.Empty.ToString()];
      List<string> nameList = ["Default Audio"];

      try
      {
        foreach (var device in DirectSoundOut.Devices.ToList())
        {
          if (device.Guid != Guid.Empty)
          {
            idList.Add(device.Guid.ToString());
            nameList.Add(device.Description);
          }
        }
      }
      catch (Exception ex)
      {
        Log.Error("Error reading Audio Devices.", ex);
      }

      return (idList, nameList);
    }

    private async void DoUpdateDeviceList(object sender, EventArgs e)
    {
      _updateTimer.Stop();

      try
      {
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
          await InitAudio();
        }

        DeviceListChanged?.Invoke(true);
      }
      catch (Exception)
      {
        // ignore
      }
    }

    protected void UpdateDeviceList()
    {
      _updateTimer.Stop();
      _updateTimer.Start();
    }

    private async Task InitAudio()
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

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, _) => tcs.TrySetResult();
        output.Play();
        output.Stop();
        await tcs.Task;
        await CleanupHelperAsync(output, reader, memStream);
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

    private void SpeakAsync(string id, byte[] audioData, WaveFormat waveFormat, int rate = 0, long priority = 5,
      int playerVolume = -1, int adjustedVolume = 4, double seconds = -1)
    {
      if (_playerAudios.TryGetValue(id, out var playerAudio))
      {
        lock (playerAudio)
        {
          var appVolume = playerVolume > -1 ? playerVolume / 100.0f : _appVolume;
          playerAudio.Events = [.. playerAudio.Events.Where(pa => pa.Priority <= priority)];

          playerAudio.Events.Add(new PlaybackEvent
          {
            AudioData = audioData,
            WaveFormat = waveFormat,
            Priority = priority,
            Rate = rate,
            Volume = ConvertVolume(appVolume, adjustedVolume),
            Seconds = seconds
          });
        }
      }
    }

    private static bool PlayAudioData(byte[] data, WaveFormat waveFormat, Guid device, float volume, int rate = 0)
    {
      RawSourceWaveStream stream = null;
      DirectSoundOut output = null;
      try
      {
        stream = new RawSourceWaveStream(data, 0, data.Length, waveFormat);
        output = CreateDirectSoundOut(device, volume, stream, rate);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, _) => tcs.TrySetResult();
        output.Play();

        // Fire-and-forget cleanup
        _ = Task.Run(async () =>
        {
          try
          {
            await tcs.Task.ConfigureAwait(false);
          }
          finally
          {
            await CleanupHelperAsync(output, stream);
          }
        });
      }
      catch (Exception ex)
      {
        Log.Error("Error playing audio.", ex);
        _ = CleanupHelperAsync(output, stream);
        return false;
      }

      return true;
    }

    private static VolumeSampleProvider CreateVolumeProvider(float volume, RawSourceWaveStream stream, int rate)
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
          Volume = volume
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

    private async Task ProcessAsync(PlayerAudio playerAudio, CancellationTokenSource cancellationTokenSource)
    {
      await Task.Run(async () =>
      {
        RawSourceWaveStream stream = null;
        DirectSoundOut output = null;
        List<DirectSoundOut> toDispose = [];

        try
        {
          while (!cancellationTokenSource.Token.IsCancellationRequested)
          {
            // if maybe playing
            if (output != null)
            {
              try
              {
                if (output.PlaybackState != PlaybackState.Stopped)
                {
                  var stopAudio = false;
                  lock (playerAudio)
                  {
                    foreach (var item in playerAudio.Events)
                    {
                      if (playerAudio.CurrentEvent != item && playerAudio.CurrentEvent?.Priority > item.Priority)
                      {
                        stopAudio = true;
                        break;
                      }
                    }
                  }

                  if (stopAudio)
                  {
                    output.Stop();
                    toDispose.Add(output);
                    output = null;
                  }
                }
              }
              catch (Exception)
              {
                // ignore stop errors
              }
            }

            // if still maybe playing
            if (output != null)
            {
              try
              {
                // skip through short sound files if there's audio pending
                if (output.PlaybackState == PlaybackState.Playing)
                {
                  var stopAudio = false;
                  lock (playerAudio)
                  {
                    if (playerAudio.PlayerRequestStop || (playerAudio.CurrentEvent?.Seconds is > -1 and < 1.0 && playerAudio.Events.Count > 0))
                    {
                      stopAudio = true;
                    }
                  }

                  if (stopAudio)
                  {
                    output.Stop();
                    toDispose.Add(output);
                    output = null;
                  }
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
                }

                if (output != null)
                {
                  toDispose.Add(output);
                  output = null;
                }

                byte[] data = null;
                var rate = 0;
                float volume = 0;
                WaveFormat format = null;
                lock (playerAudio)
                {
                  if (playerAudio.Events.Count > 0)
                  {
                    playerAudio.PlayerRequestStop = false;
                    playerAudio.CurrentEvent = playerAudio.Events[0];
                    playerAudio.Events.RemoveAt(0);
                    if (playerAudio.CurrentEvent?.AudioData?.Length > 0)
                    {
                      data = playerAudio.CurrentEvent.AudioData;
                      rate = playerAudio.CurrentEvent.Rate;
                      volume = playerAudio.CurrentEvent.Volume;
                      format = playerAudio.CurrentEvent.WaveFormat;
                    }
                  }
                }

                if (data != null && format != null)
                {
                  stream = new RawSourceWaveStream(data, 0, data.Length, format);

                  // make sure audio is still valid
                  try
                  {
                    output = CreateDirectSoundOut(GetDevice(), volume, stream, rate);
                    output.Play();
                  }
                  catch (Exception)
                  {
                    if (output != null)
                    {
                      toDispose.Add(output);
                      output = null;
                    }
                  }
                }
              }
              catch (Exception ex)
              {
                Log.Error("Error Playing Audio", ex);
              }
            }

            await Task.Delay(50, cancellationTokenSource.Token);

            foreach (var item in toDispose)
            {
              try
              {
                item?.Dispose();
              }
              catch (Exception)
              {
                // ignore dispose errors
              }
            }

            toDispose.Clear();
          }
        }
        catch (Exception)
        {
          // ignore cancel event. the rest should have it's own try/catch
        }
        finally
        {
          try
          {
            cancellationTokenSource.Dispose();

            if (stream != null)
            {
              stream.Dispose();
              stream = null;
            }

            if (output != null)
            {
              output.Stop();
              toDispose.Add(output);
              output = null;
            }
          }
          catch (Exception)
          {
            // ignore dispose errors
          }
          finally
          {
            lock (playerAudio)
            {
              playerAudio.ProcessingToken = null;
            }
          }

          foreach (var item in toDispose)
          {
            try
            {
              item?.Dispose();
            }
            catch (Exception)
            {
              // ignore dispose errors
            }
          }
        }
      }, cancellationTokenSource.Token);
    }

    private async Task<(byte[], int)> SynthesizeTextAsync(string voice, string tts)
    {
      byte[] audio = null;
      var sample = 0;

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
          if (CreateSapiSpeechSynthesizer(voice) is { } synth)
          {
            (audio, sample) = await SynthesizeTextToByteArrayAsync(tts, synth);
            synth.Dispose();
          }
        }
        else
        {
          if (CreateSpeechSynthesizer(voice) is { } synth)
          {
            (audio, sample) = await SynthesizeTextToByteArrayAsync(tts, synth);
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) synth.Dispose();
          }
        }
      }

      return (audio, sample);
    }

    private SpeechSynthesizer CreateSpeechSynthesizer(string voice)
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

    private VoiceInformation GetVoiceInfo(string name)
    {
      if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240) || _validVoices.Count == 0) return null;
      if (name == null) return _validVoices[0];

      foreach (var voice in _validVoices)
      {
        if (voice.DisplayName == name || name.StartsWith(voice.DisplayName, StringComparison.OrdinalIgnoreCase))
        {
          return voice;
        }
      }

      return _validVoices[0];
    }

    private bool IsRenderDevice(string deviceId)
    {
      if (_isRenderDevice.TryGetValue(deviceId, out var render))
      {
        return render;
      }

      try
      {
        using var dev = _deviceEnumerator.GetDevice(deviceId);
        // PKEY_AudioEndpoint_FormFactor -> int; 1=Speakers, 3=Headphones, 8=SPDIF, 9=HDMI, etc.
        var formFactor = (uint)dev.Properties[PropertyKeys.PKEY_AudioEndpoint_FormFactor].Value;
        render = dev.DataFlow == DataFlow.Render && formFactor is 0 or 1 or 2 or 3 or 5 or 6 or 8 or 9;
      }
      catch (Exception)
      {
        render = false;
      }

      _isRenderDevice[deviceId] = render;

      return render;
    }

    private Guid GetDevice()
    {
      lock (_deviceLock)
      {
        return _selectedDeviceGuid;
      }
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

    private static DirectSoundOut CreateDirectSoundOut(Guid device, float volume, RawSourceWaveStream stream, int rate)
    {
      // short sounds need a shorter latency but don't go below 10 as it may break entirely
      var latencyCalc = (int)Math.Min(Math.Max(stream.TotalTime.TotalMilliseconds - 5, 30), LATENCY);
      var output = new DirectSoundOut(device, latencyCalc);

      var provider = CreateVolumeProvider(volume, stream, rate);
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
        try
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
        catch (Exception)
        {
          // ignore
        }
      }

      return foundGuid;
    }

    private static System.Speech.Synthesis.VoiceInfo GetSapiVoiceInfo(string name)
    {
      System.Speech.Synthesis.VoiceInfo voiceInfo = null;

      try
      {
        using var synth = new System.Speech.Synthesis.SpeechSynthesizer();
        voiceInfo = synth.Voice;
        if (!string.IsNullOrEmpty(name))
        {
          // do not pass null for culture
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

    private static async Task<(byte[], int)> SynthesizeTextToByteArrayAsync(string tts, SpeechSynthesizer synth)
    {
      if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
      {
        return (null, 0);
      }

      SpeechSynthesisStream stream = null;

      try
      {
        stream = await synth.SynthesizeTextToStreamAsync(tts);
        using var reader = new WaveFileReader(stream.AsStream());
        return await ReadPcmAsync(reader);
      }
      catch (Exception ex)
      {
        Log.Debug("Error synthesizing text to byte array.", ex);
      }
      finally
      {
        try
        {
          stream?.Dispose();
        }
        catch (Exception)
        {
          // ignore dispose errors
        }
      }

      return (null, 0);
    }

    private static async Task<(byte[], int)> SynthesizeTextToByteArrayAsync(string tts, System.Speech.Synthesis.SpeechSynthesizer synth)
    {
      try
      {
        using var mem = new MemoryStream();
        synth.SetOutputToWaveStream(mem);
        synth.Speak(tts);
        synth.SetOutputToNull(); // release reference to mem
        mem.Position = 0;
        using var reader = new WaveFileReader(mem);
        return await ReadPcmAsync(reader);
      }
      catch (Exception ex)
      {
        Log.Debug("Error synthesizing text to byte array.", ex);
      }

      return (null, 0);
    }

    private static async Task<(byte[], int)> ReadPcmAsync(WaveFileReader reader)
    {
      using var pcm = WaveFormatConversionStream.CreatePcmStream(reader);
      using var ms = pcm.Length > 0 ? new MemoryStream((int)pcm.Length) : new MemoryStream();
      await pcm.CopyToAsync(ms);
      var data = ms.ToArray();
      var sample = pcm.WaveFormat.SampleRate;
      return (data, sample);
    }

    private static async Task CleanupHelperAsync(DirectSoundOut output, Stream stream, MemoryStream stream2 = null)
    {
      if (stream != null)
      {
        try
        {
          await stream.DisposeAsync();
        }
        catch (Exception)
        {
          // ignore dispose errors
        }
      }

      if (stream2 != null)
      {
        try
        {
          await stream2.DisposeAsync();
        }
        catch (Exception)
        {
          // ignore dispose errors
        }
      }

      if (output != null)
      {
        try
        {
          output.Dispose();
        }
        catch (Exception)
        {
          // ignore dispose errors
        }
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

    private class CachedAudio
    {
      internal byte[] Data { get; init; }
      internal WaveFormat WaveFormat { get; init; }
      internal double Seconds { get; init; }
    }

    private class PlayerAudio
    {
      internal List<PlaybackEvent> Events { get; set; } = [];
      internal PlaybackEvent CurrentEvent { get; set; }
      internal CancellationTokenSource ProcessingToken { get; set; }
      internal SpeechSynthesizer Synth { get; set; }
      internal System.Speech.Synthesis.SpeechSynthesizer SapiSynth { get; set; }
      internal int PiperSampleRate { get; set; }
      internal bool PlayerRequestStop { get; set; }
    }

    private class PlaybackEvent
    {
      internal long Priority { get; init; } = -1;
      internal int Rate { get; init; }
      internal float Volume { get; init; } = -1;
      internal byte[] AudioData { get; init; }
      internal WaveFormat WaveFormat { get; init; }
      internal double Seconds { get; init; }
    }

    private class AudioDeviceNotificationClient : IMMNotificationClient
    {
      public void OnDeviceStateChanged(string deviceId, DeviceState newState)
      {
        if (Instance.IsRenderDevice(deviceId))
        {
          Instance.UpdateDeviceList();
        }
      }

      public void OnDeviceAdded(string deviceId)
      {
        if (Instance.IsRenderDevice(deviceId))
        {
          Instance.UpdateDeviceList();
        }
      }

      public void OnDeviceRemoved(string deviceId)
      {
        if (Instance.IsRenderDevice(deviceId))
        {
          Instance.UpdateDeviceList();
        }
      }

      public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
      {
        if (flow == DataFlow.Render && Instance.IsRenderDevice(defaultDeviceId))
        {
          Instance.UpdateDeviceList();
        }
      }

      public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }
    }
  }
}