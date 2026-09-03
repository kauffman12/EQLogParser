using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace EQLogParser.Audio
{
  /*
   * Piper voices. The native library, its espeak-ng data and the voice models come from a GitHub hosted runtime pack
   * in local app data (docs/TtsPacks.md) and from nowhere else: nothing beside the executable is read or adopted, so
   * an engine with no pack on disk stays out of the picker until somebody downloads one. TtsPackManager picks the
   * directory and answers the native imports that live inside it. The native library keeps a process wide table of
   * loaded voices keyed by an id,
   * so each player registers its voice under its own id and the preview path registers one of its own under a
   * reserved id.
   */
  internal sealed class PiperTtsEngine : ITtsEngine
  {
    internal const string EngineName = "Piper";

    /* Native library name, answered by the import resolver below. */
    internal const string PiperApiLibrary = "piperApi.dll";

    // The native voice table is keyed by id, so preview and WAV export need an id no player can have. It doubles as
    // the prepared voice slot, which holds one voice at a time: see ResolveAdHocSpeaker.
    private const string AdHocVoiceId = "testSpeaker";

    // One short word to run a freshly built session through. Its content does not matter and nobody hears it.
    private const string WarmUpText = "Ready.";

    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    // initialize() and release() act on process wide native state, so they run once per voice pack directory no
    // matter how many engine instances come and go.
    private static string _nativeInitializedRoot;
    private static readonly object _nativeLock = new();

    private readonly ConcurrentDictionary<string, PlayerVoice> _players = new();

    /*
     * Everything below is one process wide table of loaded voices in piperApi.dll, and loading a voice into it can
     * take a few hundred milliseconds. Binding a player removes its old voice first, so speaking through an id while a
     * bind is in flight means speaking through a voice that has been removed and not yet rebuilt - which the native
     * library answers with no audio rather than an error. Choosing a voice does exactly that: it rebinds the player
     * and speaks a preview of it, so the preview went silent while the model behind it was being swapped. This lock is
     * innermost: nothing under it reaches for AudioManager's engine lock or synthesis gate, so it cannot invert with
     * them.
     */
    private readonly object _tableLock = new();

    // Voice currently loaded under AdHocVoiceId, null when that slot is empty or unusable.
    private string _preparedVoice;
    private readonly PiperVoiceData _voiceData;

    // Captured for the life of this instance: installing or removing a pack while running must not move the files
    // out from under voices that are already loaded.
    private readonly string _root;

    static PiperTtsEngine()
    {
      /*
       * piperApi.dll lives in a runtime pack rather than beside the executable. SetDllDirectory would have changed
       * the search path for every later native load in the process and overwritten any other caller's choice, so
       * resolve exactly this one library name from that folder instead. Its dependencies sit beside it, which the
       * altered search path used by NativeLibrary.Load covers. See docs/DesignNotes.md -> Piper native lookup.
       */
      NativeLibrary.SetDllImportResolver(typeof(PiperTtsEngine).Assembly, ResolveDllImport);
    }

    private PiperTtsEngine(string root, PiperVoiceData voiceData)
    {
      _root = root;
      _voiceData = voiceData;
    }

    public string Name => EngineName;

    public Task LoadVoicesAsync() => Task.CompletedTask;

    public List<string> GetVoices() => _voiceData?.Voices?.Select(voice => voice.Name).ToList() ?? [];

    public string GetDefaultVoice() => _voiceData?.Voices?.FirstOrDefault()?.Name;

    public string GetVoiceDisplayName(string voice) =>
      _voiceData?.Voices?.FirstOrDefault(candidate =>
        string.Equals(candidate.Name, voice, StringComparison.OrdinalIgnoreCase)) is { } found
        ? FormatDisplayName(found.Name, found.Locale)
        : voice;

    public string GetVoice(string playerId) =>
      playerId is not null && _players.TryGetValue(playerId, out var player) && !string.IsNullOrEmpty(player.Name)
        ? player.Name
        : GetDefaultVoice();

    public void SetVoice(string playerId, string voice)
    {
      if (string.IsNullOrEmpty(playerId))
      {
        return;
      }

      // Rebinding the voice the player already speaks is a waste of exactly the time this is all about: a config write
      // touches every setting, and rebuilding an ONNX session to end up with the same voice would be worse than doing
      // nothing.
      var bound = _players.TryGetValue(playerId, out var current);

      if (bound && string.Equals(current.Name, voice, StringComparison.OrdinalIgnoreCase))
      {
        return;
      }

      lock (_tableLock)
      {
        // The old voice goes out first. Replacing a table entry is not documented as releasing the session behind it,
        // so rebinding without removing would leak a voice model per dropdown change - which is how an afternoon of
        // trying voices ends with several hundred megabytes of voices nobody selected.
        if (current is not null)
        {
          TryRemoveNativeVoice(playerId);
        }

        if (LoadNativeVoice(playerId, voice) is { } voiceInfo)
        {
          _players[playerId] = new PlayerVoice { Name = voiceInfo.Name, SampleRate = voiceInfo.Sample };
        }
        else
        {
          /*
           * The native voice above was removed, so nothing is loaded under this id any more and nothing may claim
           * otherwise. Leaving the old binding behind gives a player that reports a voice it cannot speak: silent on
           * every callout, and worth borrowing by the preview path for a voice that produces nothing.
           */
          _ = _players.TryRemove(playerId, out _);
        }
      }
    }

    public void RemoveVoice(string playerId)
    {
      if (string.IsNullOrEmpty(playerId))
      {
        return;
      }

      lock (_tableLock)
      {
        _ = _players.TryRemove(playerId, out _);
        TryRemoveNativeVoice(playerId);
      }
    }

    private static void TryRemoveNativeVoice(string id)
    {
      try
      {
        _ = PiperInterop.removeVoice(id);
      }
      catch (Exception ex)
      {
        Log.Debug("Unable to remove piper voice", ex);
      }
    }

    public async Task<(byte[] pcm, int sampleRate)> SynthesizeForPlayerAsync(string playerId, string text) =>
      await Task.Run(() =>
      {
        lock (_tableLock)
        {
          var sample = playerId is not null && _players.TryGetValue(playerId, out var player) ? player.SampleRate : 0;
          return (SynthesizeNative(playerId, text), sample);
        }
      }).ConfigureAwait(false);

    public async Task<(byte[] pcm, int sampleRate)> SynthesizeVoiceAsync(string voice, string text) =>
      await Task.Run(() => SpeakAdHoc(voice, text)).ConfigureAwait(false);

    public async Task WarmUpVoiceAsync(string voice)
    {
      // Building the session is most of the cost and SpeakAdHoc does it. The throwaway synthesis is for the rest: ONNX
      // Runtime allocates its arenas and settles on kernels the first time it runs something, so a trigger should not
      // be the first thing through a brand new session.
      _ = await Task.Run(() => SpeakAdHoc(voice, WarmUpText)).ConfigureAwait(false);
    }

    /*
     * Resolve which id speaks for this voice and speak through it without letting go of the table in between: picking
     * the next voice in the dropdown retargets that same slot, and doing that while an earlier synthesis is still
     * running takes the voice out from under it.
     */
    private (byte[] pcm, int sampleRate) SpeakAdHoc(string voice, string text)
    {
      lock (_tableLock)
      {
        var (speaker, sampleRate) = ResolveAdHocSpeaker(voice);
        return string.IsNullOrEmpty(speaker) ? (null, 0) : (SynthesizeNative(speaker, text), sampleRate);
      }
    }

    /*
     * Which native voice speaks for a preview, and at what rate. A voice that some player already speaks reuses that
     * player's session rather than building a second one over the same model - selecting a voice in the UI has loaded
     * it already, and a preview of it should cost inference and nothing else. Otherwise the single prepared slot is
     * retargeted, releasing the voice that was in it.
     *
     * Either way the session stays loaded afterwards. Previews come in bursts - pick a voice, hear it, edit the text,
     * hear it again - and the old behaviour of dropping the voice when each one finished meant paying for the model
     * again on every single one.
     *
     * Called with _tableLock held, by SpeakAdHoc only: it both reads and rewires the native table.
     */
    private (string speaker, int sampleRate) ResolveAdHocSpeaker(string voice)
    {
      if (_voiceData?.Voices is not { Count: > 0 } voices)
      {
        return (null, 0);
      }

      var wanted = voices.FirstOrDefault(v =>
        string.Equals(v.Name, voice, StringComparison.OrdinalIgnoreCase)) ?? voices[0];
      var owned = _players.FirstOrDefault(player =>
        string.Equals(player.Value.Name, wanted.Name, StringComparison.OrdinalIgnoreCase));

      if (owned.Value is not null)
      {
        // Which slot spoke matters when a preview comes back empty: 'testSpeaker' means the prepared one, anything else
        // is a trigger player's own session being borrowed for the preview.
        Log.Debug($"Piper previews '{wanted.Name}' through the {owned.Key} player");
        return (owned.Key, owned.Value.SampleRate);
      }

      if (!string.Equals(_preparedVoice, wanted.Name, StringComparison.OrdinalIgnoreCase))
      {
        TryRemoveNativeVoice(AdHocVoiceId);
        _preparedVoice = LoadNativeVoice(AdHocVoiceId, wanted.Name)?.Name;
      }

      return _preparedVoice is not null ? (_preparedVoice, wanted.Sample) : (null, 0);
    }

    public void Dispose()
    {
      _players.Clear();

      // release() below empties the whole native table, prepared voice included; this only keeps this instance from
      // believing a slot is still loaded after another instance has released it.
      _preparedVoice = null;

      lock (_nativeLock)
      {
        if (_nativeInitializedRoot is null)
        {
          return;
        }

        try
        {
          PiperInterop.release();
        }
        catch (Exception ex)
        {
          Log.Debug("Unable to release piper-tts", ex);
        }

        _nativeInitializedRoot = null;
      }
    }

    /* Null when no voice pack is installed or the native library will not initialize. */
    internal static PiperTtsEngine TryCreate()
    {
      if (TtsPackManager.ResolveRoot(EngineName) is not { } root)
      {
        return null;
      }

      try
      {
        var json = File.ReadAllText(Path.Combine(root, "voices", "voices.json"));
        if (JsonSerializer.Deserialize<PiperVoiceData>(json) is not { } voiceData)
        {
          return null;
        }

        /*
         * The locale each voice is labelled with, settled once here rather than while a dropdown renders: it costs one
         * small file read per voice and nothing about speaking depends on the answer.
         */
        foreach (var voice in voiceData.Voices ?? [])
        {
          voice.Locale = ResolveLocale(root, voice);
        }

        return InitializeNative(root) ? new PiperTtsEngine(root, voiceData) : null;
      }
      catch (Exception ex)
      {
        Log.Error("Error initializing piper-tts", ex);
        return null;
      }
    }

    /*
     * Which locale to print beside a Piper voice's name. A pack may say so in voices.json; almost always the model's
     * own metadata does (language.code, "en_US"); and where neither can be read the file name still tells it, since
     * piper voices are named locale first (en_US-lessac-medium.onnx). Nothing spoken depends on any of this, so a
     * voice whose metadata cannot be read simply goes without the suffix.
     */
    private static string ResolveLocale(string root, PiperVoice voice)
    {
      // A pack that declares the locale outright needs no file read at all.
      if (voice.Locale is { Length: > 0 } && RegionOf(voice.Locale) is { Length: > 0 } declared)
      {
        return declared;
      }

      if (voice.Config is { Length: > 0 })
      {
        var path = Path.Combine(root, "voices", voice.Config);
        try
        {
          if (File.Exists(path) && LocaleFromMetadata(File.ReadAllText(path)) is { Length: > 0 } fromModel)
          {
            return fromModel;
          }
        }
        catch (Exception ex)
        {
          Log.Debug($"Unable to read the language of piper voice {voice.Config}", ex);
        }
      }

      return LocaleFromPath(voice.Model) ?? LocaleFromPath(voice.Config);
    }

    /* The language block of a piper model config: { "language": { "code": "en_US", ... } }. */
    internal static string LocaleFromMetadata(string jsonText)
    {
      try
      {
        using var document = JsonDocument.Parse(jsonText);
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("language", out var language) &&
            language.ValueKind == JsonValueKind.Object)
        {
          foreach (var property in new[] { "code", "region" })
          {
            if (language.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
                RegionOf(value.GetString()) is { Length: > 0 } region)
            {
              return region;
            }
          }
        }
      }
      catch (JsonException ex)
      {
        Log.Debug("Unable to parse piper voice metadata", ex);
      }

      return null;
    }

    /* en_US-lessac-medium.onnx -> US, the naming convention of every piper voice release. */
    internal static string LocaleFromPath(string path)
    {
      if (path is not { Length: > 0 })
      {
        return null;
      }

      var stem = Path.GetFileName(path);
      var dash = stem.IndexOf('-');

      if (dash > 0)
      {
        stem = stem[..dash];
      }

      var underscore = stem.IndexOf('_');

      if (underscore <= 0 || underscore == stem.Length - 1)
      {
        return null;
      }

      return stem[(underscore + 1)..].ToUpperInvariant();
    }

    /*
     * The part worth printing: "en_US" and "US" both read US and "zh_CN" reads CN. A token with no region in it - a
     * bare language, or free text somebody typed into voices.json - gives nothing, so that voice goes without a suffix
     * rather than showing something made up.
     */
    internal static string RegionOf(string locale)
    {
      if (locale is not { Length: > 0 })
      {
        return null;
      }

      var trimmed = locale.Trim();
      var underscore = trimmed.LastIndexOf('_');

      if (underscore >= 0)
      {
        trimmed = trimmed[(underscore + 1)..];
      }

      if (trimmed.Length is < 2 or > 4 || !trimmed.All(char.IsLetter))
      {
        return null;
      }

      return trimmed.ToUpperInvariant();
    }

    /* Picker text for one voice: "HFC Male (US)", or just the name when no locale is known. */
    internal static string FormatDisplayName(string name, string locale) =>
      name is not { Length: > 0 } || locale is not { Length: > 0 } ? name : $"{name} ({locale})";

    // Returning IntPtr.Zero falls through to the default resolver, so every other native import is untouched.
    private static IntPtr ResolveDllImport(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
      if (!libraryName.Equals(PiperApiLibrary, StringComparison.OrdinalIgnoreCase))
      {
        return IntPtr.Zero;
      }

      // The pack is on no search path the loader knows, so name its folder. The files piperApi.dll imports sit beside
      // it, which the altered search path NativeLibrary.Load uses covers.
      if (TtsPackManager.ResolveRoot(EngineName) is { } root &&
          NativeLibrary.TryLoad(Path.Combine(root, libraryName), out var handle))
      {
        return handle;
      }

      return IntPtr.Zero;
    }

    private static bool InitializeNative(string root)
    {
      lock (_nativeLock)
      {
        try
        {
          // Claim the onnxruntime name for the build matching our managed wrapper before piperApi.dll pulls in the one
          // sitting next to it: both engines need that module and only the first loaded can serve them.
          TtsPackManager.PreferMatchingOnnxRuntime();

          // One initialize() per pack directory: downloading a pack while an app-local Piper is live means the new
          // espeak-ng data has to be handed to the native side before its voices can be loaded from it.
          if (string.Equals(_nativeInitializedRoot, root, StringComparison.OrdinalIgnoreCase))
          {
            return true;
          }

          if (_nativeInitializedRoot is not null)
          {
            PiperInterop.release();
            _nativeInitializedRoot = null;
          }

          PiperInterop.initialize(Path.Combine(root, "espeak-ng-data"));
          _nativeInitializedRoot = root;
          return true;
        }
        catch (Exception ex)
        {
          Log.Error($"Error initializing piper-tts (onnxruntime in use: " +
            $"{TtsPackManager.DescribeLoadedOnnxRuntime()})", ex);
          return false;
        }
      }
    }

    private PiperVoice LoadNativeVoice(string playerId, string name)
    {
      if (_voiceData?.Voices is not { Count: > 0 } voices)
      {
        return null;
      }

      // fall back to the first voice in the pack so there is always something to speak with
      var voiceInfo = voices.FirstOrDefault(
          voice => string.Equals(voice.Name, name, StringComparison.OrdinalIgnoreCase)) ?? voices[0];

      var modelPath = Path.Combine(_root, "voices", voiceInfo.Model);
      var configPath = Path.Combine(_root, "voices", voiceInfo.Config);

      try
      {
        /*
         * A -1 is all the native side says, and what follows it is silence with no exception anywhere - which from the
         * user's side looks exactly like a broken sound card. This one has to reach the log without Debug turned on.
         */
        if (PiperInterop.loadVoice(playerId, modelPath, configPath) == -1)
        {
          Log.Warn($"Piper could not load the '{voiceInfo.Name}' voice for '{playerId}' from {modelPath}");
          return null;
        }

        return voiceInfo;
      }
      catch (Exception ex)
      {
        Log.Error("Error loading piper voice", ex);
        return null;
      }
    }

    private sealed class PlayerVoice
    {
      internal string Name { get; init; }
      internal int SampleRate { get; init; }
    }

    private static byte[] SynthesizeNative(string playerId, string text)
    {
      try
      {
        var size = PiperInterop.synthesize(playerId, text, out var audioBuffer);
        if (size <= 0 || audioBuffer == IntPtr.Zero)
        {
          // Silence with no exception is how a voice that is not loaded answers. Worth a line: it looks exactly like
          // the audio device being broken from the user's side.
          Log.Warn($"Piper produced no speech for voice '{playerId}' (size {size}); is that voice loaded?");
          return null;
        }

        try
        {
          var pcm = new byte[(int)size * sizeof(short)];
          var samples = new short[(int)size];
          Marshal.Copy(audioBuffer, samples, 0, samples.Length);
          Buffer.BlockCopy(samples, 0, pcm, 0, pcm.Length);
          return pcm;
        }
        finally
        {
          // the buffer belongs to the native library until this returns
          PiperInterop.freeAudioData(audioBuffer);
        }
      }
      catch (Exception ex)
      {
        Log.Debug("Error synthesizing piper speech", ex);
        return null;
      }
    }
  }

  /* Raw piperApi.dll entry points. Nothing outside this assembly needs them and nothing outside should own them. */
  internal static class PiperInterop
  {
    [DllImport(PiperTtsEngine.PiperApiLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void initialize([MarshalAs(UnmanagedType.LPStr)] string espeakDataPath);

    [DllImport(PiperTtsEngine.PiperApiLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void release();

    [DllImport(PiperTtsEngine.PiperApiLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int loadVoice([MarshalAs(UnmanagedType.LPStr)] string id,
      [MarshalAs(UnmanagedType.LPStr)] string modelPath, [MarshalAs(UnmanagedType.LPStr)] string configPath);

    [DllImport(PiperTtsEngine.PiperApiLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int removeVoice([MarshalAs(UnmanagedType.LPStr)] string id);

    [DllImport(PiperTtsEngine.PiperApiLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern long synthesize([MarshalAs(UnmanagedType.LPStr)] string id,
      [MarshalAs(UnmanagedType.LPStr)] string text, out IntPtr audioBuffer);

    [DllImport(PiperTtsEngine.PiperApiLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void freeAudioData(IntPtr buffer);
  }
}
