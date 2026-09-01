using KokoroSharp;
using KokoroSharp.Core;
using log4net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace EQLogParser.Audio
{
  // Wraps the KokoroSharp neural TTS engine (https://github.com/Lyrcaxis/KokoroSharp). Unlike Piper, the model
  // isn't bundled with the app -- it's fetched on demand into local app data the first time a user opts in.
  internal static class KokoroTts
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    // Half precision: 156MB instead of the 310MB fp32 graph, and the loss is not audible on trigger callouts.
    // Both names are served by the release that backs the KokoroSharp version referenced by this project.
    private const string ModelFileName = "kokoro-fp16.onnx";
    private const string ModelDownloadUrl =
      "https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/" + ModelFileName;

    // SHA-256 GitHub reports for that release asset. The graph is executed by onnxruntime with the app's own
    // privileges, so a truncated or substituted file must never reach LoadModel. See docs/DesignNotes.md
    // -> Kokoro model integrity.
    private const string ModelSha256 = "027a25b14aef7d3ae57fd09301ebefbec868e79d55213d07e4f3af442f5ba352";
    private const string PreferredDefaultVoice = "af_heart";
    internal const int SampleRate = 24000;

    private static readonly string DataDir = Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EQLogParser", "kokoro-tts");
    private static readonly string ModelPath = Path.Combine(DataDir, ModelFileName);

    // Written once a file has been checked, so the 156MB hash runs one time per model instead of at every start.
    private static readonly string MarkerPath = ModelPath + ".sha256";

    private static readonly object Lock = new();
    private static KokoroWavSynthesizer _synth;

    internal static bool IsModelDownloaded() => File.Exists(ModelPath);

    internal static bool Initialize()
    {
      if (_synth != null) return true;
      if (!IsModelDownloaded() || !VerifyModel()) return false;

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

        // A CDN or proxy that hands back something other than the graph would otherwise be loaded and run.
        if (!ModelSha256.Equals(ComputeSha256(tempPath), StringComparison.OrdinalIgnoreCase))
        {
          Log.Error("Kokoro model checksum mismatch after download. The downloaded file was discarded.");
          TryDelete(tempPath);
          return false;
        }

        File.Move(tempPath, ModelPath, true);
        TryWriteMarker();
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

    // Confirms the model on disk is the graph we expect. Cheap once the marker matches, so it costs nothing on
    // the normal startup path; a hand-placed or previously downloaded model pays for it once.
    private static bool VerifyModel()
    {
      try
      {
        if (File.Exists(MarkerPath) &&
            string.Equals(File.ReadAllText(MarkerPath).Trim(), ModelSha256, StringComparison.OrdinalIgnoreCase))
        {
          return true;
        }

        if (!string.Equals(ComputeSha256(ModelPath), ModelSha256, StringComparison.OrdinalIgnoreCase))
        {
          Log.Error("Kokoro model checksum mismatch. Use 'Download Kokoro' on the TTS Engine screen to replace it.");
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

    private static string ComputeSha256(string path)
    {
      using var sha = SHA256.Create();
      using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
      return Convert.ToHexString(sha.ComputeHash(stream));
    }

    private static void TryWriteMarker()
    {
      try
      {
        File.WriteAllText(MarkerPath, ModelSha256);
      }
      catch (Exception ex)
      {
        Log.Debug("Unable to write kokoro model checksum marker", ex);
      }
    }

    private static void TryDelete(string path)
    {
      try
      {
        if (File.Exists(path))
        {
          File.Delete(path);
        }
      }
      catch (Exception ex)
      {
        Log.Debug("Unable to delete file", ex);
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
