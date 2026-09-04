using KokoroSharp;
using KokoroSharp.Core;
using log4net;
using Microsoft.ML.OnnxRuntime;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace EQLogParser.Audio
{
  /*
   * Kokoro neural voices through KokoroSharp (https://github.com/Lyrcaxis/KokoroSharp). Nothing this engine needs at
   * runtime is carried by the installer: the graph, the voice embeddings, the phonemizer and onnxruntime arrive as a
   * GitHub hosted runtime pack in local app data when the user enables the engine, verified against pinned checksums
   * before onnxruntime is handed anything. See docs/TtsPacks.md and TtsPackManager for where files come from and how
   * the support assemblies and native libraries get found there. Only the WAV synthesis path is used; playback stays
   * with the audio device code so exports, rate changes and per player queues behave like every other sound.
   */
  internal sealed class KokoroTtsEngine : ITtsEngine
  {
    internal const string EngineName = "Kokoro";
    internal const int SampleRate = 24000;

    // Half precision: 156MB instead of the 310MB fp32 graph, and the loss is not audible on trigger callouts.
    // The pack carries it at model\kokoro-fp16.onnx; TtsPackManager checks the same hash per file manifest.
    internal const string ModelFileName = "kokoro-fp16.onnx";

    /*
     * SHA-256 GitHub reports for that release asset. The graph is executed by onnxruntime with the app's own
     * privileges, so a truncated or substituted file must never reach LoadModel. Changing ModelFileName means
     * updating this in the same commit. See docs/DesignNotes.md -> Kokoro model integrity.
     */
    private const string ModelSha256 = "027a25b14aef7d3ae57fd09301ebefbec868e79d55213d07e4f3af442f5ba352";
    private const string PreferredDefaultVoice = "af_heart";

    /*
     * The locale letters in Kokoro's voice ids: [locale][gender]_name, so af_heart is an American woman and bf_emma a
     * British one. Every locale upstream publishes is listed even though a pack usually carries a few, because which
     * ones ship is a build time choice (KokoroVoicePrefixes in Directory.Build.targets) and the model speaks all of
     * them either way.
     */
    private static readonly Dictionary<char, string> _voiceLocales = new()
    {
      ['a'] = "US",
      ['b'] = "GB",
      ['e'] = "ES",
      ['f'] = "FR",
      ['h'] = "HI",
      ['i'] = "IT",
      ['j'] = "JP",
      ['p'] = "PT",
      ['z'] = "CN"
    };

    // Word run through a session that has not spoken yet. Nobody hears it; see WarmUpVoiceAsync.
    private const string WarmUpText = "test";

    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    /*
     * Voice directory state, all of it read and written under _voicesLock. KokoroSharp keeps the embeddings in process
     * wide state, so they can only be read once: an engine pointing at a different directory cannot be honored without
     * a restart, and that is reported rather than quietly answered with the first set of voices. A failed load is
     * remembered per directory so reopening the dropdown does not retry - and re-log - a broken pack forever, while a
     * pack that gets reinstalled into a different directory still gets its chance. The lock matters because the picker
     * asks for the voice list from the UI thread while a switch builds an engine on another one.
     */
    private static string _voicesPath;
    private static string _voicesLoadedFrom;
    private static string _voicesLoadAttemptedFrom;
    private static bool _voicesLoadFailed;

    // Whether the "a different pack directory needs a restart" line has been said yet. The branch that raises it runs
    // on every voice lookup once true, so the repeats go to Debug rather than filling the log with one sentence.
    private static bool _crossDirectoryReported;

    // The same courtesy for the ONNX Runtime version note: an engine rebuilt by hand while a mismatch exists would
    // otherwise repeat it every time somebody touches the picker. Interlocked because engine creation is not under
    // _voicesLock and two switches can be in flight.
    private static int _runtimeDriftReported;
    private static readonly object _voicesLock = new();

    private readonly ConcurrentDictionary<string, string> _playerVoices = [];
    private readonly string _modelPath;

    // Written once a file has been checked, so the 156MB hash runs one time per model instead of at every start.
    private readonly string _markerPath;
    private KokoroWavSynthesizer _synth;

    public string Name => EngineName;

    public Task LoadVoicesAsync() => Task.CompletedTask;

    public List<string> GetVoices()
    {
      if (!EnsureVoicesLoaded())
      {
        return [];
      }
      // Alphabetical by the label the picker prints rather than by the id: ordered by id, af_* precedes am_* so every
      // American woman comes before Adam, and the Chinese and Japanese voices land between Hindi and Italian.
      return TtsVoiceOrder.ByLabel(KokoroVoiceManager.Voices.Select(voice => voice.Name), DisplayNameFor);
    }

    public string GetDefaultVoice()
    {
      if (!EnsureVoicesLoaded())
      {
        return string.Empty;
      }

      return KokoroVoiceManager.Voices.FirstOrDefault(voice =>
        string.Equals(voice.Name, PreferredDefaultVoice, StringComparison.OrdinalIgnoreCase))?.Name
        ?? KokoroVoiceManager.Voices.FirstOrDefault()?.Name;
    }

    public string GetVoiceDisplayName(string voice) => DisplayNameFor(voice);

    public string GetVoiceSpokenName(string voice) => PlainNameFor(voice);

    public string GetVoice(string playerId)
    {
      if (playerId is not null && _playerVoices.TryGetValue(playerId, out var voice) && !string.IsNullOrEmpty(voice))
      {
        return voice;
      }

      return GetDefaultVoice();
    }

    public void SetVoice(string playerId, string voice)
    {
      if (string.IsNullOrEmpty(playerId))
      {
        return;
      }

      // Bind only names this engine actually has. A name left over from another engine, or from a voice that was
      // removed, would otherwise stick to the player forever and quietly be spoken as the default voice.
      if (!string.IsNullOrEmpty(voice) && FindVoice(voice) is { } found &&
          string.Equals(found.Name, voice, StringComparison.OrdinalIgnoreCase))
      {
        _playerVoices[playerId] = voice;
      }
      else
      {
        _playerVoices.TryRemove(playerId, out _);
      }
    }

    public void RemoveVoice(string playerId)
    {
      if (playerId is not null)
      {
        _playerVoices.TryRemove(playerId, out _);
      }
    }

    public Task<(byte[] pcm, int sampleRate)> SynthesizeForPlayerAsync(string playerId, string text) =>
      SynthesizeVoiceAsync(GetVoice(playerId), text);

    public async Task<(byte[] pcm, int sampleRate)> SynthesizeVoiceAsync(string voice, string text)
    {
      if (_synth is null || FindVoice(voice) is not { } found)
      {
        return (null, 0);
      }

      // Inference is synchronous CPU work; keep it off whatever thread asked for the audio.
      var pcm = await Task.Run(() => _synth.Synthesize(text, found)).ConfigureAwait(false);
      return pcm is { Length: > 0 } ? (pcm, SampleRate) : (null, 0);
    }

    public async Task WarmUpVoiceAsync(string voice)
    {
      if (_synth is null || FindVoice(voice) is not { } found)
      {
        return;
      }

      // Unlike Piper there is nothing to build per voice here - the embeddings all arrive with the one session made at
      // engine creation, so there is no previous voice to unload either. What a first callout pays for is warming: the
      // phonemizer building its tables and ONNX Runtime sizing its arenas on the first run through the graph. One
      // throwaway synthesis covers that, and it is worth doing before a trigger has to wait on it.
      _ = await Task.Run(() => _synth.Synthesize(WarmUpText, found)).ConfigureAwait(false);
    }

    public void Dispose()
    {
      try
      {
        _synth?.Dispose();
      }
      catch (Exception ex)
      {
        Log.Debug("Unable to dispose the kokoro-tts session", ex);
      }
      finally
      {
        _synth = null;
        _playerVoices.Clear();
      }
    }

    /* Null when no runtime pack is installed, the model fails verification, or will not load. */
    internal static KokoroTtsEngine TryCreate()
    {
      if (TtsPackManager.ResolveRoot(EngineName) is not { } root)
      {
        return null;
      }

      // The support assemblies (MisakiSharp, NumSharp, OpenTK, System.Numerics.Tensors) and onnxruntime are not
      // installed with the app, so the loaders have to know about the pack before KokoroSharp touches any of them.
      TtsPackManager.EnsureResolversRegistered();

      /*
       * Two more seams before the ONNX wrapper's first P/Invoke: map EQLP's own onnxruntime.dll so this process owns
       * that module name, and pin the wrapper's imports to the same folder. Without the first, a copy another program
       * left in System32 answers the import -- it is on the operating system's search path and our resolvers are only
       * asked after that search fails. See TtsPackManager.PreferMatchingOnnxRuntime.
       */
      TtsPackManager.PreferMatchingOnnxRuntime();
      TtsPackManager.EnsureOnnxRuntimeImportResolver(typeof(InferenceSession).Assembly);

      /*
       * Somebody else's runtime is not a runtime. A 1.7 answers this graph with an error that reads exactly like a
       * broken download, and the only honest answer is to say which module is mapped and leave Kokoro out of the
       * session rather than make the user re-download 156MB they already have.
       */
      if (TtsPackManager.IsForeignOnnxRuntimeResident())
      {
        Log.Error(@"Kokoro needs EQLogParser's own onnxruntime.dll, but this process has " +
          $"{TtsPackManager.DescribeLoadedOnnxRuntime()} mapped. Another program installed that ONNX Runtime and " +
          "Windows keeps one module per name, so it answers for the whole session. Nothing in the Kokoro pack is at " +
          "fault: find what installed the other copy and restart EQLogParser once it is gone, rather than deleting " +
          "anything from Windows.");
        return null;
      }

      WarnOnRuntimeDrift();

      var engine = new KokoroTtsEngine(root);
      try
      {
        if (!engine.VerifyModel())
        {
          engine.Dispose();
          return null;
        }

        // Creating the session over a 156MB graph takes seconds. Callers keep it off the UI thread: the startup build
        // runs on the thread pool in AudioManager's constructor, and a mid-session switch already does.
        engine._synth = KokoroWavSynthesizer.LoadModel(engine._modelPath);

        // A pack whose embeddings cannot be read is not an engine: every voice lookup would answer with nothing and
        // every callout would stay silent while the picker still listed a working engine.
        if (!EnsureVoicesLoaded())
        {
          engine.Dispose();
          return null;
        }

        return engine;
      }
      catch (Exception ex)
      {
        // The runtime that answered is part of the error: a model can be refused by an older onnxruntime than the one
        // this engine ships, and without this the log points at the download instead of at the loaded module.
        Log.Error($"Error initializing kokoro-tts (onnxruntime in use: " +
          $"{TtsPackManager.DescribeLoadedOnnxRuntime()})", ex);
        engine.Dispose();
        return null;
      }
    }

    /*
     * af_nicole reads "Nicole (US)" and bm_george "George (GB)", which is what somebody picking a voice wants to know:
     * the name they will hear, and whose English it is. Anything that is not shaped like one of this engine's ids - a
     * voice kept from another engine, or an embedding named by hand - comes back exactly as stored rather than being
     * dressed up into something no longer matches the config.
     */
    internal static string DisplayNameFor(string voice) =>
      ParseVoiceName(voice) is { } parsed ? $"{parsed.Name} ({parsed.Locale})" : voice;

    /*
     * The name a preview speaks: "Nicole", with neither the locale tag the picker adds nor the letters that lead a
     * Kokoro id. Read aloud, "af_nicole" is a spelling lesson rather than a voice name, which is what a person hears
     * when they pick a voice and the app reads its identifier back at them.
     */
    internal static string PlainNameFor(string voice) => ParseVoiceName(voice)?.Name ?? voice;

    /*
     * af_nicole is an American woman called Nicole: [locale][gender]_name. Nothing comes back for anything not shaped
     * like that, so neither the label nor a preview makes up a name for an id this engine does not have.
     */
    private static (string Name, string Locale)? ParseVoiceName(string voice)
    {
      if (voice is not { Length: > 3 } || voice[1] is not ('f' or 'm') || voice[2] != '_')
      {
        return null;
      }

      if (!_voiceLocales.TryGetValue(char.ToLowerInvariant(voice[0]), out var locale))
      {
        return null;
      }

      return (char.ToUpperInvariant(voice[3]) + voice[4..].Replace('_', ' '), locale);
    }

    /*
     * The native module and the managed wrapper are published together and are meant to be the same version
     * (docs/TtsPacks.md -> publishing rules), so a drift means one of the two moved alone: a Kokoro pack that predates
     * this app build, or a runtime from elsewhere holding the name under an older install. Warn rather than refuse --
     * major.minor is not the whole contract, and an engine that speaks beats a tidy log.
     */
    private static void WarnOnRuntimeDrift()
    {
      // Nothing mapped yet is normal: it means neither engine has loaded ONNX Runtime and the wrapper's own import
      // resolver will bring ours in when the session is built. There is nothing to compare against until then.
      if (TtsPackManager.LoadedOnnxRuntimePath() is not { Length: > 0 } path)
      {
        return;
      }

      try
      {
        var native = FileVersionInfo.GetVersionInfo(path).FileVersion;
        var wrapper = typeof(InferenceSession).Assembly.GetName().Version?.ToString();

        if (MajorMinor(native) is { } runtime && MajorMinor(wrapper) is { } managed &&
            !string.Equals(runtime, managed, StringComparison.OrdinalIgnoreCase))
        {
          var message = $"ONNX Runtime version mismatch: onnxruntime.dll in use is {native}, while " +
            $"Microsoft.ML.OnnxRuntime installed with EQLogParser is {wrapper}. Reinstall Kokoro from the TTS Engine " +
            "screen so the pack's runtime matches this build.";

          if (Interlocked.Exchange(ref _runtimeDriftReported, 1) == 1)
          {
            Log.Debug(message);
          }
          else
          {
            Log.Warn(message);
          }
        }
      }
      catch (Exception ex)
      {
        // A version nobody could read is not a reason to skip the session about to be built.
        Log.Debug("Unable to compare the onnxruntime versions", ex);
      }
    }

    /* The first two dotted components, "1.22" out of "1.22.0.0"; null when the string is not shaped like a version. */
    private static string MajorMinor(string version) =>
      version is { Length: > 0 } && version.Split('.') is [var major, var minor, ..]
        ? $"{major}.{minor}"
        : null;

    /*
     * Confirms the model on disk is the graph we expect. Cheap once the marker matches, so it costs nothing on the
     * normal startup path; a hand-placed model pays for it once. The pack download already checked both the archive
     * and this file against its manifest, so a mismatch here means the bytes changed after installation.
     */
    private bool VerifyModel()
    {
      try
      {
        if (File.Exists(_markerPath) &&
            string.Equals(File.ReadAllText(_markerPath).Trim(), ModelSha256, StringComparison.OrdinalIgnoreCase))
        {
          return true;
        }

        if (!string.Equals(TtsPackManager.ComputeSha256(_modelPath), ModelSha256, StringComparison.OrdinalIgnoreCase))
        {
          Log.Error($"Kokoro model checksum mismatch at {_modelPath}. Reinstall Kokoro from the TTS Engine screen.");
          return false;
        }

        TryWriteMarker();
        return true;
      }
      catch (Exception ex)
      {
        Log.Error("Unable to verify the kokoro-tts model", ex);
        return false;
      }
    }

    private void TryWriteMarker()
    {
      try
      {
        File.WriteAllText(_markerPath, ModelSha256);
      }
      catch (Exception ex)
      {
        Log.Debug("Unable to write kokoro model checksum marker", ex);
      }
    }

    private KokoroTtsEngine(string root)
    {
      _modelPath = Path.Combine(root, "model", ModelFileName);
      _markerPath = _modelPath + ".sha256";

      lock (_voicesLock)
      {
        _voicesPath = Path.Combine(root, "voices");
      }
    }

    /*
     * The .npy embeddings live in the pack and KokoroSharp keeps them in process wide state, so they are read once.
     * A second engine pointing at a different directory cannot be honored without a restart; that is logged rather
     * than silently answered with the first set. False means there is no voice list to answer from, which is the
     * difference between an engine that falls back to its default and one that has never heard of voices at all.
     */
    private static bool EnsureVoicesLoaded()
    {
      lock (_voicesLock)
      {
        if (KokoroVoiceManager.Voices.Count > 0)
        {
          if (_voicesLoadedFrom is not null && _voicesPath is not null &&
              !string.Equals(_voicesLoadedFrom, _voicesPath, StringComparison.OrdinalIgnoreCase))
          {
            var message = $"Kokoro voices are already loaded from {_voicesLoadedFrom}; " +
              $"restart EQLogParser to use {_voicesPath}";

            if (_crossDirectoryReported)
            {
              Log.Debug(message);
            }
            else
            {
              _crossDirectoryReported = true;
              Log.Warn(message);
            }
          }

          return true;
        }

        // Nothing to read from, or this exact directory has already been tried and failed. Retrying on every dropdown
        // open would only fill the log; a different directory - a reinstalled pack - is tried once on its own merits.
        if (_voicesPath is not { Length: > 0 } path ||
            (_voicesLoadFailed && string.Equals(_voicesLoadAttemptedFrom, path, StringComparison.OrdinalIgnoreCase)))
        {
          return KokoroVoiceManager.Voices.Count > 0;
        }

        try
        {
          _voicesLoadAttemptedFrom = path;
          KokoroVoiceManager.LoadVoicesFromPath(path);
          _voicesLoadedFrom = path;
          _voicesLoadFailed = false;
          return true;
        }
        catch (Exception ex)
        {
          _voicesLoadFailed = true;
          Log.Error($"Error loading kokoro-tts voices from {path}", ex);
          return false;
        }
      }
    }

    private static KokoroVoice FindVoice(string name)
    {
      if (!EnsureVoicesLoaded())
      {
        return null;
      }

      if (!string.IsNullOrEmpty(name) &&
          KokoroVoiceManager.Voices.FirstOrDefault(voice =>
            string.Equals(voice.Name, name, StringComparison.OrdinalIgnoreCase)) is { } found)
      {
        return found;
      }

      return KokoroVoiceManager.Voices.FirstOrDefault(voice =>
        string.Equals(voice.Name, PreferredDefaultVoice, StringComparison.OrdinalIgnoreCase))
        ?? KokoroVoiceManager.Voices.FirstOrDefault();
    }
  }
}
