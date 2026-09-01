using log4net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace EQLogParser.Audio
{
  internal sealed class PiperTts
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    private static PiperVoiceData _voiceData;
    private static readonly object _voiceDataLock = new();
    private static readonly string PiperTtsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "piper-tts");
    internal const string PiperApiLibrary = "piperApi.dll";

    static PiperTts()
    {
      // Resolve piperApi.dll (and only that library) out of the piper-tts folder. SetDllDirectory would have
      // changed the search path for every later native load in the process and overwritten any other caller's
      // choice; a resolver is scoped to this assembly and this dll name. Its dependencies sit beside it, which
      // the altered search path used by NativeLibrary.Load covers. See docs/DesignNotes.md -> Piper native lookup.
      NativeLibrary.SetDllImportResolver(typeof(PiperTts).Assembly, ResolveDllImport);
    }

    private PiperTts() { }

    // Returning IntPtr.Zero falls through to the default resolver, so every other native import is untouched.
    private static IntPtr ResolveDllImport(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
      if (!libraryName.Equals(PiperApiLibrary, StringComparison.OrdinalIgnoreCase))
      {
        return IntPtr.Zero;
      }

      return NativeLibrary.TryLoad(Path.Combine(PiperTtsPath, libraryName), out var handle) ? handle : IntPtr.Zero;
    }

    internal static bool IsVoicePackAvailable() => File.Exists(Path.Combine(PiperTtsPath, "voices", "voices.json"));

    internal static bool Initialize()
    {
      try
      {
        var voiceFile = Path.Combine(PiperTtsPath, "voices/voices.json");
        if (File.Exists(voiceFile))
        {
          var json = File.ReadAllText(voiceFile);
          if (JsonSerializer.Deserialize<PiperVoiceData>(json) is { } voiceData)
          {
            lock (_voiceDataLock)
            {
              _voiceData = voiceData;
            }

            var espeakPath = Path.Combine(PiperTtsPath, "espeak-ng-data");
            PiperInterop.initialize(espeakPath);
            return true;
          }
        }
      }
      catch (Exception ex)
      {
        Log.Error("Error initializing piper-tts", ex);
      }

      return false;
    }

    internal static void Release()
    {
      PiperInterop.release();
    }

    internal static string GetDefaultVoice()
    {
      lock (_voiceDataLock)
      {
        return _voiceData?.Voices?.FirstOrDefault()?.Name;
      }
    }

    internal static List<string> GetVoiceList()
    {
      lock (_voiceDataLock)
      {
        return _voiceData?.Voices.Select(voice => voice.Name).ToList() ?? [];
      }
    }

    internal static bool LoadVoice(string id, string name, out PiperVoice data)
    {
      data = null;

      lock (_voiceDataLock)
      {
        foreach (var voiceInfo in _voiceData?.Voices)
        {
          // so we have a default
          if (data == null || voiceInfo.Name == name)
          {
            data = voiceInfo;
          }
        }
      }

      if (data != null)
      {
        var modelPath = Path.Combine(PiperTtsPath, "voices", data.Model);
        var configPath = Path.Combine(PiperTtsPath, "voices", data.Config);
        return PiperInterop.loadVoice(id, modelPath, configPath) != -1;
      }

      return false;
    }

    internal static bool RemoveVoice(string id) => PiperInterop.removeVoice(id) != -1;

    internal static byte[] SynthesizeText(string id, string text)
    {
      var size = PiperInterop.synthesize(id, text, out var audioBuffer);
      if (size > 0 && audioBuffer != IntPtr.Zero)
      {
        try
        {
          // Convert the buffer to a managed short array
          var sampleCount = (int)size;
          var shortBuffer = new short[sampleCount];
          Marshal.Copy(audioBuffer, shortBuffer, 0, sampleCount);

          // Convert the short array to a byte array
          var byteBuffer = new byte[sampleCount * sizeof(short)];
          Buffer.BlockCopy(shortBuffer, 0, byteBuffer, 0, byteBuffer.Length);
          return byteBuffer;
        }
        finally
        {
          // Free the buffer on the C++ side
          PiperInterop.freeAudioData(audioBuffer);
        }
      }

      return null;
    }
  }

  public static class PiperInterop
  {
    [DllImport(PiperTts.PiperApiLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void initialize([MarshalAs(UnmanagedType.LPStr)] string espeakDataPath);

    [DllImport(PiperTts.PiperApiLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void release();

    [DllImport(PiperTts.PiperApiLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int loadVoice([MarshalAs(UnmanagedType.LPStr)] string id, [MarshalAs(UnmanagedType.LPStr)] string modelPath, [MarshalAs(UnmanagedType.LPStr)] string configPath);

    [DllImport(PiperTts.PiperApiLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int removeVoice([MarshalAs(UnmanagedType.LPStr)] string id);

    [DllImport(PiperTts.PiperApiLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern long synthesize([MarshalAs(UnmanagedType.LPStr)] string id, [MarshalAs(UnmanagedType.LPStr)] string text, out IntPtr audioBuffer);

    [DllImport(PiperTts.PiperApiLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void freeAudioData(IntPtr buffer);
  }
}
