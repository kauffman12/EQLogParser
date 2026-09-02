using KokoroSharp;
using KokoroSharp.Core;
using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
      return KokoroVoiceManager.Voices
        .Select(voice => voice.Name)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToList();
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

      var engine = new KokoroTtsEngine(root);
      try
      {
        if (!engine.VerifyModel())
        {
          engine.Dispose();
          return null;
        }

        // Creating the session over a 156MB graph takes seconds; callers keep it off the UI thread.
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
            Log.Warn($"Kokoro voices are already loaded from {_voicesLoadedFrom}; " +
              $"restart EQLogParser to use {_voicesPath}");
          }

          return true;
        }

        // Nothing to read from, or this exact directory has already been tried and failed. Retrying on every dropdown
        // open would only fill the log; a different directory - a reinstalled pack - is tried once on its own merits.
        if (_voicesPath is not { Length: > 0 } path ||
            _voicesLoadFailed && string.Equals(_voicesLoadAttemptedFrom, path, StringComparison.OrdinalIgnoreCase))
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
