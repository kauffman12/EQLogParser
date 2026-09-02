using log4net;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EQLogParser.Audio
{
  /*
   * Speech runtime packs. Neither speech engine is carried by the installer: Piper's native library, espeak-ng data
   * and voice models, and Kokoro's support assemblies, onnxruntime, voice embeddings and 156MB graph are published as
   * GitHub release assets and fetched into local app data when a user enables the engine. See docs/TtsPacks.md.
   *
   * Two things make this work at runtime:
   *
   *   - Assembly resolution. EQLogParser.Audio.dll is compiled against KokoroSharp only, so MisakiSharp, NumSharp,
   *     OpenTK and System.Numerics.Tensors are simply missing until a pack lands; the default load context is asked
   *     for them after normal probing fails, and we answer from the pack. LoadFrom on the default context rather than
   *     a private one keeps type identity with the assemblies that install beside the executable (KokoroSharp and the
   *     ONNX runtime wrapper share NAudio-free dependencies with the app, and a second copy of those in another
   *     context would fail to bind).
   *
   *   - Native resolution. onnxruntime.dll lives in the pack, and Microsoft.ML.OnnxRuntime's P/Invoke stubs know
   *     nothing about that folder, so the same context answers those imports too.
   *
   * Trust: a pack is only accepted if the downloaded archive matches the SHA-256 pinned below, and every file inside
   * it is then checked against the manifest.json that was generated from the signed bytes at publish time. Nothing is
   * loaded from a directory that did not pass both checks, which is why a completed install leaves a marker file:
   * presence of the marker plus the few files that must exist is what "installed" means at startup, instead of
   * re-hashing 400MB every time the app starts.
   */
  internal static class TtsPackManager
  {
    private const string ReleaseRepo = "kauffman12/EQLogParser-TTS";
    private const string MarkerName = ".pack-ready";
    private const string ManifestName = "manifest.json";
    private const string OnnxRuntimeFileName = "onnxruntime.dll";

    private sealed record Pack(string Engine, string Tag, string AssetName, string Sha256, string FolderName, long DownloadBytes);

    // Bumping a pack means publishing the new tag and changing Tag and Sha256 together; old releases are never
    // overwritten once an app build that pins them exists, so an installed app keeps pointing at bytes that will still
    // exist. The packs below were still free to be replaced under their own tag, which is how the first piper-1.0
    // digest (059241c0...) differs from the one here: it was rebuilt with onnxruntime aligned to Kokoro's.
    private static readonly Dictionary<string, Pack> Packs = new(StringComparer.OrdinalIgnoreCase)
    {
      [AudioManager.PiperEngine] = new Pack(AudioManager.PiperEngine, "piper-1.0", "piper-1.0.zip",
        "dc24d7f9673b28b9e18a0801f0492107ad8c2b6e6ba6645ca67488b703f76451", "piper-tts", 348L * 1024 * 1024),
      [AudioManager.KokoroEngine] = new Pack(AudioManager.KokoroEngine, "kokoro-1.0", "kokoro-1.0.zip",
        "b1070b9e231dd0d08203fc89f6540c6de3d13de479bd506f63f6902194241788", "kokoro", 224L * 1024 * 1024)
    };

    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    private static readonly string StorageRoot = Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EQLogParser");

    // A shared client keeps the connection pool; the per-request timeout is the cancellation token instead. The user
    // agent is for other people's logs: "EQLogParser" in a proxy trace is explainable, an empty one is not.
    private static readonly HttpClient Http = CreateClient();

    // One install at a time: two of these would fight over the same staging directory, and a double click on a
    // download button is not hypothetical.
    private static readonly SemaphoreSlim _installGate = new(1, 1);

    private static readonly JsonSerializerOptions ManifestOptions = new() { PropertyNameCaseInsensitive = true };

    private static int _resolversRegistered;

    /* True when a complete, verified pack sits in local app data. Cheap: no hashing, only existence checks. */
    internal static bool IsInstalled(string engine) => InstalledDirectory(engine) is not null;

    /*
     * True when this engine's own directory is here, complete or not. Only used to word the result of a remove:
     * "nothing was downloaded here" and "the files are locked" are different problems for the user.
     */
    internal static bool IsPackOnDisk(string engine) =>
      Packs.TryGetValue(engine, out var pack) && Directory.Exists(Path.Combine(StorageRoot, pack.FolderName));

    /*
     * Where an engine reads from: its downloaded pack, and nowhere else. Null means the engine has nothing to run on,
     * so it stays out of the picker until a pack is downloaded.
     *
     * There used to be a second answer for Piper -- a complete copy beside the executable, honored so that installs
     * predating the packs kept speaking. It is gone on purpose. A directory the app silently adopts cannot be updated,
     * removed from the dialog, or matched against a pinned digest, and it makes "is Piper installed?" depend on debris
     * an old build left behind: the build output still carried espeak-ng data and voice models long after they left the
     * repository, so development runs reported a working Piper that nobody had downloaded. One place for these files,
     * owned by the pack manager, and everything else is inert.
     */
    internal static string ResolveRoot(string engine) => InstalledDirectory(engine);

    /*
     * Directories that may hold native libraries a pack provides, most specific first.
     *
     * This order only decides anything for the first load of a name. Windows keeps one module per base name, so once
     * some onnxruntime.dll is resident every later request for that name gets that module no matter which folder this
     * lists -- see PreferMatchingOnnxRuntime. Kokoro's copy is first because it is the one published against the managed
     * wrapper installed with the app.
     */
    internal static IEnumerable<string> NativeSearchDirectories()
    {
      if (InstalledDirectory(AudioManager.KokoroEngine) is { } kokoro)
      {
        yield return Path.Combine(kokoro, "native");
        yield return Path.Combine(kokoro, "bin");
      }

      if (ResolveRoot(AudioManager.PiperEngine) is { } piper)
      {
        yield return piper;
      }
    }

    /* Called before any engine is created. Registering twice would only ever answer the same lookups. */
    internal static void EnsureResolversRegistered()
    {
      if (Interlocked.Exchange(ref _resolversRegistered, 1) == 1)
      {
        return;
      }

      AssemblyLoadContext.Default.Resolving += ResolvePackAssembly;
      AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolvePackNative;
    }

    /* Approximate archive size, for "this will download about 347 MB" style wording. */
    internal static long GetDownloadBytes(string engine) => Packs.TryGetValue(engine, out var pack) ? pack.DownloadBytes : 0;

    /*
     * Downloads and installs an engine's pack. progress is 0..1 across the whole job: the archive transfer earns most
     * of it and verification the rest, so a stalled download never looks finished. Returns false without touching an
     * existing installation when anything fails.
     */
    internal static async Task<bool> InstallAsync(string engine, IProgress<float> progress, CancellationToken cancellationToken)
    {
      if (!Packs.TryGetValue(engine, out var pack))
      {
        Log.Error($"No runtime pack is defined for the {engine} engine");
        return false;
      }

      var target = Path.Combine(StorageRoot, pack.FolderName);
      var staging = target + ".staging";
      var retired = target + ".retired";
      var downloadDir = Path.Combine(StorageRoot, "_download");
      var tempArchive = Path.Combine(downloadDir, pack.AssetName + ".tmp");

      await _installGate.WaitAsync(cancellationToken).ConfigureAwait(false);
      try
      {
        Directory.CreateDirectory(downloadDir);
        EnsureResolversRegistered();

        if (!await DownloadAsync(pack, tempArchive, progress, cancellationToken).ConfigureAwait(false))
        {
          return false;
        }

        // The archive is only as trustworthy as its digest: GitHub, the CDN and the connection are all trusted
        // transports here, so a mismatch means we were handed something else and it never touches disk again. Both
        // digests belong in the message because the usual cause is mundane -- the asset was rebuilt and the pin in this
        // file is the old one -- and without the numbers that reads like a corrupt upload.
        var downloaded = ComputeSha256(tempArchive);
        if (!string.Equals(downloaded, pack.Sha256, StringComparison.OrdinalIgnoreCase))
        {
          Log.Error($@"{engine} runtime pack checksum mismatch; the download was discarded.
  {pack.AssetName} from tag {pack.Tag}: pinned {pack.Sha256}, downloaded {downloaded}.
  If the asset was rebuilt rather than corrupted, change this pin and the table in docs/TtsPacks.md together.");
          return false;
        }

        progress?.Report(0.9f);
        ReplaceDirectory(staging);
        ExtractAndVerify(tempArchive, staging, engine);

        // The old copy is moved aside rather than deleted first, so a failure above leaves the working pack alone.
        ReplaceDirectory(retired);
        if (Directory.Exists(target))
        {
          Directory.Move(target, retired);
        }

        Directory.Move(staging, target);
        TryWriteMarker(target, pack);
        progress?.Report(1f);

        DeleteTreeQuietly(retired);
        RemoveLegacyKokoroModel();
        Log.Info($"{engine} runtime pack installed from {pack.Tag}");
        return true;
      }
      catch (OperationCanceledException)
      {
        Log.Info($"{engine} runtime pack download was cancelled");
        return false;
      }
      catch (Exception ex)
      {
        Log.Error($"Error installing the {engine} runtime pack", ex);
        return false;
      }
      finally
      {
        DeleteTreeQuietly(staging);
        TryDeleteFile(tempArchive);
        _installGate.Release();
      }
    }

    /*
     * Deletes an installed pack and reports whether anything was actually deleted: a Piper that came from an older
     * installer has no pack directory to remove. The caller is responsible for not doing this while the engine is
     * speaking, because on Windows the loaded native libraries stay mapped into the process until it restarts, so
     * AudioManager refuses for the active engine instead of half deleting a directory.
     */
    internal static bool Remove(string engine)
    {
      if (!Packs.TryGetValue(engine, out var pack))
      {
        return false;
      }

      try
      {
        var target = Path.Combine(StorageRoot, pack.FolderName);
        if (!Directory.Exists(target))
        {
          return false;
        }

        Directory.Delete(target, true);
        Log.Info($"{engine} runtime pack removed from {target}");
        return true;
      }
      catch (Exception ex)
      {
        Log.Error($"Error removing the {engine} runtime pack", ex);
        return false;
      }
    }

    private static async Task<bool> DownloadAsync(Pack pack, string tempArchive, IProgress<float> progress, CancellationToken cancellationToken)
    {
      var url = $"https://github.com/{ReleaseRepo}/releases/download/{pack.Tag}/{pack.AssetName}";
      using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
        .ConfigureAwait(false);
      if (!response.IsSuccessStatusCode)
      {
        Log.Error($"Unable to fetch the {pack.Engine} runtime pack: {(int)response.StatusCode} {response.ReasonPhrase}");
        return false;
      }

      var total = response.Content.Headers.ContentLength ?? 0L;
      await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
      await using var destination = new FileStream(tempArchive, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);

      var buffer = new byte[1 << 20];
      long read = 0;
      int got;
      while ((got = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
      {
        await destination.WriteAsync(buffer.AsMemory(0, got), cancellationToken).ConfigureAwait(false);
        read += got;
        if (total > 0)
        {
          // Leave headroom in the bar for verification so it never sits at 100% while still working.
          progress?.Report(Math.Clamp((float)read / total * 0.9f, 0f, 0.9f));
        }
      }

      return true;
    }

    /*
     * Extracts into a private directory and checks every file the manifest lists. Zip entries are treated as
     * untrusted paths: an entry that tries to climb out of the target directory aborts the install outright.
     */
    private static void ExtractAndVerify(string archive, string target, string engine)
    {
      Directory.CreateDirectory(target);
      using var zip = ZipFile.OpenRead(archive);

      var manifest = ReadManifest(zip, engine);
      var rootFull = Path.GetFullPath(target) + Path.DirectorySeparatorChar;

      foreach (var entry in zip.Entries)
      {
        if (string.IsNullOrEmpty(entry.Name))
        {
          continue; // directory entry
        }

        var destination = Path.GetFullPath(Path.Combine(target, entry.FullName));
        if (!destination.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
          throw new InvalidDataException($"{engine} pack contains an unsafe path: {entry.FullName}");
        }

        var directory = Path.GetDirectoryName(destination);
        if (directory is not null && !Directory.Exists(directory))
        {
          Directory.CreateDirectory(directory);
        }

        entry.ExtractToFile(destination, true);
      }

      if (manifest is null)
      {
        Log.Warn($"{engine} pack has no {ManifestName}; files were installed without per file verification");
        return;
      }

      foreach (var file in manifest)
      {
        var path = Path.GetFullPath(Path.Combine(target, file.Path));
        if (!path.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
          throw new InvalidDataException($"{engine} pack manifest names an unsafe path: {file.Path}");
        }

        if (!File.Exists(path) || new FileInfo(path).Length != file.Size ||
            !string.Equals(ComputeSha256(path), file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
          throw new InvalidDataException($"{engine} pack failed verification for {file.Path}");
        }
      }
    }

    private sealed record PackFile(string Path, long Size, string Sha256);

    private static List<PackFile> ReadManifest(ZipArchive zip, string engine)
    {
      var entry = zip.GetEntry(ManifestName);
      if (entry is null)
      {
        return null;
      }

      try
      {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        return JsonSerializer.Deserialize<PackManifest>(text, ManifestOptions)?.Files;
      }
      catch (Exception ex)
      {
        Log.Warn($"Unable to read the {engine} pack manifest: {ex.Message}");
        return null;
      }
    }

    private sealed class PackManifest
    {
      public string Engine { get; set; }
      public string PackVersion { get; set; }
      public List<PackFile> Files { get; set; }
    }

    /*
     * Map the onnxruntime.dll that belongs with the managed wrapper before Piper gets the chance to load its own.
     *
     * Both engines ship that file and only one can be resident, because Windows keys modules by base name: whichever
     * loads first holds the name for the life of the process, and every later request for it is answered from memory.
     * So the winner has to be chosen deliberately rather than fall out of which engine the user spoke from first.
     *
     * The right winner is Kokoro's copy, because that one is published alongside the Microsoft.ML.OnnxRuntime wrapper
     * installed with the app and repacked whenever the wrapper version moves. Piper vendors its own build (1.14 today,
     * against a 1.22 wrapper) and it is the side that loses harmlessly: the ORT C API is versioned and old models keep
     * running on a newer runtime, while the older one refuses Kokoro's graphs outright -- "Unsupported model IR
     * version: 9" about a download that is perfectly fine. Loading a newer Piper build over a newer wrapper would be
     * just as wrong the other way round, which is why this matches rather than compares versions.
     */
    internal static void PreferMatchingOnnxRuntime()
    {
      if (ResolveRoot(AudioManager.KokoroEngine) is not string root) return;

      var path = Path.Combine(root, "native", OnnxRuntimeFileName);
      if (!File.Exists(path)) return;

      // An absolute path loads that file and lets its own dependencies come from the same folder.
      if (NativeLibrary.TryLoad(path, typeof(TtsPackManager).Assembly,
            DllImportSearchPath.UseDllDirectoryForDependencies, out _))
      {
        Log.Debug($"onnxruntime claimed for this process by {path}");
      }
      else
      {
        Log.Warn($"Unable to load the pack's onnxruntime from {path}; Piper may claim the name first");
      }
    }

    /*
     * Which onnxruntime.dll the process actually has mapped, with its version. Exists because a model rejected by an
     * older runtime reads as "the download is broken" unless something reports which dll answered.
     */
    internal static string DescribeLoadedOnnxRuntime()
    {
      try
      {
        using var current = Process.GetCurrentProcess();
        foreach (ProcessModule module in current.Modules)
        {
          if (string.Equals(module.ModuleName, OnnxRuntimeFileName, StringComparison.OrdinalIgnoreCase))
          {
            return $"{module.FileName} version {module.FileVersionInfo?.FileVersion}";
          }
        }
      }
      catch (Exception ex)
      {
        Log.Debug("Unable to inspect the loaded onnxruntime module", ex);
      }

      return "none loaded";
    }

    private static HttpClient CreateClient()
    {
      var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
      client.DefaultRequestHeaders.UserAgent.ParseAdd("EQLogParser");
      return client;
    }

    private static string InstalledDirectory(string engine)
    {
      if (!Packs.TryGetValue(engine, out var pack))
      {
        return null;
      }

      var dir = Path.Combine(StorageRoot, pack.FolderName);
      return HasExpectedFiles(dir, engine) ? dir : null;
    }

    /* The few files that prove a directory is usable, without reading anything expensive. */
    private static bool HasExpectedFiles(string dir, string engine)
    {
      try
      {
        // The marker records which release a directory came from; usability is decided by the files below, because a
        // half deleted or hand-edited pack should stop being offered rather than pretend to be fine.
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
          return false;
        }

        return engine switch
        {
          AudioManager.PiperEngine => File.Exists(Path.Combine(dir, "voices", "voices.json")) &&
                                      File.Exists(Path.Combine(dir, "piperApi.dll")),
          AudioManager.KokoroEngine => File.Exists(Path.Combine(dir, "model", KokoroTtsEngine.ModelFileName)) &&
                                       HasAnyVoiceEmbeddings(dir),
          _ => false
        };
      }
      catch (Exception ex)
      {
        Log.Debug($"Unable to inspect the {engine} runtime pack", ex);
        return false;
      }
    }

    private static bool HasAnyVoiceEmbeddings(string dir)
    {
      var voices = Path.Combine(dir, "voices");
      return Directory.Exists(voices) && Directory.GetFiles(voices, "*.npy").Length > 0;
    }

    /*
     * The model Kokoro used to fetch on its own, before the pack carried it. Reclaimable now that the pack holds an
     * identical copy; left alone if the pack is not installed, because that copy may still be what is speaking.
     */
    private static void RemoveLegacyKokoroModel()
    {
      if (!IsInstalled(AudioManager.KokoroEngine))
      {
        return;
      }

      DeleteTreeQuietly(Path.Combine(StorageRoot, "kokoro-tts"));
    }

    private static void ReplaceDirectory(string dir)
    {
      if (Directory.Exists(dir))
      {
        Directory.Delete(dir, true);
      }
    }

    private static string ComputeSha256(string path)
    {
      using var sha = SHA256.Create();
      using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
      return Convert.ToHexString(sha.ComputeHash(stream));
    }

    private static void TryWriteMarker(string dir, Pack pack)
    {
      try
      {
        File.WriteAllText(Path.Combine(dir, MarkerName), $"{pack.Tag} {pack.Sha256}");
      }
      catch (Exception ex)
      {
        Log.Debug("Unable to write the runtime pack marker", ex);
      }
    }

    private static void TryDeleteFile(string path)
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

    private static void DeleteTreeQuietly(string dir)
    {
      try
      {
        if (Directory.Exists(dir))
        {
          Directory.Delete(dir, true);
        }
      }
      catch (Exception ex)
      {
        Log.Debug($"Unable to remove {dir}", ex);
      }
    }

    // Returning null lets the normal loader rules carry on failing, so unrelated assemblies are untouched.
    private static Assembly ResolvePackAssembly(AssemblyLoadContext context, AssemblyName name)
    {
      if (name?.Name is not { Length: > 0 } simpleName ||
          InstalledDirectory(AudioManager.KokoroEngine) is not { } kokoro)
      {
        return null;
      }

      var candidate = Path.Combine(kokoro, "bin", simpleName + ".dll");
      if (!File.Exists(candidate))
      {
        return null;
      }

      try
      {
        return Assembly.LoadFrom(candidate);
      }
      catch (Exception ex)
      {
        Log.Debug($"Unable to load {simpleName} from the kokoro runtime pack", ex);
        return null;
      }
    }

    private static IntPtr ResolvePackNative(Assembly assembly, string libraryName)
    {
      if (string.IsNullOrEmpty(libraryName))
      {
        return IntPtr.Zero;
      }

      foreach (var dir in NativeSearchDirectories())
      {
        var candidate = Path.Combine(dir, libraryName);
        if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
        {
          return handle;
        }
      }

      return IntPtr.Zero;
    }
  }
}
