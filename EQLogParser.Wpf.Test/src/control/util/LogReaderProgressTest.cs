using EQLogParser;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace EQLogParser.Wpf.Test
{
  /*
   * What the reader's progress NUMBER is allowed to do while a bulk read runs.
   *
   * The shape being guarded is a split: the bulk loop paces a ~50-notch dial off a per-line counter, but what it reports is `_currentPos`, taken
   * from the real stream position whenever a notch is crossed (and every line, once the last few thousand bytes are in). That split is why the
   * per-line counter was changed from "count this line's UTF-8 bytes" to "count its characters" — the old form scanned the text a second time on
   * every line of a 13.7 M-line capture, roughly 1.2 GB of re-scanning, to drive a dial whose answer the stream already knew, and it was not exact
   * anyway (a hard-coded encoding plus an assumed CRLF).
   *
   * So this pins the three things that a pacer change could actually break: the number must move while lines are flowing, must never walk
   * backwards, and must have arrived at ~100 % by the time the last line is handed over. A multi-byte-heavy log crossing its notches slightly
   * late is the expected difference from the old code, and all three checks accept it because the reported value is always a position.
   *
   * Deliberately NOT asserted: how many notches were crossed, or how smooth the climb was. Sampling another thread's progress on a file this
   * reader finishes in milliseconds cannot pin either one, and a test that depends on sample timing fails on a loaded machine instead of on a bug.
   *
   * Windows-only assembly (net10.0-windows): it compiles everywhere and runs only there, so nothing here is claimed as executed from Linux.
   */
  [TestClass]
  public class LogReaderProgressTest
  {
    // Same fixture shape as LogReaderThreadTest: HandleLine refuses a line without a parseable header, and the file has to be big
    // enough that the dial crosses more than its final notch.
    private const int Lines = 20_000;

    /// <summary>Counts what the reader hands over, so "the file was read" is measured rather than assumed.</summary>
    private sealed class CountingProcessor : ILogProcessor
    {
      internal long Seen;
      private Task? _pump;

      public void LinkTo(BlockingCollection<LogReaderItem[]> collection)
      {
        _pump = Task.Run(() =>
        {
          try
          {
            foreach (var batch in collection.GetConsumingEnumerable()) Interlocked.Add(ref Seen, batch.Length);
          }
          catch (InvalidOperationException)
          {
            // CompleteAdding during Dispose.
          }
        });
      }

      public void Dispose()
      {
        try { _pump?.Wait(2000); } catch { }
      }
    }

    private static string WriteCapture()
    {
      var path = Path.Combine(Path.GetTempPath(), $"eqlog_LogReaderProgressTest_{Guid.NewGuid():N}.txt");
      var builder = new StringBuilder(64 * Lines);

      // Mix in multi-byte characters on part of the file: that is the case where counting characters paces the dial slightly
      // differently from counting bytes, and the assertions below have to hold anyway.
      for (var i = 0; i < Lines; i++)
        builder.Append("[Wed Oct 01 12:00:00 2025] You hit a training dummy for ").Append(i)
               .Append(i % 50 == 0 ? " damage. (Ésperñe)\n" : " damage.\n");
      File.WriteAllText(path, builder.ToString());   // UTF-8 without a BOM by default
      return path;
    }

    [TestMethod]
    public void ProgressMovesAndArrivesAtHundred()
    {
      var file = WriteCapture();
      var processor = new CountingProcessor();

      try
      {
        // minBack in years: the reader seeks to that date, and this capture is dated 2025-10-01 (same trick as LogReaderThreadTest).
        var reader = new LogReader(processor, file, minBack: 60 * 24 * 365 * 10, origin: "test");
        var running = reader.StartAsync();

        double last = -1.0;
        var samples = 0;
        var watch = Stopwatch.StartNew();
        while (Interlocked.Read(ref processor.Seen) < Lines && watch.Elapsed.TotalSeconds < 60)
        {
          Assert.IsFalse(running.IsFaulted, $"the read loop faulted: {running.Exception?.GetBaseException().Message}");

          var percent = reader.GetProgress();
          Assert.IsFalse(double.IsNaN(percent) || double.IsInfinity(percent), $"progress reported {percent}");
          Assert.IsTrue(percent >= last - 0.0001, $"progress walked backwards: {last} → {percent}");
          Assert.IsTrue(percent <= 100.5, $"progress overshot the end of the file: {percent}");
          last = percent;
          samples++;

          Thread.Sleep(5);
        }

        Assert.IsTrue(Interlocked.Read(ref processor.Seen) >= Lines,
          $"the reader stopped after {Interlocked.Read(ref processor.Seen)} of {Lines} lines");

        // The dial has to have moved at all — a pacer that never crossed a notch would sit at 0 the whole read and still satisfy "not backwards".
        var final = reader.GetProgress();
        Assert.IsTrue(final >= 99.5, $"the whole file was read ({samples} samples) but progress stopped at {final} %");

        reader.Dispose();
      }
      finally
      {
        processor.Dispose();
        try { File.Delete(file); } catch { }
      }
    }
  }
}
