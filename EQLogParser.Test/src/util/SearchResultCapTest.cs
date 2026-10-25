using System.Collections.Generic;

using EQLogParser;

namespace EQLogParserTest;

/*
 * The log viewer keeps matched lines and each line's file position in two lists filled together, and caps the pair at
 * MaxRows when a search is large enough to hit it. Before this helper existed the cap branch did `Clear()` and then
 * refilled from the list it had just cleared, so any query reaching the cap came back showing nothing — measured
 * reachable on a real capture, where one common word matches 814,082 of 3,573,153 lines and an unwatermarked "Any Time"
 * search returns every line in range.
 *
 * The retained set is the first maxRows entries of the CURRENT accumulator, not the first deliveries: prepended archive
 * batches can displace current-log results. The two lists stay index-paired because the filter pass reads
 * `_linePositions[lineCount]` beside the line it matched.
 */
[TestClass]
public class SearchResultCapTest
{
  [TestMethod]
  public void BelowTheCapNothingIsTouched()
  {
    var (lines, positions) = Deliver(3);

    SearchResultCap.TrimToCap(lines, positions, 4);

    Assert.AreEqual(3, lines.Count);
    Assert.AreEqual(3, positions.Count);
    Assert.AreEqual("line0", lines[0]);
  }

  [TestMethod]
  public void ExactlyAtTheCapKeepsEveryResult()
  {
    var (lines, positions) = Deliver(4);

    SearchResultCap.TrimToCap(lines, positions, 4);

    Assert.AreEqual(4, lines.Count);
    CollectionAssert.AreEqual(new[] { "line0", "line1", "line2", "line3" }, lines);
  }

  [TestMethod]
  public void OverTheCapKeepsTheFirstResultsInOrder()
  {
    var (lines, positions) = Deliver(5);

    SearchResultCap.TrimToCap(lines, positions, 4);

    Assert.AreEqual(4, lines.Count);
    CollectionAssert.AreEqual(new[] { "line0", "line1", "line2", "line3" }, lines);
  }

  /*
   * The pairing is the silent half of the bug: `_filteredLinePositions` is built by index against `_unFiltered`, and
   * "show me this line in the log" opens whatever position sits at that index. Trimming one list alone — or trimming
   * them by different amounts — would silently open the wrong file offset.
   */
  [TestMethod]
  public void ThePositionListIsTrimmedToTheSameEntriesAsTheLineList()
  {
    var lines = new List<string>();
    var positions = new List<FileSearcher<string>.LinePosition>();

    foreach (var batch in new[] { 3, 3, 3 })
    {
      var (batchLines, batchPositions) = Deliver(batch, lines.Count);
      lines.AddRange(batchLines);
      positions.AddRange(batchPositions);

      SearchResultCap.TrimToCap(lines, positions, 5);

      Assert.AreEqual(lines.Count, positions.Count, $"after a batch of {batch}");
    }

    Assert.AreEqual(5, lines.Count);
    for (var i = 0; i < lines.Count; i++)
    {
      Assert.AreEqual($"line{i}", lines[i]);
      Assert.AreEqual(i, positions[i].Position, $"position paired with {lines[i]}");
    }
  }

  /*
   * Cancelling the search does not un-post the file already in flight, so a delivery can still arrive after the cap
   * tripped. The cap has to hold on that delivery too rather than grow once per remaining file.
   */
  [TestMethod]
  public void LatePrependedArchivesDisplaceTheTailAndKeepTheirOwnSourcePositions()
  {
    var (lines, positions) = Deliver(6, file: "current.txt");
    SearchResultCap.TrimToCap(lines, positions, 4);
    CollectionAssert.AreEqual(new[] { "line0", "line1", "line2", "line3" }, lines);

    var (archiveLines, archivePositions) = Deliver(3, 100, "archive.txt");
    lines.InsertRange(0, archiveLines);
    positions.InsertRange(0, archivePositions);
    SearchResultCap.TrimToCap(lines, positions, 4);
    CollectionAssert.AreEqual(new[] { "line100", "line101", "line102", "line0" }, lines);
    CollectionAssert.AreEqual(new long[] { 100, 101, 102, 0 }, positions.Select(p => p.Position).ToArray());
    CollectionAssert.AreEqual(new[] { "archive.txt", "archive.txt", "archive.txt", "current.txt" },
      positions.Select(p => p.File).ToArray());

    var (olderLines, olderPositions) = Deliver(2, 200, "older.txt");
    lines.InsertRange(0, olderLines);
    positions.InsertRange(0, olderPositions);
    SearchResultCap.TrimToCap(lines, positions, 4);
    CollectionAssert.AreEqual(new[] { "line200", "line201", "line100", "line101" }, lines);
    CollectionAssert.AreEqual(new long[] { 200, 201, 100, 101 }, positions.Select(p => p.Position).ToArray());
    CollectionAssert.AreEqual(new[] { "older.txt", "older.txt", "archive.txt", "archive.txt" },
      positions.Select(p => p.File).ToArray());
  }

  [TestMethod]
  public void AnEmptyResultSetIsNotAnError()
  {
    var lines = new List<string>();
    var positions = new List<FileSearcher<string>.LinePosition>();

    SearchResultCap.TrimToCap(lines, positions, 500_000);

    Assert.AreEqual(0, lines.Count);
    Assert.AreEqual(0, positions.Count);
  }

  private static (List<string>, List<FileSearcher<string>.LinePosition>) Deliver(int count, int firstIndex = 0,
                                                                             string file = "eqlog_test.txt")
  {
    var lines = new List<string>(count);
    var positions = new List<FileSearcher<string>.LinePosition>(count);
    for (var i = 0; i < count; i++)
    {
      lines.Add($"line{firstIndex + i}");
      positions.Add(new FileSearcher<string>.LinePosition { File = file, Position = firstIndex + i });
    }

    return (lines, positions);
  }
}
