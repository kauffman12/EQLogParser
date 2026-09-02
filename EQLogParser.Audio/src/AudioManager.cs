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
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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

    /*
     * Warm-up takes the synthesis gate only when nothing else holds it, retrying briefly in case a preview is playing,
     * and gives up rather than getting in line ahead of real speech. See WarmUpVoice.
     */
    private const int WarmUpAttempts = 6;
    private const int WarmUpRetryDelayMs = 400;

    /*
     * Ceiling on voices waiting to be warmed. A zone-in registers dozens of players at once and most of them will not
     * be spoken with this fight; when the list is full the oldest request is dropped, because a voice someone changed
     * two minutes ago matters less than the one just picked.
     */
    private const int MaxWarmUpQueue = 8;
    private readonly ConcurrentDictionary<string, PlayerAudio> _playerAudios = [];
    private readonly ConcurrentDictionary<string, bool> _isRenderDevice = new();
    /*
     * Engines hold process wide native state (Piper's voice table, Kokoro's inference session), so only one synthesis
     * runs at a time and no engine is created or released while another thread speaks through one. A phrase already in
     * the cache takes neither this gate nor an engine at all, which is what keeps a burst of familiar callouts from
     * queueing behind a neural synthesis of something new. See docs/DesignNotes.md -> Speech synthesis and TTS engines.
     */
    private readonly SemaphoreSlim _synthGate = new(1, 1);

    /*
     * Serializes the short engine calls - binding a voice, dropping one, asking what a player speaks - against an
     * engine swap, so nothing can reach an engine whose native state has just been released. Deliberately not held
     * across synthesis: that is _synthGate's job, and a callout must never be delayed by someone using a dropdown.
     */
    private readonly object _engineLock = new();
    private readonly AudioDeviceNotificationClient _notificationClient = new();
    private readonly object _deviceLock = new();

    /*
     * Swapped in place when the user picks another engine mid session. volatile so a thread that reads it without the
     * lock sees whoever is actually speaking; anything about to call into it holds _engineLock first, except synthesis
     * and warm-up, which run under _synthGate for exactly as long as a switch holds it.
     */
    private volatile ITtsEngine _tts;

    /*
     * What the host asked each player to speak with. The engine decides whether it can honor that name, and this is
     * what gets handed to the next engine when the user switches engines without restarting.
     */
    private readonly ConcurrentDictionary<string, string> _requestedVoices = [];

    /*
     * Warm-up bookkeeping, all of it under _warmLock: the voices worth preparing, the set that keeps one voice in the
     * list once, the last voice confirmed prepared, and whether the single worker is running. A voice is recorded as
     * warm only when it actually got through, so a warm-up that gave up does not suppress the next attempt.
     */
    private readonly object _warmLock = new();
    private readonly Queue<(string Voice, string Key)> _warmQueue = [];
    private readonly HashSet<string> _warmQueued = [];
    private string _warmedVoice;
    private bool _warmWorkerRunning;

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

      // The setting can have been edited by hand, and every comparison from here on is against the names this build
      // uses. Normalizing once at the boundary is what keeps "piper" meaning Piper.
      _preferredEngine = TtsEngineFactory.Normalize(preferredEngine);
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

    public bool IsEngineAvailable(string engine) => EngineIsAvailable(TtsEngineFactory.Normalize(engine));

    public bool IsEngineDownloaded(string engine) => TtsPackManager.IsPackOnDisk(TtsEngineFactory.Normalize(engine));

    public long GetEngineDownloadBytes(string engine) =>
      TtsPackManager.GetDownloadBytes(TtsEngineFactory.Normalize(engine));

    public Task<bool> InstallEngineAsync(string engine, IProgress<float> progress,
      CancellationToken cancellationToken = default) =>
      TtsPackManager.InstallAsync(TtsEngineFactory.Normalize(engine), progress, cancellationToken);

    /*
     * Reclaims the space a pack uses. The engine currently speaking keeps its native libraries mapped for the life of
     * the process on Windows, so removing that one would leave a directory half deleted and an engine that still
     * claims to work; the caller picks another engine first.
     */
    public bool RemoveEngineFiles(string engine)
    {
      var target = TtsEngineFactory.Normalize(engine);

      if (string.IsNullOrEmpty(target) || string.Equals(target, GetActiveEngine(), StringComparison.OrdinalIgnoreCase))
      {
        return false;
      }

      return TtsPackManager.Remove(target);
    }

    /// <summary>Every engine the app knows how to drive, whether or not its runtime pack is installed. The picker
    /// lists these so a download can be offered from the same place the engine is chosen.</summary>
    public static List<string> GetAllEngines() => [PiperEngine, KokoroEngine, WindowsEngine];

    private static bool EngineIsAvailable(string engine) => engine switch
    {
      // Not assumed: the Windows voices are available until something proves otherwise, which they do on Wine and on
      // Windows images with the speech runtime removed. See WindowsTtsEngine.IsAvailable.
      WindowsEngine => WindowsTtsEngine.IsAvailable(),
      _ => TtsPackManager.ResolveRoot(engine) is not null
    };

    /// <summary>The engine actually in use for this running session.</summary>
    public string GetActiveEngine() => _tts.Name;

    /// <summary>Switches the speech engine without a restart; only engines whose runtime pack is installed can be
    /// selected. Returns false when the switch did not happen, leaving the current engine speaking.</summary>
    public async Task<bool> SwitchEngineAsync(string engine)
    {
      if (_disposed) return false;

      var wanted = TtsEngineFactory.Normalize(engine);

      if (string.IsNullOrEmpty(wanted))
      {
        return false;
      }

      // Already speaking it, or nothing on disk to speak it with. Reported as "no switch" either way; the picker tells
      // those two apart from what it can see on disk.
      if (string.Equals(wanted, GetActiveEngine(), StringComparison.OrdinalIgnoreCase) || !EngineIsAvailable(wanted))
      {
        return string.Equals(wanted, GetActiveEngine(), StringComparison.OrdinalIgnoreCase);
      }

      var (switched, voices) = await SwitchUnderGateAsync(wanted).ConfigureAwait(false);

      if (!switched)
      {
        return false;
      }

      /*
       * Warm the new engine now that the gate is back. This cannot happen inside the switch: warm-up enters that gate
       * only when it is free, and the switch holds it for as long as it takes to build a model, so asking from in
       * there means every warm-up quietly gives up and the first callout after a switch pays for everything.
       *
       * Distinct voices only: a machine with six characters configured speaks with up to six of them, and each of
       * these is one short word.
       */
      foreach (var voice in voices)
      {
        WarmUpVoice(voice);
      }

      return true;
    }

    /*
     * The part of a switch that needs the synthesis gate: build the new engine, hand it the players, swap it in, and
     * let go of the one it replaced. Returns whether the switch happened and which voices are worth warming once the
     * gate is back.
     */
    private async Task<(bool Switched, string[] Voices)> SwitchUnderGateAsync(string wanted)
    {
      var previous = _tts;

      // held for the whole switch: creation and disposal both touch process wide native state, and no synthesis may
      // run against an engine that is being created or torn down
      await _synthGate.WaitAsync().ConfigureAwait(false);

      var next = (ITtsEngine) null;
      var retired = (ITtsEngine) null;
      var voices = Array.Empty<string>();

      try
      {
        // Creation is where the cost is: Kokoro builds an inference session over a 156 MB graph and Windows proves
        // every voice by synthesizing a word into a stream. Callouts that arrive meanwhile wait on this gate, which
        // is still kinder than speaking half of one engine and half of the other.
        next = await Task.Run(() => TtsEngineFactory.CreateNamed(wanted)).ConfigureAwait(false);

        if (next is null)
        {
          Log.Debug($"Unable to switch the TTS engine to {wanted}; staying on {previous.Name}.");
          return (false, voices);
        }

        try
        {
          // Nothing else can reach this engine yet - _tts still answers for the session - so these need no lock.
          await next.LoadVoicesAsync().ConfigureAwait(false);

          // An engine that has no voice at all cannot speak, whatever its name is. Switching to one would report
          // success and then deliver silence; the current engine keeps the microphone.
          if (next.GetVoices().Count == 0)
          {
            Log.Debug($"{wanted} has no usable voices; staying on {previous.Name}.");
            return (false, voices);
          }

          foreach (var requested in _requestedVoices)
          {
            next.SetVoice(requested.Key, requested.Value);
          }
        }
        catch (Exception ex)
        {
          // A half prepared engine is dropped where every unusable one is dropped, below.
          Log.Error($"Unable to prepare the {wanted} TTS engine", ex);
          return (false, voices);
        }

        /*
         * Swapping and retiring belong together under _engineLock: a voice being bound to a player either finishes
         * against the old engine or starts against the new one, never across both. The new engine keeps whatever the
         * players asked for that it recognizes; a name it does not have was already dropped by SetVoice.
         */
        lock (_engineLock)
        {
          retired = previous;
          _tts = next;
          ResetWarmUpState();
        }

        var active = next;
        next = null; // owned by _tts from here on, so there is nothing left to clean up below

        voices = [.. _requestedVoices.Values.Distinct(StringComparer.OrdinalIgnoreCase)];

        // A milestone worth having in a bug report; Windows is the boring default, so stay quiet there.
        if (active.Name is KokoroEngine or PiperEngine)
        {
          Log.Info($"Using {active.Name.ToLowerInvariant()}-tts");
        }

        return (true, voices);
      }
      finally
      {
        /*
         * Both disposals run under _engineLock while this still holds the gate, so nothing can be speaking with either
         * engine and a voice call that was already in flight against the retired one has finished. next is set only
         * when an engine was built and never became active, retired only when one was replaced: releasing the wrong
         * one here would take the native voices away from the engine still speaking.
         */
        lock (_engineLock)
        {
          next?.Dispose();
          retired?.Dispose();
        }

        ReleaseSynthGate();
      }
    }

    private static void ShowAudioError()
    {
      _showError?.Invoke("Unable to Play sound. No audio device?");
    }

    /* Releasing after the manager has been disposed is normal during shutdown; there is nobody left to wait. */
    private void ReleaseSynthGate()
    {
      try
      {
        _synthGate.Release();
      }
      catch (ObjectDisposedException)
      {
        // ignore: the app is closing
      }
    }

    public int GetVolume() => (int)(_appVolume * 100.0f);
    public void SetVolume(int volume) => _appVolume = volume / 100.0f;

    /*
     * Proves the engine that started the session. Takes the synthesis gate because proving voices is engine lifecycle
     * work, the same as the creation and release a switch performs; nothing may speak with an engine while it runs,
     * and this must not run against an engine somebody else has already retired.
     */
    public async Task LoadValidVoicesAsync()
    {
      var engine = _tts;

      await _synthGate.WaitAsync().ConfigureAwait(false);

      try
      {
        await engine.LoadVoicesAsync().ConfigureAwait(false);
      }
      finally
      {
        ReleaseSynthGate();
      }

      // By now the engine that started the session has been asked to prove its voices, which is the earliest point
      // where "there is no speech on this machine" is knowable. Worth a log line: from the user's side it is only
      // silence, and that makes for a bug report with nothing in it.
      if (!EngineIsAvailable(engine.Name))
      {
        Log.Warn($"{engine.Name} TTS has no usable voices on this machine. Callouts stay silent until an engine is " +
          "enabled on the TTS Engine screen.");
      }
    }

    public List<string> GetVoiceList()
    {
      var engine = _tts;

      lock (_engineLock)
      {
        return engine.GetVoices();
      }
    }

    public string GetDefaultVoice()
    {
      var engine = _tts;

      lock (_engineLock)
      {
        return engine.GetDefaultVoice();
      }
    }

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
        BindVoice(id, voice);
        WarmUpVoice(voice);
      }
    }

    public void Add(string id, string voice)
    {
      _requestedVoices[id] = voice;
      _playerAudios.TryAdd(id, new PlayerAudio());
      BindVoice(id, voice);
      WarmUpVoice(voice);
    }

    /*
     * Binds a player to a voice on whatever engine speaks now. Under _engineLock because a voice is engine state: for
     * Piper this builds an ONNX session in a native table that the next engine swap releases wholesale, so binding may
     * not straddle a swap. It is not cheap (a few hundred milliseconds for a first voice) and callers are the UI, which
     * is why it is never held across synthesis.
     */
    private void BindVoice(string id, string voice)
    {
      var engine = _tts;

      lock (_engineLock)
      {
        engine.SetVoice(id, voice);
      }
    }

    /*
     * Get a voice ready to speak so the first thing said with it is not slow: building a Piper voice is an ONNX
     * session plus espeak-ng dictionaries, and any engine's first synthesis also warms the runtime behind it. Called
     * when a player is registered, when its voice changes - which is what the voice dropdown does - and after an
     * engine switch. Nothing plays, and the work happens in the background.
     *
     * This is a nicety, not a requirement: every path here still synthesizes correctly if warm-up never ran, only the
     * first utterance pays for it.
     */
    private void WarmUpVoice(string voice)
    {
      if (_disposed)
      {
        return;
      }

      var engine = _tts;
      var target = string.IsNullOrEmpty(voice) ? SpokenVoice(engine, null) : voice;

      if (string.IsNullOrEmpty(target))
      {
        return;
      }

      // A name belongs to an engine: the same name on another one is a different voice and has to be prepared again.
      var key = $"{engine.Name}:{target}";

      lock (_warmLock)
      {
        // Already prepared, or already on the list to be. One short synthesis per voice per engine is the whole point.
        if (string.Equals(_warmedVoice, key, StringComparison.OrdinalIgnoreCase) || !_warmQueued.Add(key))
        {
          return;
        }

        while (_warmQueue.Count >= MaxWarmUpQueue && _warmQueue.TryDequeue(out var dropped))
        {
          _warmQueued.Remove(dropped.Key);
        }

        _warmQueue.Enqueue((target, key));

        if (_warmWorkerRunning)
        {
          return;
        }

        _warmWorkerRunning = true;
      }

      _ = Task.Run(WarmUpWorkerAsync);
    }

    /*
     * What this engine would answer for a player, or its default voice when there is no player. That is engine state,
     * so it is read under _engineLock like every other call into an engine; it is also cheap enough for a UI thread.
     */
    private string SpokenVoice(ITtsEngine engine, string playerId)
    {
      lock (_engineLock)
      {
        return playerId is not null ? engine.GetVoice(playerId) : engine.GetDefaultVoice();
      }
    }

    /*
     * One worker for the whole list, because warming is CPU work nobody is waiting on: registering thirty characters at
     * a zone-in must not start thirty warm-ups at once, and none of them may hold up anything audible. Each voice
     * slips into the synthesis gate only when that gate is free, which is what keeps it from building or releasing a
     * native voice while another thread speaks through one - choosing a voice speaks a preview straight away, and that
     * preview would otherwise wait behind this. Speech that stays busy makes a warm-up retry, then give up; the voice
     * stays cold until something asks again, which costs nothing but speed.
     */
    private async Task WarmUpWorkerAsync()
    {
      while (!_disposed)
      {
        (string Voice, string Key) item;

        lock (_warmLock)
        {
          if (!_warmQueue.TryDequeue(out item))
          {
            _warmWorkerRunning = false;
            return;
          }

          _warmQueued.Remove(item.Key);
        }

        var prepared = false;

        for (var attempt = 0; attempt < WarmUpAttempts && !_disposed; attempt++)
        {
          bool taken;

          try
          {
            taken = await _synthGate.WaitAsync(0).ConfigureAwait(false);
          }
          catch (ObjectDisposedException)
          {
            // The app is closing and nothing needs warming.
            return;
          }

          if (!taken)
          {
            await Task.Delay(WarmUpRetryDelayMs).ConfigureAwait(false);
            continue;
          }

          try
          {
            // The engine may have changed since this was queued. Warm what is speaking now rather than what was
            // selected then; an engine handed a voice it does not have falls back to its own default.
            await _tts.WarmUpVoiceAsync(item.Voice).ConfigureAwait(false);
            prepared = true;
          }
          catch (Exception ex)
          {
            // A voice that will not warm is not worth reporting: the trigger that follows synthesizes normally, just
            // without the head start.
            Log.Debug($"Unable to warm up the {item.Voice} voice", ex);
          }
          finally
          {
            ReleaseSynthGate();
          }

          break;
        }

        /*
         * Only a voice that actually got through counts as prepared. Recording one that gave up would suppress the
         * next attempt at it, and that is how a busy moment leaves a player cold for the rest of the evening.
         */
        if (prepared)
        {
          lock (_warmLock)
          {
            _warmedVoice = item.Key;
          }
        }
      }
    }

    /* Nothing the previous engine had prepared counts for the next one; it warms on its own terms. */
    private void ResetWarmUpState()
    {
      lock (_warmLock)
      {
        _warmQueue.Clear();
        _warmQueued.Clear();
        _warmedVoice = null;
      }
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

          // whatever the engine held for this player is the engine's to release, outside this lock but not around a
          // swap: see BindVoice for why engine state is only ever touched under _engineLock
          var engine = _tts;

          lock (_engineLock)
          {
            engine.RemoveVoice(id);
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

    public void SpeakFileAsync(string id, string filePath, long priority, int playerVolume, int adjustedVolume = 4) =>
      _ = SpeakFileCoreAsync(id, filePath, priority, playerVolume, adjustedVolume);

    private async Task SpeakFileCoreAsync(string id, string filePath, long priority, int playerVolume,
      int adjustedVolume)
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
     * A phrase that is already cached needs no gate and no engine at all: cached bytes belong to an engine, a voice
     * and a piece of text rather than to whichever engine happens to be speaking, so the first lookup below settles
     * itself against the cache alone. That is what keeps twenty familiar callouts from queueing behind one new
     * sentence being synthesized. Everything that can miss runs under the gate, where the engine cannot be swapped out
     * underneath it - SwitchEngineAsync takes an engine out under this same gate - so voice, key and result are all
     * resolved against one engine.
     */
    private async Task<(byte[] pcm, int sampleRate)> SynthesizeCachedAsync(string playerId, string voice, string text,
      Func<ITtsEngine, string, Task<(byte[] pcm, int sampleRate)>> synthesize)
    {
      var probedEngine = _tts;
      var probedVoice = _cache is null ? null : RequestedVoice(probedEngine, playerId, voice);
      var probe = _cache is null ? null : BuildCacheKey(probedEngine.Name, probedVoice, text);

      if (probe is not null && TryGetCached(probe, out var cachedPcm, out var cachedRate))
      {
        return (cachedPcm, cachedRate);
      }

      await _synthGate.WaitAsync().ConfigureAwait(false);

      try
      {
        // Resolved again: this waited, and the engine may have changed while it did. When nothing it depends on moved,
        // the key built above is the same one, and a second digest of the same phrase buys nothing.
        var engine = _tts;
        var spokenVoice = RequestedVoice(engine, playerId, voice);
        var key = probe is not null && ReferenceEquals(engine, probedEngine) && spokenVoice == probedVoice
          ? probe
          : _cache is null ? null : BuildCacheKey(engine.Name, spokenVoice, text);

        if (key is not null && TryGetCached(key, out cachedPcm, out cachedRate))
        {
          return (cachedPcm, cachedRate);
        }

        var (pcm, sampleRate) = await synthesize(engine, spokenVoice).ConfigureAwait(false);

        if (pcm is { Length: > 0 } && sampleRate <= 0)
        {
          // Nothing downstream can play this and it must not be remembered as audio: a WaveFormat of zero hertz turns
          // into an exception at the audio device, far from the engine that produced it.
          Log.Debug($"{engine.Name} returned speech with no sample rate; dropped.");
          return (null, 0);
        }

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
        ReleaseSynthGate();
      }
    }

    /*
     * The voice this request speaks with: whatever the player is bound to for a player, and the name that was asked
     * for - which no player owns - for a preview or a WAV export.
     */
    private string RequestedVoice(ITtsEngine engine, string playerId, string voice) =>
      playerId is not null ? SpokenVoice(engine, playerId) : voice;

    /*
     * PCM belongs to one engine speaking one voice, so nothing cached under one name is ever played by another.
     *
     * The digest runs over the string's own UTF-16 bytes rather than a UTF-8 copy: this key never leaves the process,
     * so all it has to be is stable for as long as the cache lives, and skipping the encoding pass keeps an allocation
     * per callout out of a zone-in with twenty players talking at once.
     */
    private static string BuildCacheKey(string engine, string voice, string text)
    {
      var phrase = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(text.AsSpan())));
      return $"{AudioCacheKey}tts:{engine}:{voice}:{phrase}";
    }

    /* Only audio that could actually be played counts as a hit. */
    private static bool TryGetCached(string key, out byte[] pcm, out int sampleRate)
    {
      pcm = null;
      sampleRate = 0;

      if (_cache.TryGetValue(key, out object entry) &&
          entry is CachedAudio { Data.Length: > 0 } cached && cached.WaveFormat is { SampleRate: > 0 } format)
      {
        pcm = cached.Data;
        sampleRate = format.SampleRate;
        return true;
      }

      return false;
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