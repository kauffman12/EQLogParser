using EQLogParser.Audio;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

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
        long reported = 0L;

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
