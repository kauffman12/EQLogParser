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
   * in local app data (docs/TtsPacks.md); installs made before packs had the same files beside the executable and are
   * still read from there, so nobody has to re-download 347MB. TtsPackManager picks the directory and answers the
   * native imports that live inside it. The native library keeps a process wide table of loaded voices keyed by an id,
   * so each player registers its voice under its own id and the preview path registers one of its own under a
   * reserved id.
   */
  internal sealed class PiperTtsEngine : ITtsEngine
  {
    internal const string EngineName = "Piper";

    /* Native library name, answered by the import resolver below. */
    internal const string PiperApiLibrary = "piperApi.dll";

    // The native voice table is keyed by id, so preview and WAV export need an id no player can have.
    private const string AdHocVoiceId = "testSpeaker";

    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    // initialize() and release() act on process wide native state, so they run once per voice pack directory no
    // matter how many engine instances come and go.
    private static string _nativeInitializedRoot;
    private static readonly object _nativeLock = new();

    private readonly ConcurrentDictionary<string, PlayerVoice> _players = new();
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
      playerId != null && _players.TryGetValue(playerId, out var player) && !string.IsNullOrEmpty(player.Name)
        ? player.Name
        : GetDefaultVoice();

    public void SetVoice(string playerId, string voice)
    {
      if (!string.IsNullOrEmpty(playerId) && LoadNativeVoice(playerId, voice) is { } voiceInfo)
      {
        _players[playerId] = new PlayerVoice { Name = voiceInfo.Name, SampleRate = voiceInfo.Sample };
      }
    }

    public void RemoveVoice(string playerId)
    {
      if (string.IsNullOrEmpty(playerId))
      {
        return;
      }

      _players.TryRemove(playerId, out _);

      try
      {
        _ = PiperInterop.removeVoice(playerId);
      }
      catch (Exception ex)
      {
        Log.Debug("Unable to remove piper voice", ex);
      }
    }

    public async Task<(byte[] pcm, int sampleRate)> SynthesizeForPlayerAsync(string playerId, string text)
    {
      var sample = playerId != null && _players.TryGetValue(playerId, out var player) ? player.SampleRate : 0;
      var pcm = await Task.Run(() => SynthesizeNative(playerId, text)).ConfigureAwait(false);
      return (pcm, sample);
    }

    public async Task<(byte[] pcm, int sampleRate)> SynthesizeVoiceAsync(string voice, string text)
    {
      if (LoadNativeVoice(AdHocVoiceId, voice) is not { } voiceInfo)
      {
        return (null, 0);
      }

      try
      {
        var pcm = await Task.Run(() => SynthesizeNative(AdHocVoiceId, text)).ConfigureAwait(false);
        return (pcm, voiceInfo.Sample);
      }
      finally
      {
        try
        {
          _ = PiperInterop.removeVoice(AdHocVoiceId);
        }
        catch (Exception ex)
        {
          Log.Debug("Unable to remove piper preview voice", ex);
        }
      }
    }

    public void Dispose()
    {
      _players.Clear();

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

      // Whichever copy of the pack is in play has its own piperApi.dll; both candidates are tried because a pack can
      // be installed while an engine built on the app-local copy is still alive.
      foreach (var dir in CandidateDirectories())
      {
        if (NativeLibrary.TryLoad(Path.Combine(dir, libraryName), out var handle))
        {
          return handle;
        }
      }

      return IntPtr.Zero;
    }

    private static IEnumerable<string> CandidateDirectories()
    {
      if (TtsPackManager.ResolveRoot(EngineName) is { } resolved)
      {
        yield return resolved;
      }

      yield return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "piper-tts");
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
          Log.Error($"Error initializing piper-tts (onnxruntime in use: {TtsPackManager.DescribeLoadedOnnxRuntime()})", ex);
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
      var voiceInfo = voices.FirstOrDefault(voice => string.Equals(voice.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? voices[0];

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

  public static class PiperInterop
  {
    [DllImport(PiperTtsEngine.PiperApiLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void initialize([MarshalAs(UnmanagedType.LPStr)] string espeakDataPath);

    [DllImport(PiperTtsEngine.PiperApiLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void release();

    [DllImport(PiperTtsEngine.PiperApiLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int loadVoice([MarshalAs(UnmanagedType.LPStr)] string id, [MarshalAs(UnmanagedType.LPStr)] string modelPath, [MarshalAs(UnmanagedType.LPStr)] string configPath);

    [DllImport(PiperTtsEngine.PiperApiLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int removeVoice([MarshalAs(UnmanagedType.LPStr)] string id);

    [DllImport(PiperTtsEngine.PiperApiLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern long synthesize([MarshalAs(UnmanagedType.LPStr)] string id, [MarshalAs(UnmanagedType.LPStr)] string text, out IntPtr audioBuffer);

    [DllImport(PiperTtsEngine.PiperApiLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void freeAudioData(IntPtr buffer);
  }
}
