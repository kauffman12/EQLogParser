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
        heals.AddHeal(new HealFact(i, 1_000 + i, rune, niktaza, (uint)(i * 5), overTotal: 3,
                                   typeId: LabelTypes.Heal, flags: 0, modMask: 0, subIdx: HealFact.NoSpell));
      }

      Assert.IsTrue(heals.SlackBytes > 0, "300 heals in a buffer that doubled to 512");
      var before = heals.Heals.ToArray();
      var slackBefore = heals.SlackBytes;

      var freed = heals.CompactToCount();

      Assert.AreEqual(before.Length, heals.HealCount, "no heal lost");
      Assert.AreEqual(slackBefore, freed, "the trim reports exactly the slack the buffer was holding");
      Assert.AreEqual(0L, heals.SlackBytes, "and nothing is left empty");
      for (var i = 0; i < before.Length; i++)
      {
        var now = heals.Heals[i];
        Assert.AreEqual(before[i].Total, now.Total, $"heal {i} kept its amount");
        Assert.AreEqual(before[i].HealerIdx, now.HealerIdx, "and its index into the shared name pool");
      }

      Assert.AreEqual("Niktaza", facts.NameOf(heals.Heals[0].HealedIdx), "the shared pool still answers across both tables");
    }

    /*
     * The policy. CombatCapture.CompactRows runs at the ingest gate and at most once per doubling of the capture, which is
     * also the only moment there is fresh slack (a doubling just copied these same bytes). The middle step is the one to
     * read: after a trim, ONE new fact doubles the array, slack is large again — and the answer is still "nothing", because
     * trimming there would be a full-table copy per fact for the rest of a live session.
     */
    [TestMethod]
    public void TheCaptureTrimsOncePerDoublingAndNeverBetween()
    {
      var table = new DamageFactTable(1_024);
      var capture = new CombatCapture(table);

      // Just past a doubling boundary: 262,145 facts in a 524,288-slot buffer leaves ~8 MB of empty slots, over the floor.
      for (var i = 1; i <= 262_145; i++) table.AddFact(Fact(i, 1, 2, (uint)i));
      Assert.IsTrue(table.SlackBytes > CombatCapture.MinSlackToCompact, "the fixture is over the floor it asks for");

      var first = capture.CompactRows();
      Assert.IsTrue(first > 0, "a load's leftover capacity goes back");

      Assert.AreEqual(0L, capture.CompactRows(), "an immediate second ask costs nothing");

      table.AddFact(Fact(262_146, 1, 2, 5));   // this is the add that doubles the buffer...
      Assert.IsTrue(table.SlackBytes > CombatCapture.MinSlackToCompact, "...so slack exists again right away");
      Assert.AreEqual(0L, capture.CompactRows(), "and it is still refused: no trim inside the same doubling");

      for (var i = 262_147; i <= 530_000; i++) table.AddFact(Fact(i, 1, 2, (uint)i));
      Assert.IsTrue(capture.CompactRows() > 0, "a doubling's worth of new facts earns the next trim");
      Assert.AreEqual(530_000, table.FactCount, "and no fact was lost across either trim");
    }
  }
}
