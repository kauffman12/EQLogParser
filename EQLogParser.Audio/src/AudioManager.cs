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
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EQLogParser.Audio
{
  /// <summary>
  /// Manages all audio playback: device selection, TTS synthesis, file playback,
  /// per-player audio queues, volume/tempo control, and caching.
  /// </summary>
  public partial class AudioManager : IAudioManager, IDisposable
  {
    public const string AudioCacheKey = "audio-cache:";
    public const string WindowsEngine = WindowsTtsEngine.EngineName;
    public const string PiperEngine = PiperTtsEngine.EngineName;
    public const string KokoroEngine = KokoroTtsEngine.EngineName;
    public event Action<bool> DeviceListChanged;
    public static AudioManager Instance => Lazy.Value;

    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly Lazy<AudioManager> Lazy = new(() => new AudioManager());
    private const int LATENCY = 72;
    private readonly ConcurrentDictionary<string, PlayerAudio> _playerAudios = [];
    private readonly ConcurrentDictionary<string, bool> _isRenderDevice = new();
    /*
     * Engines hold process wide native state (Piper's voice table, Kokoro's inference session), so only one
     * synthesis runs at a time. Cached phrases never take the gate. See docs/DesignNotes.md -> Speech synthesis
     * and TTS engines.
     */
    private readonly SemaphoreSlim _synthGate = new(1, 1);
    private readonly AudioDeviceNotificationClient _notificationClient = new();
    private readonly object _deviceLock = new();

    /* Swapped in place when the user picks another engine mid session; read it under _synthGate when it matters. */
    private ITtsEngine _tts;

    /*
     * What the host asked each player to speak with. The engine decides whether it can honor that name, and this is
     * what gets handed to the next engine when the user switches engines without restarting.
     */
    private readonly ConcurrentDictionary<string, string> _requestedVoices = [];
    private MMDeviceEnumerator _deviceEnumerator;
    private Guid _selectedDeviceGuid = Guid.Empty;
    private bool _disposed;
    private volatile float _appVolume = 1.0f;
    private volatile bool _initialized;
    private readonly Timer _deviceUpdateTimer;

    // Dependencies injected by host application
    private static IMemoryCache _cache;
    private static Action<string> _showError;
    private static string _preferredEngine;

    /// <summary>Call once at app startup to inject dependencies.</summary>
    /// <param name="preferredEngine">One of <see cref="WindowsEngine"/>, <see cref="PiperEngine"/>, or
    /// <see cref="KokoroEngine"/>. If null/empty or the engine isn't available, falls back to the first
    /// available engine in Piper, Kokoro, Windows order.</param>
    public static void Initialize(IMemoryCache cache, Action<string> showError = null, string preferredEngine = null)
    {
      _cache = cache;
      _showError = showError;
      _preferredEngine = preferredEngine;
    }

    private AudioManager()
    {
      // Use System.Threading.Timer instead of DispatcherTimer to avoid WPF dependency
      _deviceUpdateTimer = new Timer(DoUpdateDeviceList, null, Timeout.Infinite, Timeout.Infinite);
      _ = InitAudio();

      // The speech runtimes are downloaded rather than installed, so the loaders need to know where a pack lives
      // before an engine built against it reaches for a type or a native library.
      TtsPackManager.EnsureResolversRegistered();

      _tts = TtsEngineFactory.Create(_preferredEngine);

      // A milestone worth having in a bug report; Windows is the boring default, so stay quiet there.
      if (_tts.Name is KokoroEngine or PiperEngine)
      {
        Log.Info($"Using {_tts.Name.ToLowerInvariant()}-tts");
      }
    }

    public bool IsEngineAvailable(string engine) => EngineIsAvailable(engine);

    public bool IsEngineDownloaded(string engine) => TtsPackManager.IsPackOnDisk(engine);

    public long GetEngineDownloadBytes(string engine) => TtsPackManager.GetDownloadBytes(engine);

    public Task<bool> InstallEngineAsync(string engine, IProgress<float> progress, CancellationToken cancellationToken = default) =>
      TtsPackManager.InstallAsync(engine, progress, cancellationToken);

    /*
     * Reclaims the space a pack uses. The engine currently speaking keeps its native libraries mapped for the life of
     * the process on Windows, so removing that one would leave a directory half deleted and an engine that still
     * claims to work; the caller picks another engine first.
     */
    public bool RemoveEngineFiles(string engine)
    {
      if (string.IsNullOrEmpty(engine) || engine == GetActiveEngine())
      {
        return false;
      }

      return TtsPackManager.Remove(engine);
    }

    /// <summary>Every engine the app knows how to drive, whether or not its runtime pack is installed. The picker
    /// lists these so a download can be offered from the same place the engine is chosen.</summary>
    public static List<string> GetAllEngines() => [PiperEngine, KokoroEngine, WindowsEngine];

    /// <summary>Engines that can currently be selected: Windows is always available; Piper/Kokoro only once their
    /// runtime pack (or, for Piper, an older app installed copy) is on disk.</summary>
    public static List<string> GetAvailableEngines() => GetAllEngines().Where(EngineIsAvailable).ToList();

    private static bool EngineIsAvailable(string engine) =>
      engine == WindowsEngine || TtsPackManager.ResolveRoot(engine) is not null;

    /// <summary>The engine actually in use for this running session.</summary>
    public string GetActiveEngine() => _tts.Name;

    /// <summary>Switches the speech engine without a restart; only engines whose runtime pack is installed can be
    /// selected. Returns false when the switch did not happen, leaving the current engine speaking.</summary>
    public async Task<bool> SwitchEngineAsync(string engine)
    {
      if (_disposed || string.IsNullOrEmpty(engine))
      {
        return false;
      }

      var previous = _tts;

      if (engine == previous.Name || !GetAvailableEngines().Contains(engine))
      {
        return engine == previous.Name;
      }

      // held for the whole switch: creation and disposal both touch process wide native state, and no synthesis may
      // run against an engine that is being created or torn down
      await _synthGate.WaitAsync().ConfigureAwait(false);

      try
      {
        // Creation is where the cost is: Kokoro builds an inference session over a 156 MB graph and Windows proves
        // every voice by synthesizing a word into a stream. Callouts that arrive meanwhile wait on this gate, which
        // is still kinder than speaking half of one engine and half of the other.
        var next = await Task.Run(() => TtsEngineFactory.CreateNamed(engine)).ConfigureAwait(false);

        if (next is null)
        {
          Log.Debug($"Unable to switch the TTS engine to {engine}; staying on {previous.Name}.");
          return false;
        }

        await next.LoadVoicesAsync().ConfigureAwait(false);

        foreach (var requested in _requestedVoices)
        {
          next.SetVoice(requested.Key, requested.Value);
        }

        _tts = next;

        // retired under the gate on purpose: every call into an engine happens under this gate, so an engine that
        // nobody can reach any more is the only one safe to hand to the native release
        previous.Dispose();

        if (next.Name is KokoroEngine or PiperEngine)
        {
          Log.Info($"Using {next.Name.ToLowerInvariant()}-tts");
        }

        return true;
      }
      finally
      {
        _synthGate.Release();
      }
    }

    private static void ShowAudioError()
    {
      _showError?.Invoke("Unable to Play sound. No audio device?");
    }

    public int GetVolume() => (int)(_appVolume * 100.0f);
    public void SetVolume(int volume) => _appVolume = volume / 100.0f;

    public Task LoadValidVoicesAsync() => _tts.LoadVoicesAsync();

    public List<string> GetVoiceList() => _tts.GetVoices();

    public string GetDefaultVoice() => _tts.GetDefaultVoice();

    public void SelectDevice(string id)
    {
      var device = GetDeviceOrDefault(id);
      lock (_deviceLock)
      {
        _selectedDeviceGuid = device;
      }
    }

    public void SetVoice(string id, string voice)
    {
      if (!string.IsNullOrEmpty(voice) && _playerAudios.ContainsKey(id))
      {
        _requestedVoices[id] = voice;
        _tts.SetVoice(id, voice);
      }
    }

    public void Add(string id, string voice)
    {
      _requestedVoices[id] = voice;
      _tts.SetVoice(id, voice);
      _playerAudios.TryAdd(id, new PlayerAudio());
    }

    public void StartAudio(string id)
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

    public void StopAudio(string id, bool remove = false)
    {
      if (!string.IsNullOrEmpty(id) && _playerAudios.TryGetValue(id, out var playerAudio))
      {
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
          }
        }

        if (remove)
        {
          _requestedVoices.TryRemove(id, out _);

          // whatever the engine held for this player is the engine's to release, outside this lock
          _tts.RemoveVoice(id);
        }

        try
        {
          cts?.Cancel();
        }
        catch (Exception)
        {
          // ignore
        }
      }
    }

    public void TestSpeakFileAsync(string filePath, int adjustedVolume = 4) =>
      _ = TestSpeakFileCoreAsync(filePath, adjustedVolume);

    private async Task TestSpeakFileCoreAsync(string filePath, int adjustedVolume)
    {
      try
      {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
          return;
        }

        await using var reader = new AudioFileReader(filePath);
        if (await ReadFileToByteArrayAsync(reader).ConfigureAwait(false) is { Length: > 0 } data)
        {
          var volume = ConvertVolume(_appVolume, adjustedVolume);
          if (!PlayAudioData(data, reader.WaveFormat, GetDevice(), volume, 0))
          {
            ShowAudioError();
          }
        }
      }
      catch (Exception ex)
      {
        Log.Debug($"Error while previewing file: {filePath}", ex);
      }
    }

    public void TestSpeakTtsAsync(string tts, string voice = null, int rate = 0, int playerVolume = -1,
      int adjustedVolume = 4) =>
      _ = TestSpeakTtsCoreAsync(tts, voice, rate, playerVolume, adjustedVolume);

    private async Task TestSpeakTtsCoreAsync(string tts, string voice, int rate, int playerVolume, int adjustedVolume)
    {
      if (string.IsNullOrEmpty(tts))
      {
        return;
      }

      try
      {
        (var audio, var sample) = await SynthesizeVoiceCachedAsync(voice, tts).ConfigureAwait(false);

        if (audio?.Length > 0)
        {
          var waveFormat = new WaveFormat(sample, 16, 1);
          var appVolume = playerVolume > -1 ? playerVolume / 100.0f : _appVolume;
          var volume = ConvertVolume(appVolume, adjustedVolume);
          if (!PlayAudioData(audio, waveFormat, GetDevice(), volume, rate))
          {
            ShowAudioError();
          }
        }
      }
      catch (Exception ex)
      {
        Log.Debug("Error synthesizing text.", ex);
      }
    }

    public void SpeakOrSaveTtsAsync(string tts, string voice, string id, float specificVolume, int rate,
      string fileName = null) =>
      _ = SpeakOrSaveTtsCoreAsync(tts, voice, id, specificVolume, rate, fileName);

    private async Task SpeakOrSaveTtsCoreAsync(string tts, string voice, string id, float specificVolume, int rate,
      string fileName)
    {
      if (!string.IsNullOrEmpty(tts))
      {
        (var audio, var sample) = await SynthesizeVoiceCachedAsync(voice, tts).ConfigureAwait(false);

        if (audio?.Length > 0)
        {
          var waveFormat = new WaveFormat(sample, 16, 1);

          if (string.IsNullOrEmpty(fileName))
          {
            var device = GetDeviceOrDefault(id);
            if (!PlayAudioData(audio, waveFormat, device, specificVolume, rate))
            {
              ShowAudioError();
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
              _showError?.Invoke("Failed to Export wav file. Check the Error Log for Details.");
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

    public async void SpeakFileAsync(string id, string filePath, long priority, int playerVolume, int adjustedVolume = 4)
    {
      if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(filePath) && File.Exists(filePath) && _cache != null)
      {
        try
        {
          var cacheKey = $"{AudioCacheKey}{Path.GetFullPath(filePath).ToLowerInvariant()}";
          var cachedAudio = await _cache.GetOrCreateAsync(cacheKey, async entry =>
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

    /*
     * The public entry points hand the work to the thread pool and return. Callers (the trigger processor and several
     * buttons) fire and forget, so nothing here may resume on the caller's context: a neural synthesis costs a few
     * hundred milliseconds and it used to run on the UI thread, freezing the window mid callout.
     */
    public void SpeakTtsAsync(string id, string tts, long priority, int rate, int playerVolume, int adjustedVolume) =>
      _ = SpeakTtsCoreAsync(id, tts, priority, rate, playerVolume, adjustedVolume);

    private async Task SpeakTtsCoreAsync(string id, string tts, long priority, int rate, int playerVolume,
      int adjustedVolume)
    {
      if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(tts) || !_playerAudios.ContainsKey(id))
      {
        return;
      }

      try
      {
        (var audio, var sample) = await SynthesizeForPlayerCachedAsync(id, tts).ConfigureAwait(false);

        if (audio is { Length: > 0 })
        {
          var waveFormat = new WaveFormat(sample, 16, 1);
          SpeakAsync(id, audio, waveFormat, rate, priority, playerVolume, adjustedVolume);
        }
      }
      catch (Exception ex)
      {
        Log.Debug("Error synthesizing text.", ex);
      }
    }

    // Speaks for a registered player; that player's voice resolves against the engine doing the speaking.
    private Task<(byte[] pcm, int sampleRate)> SynthesizeForPlayerCachedAsync(string playerId, string text) =>
      SynthesizeCachedAsync(playerId, null, text, (engine, voice) => engine.SynthesizeForPlayerAsync(playerId, text));

    // Preview and WAV export speak voices no player owns.
    private Task<(byte[] pcm, int sampleRate)> SynthesizeVoiceCachedAsync(string voice, string text) =>
      SynthesizeCachedAsync(null, voice, text, (engine, name) => engine.SynthesizeVoiceAsync(name, text));

    /*
     * Trigger callouts come from a small set of sentences and speech does not change for a given engine, voice and
     * text, so the PCM is cached: only fresh text pays for inference, which matters most for Kokoro. The text is
     * hashed so a long custom callout cannot produce an unbounded cache key.
     *
     * Everything, cache lookup included, happens under the gate. The engine can be swapped while this waits, and
     * resolving voice, key and synthesis against one local copy means cached PCM never crosses engines and an engine
     * that is being disposed is never spoken with: SwitchEngineAsync takes it out under this same gate.
     */
    private async Task<(byte[] pcm, int sampleRate)> SynthesizeCachedAsync(string playerId, string voice, string text,
      Func<ITtsEngine, string, Task<(byte[] pcm, int sampleRate)>> synthesize)
    {
      await _synthGate.WaitAsync().ConfigureAwait(false);

      try
      {
        var engine = _tts;
        var spokenVoice = playerId is not null ? engine.GetVoice(playerId) : voice;
        var textHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        var key = _cache is null ? null : $"{AudioCacheKey}tts:{engine.Name}:{spokenVoice}:{textHash}";

        if (key is not null && _cache.TryGetValue(key, out object entry) &&
            entry is CachedAudio { Data.Length: > 0 } cached)
        {
          return (cached.Data, cached.WaveFormat?.SampleRate ?? 0);
        }

        var (pcm, sampleRate) = await synthesize(engine, spokenVoice).ConfigureAwait(false);

        if (key is not null && pcm is { Length: > 0 })
        {
          var options = new MemoryCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromMinutes(60));
          options.SetSize(pcm.Length);
          _cache.Set(key, new CachedAudio
          {
            Data = pcm,
            WaveFormat = new WaveFormat(sampleRate, 16, 1)
          }, options);
        }

        return (pcm, sampleRate);
      }
      finally
      {
        _synthGate.Release();
      }
    }

    public static (List<string> idList, List<string> nameList) GetDeviceList()
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

    private async void DoUpdateDeviceList(object state)
    {
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
      _deviceUpdateTimer?.Change(1500, Timeout.Infinite);
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
        _synthGate?.Dispose();
        _deviceEnumerator?.UnregisterEndpointNotificationCallback(_notificationClient);
        _deviceEnumerator?.Dispose();
        _tts?.Dispose();
      }

      _disposed = true;
    }

    private sealed class CachedAudio
    {
      internal byte[] Data { get; init; }
      internal WaveFormat WaveFormat { get; init; }
      internal double Seconds { get; init; }
    }

    // queue state only; whatever a speech engine holds for a player lives with the engine
    private sealed class PlayerAudio
    {
      internal List<PlaybackEvent> Events { get; set; } = [];
      internal PlaybackEvent CurrentEvent { get; set; }
      internal CancellationTokenSource ProcessingToken { get; set; }
      internal bool PlayerRequestStop { get; set; }
    }

    private sealed class PlaybackEvent
    {
      internal long Priority { get; init; } = -1;
      internal int Rate { get; init; }
      internal float Volume { get; init; } = -1;
      internal byte[] AudioData { get; init; }
      internal WaveFormat WaveFormat { get; init; }
      internal double Seconds { get; init; }
    }

    private sealed class AudioDeviceNotificationClient : IMMNotificationClient
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