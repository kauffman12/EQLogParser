using Microsoft.VisualStudio.TestTools.UnitTesting;

#nullable enable annotations

namespace EQLogParser
{
  /*
   * The classification stages read their cursor loops through FactRowWalk: chunk slices instead of one row at a time, because the ordinal
   * reader costs a bounds check plus a shift/mask split per row and that measured ~50 ms on a whole-capture pass (2c). Speed is only
   * allowed to arrive if the walk answers EXACTLY what the row-at-a-time door answers — same rows, same order, same values — including
   * across a chunk seam and starting from a cursor parked mid-chunk, which is where an off-by-one in the slot mask reads a neighbour
   * without ever throwing.
   *
   * These walks cross more than one chunk on purpose (65,536 rows each), so a wrong shift cannot hide inside a single slice, and every
   * comparison is against `reader[ordinal]`, the door the rest of the app still uses.
   */
  [TestClass]
  public class FactRowWalkTest
  {
    // Two chunks plus a spill, so the walk crosses two seams and ends mid-chunk.
    // Damage chunks hold 65,536 rows, heal chunks 32,768 — a row count that crosses two seams of the first and three of the second.
    private const int Rows = (DamageFactTable.FactChunkSize * 2) + 731;

    private static DamageFactTable Filled()
    {
      var table = new DamageFactTable(16);
      for (var i = 1; i <= Rows; i++)
        table.AddFact(new DamageFact(i, 1_000 + i, i % 5, (i * 3) % 7, (uint)(i * 31),
          LabelTypes.Melee, i % 2 == 0 ? DamageFact.FlagOwnerInLine : (byte)0, 0, DamageFactTable.NoSubtype));
      return table;
    }

    /* The heal stream shares the damage table's name pool — one raider, one index, or a merge splits her in half. */
    private static HealFactTable FilledHeals(DamageFactTable names)
    {
      var table = new HealFactTable(names);
      for (var i = 1; i <= Rows; i++)
        table.AddHeal(new HealFact(i, 2_000 + i, i % 4, (i * 5) % 9, (uint)(i * 7), (uint)i,
          LabelTypes.Hot, 0, 0, HealFact.NoSpell));
      return table;
    }

    [TestMethod]
    public void TheSliceWalkVisitsEveryRowInTheSameOrderAsTheReader()
    {
      var table = Filled();
      var reader = table.Facts;

      var visited = 0;
      foreach (var slice in FactRowWalk.Damage(table, 0))
        for (var i = 0; i < slice.Length; i++)
        {
          var expected = reader[visited];
          Assert.AreEqual(expected.Seq, slice[i].Seq, $"row {visited} arrived out of order");
          Assert.AreEqual(expected.Total, slice[i].Total, $"row {visited} arrived with a different amount");
          Assert.AreEqual(expected.AtkIdx, slice[i].AtkIdx, $"row {visited} arrived with a different attacker");
          Assert.AreEqual(expected.DefIdx, slice[i].DefIdx, $"row {visited} arrived with a different defender");
          visited++;
        }

      Assert.AreEqual(Rows, visited, "the walk covered every row of the capture");
    }

    [TestMethod]
    public void ACursorParkedMidChunkWalksTheRestWithoutSkippingOrRepeating()
    {
      var table = Filled();
      var reader = table.Facts;

      // Three rows before a seam: the slice starts mid-chunk, spans a whole chunk, and stops inside the third.
      var from = DamageFactTable.FactChunkSize - 3;
      var visited = from;
      foreach (var slice in FactRowWalk.Damage(table, from))
        for (var i = 0; i < slice.Length; i++)
        {
          Assert.AreEqual(reader[visited].Seq, slice[i].Seq, $"ordinal {visited} is not the row the reader calls that");
          visited++;
        }

      Assert.AreEqual(Rows, visited, "a cursor walk ends at the last row and never past it");
    }

    [TestMethod]
    public void AWalkFromTheEndOfTheCaptureYieldsNothing()
    {
      var table = Filled();

      var rows = 0;
      foreach (var slice in FactRowWalk.Damage(table, table.FactCount)) rows += slice.Length;
      Assert.AreEqual(0, rows, "a cursor at the end owes no work — the quiet-pass case");
    }

    [TestMethod]
    public void TheHealStreamWalksItsChunksLikeTheDamageStream()
    {
      var table = FilledHeals(Filled());
      var reader = table.Heals;

      var from = HealFactTable.HealChunkSize - 1;
      var visited = from;
      foreach (var slice in FactRowWalk.Healing(table, from))
        for (var i = 0; i < slice.Length; i++)
        {
          Assert.AreEqual(reader[visited].Seq, slice[i].Seq, $"heal {visited} arrived out of order");
          Assert.AreEqual(reader[visited].Total, slice[i].Total, $"heal {visited} arrived with a different amount");
          Assert.AreEqual(reader[visited].HealedIdx, slice[i].HealedIdx, $"heal {visited} arrived with a different target");
          visited++;
        }

      Assert.AreEqual(Rows, visited, "the heal walk covered every row it was owed");
    }
  }
}
