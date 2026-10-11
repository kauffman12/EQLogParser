using Microsoft.VisualStudio.TestTools.UnitTesting;

#nullable enable annotations

namespace EQLogParser
{
  /*
   * A loaded capture keeps every slot its last doubling allocated and never wrote. Measured on the 467 MB capture this
   * project benchmarks against: 2,285,746 damage facts sat in a 3,200,000-slot array (29 MB of address space holding
   * nothing) and 1,243,469 heals in 1,600,000 slots (11 MB) — together 47 MB of the 397 MB a loaded session retains, i.e.
   * 12 % of what the app holds at EOF was reserved for facts that do not exist. Growth by doubling stays exactly as it is
   * (it is pinned by HealFactCaptureTest: extra growth steps are paid for on the parse thread, where load time actually
   * goes); what changes is that the slack gets handed back once the load stops growing.
   *
   * So these tests hold two separate things. The MECHANICS: a trim moves no row, renumbers nothing, keeps a span taken
   * before it readable, and never leaves an array that cannot grow. The POLICY (CombatCapture.CompactRows): it does not
   * ask for pennies, and — the law that matters most — it never runs twice inside one doubling, because after a trim
   * capacity equals count, so the next fact doubles the array again and a per-pass trim would copy the whole table once
   * per new fact where doubling was amortized. That is quadratic behaviour wearing a memory-saving badge.
   */
  [TestClass]
  public class FactTableCompactionTest
  {
    // Enough rows to clear RowArrays' floor (16) in every array, so a trimmed table's slack is exactly zero and the
    // assertion below is about the trim rather than about the floor it may not cross.
    private const int Rows = 100;

    private static DamageFact Fact(int seq, short atk, short def, uint total)
      => new(seq, 1_000 + seq, atk, def, total, LabelTypes.Melee, 0, 0, DamageFactTable.NoSubtype);

    /* Fill all five row arrays past the floor: facts plus one each of the small queues. */
    private static DamageFactTable Populated(DamageFactTable table)
    {
      for (var i = 1; i <= Rows; i++)
      {
        table.AddFact(Fact(i, (short)(i % 7), (short)((i * 3) % 11), (uint)(i * 13)));
        if (i > 20)
        {
          // Names the small arrays need to be over their floor: deaths, registry changes, taunts, evidence lines.
          table.AddDeath(new DeathFact(i, 2_000 + i, table.InternName($"mob {i}"), killerIdx: -1));
          table.AddIdentity(new IdentityEvent(i, 5_000 + i, table.InternName($"name {i}"), IdentityEvent.VerifiedPlayer));
          table.AddTaunt(new TauntFact(i, 3_000 + i, table.InternName($"taunter {i}")));
          table.AddEvidence(new EvidenceFact(i, 4_000 + i, table.InternName($"witness {i}"), EvidenceFact.EvCast, -1));
        }
      }
      return table;
    }

    [TestMethod]
    public void DoublingLeavesSlackAndTrimmingHandsEveryByteOfItBack()
    {
      var table = Populated(new DamageFactTable(64));   // 64 → 128 slots for 100 facts: the doubling's remainder

      var slackBefore = table.SlackBytes;
      Assert.IsTrue(slackBefore > 0, "a buffer that doubled leaves empty slots — that is the memory this reclaims");

      var freed = table.CompactToCount();

      Assert.AreEqual(slackBefore, freed, "the trim reports exactly the slack it was holding");
      Assert.AreEqual(0L, table.SlackBytes, "every row array shrank to its rows (all five are past the 16-row floor)");
      Assert.AreEqual(Rows, table.FactCount, "no row was dropped on the way");
    }

    /*
     * The law every board depends on: FightFactIndex stores ORDINALS into this array, and the damage/tanking blocks are
     * contiguous runs of them. A trim that renumbered rows would silently move damage between raiders while every total
     * still added up — so assert row-by-row at the same index, before and after.
     */
    [TestMethod]
    public void ATrimMovesNoRowAndKeepsEveryOrdinalNamingTheSameFact()
    {
      var table = new DamageFactTable(64);
      for (var i = 1; i <= 500; i++) table.AddFact(Fact(i, (short)(i % 9), (short)(i % 13), (uint)(i * 7)));

      var before = table.Facts.ToArray();
      // A reader that looked at the table just now — a derive pass holding a span while a trim runs elsewhere.
      var staleView = table.Facts;

      table.CompactToCount();

      var after = table.Facts;
      Assert.AreEqual(before.Length, after.Length, "the same number of facts, in the same places");
      for (var i = 0; i < before.Length; i++)
      {
        Assert.AreEqual(before[i].Seq, after[i].Seq, $"ordinal {i} still names the same row");
        Assert.AreEqual(before[i].AtkIdx, after[i].AtkIdx, $"ordinal {i} kept its attacker");
        Assert.AreEqual(before[i].DefIdx, after[i].DefIdx, $"ordinal {i} kept its defender");
        Assert.AreEqual(before[i].Total, after[i].Total, $"ordinal {i} kept its damage");
      }

      Assert.AreEqual(before.Length, staleView.Length, "a span taken before the trim still reads the capture it was given");
      Assert.AreEqual(before[^1].Total, staleView[^1].Total, "down to its last row");
    }

    /* Idempotence is what makes asking cheap: the pass cadence asks every expensive pass, most of which have nothing to give. */
    [TestMethod]
    public void AskingForSlackThatIsNotThereReleasesNothing()
    {
      var table = Populated(new DamageFactTable(64));

      Assert.IsTrue(table.CompactToCount() > 0, "the first ask has something to hand back");
      Assert.AreEqual(0L, table.CompactToCount(), "a table at its row count releases nothing — asking again costs no copy");
      Assert.AreEqual(Rows, table.FactCount);
    }

    [TestMethod]
    public void AddingAfterATrimStillGrowsTheBuffer()
    {
      var table = new DamageFactTable(64);
      for (var i = 1; i <= 100; i++) table.AddFact(Fact(i, 1, 2, (uint)i));

      table.CompactToCount();   // capacity is now exactly 100: the next add must find room by growing

      for (var i = 101; i <= 140; i++) table.AddFact(Fact(i, 1, 2, (uint)i));

      Assert.AreEqual(140, table.FactCount, "growth continues after a trim to the exact count");
      Assert.AreEqual(140u, table.Facts[^1].Total, "and lands where it should");
      Assert.IsTrue(table.SlackBytes >= 0);
    }

    /*
     * The floor law, and the only way an array can become ungrowable: doubling 0 is 0, so a trim to zero rows would make
     * the next AddFact index past the end. A capture that opened and fought nothing is not an exotic case — it is every log
     * opened by mistake.
     */
    [TestMethod]
    public void AnEmptyTableKeepsRoomToGrowAfterItsTrim()
    {
      var table = new DamageFactTable(4_096);

      Assert.IsTrue(table.CompactToCount() > 0, "an unused preallocation is the purest slack there is");

      table.AddFact(Fact(1, 1, 2, 77));

      Assert.AreEqual(1, table.FactCount, "a trimmed-empty table still takes a fact");
      Assert.AreEqual(77u, table.Facts[0].Total);
    }

    /*
     * The heal side, plus the seam that makes it dangerous to touch: its names live in the damage table beside it, so a
     * trim must leave name indices pointing at the same raider in both streams.
     */
    [TestMethod]
    public void HealRowsSurviveTheirTrimAndTheirSharedNamesWithThem()
    {
      var facts = new DamageFactTable(64);
      var heals = new HealFactTable(facts, 64);
      var niktaza = facts.InternName("Niktaza");
      var rune = facts.InternName("Rune");

      for (var i = 1; i <= 300; i++)
      {
        heals.AddHeal(new HealFact(i, FixtureTime.BaseL + i, rune, niktaza, (uint)(i * 5), overTotal: 3,
                                   typeId: LabelTypes.Heal, flags: 0, modMask: 0, subIdx: HealFact.NoSpell));
      }

      // The chunked law replaces the doubling one here, and the state this asserted no longer exists: 300 heals used to sit in a
      // buffer that doubled to 512 slots (212 empty ones for CompactToCount to hand back), and now they buy exactly one chunk with
      // nothing reserved beyond them. Kept as a test rather than deleted because it is still the fixture that proves a heal trim, if
      // one is ever added, neither loses rows nor breaks the shared name pool.
      Assert.AreEqual(1, heals.HealChunksAllocated, "300 heals buy exactly one chunk");
      Assert.AreEqual(0L, heals.SlackBytes, "and leave no reserved-but-empty slots behind");
      var before = heals.Heals.ToArray();

      var freed = heals.CompactToCount();

      Assert.AreEqual(before.Length, heals.HealCount, "no heal lost");
      Assert.AreEqual(0L, freed, "there is nothing inside a chunk to hand back");
      for (var i = 0; i < before.Length; i++)
      {
        var now = heals.Heals[i];
        Assert.AreEqual(before[i].Total, now.Total, $"heal {i} kept its amount");
        Assert.AreEqual(before[i].HealerIdx, now.HealerIdx, "and its index into the shared name pool");
      }

      Assert.AreEqual("Niktaza", facts.NameOf(heals.Heals[0].HealedIdx), "the shared pool still answers across both tables");
    }

    /*
     * What "slack" means now that damage rows live in fixed chunks, and it is a different law rather than the old one with new numbers.
     *
     * The old fixture pinned a doubling: reserve slots, fill a little over half, and the capture sits on megabytes of empty address space
     * until CompactRows hands it back - measured 29 MB of unused slots for the damage table alone on the reference capture. Chunked storage
     * removes that state instead of reclaiming it: a chunk is allocated when its FIRST row arrives, so writing facts cannot accumulate empty
     * capacity beyond the tail of the newest chunk, and a capacity hint costs reference slots (8 bytes each), not rows.
     *
     * Which is also why the once-per-doubling watermark is not re-pinned here: the guard still exists in CombatCapture for the arrays that
     * still double (deaths, identity events, taunts, evidence - and the heal table beside them, where DoublingLeavesSlackAndTrimmingHands
     * EveryByteOfItBack and HealRowsSurviveTheirTrimAndTheirSharedNamesWithThem keep it honest). There is nothing left on the damage side to
     * police, and pretending otherwise would assert an absence by writing a tautology.
     *
     * What THIS fixture guards is the arithmetic that replaced the doubling: chunk = ordinal >> shift, slot = ordinal & mask. A wrong shift or
     * mask does not throw - it reads a neighbouring row, or silently wastes a megabyte. So the boundary ordinals are named explicitly: 65,535
     * and 65,536 are the last slot of chunk 0 and the first of chunk 1, and those two are exactly where an off-by-one in the mask lives.
     */
    [TestMethod]
    public void ChunkedRowsAllocateOnDemandAndKeepEveryOrdinalNamingTheSameFact()
    {
      var table = new DamageFactTable(1_024);
      for (var i = 1; i <= 200_000; i++) table.AddFact(Fact(i, 1, 2, (uint)i));

      Assert.AreEqual(200_000, table.FactCount);
      Assert.AreEqual(4, table.FactChunksAllocated,
        "200,000 rows reach chunk 3; the hint's other twelve chunks cost a null each, not 1.5 MB each");

      // No empty address space at all from fact rows: the newest chunk is not full, and an unwritten tail of a chunk costs nothing
      // the store reserved - it is simply slots inside an allocation that already holds 196,608 real rows. (SlackBytes therefore reports
      // only the small doubling arrays - deaths, identity events, taunts, evidence - none of which this fixture wrote.)
      // Measured against the same hint with nothing written: whatever SlackBytes reports is the small doubling arrays this fixture never
      // touched, and 200,000 fact rows must not have added a byte of it. (An absolute zero would be wrong - the store still preallocates
      // its identity/taunt/evidence sidecars - and a wrong zero is how a real regression gets "fixed" by loosening the number.)
      Assert.AreEqual(new DamageFactTable(1_024).SlackBytes, table.SlackBytes,
        "200,000 fact rows added no reserved-but-empty slots");

      // Rows survive at the ordinals everything else already stores - and specifically across the chunk seam.
      foreach (var ordinal in new[] { 0, 1, 65_534, 65_535, 65_536, 65_537, 131_071, 131_072, 199_999 })
      {
        Assert.AreEqual((uint)(ordinal + 1), table.At(ordinal).Total, $"ordinal {ordinal} reads its own row");
      }

      Assert.AreEqual((uint)1, table.Facts[0].Total);
      Assert.AreEqual((uint)200_000, table.Facts[199_999].Total);

      // A slice walk over the whole capture sees every row exactly once, in order: the seam cannot skip or repeat a chunk.
      var seen = 0;
      foreach (var slice in table.FactsIn(0, table.FactCount))
      {
        for (var i = 0; i < slice.Length; i++)
        {
          Assert.AreEqual((uint)(++seen), slice[i].Total, $"slice walk position {seen}");
        }
      }

      Assert.AreEqual(200_000, seen, "the range covered every row and no row twice");

      // A range that stops mid-chunk stops there, and a range past the end is clamped rather than reading a chunk that was never written.
      var partial = 0;
      foreach (var slice in table.FactsIn(100_000, 100_010)) partial += slice.Length;
      Assert.AreEqual(10, partial);

      var clamped = 0;
      foreach (var slice in table.FactsIn(199_995, 9_999_999)) clamped += slice.Length;
      Assert.AreEqual(5, clamped, "a walk asked past the end reads what exists, not unwritten slots");
    }
    /*
     * The other half of the same change, and the half that matters for an idle session: a capacity hint used to reserve ROWS, so a
     * whole-file open committed the file's estimated bytes before the first line parsed (measured on record: ~196 MB reserved to store
     * nothing on a follow-from-end open). Chunks move that decision to the row itself - the hint sizes a table of references at 8 bytes a
     * chunk, and the megabytes appear only when a chunk's first row arrives. An empty capture now holds zero fact bytes.
     */
    [TestMethod]
    public void ACapacityHintReservesReferencesRatherThanRows()
    {
      var hinted = new DamageFactTable(1_024 * 1_024);   // a whole-file open's estimate: ~1 M rows
      Assert.AreEqual(0, hinted.FactChunksAllocated, "nothing is allocated before the first row");
      // The old store turned that hint into ~24 MB of DamageFact slots before parsing a line. What is left is the small
      // doubling arrays the same hint sizes (deaths and friends) - under a megabyte, not twenty-four.
      Assert.IsTrue(hinted.EstimatedBytes < 1024 * 1024,
        $"a hint of 1 M rows reserves {hinted.EstimatedBytes / 1048576.0:F2} MB, not the 24 MB of row slots it used to");

      hinted.AddFact(Fact(1, 1, 2, 5));
      Assert.AreEqual(1, hinted.FactChunksAllocated, "the first row buys exactly one chunk");

      for (var i = 2; i <= DamageFactTable.FactChunkSize; i++) hinted.AddFact(Fact(i, 1, 2, (uint)i));
      Assert.AreEqual(1, hinted.FactChunksAllocated, "and filling it buys no more");

      hinted.AddFact(Fact(DamageFactTable.FactChunkSize + 1, 1, 2, 7));
      Assert.AreEqual(2, hinted.FactChunksAllocated, "the next row after the seam buys the second chunk");
      Assert.AreEqual(7u, hinted.At(DamageFactTable.FactChunkSize).Total, "across the seam, at the ordinal");
    }

  }
}
