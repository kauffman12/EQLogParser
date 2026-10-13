using Microsoft.VisualStudio.TestTools.UnitTesting;
using EQLogParser;
using System;
using System.IO;
using System.Text;

namespace EQLogParser.Test.src.control.pipeline;

/*
 * LogShardPlan: where to cut a file for parallel reading — and whether it earns sharding at all.
 * The correctness contract is geometric: cuts strictly increase, every interior boundary sits one past a
 * '\n' (so each line belongs to exactly one range), no range is empty, and the ranges tile the file.
 */
[TestClass]
public sealed class LogShardPlanTest
{
  [TestMethod]
  public void WorkersForPolicy()
  {
    // The ship policy: two workers only once EACH would hold at least MinWindowBytes (docs/DesignNotes).
    Assert.AreEqual(1, LogShardPlan.WorkersFor(LogShardPlan.MinWindowBytes * 2 - 1));
    Assert.AreEqual(LogShardPlan.Workers, LogShardPlan.WorkersFor(LogShardPlan.MinWindowBytes * 2));
    Assert.AreEqual(LogShardPlan.Workers, LogShardPlan.WorkersFor(long.MaxValue / 4));
    Assert.AreEqual(1, LogShardPlan.WorkersFor(0));
  }

  [TestMethod]
  public void CutsTileTheFileOnLineBoundaries()
  {
    var path = TempLog(lines: 5000, lineLength: 60);
    try
    {
      var len = new FileInfo(path).Length;
      var cuts = LogShardPlan.CutPoints(path, 3);
      Assert.AreEqual(4, cuts.Length);
      Assert.AreEqual(0, cuts[0]);
      Assert.AreEqual(len, cuts[^1]);

      for (var i = 1; i < cuts.Length; i++)
      {
        Assert.IsTrue(cuts[i] > cuts[i - 1], $"range {i} must be non-empty");
        if (i < cuts.Length - 1 || cuts[i] < len)
        {
          // Interior boundaries (and the final one when it was found rather than being the length) come
          // one byte past a newline.
          Assert.AreEqual((byte)'\n', ReadByte(path, cuts[i] - 1), $"cut {i} must land after a newline");
        }
      }
    }
    finally
    {
      File.Delete(path);
    }
  }

  [TestMethod]
  public void TinyLineCountCollapsesRangesInsteadOfEmpties()
  {
    // Three lines cannot supply three interior boundaries: the plan may return FEWER ranges, but never an
    // empty one and never an unsorted or incomplete tiling.
    var path = TempLog(lines: 3, lineLength: 40);
    try
    {
      var len = new FileInfo(path).Length;
      var cuts = LogShardPlan.CutPoints(path, 8);
      Assert.AreEqual(0, cuts[0]);
      Assert.AreEqual(len, cuts[^1]);
      for (var i = 1; i < cuts.Length; i++)
      {
        Assert.IsTrue(cuts[i] > cuts[i - 1]);
      }
    }
    finally
    {
      File.Delete(path);
    }
  }

  [TestMethod]
  public void OnePartIsWholeFile()
  {
    var path = TempLog(lines: 10, lineLength: 30);
    try
    {
      var cuts = LogShardPlan.CutPoints(path, 1);
      CollectionAssert.AreEqual(new long[] { 0, new FileInfo(path).Length }, cuts);
    }
    finally
    {
      File.Delete(path);
    }
  }

  private static string TempLog(int lines, int lineLength)
  {
    var path = Path.Combine(Path.GetTempPath(), $"shardplan-{Guid.NewGuid():N}.txt");
    var body = new string('x', Math.Max(1, lineLength - 1));
    var sb = new StringBuilder(lines * (lineLength + 1));
    for (var i = 0; i < lines; i++)
    {
      sb.Append(body).Append('\n');
    }

    File.WriteAllText(path, sb.ToString());
    return path;
  }

  private static byte ReadByte(string path, long offset)
  {
    using var fs = File.OpenRead(path);
    fs.Seek(offset, SeekOrigin.Begin);
    var b = fs.ReadByte();
    Assert.IsTrue(b >= 0);
    return (byte)b;
  }
}
