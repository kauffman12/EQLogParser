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

    // One buffer for every bulk read and write in an install: hashing, unpacking and the download all move whole
    // megabytes, and 4MB is what makes per-chunk cancellation and progress cheap enough to bother with.
    private const int BulkBufferSize = 1 << 22;

    /*
     * How the progress bar is earned. Nine tenths is bytes off the network; the last tenth is split between reading
     * them back (the archive digest), unpacking them, and checking every extracted file against the manifest. Without
     * the split the bar sits still for the better part of a minute at the end of a download and looks like a job that
     * stopped answering.
     */
    private const float VerifyStart = 0.9f;
    private const float DigestEnd = 0.93f;
    private const float ExtractEnd = 0.97f;

    private sealed record Pack(string Engine, string Tag, string AssetName, string Sha256, string FolderName,
      long DownloadBytes);

    /*
     * Bumping a pack means publishing the new tag and changing Tag and Sha256 together; old releases are never
     * overwritten once an app build that pins them exists, so an installed app keeps pointing at bytes that will still
     * exist. Both packs below have been replaced under their own tag anyway - onnxruntime aligned between them first,
     * then more voices in each - which is defensible only because nothing shipped can pin what it never had: master
     * carries no pack code at all, so the sole builds that ever fetched these assets are builds of this branch. Neither
     * replacement touched a binary: a signature covers the bytes of one PE file rather than the archive around it, so
     * adding voices and regenerating manifest.json leaves every DLL inside exactly as signed. What does change is the
     * digest pinned here, which has to go up with the asset rather than ahead of it.
     */
    private static readonly Dictionary<string, Pack> Packs = new(StringComparer.OrdinalIgnoreCase)
    {
      [AudioManager.PiperEngine] = new Pack(AudioManager.PiperEngine, "piper-1.0", "piper-1.0.zip",
        "4d51eb9e821252aecc14024b5cb5ffc2d21e9d2cd242f9870a7ea8c52868e311", "piper-tts", 682L * 1024 * 1024),
      [AudioManager.KokoroEngine] = new Pack(AudioManager.KokoroEngine, "kokoro-1.0", "kokoro-1.0.zip",
        "6f4728392c15fe6da8cbb115ed3a227c334f426e12598bd4482e9863abc48783", "kokoro", 228L * 1024 * 1024)
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
     * lists -- see PreferMatchingOnnxRuntime. Kokoro's copy is first because it is the one published against the
     * managed wrapper installed with the app.
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

      // Before any runtime is mapped: this is the one moment in a session when files an earlier removal could not
      // delete are certain to be releasable.
      SweepParkedRemovals();
    }

    /* Approximate archive size, for "Download Piper (682 MB)" style button wording. */
    internal static long GetDownloadBytes(string engine) =>
      Packs.TryGetValue(engine, out var pack) ? pack.DownloadBytes : 0;

    /*
     * Downloads and installs an engine's pack. progress is 0..1 across the whole job: the archive transfer earns most
     * of it and verification the rest, so a stalled download never looks finished. Returns false without touching an
     * existing installation when anything fails.
     */
    internal static async Task<bool> InstallAsync(string engine, IProgress<float> progress,
      CancellationToken cancellationToken)
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

      // Refuses only the case that cannot possibly work, before spending anybody's bandwidth on it: room for the
      // archive and nothing more. Unpacking gets its own check further down against the real extracted size, because
      // guessing a compression ratio here would turn away machines that manage fine.
      if (!HasRoom(pack.Engine, pack.DownloadBytes, "download"))
      {
        return false;
      }

      var gated = false;

      try
      {
        // Inside the try: waiting here can be cancelled by whoever pressed the button a second time, and that has to
        // read as a failed install rather than as an unhandled exception on a UI handler. Only a wait that succeeded
        // may release the gate below; releasing one that was never taken would let two installs run at once.
        await _installGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        gated = true;

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
        var digest = new StageProgress(progress, VerifyStart, DigestEnd);
        digest.Begin(new FileInfo(tempArchive).Length);
        var downloaded = ComputeSha256(tempArchive, digest.Advance, cancellationToken);
        digest.Finish();
        if (!string.Equals(downloaded, pack.Sha256, StringComparison.OrdinalIgnoreCase))
        {
          Log.Error($@"{engine} runtime pack checksum mismatch; the download was discarded.
  {pack.AssetName} from tag {pack.Tag}: pinned {pack.Sha256}, downloaded {downloaded}.
  If the asset was rebuilt rather than corrupted, change this pin and the table in docs/TtsPacks.md together.");
          return false;
        }

        // Room for the tree that is about to come out of it. The archive's own central directory carries the
        // uncompressed size of every entry, so this is the real number rather than a guessed ratio, and "the disk is
        // full" said here beats an IOException halfway through a few hundred megabytes of extracted files.
        if (!HasRoom(pack.Engine, UncompressedBytes(tempArchive, pack.Engine), "unpack"))
        {
          return false;
        }

        CleanDirectory(staging);
        ExtractAndVerify(tempArchive, staging, engine, progress, cancellationToken);

        // The old copy is moved aside rather than deleted first, so a failure above leaves the working pack alone.
        CleanDirectory(retired);
        if (Directory.Exists(target))
        {
          Directory.Move(target, retired);
        }

        try
        {
          Directory.Move(staging, target);
        }
        catch (Exception ex)
        {
          /*
           * The new copy could not be put in place and the old one is parked under another name, which would leave the
           * engine that worked this morning unable to start. Put it back before anything else: the retired directory
           * keeps its own copy of every file, so restoring is a rename and not a re-download.
           */
          Log.Error($"Unable to install the {engine} runtime pack; putting the previous copy back", ex);

          if (!Directory.Exists(target) && Directory.Exists(retired))
          {
            Directory.Move(retired, target);
          }

          return false;
        }

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
        // Only this method's own waiter may leave the gate, and only after the staging directory and the half read
        // archive are gone: the next install would otherwise find them in the way.
        DeleteTreeQuietly(staging);
        TryDeleteFile(tempArchive);

        if (gated)
        {
          try
          {
            _installGate.Release();
          }
          catch (ObjectDisposedException)
          {
            // the app is closing; nobody is waiting
          }
        }
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

        /*
         * Deleting a pack file by file is not one gesture. Recursive delete walks the tree and throws at the first file
         * this process still has mapped - a runtime that spoke earlier in this session keeps its libraries loaded until
         * EQLogParser closes - so it takes some of a runtime with it and leaves the rest behind. That leftover is the
         * worst state available: too broken to speak with, not gone either, and the dialog has nothing honest to say
         * about a directory that is half a product.
         *
         * Moving the directory aside first is one operation on one object. If the move succeeds, whatever refuses to
         * delete after it is unreachable and gets reclaimed the next time the app starts; if the move fails, nothing
         * was touched and the pack is exactly as complete as it was.
         */
        var parked = ParkDirectory(target);
        DeleteTreeQuietly(parked);

        // Anything else parked next to it is worth reclaiming in the same gesture: an interrupted install leaves a
        // .staging or .retired directory behind and this is the one place somebody asks for the space back.
        DeleteTreeQuietly(target + ".staging");
        DeleteTreeQuietly(target + ".retired");

        Log.Info($"{engine} runtime pack removed from {target}");
        return true;
      }
      catch (Exception ex)
      {
        Log.Error($"Error removing the {engine} runtime pack", ex);
        return false;
      }
    }

    private static async Task<bool> DownloadAsync(Pack pack, string tempArchive, IProgress<float> progress,
      CancellationToken cancellationToken)
    {
      var url = $"https://github.com/{ReleaseRepo}/releases/download/{pack.Tag}/{pack.AssetName}";
      using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
        .ConfigureAwait(false);
      if (!response.IsSuccessStatusCode)
      {
        Log.Error($"Unable to fetch the {pack.Engine} runtime pack: " +
          $"{(int)response.StatusCode} {response.ReasonPhrase}");
        return false;
      }

      var total = response.Content.Headers.ContentLength ?? 0L;
      await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
      await using var destination = new FileStream(tempArchive, FileMode.Create, FileAccess.Write, FileShare.None,
        BulkBufferSize);

      var buffer = new byte[BulkBufferSize];
      long read = 0;
      int got;
      while ((got = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
      {
        await destination.WriteAsync(buffer.AsMemory(0, got), cancellationToken).ConfigureAwait(false);
        read += got;
        if (total > 0)
        {
          // Leave headroom in the bar for verification so it never sits at 100% while still working.
          progress?.Report(Math.Clamp((float)read / total * VerifyStart, 0f, VerifyStart));
        }
      }

      return true;
    }

    /*
     * Extracts into a private directory and checks every file the manifest lists. Zip entries are treated as
     * untrusted paths: an entry that tries to climb out of the target directory aborts the install outright.
     */
    private static void ExtractAndVerify(string archive, string target, string engine, IProgress<float> progress,
      CancellationToken cancellationToken)
    {
      Directory.CreateDirectory(target);
      using var zip = ZipFile.OpenRead(archive);

      var manifest = ReadManifest(zip, engine);
      var rootFull = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
      var unpacking = new StageProgress(progress, DigestEnd, ExtractEnd);
      unpacking.Begin(UncompressedBytes(zip));
      var buffer = new byte[BulkBufferSize];

      foreach (var entry in zip.Entries)
      {
        // Cancelling a download is only honest if it also stops the unpacking: an archive this size takes long enough
        // that somebody changes their mind during it. The finally in InstallAsync clears the half written directory.
        cancellationToken.ThrowIfCancellationRequested();

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

        // Copied by hand instead of ExtractToFile: that reports no bytes and notices a cancellation only once the
        // entry is complete, and one entry here is a 156MB model.
        using var source = entry.Open();
        using var sink = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, BulkBufferSize);
        CopyStream(source, sink, buffer, unpacking.Advance, cancellationToken);
      }

      unpacking.Finish();

      if (manifest is null)
      {
        Log.Warn($"{engine} pack has no {ManifestName}; files were installed without per file verification");
        return;
      }

      var checking = new StageProgress(progress, ExtractEnd, 1f);
      checking.Begin(TotalManifestBytes(manifest));

      foreach (var file in manifest)
      {
        cancellationToken.ThrowIfCancellationRequested();

        var path = Path.GetFullPath(Path.Combine(target, file.Path));
        if (!path.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
          throw new InvalidDataException($"{engine} pack manifest names an unsafe path: {file.Path}");
        }

        if (!File.Exists(path) || new FileInfo(path).Length != file.Size ||
            !string.Equals(ComputeSha256(path, checking.Advance, cancellationToken), file.Sha256,
              StringComparison.OrdinalIgnoreCase))
        {
          throw new InvalidDataException($"{engine} pack failed verification for {file.Path}");
        }
      }

      checking.Finish();
    }

    private static long TotalManifestBytes(List<PackFile> manifest)
    {
      if (manifest is null)
      {
        return 0L;
      }

      long total = 0L;
      foreach (var file in manifest)
      {
        total += Math.Max(0L, file.Size);
      }

      return total;
    }

    /*
     * One stage of the tail of an install: bytes moved in, a slice of the progress bar out. Reports per chunk, which
     * at this buffer size is a few dozen reports for a whole pack.
     */
    private sealed class StageProgress
    {
      private readonly IProgress<float> _progress;
      private readonly float _from;
      private readonly float _to;
      private long _total = 1L;
      private long _done;

      internal StageProgress(IProgress<float> progress, float from, float to)
      {
        _progress = progress;
        _from = from;
        _to = to;
      }

      internal void Begin(long totalBytes)
      {
        _total = Math.Max(1L, totalBytes);
        _done = 0L;
      }

      internal void Advance(long bytes)
      {
        _done += bytes;
        Report((float)(_done / (double)_total));
      }

      internal void Finish() => Report(1f);

      private void Report(float fraction) =>
        _progress?.Report(_from + (_to - _from) * Math.Clamp(fraction, 0f, 1f));
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
      if (InstalledDirectory(AudioManager.KokoroEngine) is null)
      {
        return;
      }

      DeleteTreeQuietly(Path.Combine(StorageRoot, "kokoro-tts"));
    }

    /*
     * Whether the volume holding local app data has room for another `bytes`. Anything unknown - a drive that cannot
     * be queried, a path without a root, a size nobody could measure - is treated as room enough, because this exists
     * to refuse a job that cannot finish and not to second guess a disk it cannot read. A disk that fills up anyway
     * reports the real number by itself.
     */
    private static bool HasRoom(string engine, long bytes, string stage)
    {
      if (bytes <= 0)
      {
        return true;
      }

      try
      {
        var root = Path.GetPathRoot(StorageRoot);
        if (string.IsNullOrEmpty(root))
        {
          return true;
        }

        var drive = new DriveInfo(root);
        if (drive.IsReady && drive.AvailableFreeSpace < bytes)
        {
          Log.Error($"Not enough free space on {root} to {stage} the {engine} runtime pack: " +
            $"{bytes / (1024 * 1024)} MB needed, {drive.AvailableFreeSpace / (1024 * 1024)} MB free.");
          return false;
        }
      }
      catch (Exception ex)
      {
        Log.Debug("Unable to check the free space for a runtime pack", ex);
      }

      return true;
    }

    /* What this archive will occupy once unpacked, straight from its central directory. 0 means unknown. */
    private static long UncompressedBytes(string archive, string engine)
    {
      try
      {
        using var zip = ZipFile.OpenRead(archive);
        return UncompressedBytes(zip);
      }
      catch (Exception ex)
      {
        Log.Debug($"Unable to measure the {engine} pack archive", ex);
        return 0L;
      }
    }

    private static long UncompressedBytes(ZipArchive zip)
    {
      long total = 0L;
      foreach (var entry in zip.Entries)
      {
        if (!string.IsNullOrEmpty(entry.Name))
        {
          total += Math.Max(0L, entry.Length);
        }
      }

      return total;
    }

    /*
     * Hashes a file the way the pack manifests do: streamed, so a 156MB model costs memory nothing. The chunk loop is
     * what makes cancellation and progress possible during it; hashing an archive and then every file in it runs to
     * tens of seconds, and a Cancel button that had to wait for that would look broken.
     */
    internal static string ComputeSha256(string path, Action<long> onBytes = null,
      CancellationToken cancellationToken = default)
    {
      using var sha = SHA256.Create();
      using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BulkBufferSize);

      var buffer = new byte[BulkBufferSize];
      int read;
      while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
      {
        sha.TransformBlock(buffer, 0, read, null, 0);
        onBytes?.Invoke(read);
        cancellationToken.ThrowIfCancellationRequested();
      }

      sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
      return Convert.ToHexString(sha.Hash ?? Array.Empty<byte>());
    }

    private static void CopyStream(Stream source, FileStream destination, byte[] buffer, Action<long> onBytes,
      CancellationToken cancellationToken)
    {
      int got;
      while ((got = source.Read(buffer, 0, buffer.Length)) > 0)
      {
        destination.Write(buffer, 0, got);
        onBytes?.Invoke(got);
        cancellationToken.ThrowIfCancellationRequested();
      }
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

    /*
     * Removes a directory that must not be there, letting anything go wrong surface. Used for the staging directory
     * before unpacking into it: half a tree left behind from an interrupted install is a reason to stop, not something
     * to extract on top of.
     */
    private static void CleanDirectory(string dir)
    {
      if (Directory.Exists(dir))
      {
        Directory.Delete(dir, true);
      }
    }

    // The same, for directories whose disappearance is only worth having: parking the replaced pack, or a leftover the
    // app no longer needs. Failing at those must not fail the job that was actually asked for.
    /*
     * Somewhere to move a pack on its way out. Timestamped after the first attempt because a directory parked by an
     * earlier removal may still be holding files nobody will release until the app closes, and two removals of one
     * engine must not be stacked on top of each other.
     */
    private static string ParkDirectory(string target)
    {
      var parked = $"{target}.removing";

      if (Directory.Exists(parked))
      {
        parked = $"{target}.removing-{DateTime.Now:yyyyMMdd-HHmmss}";
      }

      Directory.Move(target, parked);
      return parked;
    }

    /* Space reclaimed late is still space reclaimed: what an earlier session could not delete goes first chance. */
    private static void SweepParkedRemovals()
    {
      try
      {
        if (!Directory.Exists(StorageRoot))
        {
          return;
        }

        foreach (var dir in Directory.GetDirectories(StorageRoot, "*.removing*", SearchOption.TopDirectoryOnly))
        {
          DeleteTreeQuietly(dir);
        }
      }
      catch (Exception ex)
      {
        Log.Debug("Unable to look for pack directories left behind by an earlier removal", ex);
      }
    }

    private static void DeleteTreeQuietly(string dir)
    {
      try
      {
        CleanDirectory(dir);
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
