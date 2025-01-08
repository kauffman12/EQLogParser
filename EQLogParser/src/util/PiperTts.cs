using log4net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace EQLogParser
{
  internal class PiperTts
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    private static PiperVoiceData _voiceData;
    private static readonly object _voiceDataLock = new object();
    private static readonly string PiperTtsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "piper-tts");

    private PiperTts() { }

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

            if (!NativeMethods.SetDllDirectory(PiperTtsPath))
            {
              Log.Error($"SetDllDirectory failed: {Marshal.GetLastWin32Error()}");
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
    [DllImport("piperApi.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void initialize([MarshalAs(UnmanagedType.LPStr)] string espeakDataPath);

    [DllImport("piperApi.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void release();

    [DllImport("piperApi.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int loadVoice([MarshalAs(UnmanagedType.LPStr)] string id, [MarshalAs(UnmanagedType.LPStr)] string modelPath, [MarshalAs(UnmanagedType.LPStr)] string configPath);

    [DllImport("piperApi.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int removeVoice([MarshalAs(UnmanagedType.LPStr)] string id);

    [DllImport("piperApi.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern long synthesize([MarshalAs(UnmanagedType.LPStr)] string id, [MarshalAs(UnmanagedType.LPStr)] string text, out IntPtr audioBuffer);

    [DllImport("piperApi.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void freeAudioData(IntPtr buffer);
  }
}
