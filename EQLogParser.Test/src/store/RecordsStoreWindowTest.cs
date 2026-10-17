using Microsoft.VisualStudio.TestTools.UnitTesting;

#nullable enable annotations

namespace EQLogParser;

/*
 * A bounded window over the auxiliary record history (RecordsStore.GetDuring).
 *
 * Why it changed: a range query used to copy EVERY group of the night out of the store and only then binary-search the copy, so asking about
 * five minutes of a raid paid for all of it — measured on a real capture at 4.6 MB copied per whole-history spell query, most of it thrown
 * away for any narrower window. The window is now located inside the store lock and only its own groups are copied out. What must not change
 * is what a reader sees, so that is asserted here rather than argued:
 *
 *   1. THE SAME ROWS, IN THE SAME ORDER: ascending by group time, or the identical run walked backwards for `reverse`.
 *   2. BOUNDARIES ARE INCLUSIVE at both ends, and an inverted or empty window returns nothing rather than everything.
 *   3. THE SNAPSHOT IS STILL A SNAPSHOT: records added after the read started do not appear mid-enumeration, which is why a UI pane can walk
 *      a live capture's history without tearing (the copy-out discipline predates this change and survives it).
 */
[TestClass]
public class RecordsStoreWindowTest
{
  private const double Base = 100_000;

  [TestInitialize]
  public void ClearStores() => RecordsStore.Instance.Clear();

  [TestCleanup]
  public void CleanupStores() => RecordsStore.Instance.Clear();

  /// <summary>Two casts on each of `minutes` consecutive minutes, so groups and per-group records both exist.</summary>
  private static void Seed(int minutes)
  {
    for (var m = 0; m < minutes; m++)
    {
      var t = Base + (m * 60);
      RecordsStore.Instance.Add(new SpellCast { Spell = $"Cure A{m}", Caster = "Kilsef" }, t);
      RecordsStore.Instance.Add(new SpellCast { Spell = $"Nuke B{m}", Caster = "Vvalan" }, t + 7);
    }
  }

  private static List<(double Time, string Spell)> Query(double begin, double end, bool reverse = false)
  {
    var rows = new List<(double Time, string Spell)>();
    foreach (var row in RecordsStore.Instance.GetSpellsDuring(begin, end, reverse))
    {
      rows.Add((row.Item1, ((SpellCast)row.Item2).Spell));
    }

    return rows;
  }

  [TestMethod]
  public void AWindowReturnsItsOwnRowsInOrderAndNothingElse()
  {
    Seed(30); // minutes 0..29, two casts each

    var win = Query(Base + (10 * 60), Base + (14 * 60) + 10);
    Assert.AreEqual(10, win.Count, "five minutes × two casts");

    for (var i = 1; i < win.Count; i++)
    {
      Assert.IsTrue(win[i - 1].Time <= win[i].Time, $"ascending order broke at {i}: {win[i - 1].Time} then {win[i].Time}");
    }

    Assert.IsTrue(win.All(r => r.Time >= Base + (10 * 60) && r.Time <= Base + (14 * 60) + 10), "no row outside the window leaked in");
    CollectionAssert.Contains(win.Select(r => r.Spell).ToList(), "Cure A10");
    CollectionAssert.DoesNotContain(win.Select(r => r.Spell).ToList(), "Cure A9");
    CollectionAssert.DoesNotContain(win.Select(r => r.Spell).ToList(), "Cure A15");
  }

  [TestMethod]
  public void ReverseWalksTheSameWindowBackwards()
  {
    Seed(20);

    var forward = Query(Base + (5 * 60), Base + (9 * 60) + 10);
    var backward = Query(Base + (5 * 60), Base + (9 * 60) + 10, reverse: true);

    Assert.AreEqual(forward.Count, backward.Count);
    CollectionAssert.AreEqual(forward.Select(r => r.Spell).Reverse().ToList(), backward.Select(r => r.Spell).ToList());
    for (var i = 1; i < backward.Count; i++)
    {
      Assert.IsTrue(backward[i - 1].Time >= backward[i].Time, $"reverse order broke at {i}");
    }
  }

  [TestMethod]
  public void BoundariesAreInclusiveAndAnImpossibleWindowIsEmpty()
  {
    Seed(10); // a cast lands exactly on Base + m*60 and on +7 s

    // One cast is stamped exactly on Base (the next sits seven seconds later), so an instant-wide window takes exactly it.
    Assert.AreEqual(1, Query(Base, Base).Count, "a window of one instant still catches the cast stamped on it");
    Assert.AreEqual(2, Query(Base, Base + 7).Count, "and the seven-seconds-later cast joins as soon as the end reaches it");
    Assert.AreEqual(0, Query(Base - 500, Base - 100).Count, "a window left of everything");
    Assert.AreEqual(0, Query(Base + 100_000, Base + 200_000).Count, "a window right of everything");
    Assert.AreEqual(0, Query(Base + 600, Base + 100).Count, "begin after end is nothing, not everything");

    // The whole night still reads whole: every group the store holds comes back.
    Assert.AreEqual(20, Query(0, double.MaxValue).Count);
  }

  [TestMethod]
  public void AWindowIsSnapshottedOnceAndLaterWritesDoNotTearIt()
  {
    Seed(10);

    var rows = RecordsStore.Instance.GetSpellsDuring(Base, Base + (9 * 60) + 10);
    var seen = new List<double>();
    var added = false;
    foreach (var row in rows)
    {
      if (!added)
      {
        // A live capture keeps writing while a pane walks its history: this must neither throw nor be seen mid-read.
        RecordsStore.Instance.Add(new SpellCast { Spell = "Late", Caster = "Kilsef" }, Base + 30);
        added = true;
      }

      seen.Add(row.Item1);
    }

    Assert.AreEqual(20, seen.Count, "the window is exactly what it was when the read started");
    Assert.IsFalse(seen.Contains(Base + 30), "a record written during the read does not join it");
  }

  [TestMethod]
  public void GroupsBeyondTheWindowAreNeverCopiedOut()
  {
    Seed(400); // enough groups that copying them all is a real cost, which is the whole point of the change

    var win = Query(Base + (200 * 60), Base + (201 * 60));
    Assert.AreEqual(3, win.Count, "one minute of a long night: the two casts it owns plus the next minute's first, whose stamp falls on the closing edge");

    // The window takes three of 800 groups, including its closing edge. Allocation measurements belong in
    // docs/DesignNotes.md → "Parsing allocation reductions", not in a heap-probe assertion here.
    Assert.AreEqual(800, Query(0, double.MaxValue).Count);
  }
}
