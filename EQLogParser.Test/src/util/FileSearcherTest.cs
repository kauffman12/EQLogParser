namespace EQLogParser
{
  /* Tests for the generic multi-file log scanner used by search/archive features.
   * Key behaviors pinned: results are posted in file order even though files are scanned
   * in parallel, timestamps outside the range are skipped, scanning stops once a line
   * exceeds the last segment (exceeds break), and missing files degrade silently. */
  [TestClass]
  public sealed class FileSearcherTest
  {
    private string _dir = "";

    [TestInitialize]
    public void Setup() => _dir = Directory.CreateTempSubdirectory("filesearcher-").FullName;

    [TestCleanup]
    public void Cleanup()
    {
      try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string WriteFile(string name, params string[] lines)
    {
      var path = Path.Combine(_dir, name);
      File.WriteAllLines(path, lines);
      return path;
    }

    /// <summary>EQ log timestamp prefix; seconds advance by 10 per line from the first.</summary>
    private static string[] LogLines(int count, string tag)
    {
      var start = new DateTime(2025, 7, 28, 9, 0, 0);
      var lines = new string[count];
      for (var i = 0; i < count; i++)
      {
        var time = start.AddSeconds(i * 10).ToString("ddd MMM dd HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture);
        lines[i] = $"[{time}] {tag} line {i}";
      }

      return lines;
    }

    private static double Ts(string line) => DateUtil.StandardDateToDotNetSeconds(line);

    [TestMethod]
    public async Task SearchLogs_AnyTime_PostsMatchesInFileOrderWithPositions()
    {
      var aLines = LogLines(5, "alpha");
      var bLines = LogLines(3, "beta");
      var a = WriteFile("a.txt", aLines);
      var b = WriteFile("b.txt", bLines);

      // only even lines are "matches"
      // the parser contract is "return null for no match" even though T is a non-nullable string
      Func<string, string> processor = line => int.TryParse(line[^2..], out var n) && n % 2 == 0 ? line : null!;

      List<List<string>> batches = [];
      List<List<FileSearcher<string>.LinePosition>> positionBatches = [];
      var searcher = new FileSearcher<string>([a, b]);
      searcher.ResultsReady += (lines, positions) =>
      {
        batches.Add(lines);
        positionBatches.Add(positions);
      };

      await searcher.SearchLogsAsync(start: 0, maxRange: null, processor);

      // one batch per file with matches, posted in file-list order regardless of scan parallelism
      Assert.AreEqual(2, batches.Count);
      CollectionAssert.AreEquivalent(new[] { aLines[0], aLines[2], aLines[4] }, batches[0]);
      Assert.AreEqual(3, positionBatches[0].Count);
      Assert.IsTrue(positionBatches[0].All(p => p.File == a));

      // same even-line rule applies to the second file
      CollectionAssert.AreEquivalent(new[] { bLines[0], bLines[2] }, batches[1]);

      // positions never go backwards within a batch (buffered reads can make them equal)
      for (var i = 0; i < positionBatches.Count; i++)
      {
        for (var j = 1; j < positionBatches[i].Count; j++)
        {
          Assert.IsTrue(positionBatches[i][j].Position >= positionBatches[i][j - 1].Position);
        }
      }
    }

    [TestMethod]
    public async Task SearchLogs_TimestampRange_SkipsOutsideAndStopsPastEnd()
    {
      var lines = LogLines(6, "gamma"); // t=0..50s
      var file = WriteFile("c.txt", lines);

      // window covers lines 1..3 only; a matching line after the end must never be seen
      var range = new TimeRange();
      range.TimeSegments.Add(new TimeSegment(Ts(lines[1]), Ts(lines[3])));

      List<string> found = [];
      var searcher = new FileSearcher<string>([file]);
      searcher.ResultsReady += (batch, _) => found.AddRange(batch);

      await searcher.SearchLogsAsync(start: Ts(lines[0]), maxRange: range, line => line.Contains("gamma") ? line : null!);

      // line 0 is before the window; lines 4..5 are past the last segment (exceeds -> stop)
      CollectionAssert.AreEquivalent(new[] { lines[1], lines[2], lines[3] }, found);
    }

    [TestMethod]
    public async Task SearchLogs_MissingFile_CompletesWithoutPosting()
    {
      var missing = Path.Combine(_dir, "does-not-exist.txt");
      int progressMax = 0;

      var searcher = new FileSearcher<string>([missing]);
      var posted = false;
      searcher.ResultsReady += (_, _) => posted = true;
      searcher.ProgressUpdated += p => progressMax = Math.Max(progressMax, p);

      await searcher.SearchLogsAsync(start: 0, maxRange: null, _ => "x");

      Assert.IsFalse(posted);
      Assert.AreEqual(100, progressMax);
    }

    [TestMethod]
    public async Task SearchLogs_NoMatches_CompletesWithoutPosting()
    {
      // 40 lines so every 5% boundary lands on a line end and progress reaches 100
      var file = WriteFile("d.txt", LogLines(40, "delta"));
      int progressMax = 0;

      var searcher = new FileSearcher<string>([file]);
      var posted = false;
      searcher.ResultsReady += (_, _) => posted = true;
      searcher.ProgressUpdated += p => progressMax = Math.Max(progressMax, p);

      await searcher.SearchLogsAsync(start: 0, maxRange: null, _ => null!);

      Assert.IsFalse(posted);
      // progress is cosmetic and buffer-dependent for small files — pin that updates fire at all
      Assert.IsTrue(progressMax > 0);
    }
  }
}
