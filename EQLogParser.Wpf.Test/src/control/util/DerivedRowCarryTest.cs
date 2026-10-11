using EQLogParser;

namespace EQLogParser.Wpf.Test
{
  /*
   * A live raid asks `DerivedFightRows.Build` the same question twice a second, over every row the capture has ever made, and
   * the answer used to be a brand-new object graph every time: a row plus roughly six formatted strings each, for thousands of
   * rows, whether or not anything happened to them. That is the allocation a task manager shows climbing during a pull.
   *
   * `DerivedRowCarry` is allowed to hand back the SAME row object, and it may do so only when every value on that row is
   * identical by definition — same `DerivedFight` instance, same identity answer, same hit counts, same end status. So the law
   * this file pins is a pair: reuse exactly when nothing moved (an unchanged row costs no object), and NOT otherwise (a row whose
   * numbers grew must be a fresh row, because the grid already holds the old one and a mutated instance would change the screen
   * without anything announcing it). The third test is the anti-static guard: a different carry must allocate fresh rows, so reuse
   * can never come from process state that outlives a capture.
   */
  [TestClass]
  public class DerivedRowCarryTest
  {
    private const double T0 = 1_000;

    private static DerivedFight Grul(double lastS, uint hits = 4, ulong damage = 5_000)
      => new()
      {
        Name = "Grul", Id = 1, BeginTime = T0, LastTime = T0 + lastS,
        DamageHits = hits, DamageTotal = (long)damage,
      };

    [TestMethod]
    public void AnUnchangedRowComesBackAsTheSameObject()
    {
      var carry = new DerivedRowCarry();
      var fight = Grul(30);

      var first = DerivedFightRows.Build([fight], new EntityTimeline(), 10, new DamageFactTable(8),
        new FightFactIndex(), carry).Rows[0];
      var second = DerivedFightRows.Build([fight], new EntityTimeline(), 11, new DamageFactTable(8),
        new FightFactIndex(), carry).Rows[0];

      Assert.AreSame(first, second, "nothing on this row moved, so drawing it again must not cost an object");

      // ...and the reused row still says everything it said: reuse may not lose a cell to save an allocation.
      Assert.AreEqual("Grul", second.Name);
      Assert.AreEqual(5_000, second.Damage);
      Assert.AreEqual("00:31", second.Duration, "inclusive seconds, same as a cold build");
    }

    [TestMethod]
    public void ARowThatGrewDamageIsNotTheOldObject()
    {
      var carry = new DerivedRowCarry();
      var fight = Grul(30);
      var first = DerivedFightRows.Build([fight], new EntityTimeline(), 10, new DamageFactTable(8),
        new FightFactIndex(), carry).Rows[0];

      // The live case: the same fight object, bigger numbers. The grid holds `first`, so this must be a NEW row that
      // RowPatch can copy into place — reusing the instance would edit the screen without announcing it.
      fight.DamageTotal = 9_000;
      fight.DamageHits = 7;
      var second = DerivedFightRows.Build([fight], new EntityTimeline(), 12, new DamageFactTable(8),
        new FightFactIndex(), carry).Rows[0];

      Assert.AreNotSame(first, second, "a row whose printed values moved must not mutate what the grid already holds");
      Assert.AreEqual(9_000, second.Damage);
      Assert.AreEqual(7u, second.Hits);
      Assert.AreEqual(5_000, first.Damage, "the row that was handed out earlier keeps what it was told");
    }

    [TestMethod]
    public void ADifferentCarryStartsFreshRatherThanReadingSomeonesElseRows()
    {
      var fight = Grul(20);
      var timeline = new EntityTimeline();

      var built = DerivedFightRows.Build([fight], timeline, 5, new DamageFactTable(8), new FightFactIndex(),
        new DerivedRowCarry()).Rows[0];
      var elsewhere = DerivedFightRows.Build([fight], timeline, 5, new DamageFactTable(8), new FightFactIndex(),
        new DerivedRowCarry()).Rows[0];

      // Nothing static: the carry lives on the session (and DeriveEngine nulls it on Dispose), so a closed capture's rows
      // cannot be handed to the next one — which would also be the failure where a stale row outlives its log.
      Assert.AreNotSame(built, elsewhere);
      Assert.AreEqual(built.Damage, elsewhere.Damage);
    }

    [TestMethod]
    public void RowsThatLeftTheListStopBeingCarried()
    {
      var carry = new DerivedRowCarry();
      DerivedFight Fight(string name, int id, double beginS) => new()
      {
        Name = name, Id = id, BeginTime = T0 + beginS, LastTime = T0 + beginS + 10, DamageTotal = 100, DamageHits = 1,
      };

      DerivedFightRows.Build([Fight("Grul", 1, 0), Fight("MoK", 2, 600), Fight("Hro", 3, 1200)],
        new EntityTimeline(), 5, new DamageFactTable(8), new FightFactIndex(), carry);
      Assert.AreEqual(3, carry.RowCount, "three fights, three carried rows");

      // A capture that drops rows (a clear, a re-projection) must not go on carrying the ones that left: entries this pass
      // did not touch are swept once they outnumber what is on screen, or the carry would grow with every name of the night.
      DerivedFightRows.Build([Fight("Grul", 1, 0)], new EntityTimeline(), 5, new DamageFactTable(8),
        new FightFactIndex(), carry);
      Assert.AreEqual(1, carry.RowCount, "the two rows that left the list must not stay carried");
    }
  }
}
