using Microsoft.VisualStudio.TestTools.UnitTesting;

using System.Text;

namespace EQLogParser;

/*
 * The windowing laws of `HealSummarySource.Materialize` (docs/DesignNotes → "A window is searched, never walked").
 *
 * This call feeds the healing board for every derived selection, and it used to answer "is this second inside one of these spans?" by
 * scanning the span list from zero for every heal row of the capture. On Incogitable — 420k heals against a 2,653-span whole-capture
 * window — that was **2,159 ms of a 2.2 s call**, twice the cost of building every record in it (the filter-free floor measures 34 ms),
 * and it repeated on every announce of a held selection. It now walks: enter the range by halving the clock, carry a cursor over the
 * spans, leave at the last one. 27 ms.
 *
 * Speed bought with an answer nobody checked is how this file's other laws got written, so the assertions here are the boring kind:
 * the fast walk must return EXACTLY what a naive linear scan returns — same rows, same order, same totals — across randomized layouts
 * and every boundary that could disagree (a heal exactly on a span's first second and its last, heals in the gaps, windows wholly to
 * the left or right of the capture, adjacent spans separated by one second, and the two different no's: null window vs empty window).
 *
 * The out-of-order case is the one that makes the design honest rather than clever: `AscendingTime` exists because a halving over rows
 * that are not ordered hands back a window with rows missing and nothing thrown. A store says false, the walk falls back, and the answer
 * stays identical — which is the third time this repo has made that trade (`CastHistory.AscendingTime`).
 */
[TestClass]
public sealed class HealWindowWalkTest
{
  /*
   * A real dotnet-epoch second (~2022-12), because a fact's clock stores seconds-since-2000 and CLAMPS outside 1931-2068:
   * a base picked an order of magnitude too large silently folded every row onto the same timestamp and the window tests
   * then "passed" by matching everything. The base is inside the representable window, so this fixture measures windows.
   */
  private const long Base = 63_800_000_000L;

  private static HealFact Fact(long time, uint total, short healer = 1, short healed = 2) =>
      new(0, time, healer, healed, total, 0, LabelTypes.Heal, 0, LineModifiersParser.None, HealFact.NoSpell);

  /* A table with two interned names, because NameOf reads the pool and index 0 is no one. */
  private static HealFactTable Table(int capacity, params long[] times)
  {
    var heals = new HealFactTable(new DamageFactTable(1), Math.Max(4, capacity));
    var healer = heals.InternName("Reisil");
    var healed = heals.InternName("Kilsa");
    for (var i = 0; i < times.Length; i++) heals.AddHeal(Fact(times[i], (uint)(i + 1), healer, healed));

    return heals;
  }

  /// <summary>The answer this file must keep: the naive question, asked per row against every span.</summary>
  private static List<(double Time, uint Total)> Naive(HealFactTable heals, TimeRange? range)
  {
    var answer = new List<(double, uint)>();
    var rows = heals.Heals;

    for (var i = 0; i < rows.Length; i++)
    {
      var time = (double)rows[i].TimeS;
      if (range is null)
      {
        answer.Add((time, rows[i].Total));
        continue;
      }

      foreach (var span in range.TimeSegments)
      {
        if (time >= span.BeginTime && time <= span.EndTime)
        {
          answer.Add((time, rows[i].Total));
          break;
        }
      }
    }

    return answer;
  }

  private static string Signature(List<(double Time, uint Total)> rows)
  {
    var sb = new StringBuilder();
    foreach (var row in rows) sb.Append(row.Time.ToString("F0")).Append(':').Append(row.Total).Append(';');
    return sb.ToString();
  }

  private static List<(double Time, uint Total)> Window(HealFactTable heals, TimeRange? range)
    => HealSummarySource.Materialize(heals, range!).ConvertAll(p => (p.Item1, p.Item2.Total));

  [TestMethod]
  public void AWindowAnswersExactlyWhatALinearScanWould_Randomized()
  {
    var random = new Random(20261111);

    for (var trial = 0; trial < 40; trial++)
    {
      // Heals scattered over a 4,000 second field, sometimes several in the same second.
      var times = new long[300];
      for (var i = 0; i < times.Length; i++) times[i] = Base + random.Next(0, 4_000);
      Array.Sort(times);   // ingest order: ascending, with duplicates — the shape a real capture has

      var heals = Table(times.Length, times);
      Assert.IsTrue(heals.AscendingTime, "sorted fixture must read as ordered, or the trial measures the fallback");

      // A window of 0..6 disjoint spans inside the same field, so gaps on both sides are always in play.
      var range = new TimeRange();
      var spanCount = random.Next(0, 7);
      for (var s = 0; s < spanCount; s++)
      {
        var begin = Base + random.Next(0, 3_900);
        range.Add(new TimeSegment(begin, begin + random.Next(0, 60)));
      }

      Assert.AreEqual(Signature(Naive(heals, range)), Signature(Window(heals, range)),
          $"trial {trial}: {spanCount} span(s) over {times.Length} rows disagreed with the linear test");
    }
  }

  [TestMethod]
  public void BothEndsOfASpanAreInside_AndTheGapBesideThemAreNot()
  {
    var heals = Table(8, Base, Base + 10, Base + 15, Base + 20, Base + 30);
    var range = new TimeRange(new TimeSegment(Base + 10, Base + 20));

    var window = Window(heals, range);
    Assert.AreEqual(3, window.Count, "inclusive on both ends — the same test the builder applies to its own window");
    Assert.AreEqual((double)Base + 10, window[0].Time, "the row on the span's first second is in");
    Assert.AreEqual((double)Base + 15, window[1].Time);
    Assert.AreEqual((double)Base + 20, window[2].Time, "and the row on its last second");

    // One second inside each edge is outside, and the fast walk must not borrow a neighbour's seconds.
    Assert.AreEqual(1, Window(heals, new TimeRange(new TimeSegment(Base + 11, Base + 19))).Count, "only the row at 15 remains");
    Assert.AreEqual(0, Window(heals, new TimeRange(new TimeSegment(Base + 21, Base + 29))).Count);
    Assert.AreEqual(Signature(Naive(heals, range)), Signature(window));
  }

  [TestMethod]
  public void AWindowWhollyLeftOrRightOfTheCaptureIsEmptyRatherThanEverything()
  {
    var heals = Table(4, Base + 100, Base + 200);

    Assert.AreEqual(0, Window(heals, new TimeRange(new TimeSegment(Base - 500, Base + 50))).Count, "left of every row");
    Assert.AreEqual(0, Window(heals, new TimeRange(new TimeSegment(Base + 900, Base + 999))).Count, "right of every row");

    // The halving's own two edges: start before all rows, start past all rows.
    Assert.AreEqual(heals.HealCount, heals.FirstIndexAtOrAfter(Base + 901), "past every row returns the count, not a clamped index");
    Assert.AreEqual(0, heals.FirstIndexAtOrAfter(Base - 1));
    Assert.AreEqual(1, heals.FirstIndexAtOrAfter(Base + 200), "an exact hit returns that row, not the one after");
    Assert.AreEqual(1, heals.FirstIndexAtOrAfter(Base + 150), "nothing at 150: the next row is the answer");
  }

  [TestMethod]
  public void NoWindowIsTheWholeCaptureAndAnEmptyWindowIsNothing()
  {
    var heals = Table(4, Base, Base + 60, Base + 120);

    Assert.AreEqual(3, Window(heals, null).Count, "null means this capture, every row");
    Assert.AreEqual(0, Window(heals, new TimeRange()).Count, "a window with no seconds means there were none — not everything");

    // And neither one is allowed to be the other by accident on an empty store.
    var none = Table(4);
    Assert.AreEqual(0, Window(none, null).Count);
    Assert.AreEqual(0, Window(none, new TimeRange()).Count);
  }

  [TestMethod]
  public void ASpanSeparatingTwoRunsKeepsItsOwnSeconds()
  {
    // Two runs of heals with a hole: a window covering both runs must take both, and one narrow span must take only its own.
    var times = new long[6];
    for (var i = 0; i < 3; i++) times[i] = Base + i;
    for (var i = 0; i < 3; i++) times[3 + i] = Base + 600 + i;

    var heals = Table(6, times);
    var both = new TimeRange();
    both.Add(new TimeSegment(Base, Base));
    both.Add(new TimeSegment(Base + 602, Base + 602));

    var window = Window(heals, both);
    Assert.AreEqual(2, window.Count, "the cursor moves forward with the rows and finds the second run too");
    Assert.AreEqual((double)Base, window[0].Time);
    Assert.AreEqual((double)Base + 602, window[1].Time);
    Assert.AreEqual(Signature(Naive(heals, both)), Signature(window));
  }

  [TestMethod]
  public void AStoreThatWentBackwardsInTimeSaysSoAndStillAnswers()
  {
    var heals = new HealFactTable(new DamageFactTable(1), 8);
    var healer = heals.InternName("Reisil");
    var healed = heals.InternName("Kilsa");
    heals.AddHeal(Fact(Base + 10, 1, healer, healed));
    heals.AddHeal(Fact(Base + 20, 2, healer, healed));
    Assert.IsTrue(heals.AscendingTime, "an ordered store orders");

    // One backwards append — a restored or concatenated log — retires the fast path permanently.
    heals.AddHeal(Fact(Base + 5, 3, healer, healed));
    Assert.IsFalse(heals.AscendingTime, "one row out of order is enough to say so");
    heals.AddHeal(Fact(Base + 30, 4, healer, healed));
    Assert.IsFalse(heals.AscendingTime, "and it does not come back because later rows happen to ascend");

    var range = new TimeRange(new TimeSegment(Base + 5, Base + 20));
    var window = Window(heals, range);

    Assert.AreEqual(Signature(Naive(heals, range).OrderBy(static row => row.Time).ToList()), Signature(window),
        "the same rows as the linear test, sorted for the builder's time cursor");
    Assert.AreEqual(3, window.Count);
    Assert.AreEqual((double)Base + 5, window[0].Time);
    Assert.AreEqual((double)Base + 5, Window(heals, null)[0].Time, "the whole-capture door also sorts");

    // The whole-capture and empty-window no's keep their meaning on an unordered store too.
    Assert.AreEqual(4, Window(heals, null).Count);
    Assert.AreEqual(0, Window(heals, new TimeRange()).Count);
  }
}
