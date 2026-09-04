using EQLogParser.Audio;
using System.Text.RegularExpressions;

namespace EQLogParser.Wpf.Test
{
  /*
   * Offline coverage for the speech runtime packs (docs/TtsPacks.md): engine-name normalization, the digest helper the
   * installers and the model marker both use, and the pin table against the documentation people publish from.
   *
   * Nothing here downloads, unpacks, or touches a real pack directory. Doing that would mean either a network or the
   * developer's own %LOCALAPPDATA%, and in both cases the test would fail for reasons that have nothing to do with the
   * code; the install paths are covered by the manual pass in docs/ReleaseChecklist.md instead.
   */
  [TestClass]
  public class TtsPackTest
  {
    private const string UnknownEngine = "NoSuchEngine";

    // The one native module name this app chooses for itself; see the ONNX Runtime selection region.
    private const string OnnxRuntimeFile = "onnxruntime.dll";

    // "abc" and the empty input, the two digests anybody verifies a hash implementation against by hand.
    private const string AbcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private static readonly Regex DigestPattern = new("[0-9a-fA-F]{64}", RegexOptions.Compiled);

    #region Engine names

    [TestMethod]
    public void Normalize_CaseVariants_ReturnsTheCanonicalName()
    {
      Assert.AreEqual(AudioManager.PiperEngine, TtsEngineFactory.Normalize("piper"));
      Assert.AreEqual(AudioManager.PiperEngine, TtsEngineFactory.Normalize("PIPER"));
      Assert.AreEqual(AudioManager.KokoroEngine, TtsEngineFactory.Normalize("kOkOrO"));
      Assert.AreEqual(AudioManager.WindowsEngine, TtsEngineFactory.Normalize("WINDOWS"));
    }

    /*
     * The setting is plain text and gets hand edited, so the exact spelling the app writes is one of three names by
     * construction; anything else has to stay itself. Create() reads an unknown name as "no preference" and the picker
     * reads it as "not available", which are both better answers than guessing at what was meant.
     */
    [TestMethod]
    public void Normalize_UnknownOrEmptyName_ReturnsItUnchanged()
    {
      Assert.AreEqual("espeak", TtsEngineFactory.Normalize("espeak"));
      Assert.IsNull(TtsEngineFactory.Normalize(null));
      Assert.AreEqual(string.Empty, TtsEngineFactory.Normalize(string.Empty));
    }

    #endregion

    #region Fallback order

    /*
     * A preference that cannot be honored falls through rather than failing, and the fall-through goes to the next
     * engine the machine actually has: a Piper whose pack was removed or will not load keeps getting an installed
     * Kokoro before the Windows voices. Without the Kokoro in the middle this user would skip straight to the last
     * resort even though they chose Piper knowing speech was expected.
     */
    [TestMethod]
    public void FallbackOrder_PiperPreference_TriesKokoroBeforeTheWindowsVoices()
    {
      var order = TtsEngineFactory.FallbackOrder(AudioManager.PiperEngine);

      Assert.AreEqual(AudioManager.PiperEngine, order[0]);
      CollectionAssert.Contains(order, AudioManager.KokoroEngine);
      Assert.IsTrue(Array.IndexOf(order, AudioManager.KokoroEngine) < Array.IndexOf(order, AudioManager.WindowsEngine),
        "A Piper preference must reach an installed Kokoro before the Windows voices.");
    }

    [TestMethod]
    public void FallbackOrder_NoOrUnrecognizedPreference_KeepsTheHistoricOrder()
    {
      var historic = new[] { AudioManager.PiperEngine, AudioManager.KokoroEngine, AudioManager.WindowsEngine };

      CollectionAssert.AreEqual(historic, TtsEngineFactory.FallbackOrder(null));
      CollectionAssert.AreEqual(historic, TtsEngineFactory.FallbackOrder(string.Empty));
      CollectionAssert.AreEqual(historic, TtsEngineFactory.FallbackOrder("espeak"));
    }

    [TestMethod]
    public void FallbackOrder_KokoroPreference_StartsWithKokoro()
    {
      Assert.AreEqual(AudioManager.KokoroEngine, TtsEngineFactory.FallbackOrder(AudioManager.KokoroEngine)[0]);
    }

    #endregion

    #region Digests

    [TestMethod]
    public void ComputeSha256_KnownInput_MatchesPublishedDigest()
    {
      var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");

      try
      {
        File.WriteAllText(path, "abc");
        Assert.IsTrue(string.Equals(AbcSha256, TtsPackManager.ComputeSha256(path),
          StringComparison.OrdinalIgnoreCase));

        File.WriteAllBytes(path, Array.Empty<byte>());
        Assert.IsTrue(string.Equals(EmptySha256, TtsPackManager.ComputeSha256(path),
          StringComparison.OrdinalIgnoreCase));
      }
      finally
      {
        File.Delete(path);
      }
    }

    /*
     * Cancelling has to land during the hashing, not only after it: an install hashes the archive and then every file
     * inside it, which is tens of seconds, and a Cancel button that waited for that reads as a hung dialog. The loop
     * also feeds the progress bar, so the byte count it reports is worth pinning.
     */
    [TestMethod]
    public void ComputeSha256_CalledWithCancelledToken_StopsInsteadOfHashing()
    {
      var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");

      try
      {
        File.WriteAllBytes(path, new byte[4096]);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(
          () => TtsPackManager.ComputeSha256(path, null, cancelled.Token));
      }
      finally
      {
        File.Delete(path);
      }
    }

    [TestMethod]
    public void ComputeSha256_ReportsEveryByteItReads()
    {
      var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");

      try
      {
        File.WriteAllBytes(path, new byte[10000]);
        var reported = 0L;

        var digest = TtsPackManager.ComputeSha256(path, count => Interlocked.Add(ref reported, count));

        Assert.AreEqual(10000L, reported);
        Assert.AreEqual(64, digest.Length);
      }
      finally
      {
        File.Delete(path);
      }
    }

    #endregion

    #region Pack table

    [TestMethod]
    public void GetDownloadBytes_KnownEnginesHaveAnEstimatedSizeForTheButton()
    {
      Assert.IsTrue(TtsPackManager.GetDownloadBytes(AudioManager.PiperEngine) > 0L);
      Assert.IsTrue(TtsPackManager.GetDownloadBytes(AudioManager.KokoroEngine) > 0L);
    }

    [TestMethod]
    public void PackHelpers_ForAnEngineWithNoPack_SaySoWithoutTouchingDisk()
    {
      Assert.AreEqual(0L, TtsPackManager.GetDownloadBytes(UnknownEngine));
      Assert.IsFalse(TtsPackManager.IsPackOnDisk(UnknownEngine));
      Assert.IsFalse(TtsPackManager.Remove(UnknownEngine));
    }

    /*
     * An engine with no entry in the pack table must fail here rather than reach the network with a null pack; this is
     * the guard that keeps a typo in the picker from turning into an unhandled exception on a UI thread.
     */
    [TestMethod]
    public async Task InstallAsync_EngineWithNoPack_FailsWithoutDownloading()
    {
      Assert.IsFalse(await TtsPackManager.InstallAsync(UnknownEngine, null, CancellationToken.None));
    }

    #endregion

    #region ONNX Runtime selection

    /*
     * One onnxruntime.dll can be resident per process, so the candidate order decides which one a session gets and it
     * decides silently: Kokoro's copy leads because it is published together with the managed wrapper installed beside
     * the executable, and anything without a runtime in it has to be stepped over rather than trusted. A directory that
     * holds KokoroSharp but no runtime is a real entry in that list (`<kokoro>\bin`), which is what the first assertion
     * is for. See docs/DesignNotes.md -> Which onnxruntime.dll wins.
     */
    [TestMethod]
    public void FirstDirectoryWithOnnxRuntime_PicksTheFirstCandidateCarryingARuntime()
    {
      var root = Path.Combine(Path.GetTempPath(), $"eqlp-onnx-{Guid.NewGuid():N}");

      try
      {
        var kokoroBin = Directory.CreateDirectory(Path.Combine(root, "kokoro", "bin")).FullName;
        var kokoroNative = Directory.CreateDirectory(Path.Combine(root, "kokoro", "native")).FullName;
        var piper = Directory.CreateDirectory(Path.Combine(root, "piper-tts")).FullName;
        var candidates = new[] { kokoroBin, kokoroNative, piper };

        File.WriteAllText(Path.Combine(kokoroBin, "MisakiSharp.dll"), string.Empty);
        File.WriteAllText(Path.Combine(piper, OnnxRuntimeFile), string.Empty);

        Assert.AreEqual(piper, TtsPackManager.FirstDirectoryWithOnnxRuntime(candidates));

        // Both packs in step is the normal state, and then Kokoro's copy has to be the one that wins.
        File.WriteAllText(Path.Combine(kokoroNative, OnnxRuntimeFile), string.Empty);
        Assert.AreEqual(kokoroNative, TtsPackManager.FirstDirectoryWithOnnxRuntime(candidates),
          "Kokoro's copy must claim the name: it is published with the ONNX wrapper installed beside the executable.");

        Assert.IsNull(TtsPackManager.FirstDirectoryWithOnnxRuntime([kokoroBin]));
        Assert.IsNull(TtsPackManager.FirstDirectoryWithOnnxRuntime(Array.Empty<string>()));
      }
      finally
      {
        Directory.Delete(root, true);
      }
    }

    /*
     * Whether the runtime in use came from EQLP is the difference between "reinstall Kokoro" and "another program's
     * 2021 onnxruntime.dll is answering your imports", so the check has to name a foreign path as foreign -- a copy in
     * System32 above all, since that is the one found in the field. Directory prefixes compare with a trailing
     * separator, which is what stops an "EQLogParser-old" sibling from counting as ours.
     */
    [TestMethod]
    public void IsOwnedNativePath_PacksAndProgramFolderAreOursEverythingElseIsNot()
    {
      var root = Path.Combine(Path.GetTempPath(), $"eqlp-owned-{Guid.NewGuid():N}");
      var packs = Path.Combine(root, "EQLogParser");
      var program = Path.Combine(root, "Program Files", "EQLogParser");
      var owned = new[] { packs, program };

      try
      {
        Assert.IsTrue(TtsPackManager.IsOwnedNativePath(
          Path.Combine(packs, "kokoro", "native", OnnxRuntimeFile), owned));
        Assert.IsTrue(TtsPackManager.IsOwnedNativePath(
          Path.Combine(program, "runtimes", "win-x64", "native", OnnxRuntimeFile), owned),
          "A development run resolves the NuGet runtime through EQLogParser.deps.json and is still EQLP's own.");

        Assert.IsFalse(TtsPackManager.IsOwnedNativePath(
          Path.Combine(Path.GetPathRoot(Path.GetFullPath(root)) ?? Path.DirectorySeparatorChar.ToString(),
            "Windows", "System32", OnnxRuntimeFile), owned));
        Assert.IsFalse(TtsPackManager.IsOwnedNativePath(
          Path.Combine(root, "EQLogParser-old", OnnxRuntimeFile), owned));
        Assert.IsFalse(TtsPackManager.IsOwnedNativePath(null, owned));
        Assert.IsFalse(TtsPackManager.IsOwnedNativePath(string.Empty, owned));
      }
      finally
      {
        // Nothing was created here; this only removes the root should that ever change.
        if (Directory.Exists(root))
        {
          Directory.Delete(root, true);
        }
      }
    }

    /*
     * The resolver pins one module name and nothing else: `onnxruntime_providers_shared.dll` is loaded by ONNX Runtime
     * itself from beside its own module, and piperApi.dll has a resolver of its own in PiperTtsEngine. Taking either of
     * those over would mean answering for imports whose location this code does not decide.
     */
    [TestMethod]
    public void IsOnnxRuntimeLibrary_OnlyTheRuntimeItselfIsOursToChoose()
    {
      Assert.IsTrue(TtsPackManager.IsOnnxRuntimeLibrary("onnxruntime"));
      Assert.IsTrue(TtsPackManager.IsOnnxRuntimeLibrary(OnnxRuntimeFile));
      Assert.IsTrue(TtsPackManager.IsOnnxRuntimeLibrary("ONNXRUNTIME.DLL"));

      Assert.IsFalse(TtsPackManager.IsOnnxRuntimeLibrary("onnxruntime_providers_shared.dll"));
      Assert.IsFalse(TtsPackManager.IsOnnxRuntimeLibrary(PiperTtsEngine.PiperApiLibrary));
      Assert.IsFalse(TtsPackManager.IsOnnxRuntimeLibrary(null));
      Assert.IsFalse(TtsPackManager.IsOnnxRuntimeLibrary(string.Empty));
    }

    #endregion

    #region Publish documentation

    /*
     * The digest pinned in TtsPackManager and the one in docs/TtsPacks.md have to move together: someone publishing a
     * rebuilt pack reads the table in the docs, and an app that still pins the old bytes will reject it with a
     * checksum error that points at the download rather than at the pin. Both directions are checked because either
     * half can be updated alone.
     *
     * This reads source text rather than the table itself because the pack records are private to the manager, and
     * making them visible so a test can read them would be the worse trade. 64 hex characters is the whole signature
     * of a SHA-256, and nothing else in either file looks like one.
     */
    [TestMethod]
    public void PinnedPackDigests_AreTheOnesTheDocumentationPublishes()
    {
      var root = FindRepositoryRoot();

      if (root is null)
      {
        Assert.Inconclusive("This test needs the source tree, not just the compiled assemblies.");
      }

      var code = ReadRequired(Path.Combine(root, "EQLogParser.Audio", "src", "TtsPackManager.cs"));
      var docs = ReadRequired(Path.Combine(root, "docs", "TtsPacks.md"));
      var pinned = Digests(code);
      var documented = Digests(docs);

      Assert.IsTrue(pinned.Count > 0, "No pinned pack digests found in TtsPackManager.cs.");
      Assert.AreEqual(documented.Count, pinned.Count,
        "The pack digests in the code and in docs/TtsPacks.md are not the same set.");

      foreach (var digest in pinned)
      {
        Assert.IsTrue(documented.Contains(digest), $"{digest} is pinned in code but published nowhere.");
      }
    }

    /*
     * The download button wording comes from the byte count in the pack table, and the docs quote the same numbers.
     * A pack that grew without the docs being re-read tells people one thing and downloads another.
     */
    [TestMethod]
    public void PackSizes_QuotedInTheDocumentationMatchTheTable()
    {
      var root = FindRepositoryRoot();

      if (root is null)
      {
        Assert.Inconclusive("This test needs the source tree, not just the compiled assemblies.");
      }

      var docs = ReadRequired(Path.Combine(root, "docs", "TtsPacks.md"));

      foreach (var engine in new[] { AudioManager.PiperEngine, AudioManager.KokoroEngine })
      {
        var megabytes = TtsPackManager.GetDownloadBytes(engine) / (1024 * 1024);
        Assert.IsTrue(docs.Contains($"{megabytes} MB", StringComparison.OrdinalIgnoreCase),
          $"docs/TtsPacks.md never mentions the {megabytes} MB the {engine} download actually is.");
      }
    }

    #endregion

    private static HashSet<string> Digests(string text) =>
      DigestPattern.Matches(text).Select(match => match.Value.ToLowerInvariant())
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string ReadRequired(string path) =>
      File.Exists(path) ? File.ReadAllText(path) : throw new FileNotFoundException(path);

    /* Test binaries live several directories below the checkout; walk up until the solution file says otherwise. */
    private static string? FindRepositoryRoot()
    {
      for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
      {
        if (File.Exists(Path.Combine(dir.FullName, "EQLogParser.sln")))
        {
          return dir.FullName;
        }
      }

      return null;
    }
  }
}
