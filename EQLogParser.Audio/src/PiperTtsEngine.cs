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

      // The old voice goes out first. Replacing a table entry is not documented as releasing the session behind it, so
      // rebinding without removing would leak a voice model per dropdown change - which is how an afternoon of trying
      // voices ends with several hundred megabytes of voices nobody selected.
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

    public void RemoveVoice(string playerId)
    {
      if (string.IsNullOrEmpty(playerId))
      {
        return;
      }

      _players.TryRemove(playerId, out _);
      TryRemoveNativeVoice(playerId);
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

    public async Task<(byte[] pcm, int sampleRate)> SynthesizeForPlayerAsync(string playerId, string text)
    {
      var sample = playerId is not null && _players.TryGetValue(playerId, out var player) ? player.SampleRate : 0;
      var pcm = await Task.Run(() => SynthesizeNative(playerId, text)).ConfigureAwait(false);
      return (pcm, sample);
    }

    public async Task<(byte[] pcm, int sampleRate)> SynthesizeVoiceAsync(string voice, string text)
    {
      var (speaker, sampleRate) = ResolveAdHocSpeaker(voice);

      if (string.IsNullOrEmpty(speaker))
      {
        return (null, 0);
      }

      var pcm = await Task.Run(() => SynthesizeNative(speaker, text)).ConfigureAwait(false);
      return (pcm, sampleRate);
    }

    public async Task WarmUpVoiceAsync(string voice)
    {
      var (speaker, _) = ResolveAdHocSpeaker(voice);

      if (string.IsNullOrEmpty(speaker))
      {
        return;
      }

      // Building the session is most of the cost and ResolveAdHocSpeaker has just done it. The throwaway synthesis is
      // for the rest: ONNX Runtime allocates its arenas and settles on kernels the first time it runs something, so a
      // trigger should not be the first thing through a brand new session.
      _ = await Task.Run(() => SynthesizeNative(speaker, WarmUpText)).ConfigureAwait(false);
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

        return InitializeNative(root) ? new PiperTtsEngine(root, voiceData) : null;
      }
      catch (Exception ex)
      {
        Log.Error("Error initializing piper-tts", ex);
        return null;
      }
    }

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
        return PiperInterop.loadVoice(playerId, modelPath, configPath) != -1 ? voiceInfo : null;
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
