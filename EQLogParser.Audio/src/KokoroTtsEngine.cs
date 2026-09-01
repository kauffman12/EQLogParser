using KokoroSharp;
using KokoroSharp.Core;
using log4net;
using System;
using System.Collections.Concurrent;
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
  /*
   * Kokoro neural voices through KokoroSharp (https://github.com/Lyrcaxis/KokoroSharp). Unlike Piper, the model is
   * not bundled with the app: it is fetched on demand into local app data when a user opts in, and verified against
   * a pinned checksum before onnxruntime is handed it. Only the WAV synthesis path is used; playback stays with the
   * audio device code so exports, rate changes and per player queues behave like every other sound.
   */
  internal sealed class KokoroTtsEngine : ITtsEngine
  {
    internal const string EngineName = "Kokoro";
    internal const int SampleRate = 24000;

    // Half precision: 156MB instead of the 310MB fp32 graph, and the loss is not audible on trigger callouts.
    // Both names are served by the release that backs the KokoroSharp version referenced by this project.
    private const string ModelFileName = "kokoro-fp16.onnx";
    private const string ModelDownloadUrl =
      "https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/" + ModelFileName;

    /*
     * SHA-256 GitHub reports for that release asset. The graph is executed by onnxruntime with the app's own
     * privileges, so a truncated or substituted file must never reach LoadModel. Changing ModelFileName means
     * updating this in the same commit. See docs/DesignNotes.md -> Kokoro model integrity.
     */
    private const string ModelSha256 = "027a25b14aef7d3ae57fd09301ebefbec868e79d55213d07e4f3af442f5ba352";
    private const string PreferredDefaultVoice = "af_heart";

    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    private static readonly string DataDir = Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EQLogParser", "kokoro-tts");
    private static readonly string ModelPath = Path.Combine(DataDir, ModelFileName);

    // Written once a file has been checked, so the 156MB hash runs one time per model instead of at every start.
    private static readonly string MarkerPath = ModelPath + ".sha256";

    private readonly ConcurrentDictionary<string, string> _playerVoices = [];
    private KokoroWavSynthesizer _synth;

    public string Name => EngineName;

    public Task LoadVoicesAsync() => Task.CompletedTask;

    public List<string> GetVoices()
    {
      EnsureVoicesLoaded();
      return KokoroVoiceManager.Voices
        .Select(voice => voice.Name)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToList();
    }

    public string GetDefaultVoice()
    {
      EnsureVoicesLoaded();
      return KokoroVoiceManager.Voices.FirstOrDefault(voice =>
        string.Equals(voice.Name, PreferredDefaultVoice, StringComparison.OrdinalIgnoreCase))?.Name
        ?? KokoroVoiceManager.Voices.FirstOrDefault()?.Name;
    }

    public string GetVoice(string playerId)
    {
      if (playerId != null && _playerVoices.TryGetValue(playerId, out var voice) && !string.IsNullOrEmpty(voice))
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
      if (playerId != null)
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

    internal static bool IsModelDownloaded() => File.Exists(ModelPath);

    /* Null when the model is absent, fails verification, or will not load. */
    internal static KokoroTtsEngine TryCreate()
    {
      if (!IsModelDownloaded() || !VerifyModel())
      {
        return null;
      }

      var engine = new KokoroTtsEngine();
      try
      {
        engine._synth = KokoroWavSynthesizer.LoadModel(ModelPath);
        EnsureVoicesLoaded();
        return engine;
      }
      catch (Exception ex)
      {
        Log.Error("Error initializing kokoro-tts", ex);
        engine.Dispose();
        return null;
      }
    }

    internal static async Task<bool> DownloadModelAsync(Action<float> onProgress, CancellationToken cancellationToken = default)
    {
      try
      {
        Directory.CreateDirectory(DataDir);
        var tempPath = ModelPath + ".tmp";

        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await client.GetAsync(ModelDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
          .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        await using (var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
          var buffer = new byte[81920];
          long totalRead = 0;
          int bytesRead;

          while ((bytesRead = await responseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
          {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
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

    /*
     * Confirms the model on disk is the graph we expect. Cheap once the marker matches, so it costs nothing on the
     * normal startup path; a hand-placed or previously downloaded model pays for it once. A mismatch is reported
     * rather than repaired: deleting a user's 156MB download over a pin we could have misrecorded is worse.
     */
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
      // The voice embeddings ship next to the executable and KokoroSharp keeps them in process wide state.
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
