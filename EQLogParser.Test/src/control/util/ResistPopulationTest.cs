using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser
{
  /*
   * StatsUtil.PopulateSpecials folds the stored resists into per-player counts, and it keys a ConcurrentDictionary on
   * two record fields. A ConcurrentDictionary throws on a null key — and because PopulateSpecials runs inside the damage
   * board build, ONE such record did not cost one count: it aborted the whole build, whose caller (MainWindow.BuildBoards)
   * catches, logs and leaves the panes showing their previous content. A frozen board and a stale board are the same report.
   *
   * The producer was fixed first (MiscLineParser's `resisted` branch minted a null caster whenever Player Name was unset —
   * see LineParsersTest.Process_Resist_YourSpellWithNoPlayerNameStillNamesAnActor). This file pins the reader, because a
   * store restored from an older session still carries records written before that fix and no parser runs over them again.
   */
  [DoNotParallelize]
  [TestClass]
  public sealed class ResistPopulationTest
  {
    [TestInitialize]
    public void Setup() => RecordsStore.Instance.Clear();

    [TestCleanup]
    public void Cleanup() => RecordsStore.Instance.Clear();

    private static PlayerStats StatsOver(long begin, long end)
    {
      var stats = new PlayerStats();
      stats.AllRanges.Add(new TimeSegment(begin, end));
      return stats;
    }

    [TestMethod]
    public void AResistWithNoCasterIsSkippedRatherThanEndingTheBuild()
    {
      // The old shape: `A corrupted egg resisted your Force of Flame XXI!` parsed with no Player Name.
      RecordsStore.Instance.Add(new ResistRecord { Attacker = null, Spell = "Force of Flame XXI", Defender = "A corrupted egg" }, 100);
      RecordsStore.Instance.Add(new ResistRecord { Attacker = "Reisil", Spell = "Doom of Severed Souls IV", Defender = "Zelnithak" }, 110);

      var stats = StatsOver(90, 120);
      StatsUtil.PopulateSpecials(stats, true);

      Assert.AreEqual(1, stats.ResistCounts.Count, "one key: the named caster. The hole contributes nothing rather than taking the build down.");
      Assert.AreEqual(1, stats.ResistCounts["Reisil"]["Doom of Severed Souls IV"],
          "the named resist on the NEXT record still counts: skipping one hole must not stop the walk");
    }

    [TestMethod]
    public void AResistWithNoSpellIsSkippedToo()
    {
      // The inner map keys on the spell, so a hole there is the same abort in the next line.
      RecordsStore.Instance.Add(new ResistRecord { Attacker = "Reisil", Spell = null, Defender = "Zelnithak" }, 100);
      RecordsStore.Instance.Add(new ResistRecord { Attacker = "Reisil", Spell = "Doom of Severed Souls IV", Defender = "Zelnithak" }, 110);

      var stats = StatsOver(90, 120);
      StatsUtil.PopulateSpecials(stats, true);

      Assert.AreEqual(1, stats.ResistCounts.Count);
      Assert.AreEqual(1, stats.ResistCounts["Reisil"].Count);
      Assert.AreEqual(1, stats.ResistCounts["Reisil"]["Doom of Severed Souls IV"]);
    }
  }
}
