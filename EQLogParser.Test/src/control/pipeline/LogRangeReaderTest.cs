using Microsoft.VisualStudio.TestTools.UnitTesting;
using EQLogParser;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace EQLogParser.Test.src.control.pipeline;

/*
 * LogRangeReader.ForEachLine must return EXACTLY the lines sequential StreamReader.ReadLine would for the
 * same bytes — this property is what makes a sharded load's content identical to a sequential one, line for
 * line (the merge then only renumbers). Every quirky shape a real log has gets checked across partitioned
 * ranges: BOM, CRLF, no trailing newline, multi-byte characters, long lines spanning many chunks.
 */
[TestClass]
public sealed class LogRangeReaderTest
{
  [TestMethod]
  public void PartitionedRangesEqualSequentialReadLine()
  {
    var sb = new StringBuilder();
    sb.Append("Tue Apr 14 09:00:00 2026   Your attack damages Froduil for 5 points.\n");
    sb.Append("Tue Apr 14 09:00:01 2026  A dragon with a French name: café naïve ☃ hits you!\r\n");   // CRLF + multibyte
    sb.Append(new string('x', 300_000)).Append('\n');                                                // line across chunks
    sb.Append("Tue Apr 14 09:00:02 2026   no terminator on this one");                               // EOF, unterminated

    var path = Path.Combine(Path.GetTempPath(), $"rangereader-{Guid.NewGuid():N}.txt");
    try
    {
      File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
      var len = new FileInfo(path).Length;

      // Sequential truth: exactly what the app's reader produces (ReadLine strips CRLF whole).
      var expected = new List<string>();
      using (var sr = new StreamReader(path))
      {
        while (sr.ReadLine() is { } l)
        {
          expected.Add(l);
        }
      }

      // Several 2-range splits at DIFFERENT byte offsets — including ones inside the giant line and inside
      // multi-byte sequences' neighborhoods — must concatenate to the same list.
      foreach (var cut in new[] { 1L, len / 3, len / 2, len - 5 })
      {
        var aligned = AlignAfterNewline(path, Math.Clamp(cut, 1, len - 1));
        if (aligned <= 0 || aligned >= len)
        {
          continue;
        }

        var actual = new List<string>();
        LogRangeReader.ForEachLine(path, 0, aligned, actual.Add);
        LogRangeReader.ForEachLine(path, aligned, len, actual.Add);

        CollectionAssert.AreEqual(expected, actual, $"partition at {aligned} changed the lines");
      }
    }
    finally
    {
      File.Delete(path);
    }
  }

  [TestMethod]
  public void BomBelongsToNoLine()
  {
    var path = Path.Combine(Path.GetTempPath(), $"rangereader-bom-{Guid.NewGuid():N}.txt");
    try
    {
      File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("hello\nworld\n")]);

      var whole = new List<string>();
      LogRangeReader.ForEachLine(path, 0, new FileInfo(path).Length, whole.Add);
      CollectionAssert.AreEqual(new[] { "hello", "world" }, whole);

      // The sequential reference agrees (StreamReader strips the BOM too).
      using var sr = new StreamReader(path);
      Assert.AreEqual("hello", sr.ReadLine());
    }
    finally
    {
      File.Delete(path);
    }
  }

  [TestMethod]
  public void ProgressTracksRangeBytes()
  {
    var path = Path.Combine(Path.GetTempPath(), $"rangereader-prog-{Guid.NewGuid():N}.txt");
    try
    {
      File.WriteAllText(path, new string('a', 500) + "\n" + new string('b', 700) + "\n");
      var len = new FileInfo(path).Length;
      long last = -1;
      var final = 0L;
      LogRangeReader.ForEachLine(path, 0, len, _ => { }, p => { last = p; final = p; });
      Assert.AreEqual(len, final);
      Assert.IsTrue(last > 0);
    }
    finally
    {
      File.Delete(path);
    }
  }

  [TestMethod]
  public void CancelledTokenThrowsBeforeAnyLine()
  {
    var path = Path.Combine(Path.GetTempPath(), $"rangereader-cancel-{Guid.NewGuid():N}.txt");
    try
    {
      File.WriteAllText(path, "a\nb\nc\n");
      using var cts = new CancellationTokenSource();
      cts.Cancel();
      var seen = 0;
      Assert.Throws<OperationCanceledException>(
        () => LogRangeReader.ForEachLine(path, 0, new FileInfo(path).Length, _ => seen++, null, cts.Token));
      Assert.AreEqual(0, seen);
    }
    finally
    {
      File.Delete(path);
    }
  }

  private static long AlignAfterNewline(string path, long target)
  {
    using var fs = File.OpenRead(path);
    fs.Seek(target - 1, SeekOrigin.Begin);
    var b = 0;
    while ((b = fs.ReadByte()) >= 0 && b != '\n')
    {
    }

    return b < 0 ? -1 : fs.Position;
  }
}
