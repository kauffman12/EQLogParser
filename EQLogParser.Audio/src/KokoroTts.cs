using KokoroSharp;
using KokoroSharp.Core;
using log4net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace EQLogParser.Audio
{
  // Wraps the KokoroSharp neural TTS engine (https://github.com/Lyrcaxis/KokoroSharp). Unlike Piper, the ~320MB
  // model isn't bundled with the app -- it's fetched on demand into local app data the first time a user opts in.
  internal static class KokoroTts
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    // Pinned to the release that backs the KokoroSharp NuGet version referenced by this project.
    private const string ModelDownloadUrl = "https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/kokoro.onnx";
    private const string PreferredDefaultVoice = "af_heart";
    internal const int SampleRate = 24000;

    private static readonly string DataDir = Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EQLogParser", "kokoro-tts");
    private static readonly string ModelPath = Path.Combine(DataDir, "kokoro.onnx");

    private static readonly object Lock = new();
    private static KokoroWavSynthesizer _synth;

    internal static bool IsModelDownloaded() => File.Exists(ModelPath);

    internal static bool Initialize()
    {
      if (_synth != null) return true;
      if (!IsModelDownloaded()) return false;

      lock (Lock)
      {
        if (_synth != null) return true;

        try
        {
          _synth = KokoroWavSynthesizer.LoadModel(ModelPath);
          KokoroVoiceManager.LoadVoicesFromPath();
          return true;
        }
        catch (Exception ex)
        {
          Log.Error("Error initializing kokoro-tts", ex);
          _synth = null;
          return false;
        }
      }
    }

    internal static async Task<bool> DownloadModelAsync(Action<float> onProgress, CancellationToken cancellationToken = default)
    {
      try
      {
        Directory.CreateDirectory(DataDir);
        var tempPath = ModelPath + ".tmp";

        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await client.GetAsync(ModelDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        await using (var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
          var buffer = new byte[81920];
          long totalRead = 0;
          int bytesRead;

          while ((bytesRead = await responseStream.ReadAsync(buffer, cancellationToken)) > 0)
          {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            totalRead += bytesRead;

            if (totalBytes > 0)
            {
              onProgress?.Invoke((float)totalRead / totalBytes);
            }
          }
        }

        File.Move(tempPath, ModelPath, true);
        return true;
      }
      catch (Exception ex)
      {
        Log.Error("Error downloading kokoro-tts model", ex);
        return false;
      }
    }

    internal static List<string> GetVoiceList()
    {
      EnsureVoicesLoaded();
      return KokoroVoiceManager.Voices.Select(voice => voice.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static string GetDefaultVoice()
    {
      EnsureVoicesLoaded();
      return KokoroVoiceManager.Voices.FirstOrDefault(voice =>
        string.Equals(voice.Name, PreferredDefaultVoice, StringComparison.OrdinalIgnoreCase))?.Name
        ?? KokoroVoiceManager.Voices.FirstOrDefault()?.Name;
    }

    internal static byte[] SynthesizeText(string voiceName, string text)
    {
      if (_synth == null || FindVoice(voiceName) is not { } voice)
      {
        return null;
      }

      return _synth.Synthesize(text, voice);
    }

    internal static void Release()
    {
      lock (Lock)
      {
        _synth?.Dispose();
        _synth = null;
      }
    }

    private static void EnsureVoicesLoaded()
    {
      if (KokoroVoiceManager.Voices.Count == 0)
      {
        try
        {
          KokoroVoiceManager.LoadVoicesFromPath();
        }
        catch (Exception ex)
        {
          Log.Error("Error loading kokoro-tts voices", ex);
        }
      }
    }

    private static KokoroVoice FindVoice(string name)
    {
      EnsureVoicesLoaded();

      if (!string.IsNullOrEmpty(name) &&
          KokoroVoiceManager.Voices.FirstOrDefault(voice => string.Equals(voice.Name, name, StringComparison.OrdinalIgnoreCase)) is { } found)
      {
        return found;
      }

      return KokoroVoiceManager.Voices.FirstOrDefault(voice =>
        string.Equals(voice.Name, PreferredDefaultVoice, StringComparison.OrdinalIgnoreCase)) ?? KokoroVoiceManager.Voices.FirstOrDefault();
    }
  }
}
