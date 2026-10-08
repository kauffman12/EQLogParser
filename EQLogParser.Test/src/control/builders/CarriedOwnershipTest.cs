using EQLogParser;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser;

/*
 * What a cell cache has to know about ownership — three tests, one of which exists because it disproved the assumption behind it.
 *
 * `DamageStatsBuilder` places a record on a ROW using its own owner word (`AttackerOwner`, per-record) and two learned maps (`_petToPlayer`,
 * `_playerPets`). Two facts follow, and they point at different cache designs:
 *
 *  - **Within one build, placement is order-free.** Learning happens in the grouping pass, which sweeps every block of the selection before any
 *    record is counted, so the maps are complete by the time placement runs. A cell therefore does NOT need "what my prefix had learned so far"; it
 *    needs the maps of the whole selection, computed once and stamped with it. (Test: `OrderInsideOneBuildDoesNotMoveDamageBecauseLearningIsAPrePass`.)
 *  - **Placement depends on the SELECTION containing the teaching record.** A window or row set that omits every line naming an owner answers on the
 *    pet's own name — same record, different row, nothing thrown. That is the real hazard for cached cells: a cell computed over a narrower selection
 *    than the one being answered may disagree with the live walk, which is why cell identity carries the selection stamp (and why the answer stamp,
 *    not a per-cell map, is the right key). (Tests below.)
 *
 * Measured reach (docs/DesignNotes.md → "What a cached walk is allowed to carry"; three captures): the record's own owner word covers 20.7 % / 30.0 %
 * / 45.1 % of records; the learned fallback carries up to **8.9 %** of a group night (312,944 of 3.5 M on eqlog_Roper_thj.txt); and the records whose
 * placement would change if their owner claim were outside the selection are 0.010 %–7.79 %. So carry-and-stamp is cheap; ignoring ownership is not.
 *
 * Absolute numbers are asserted first throughout: an equality between two wrong boards would pass.
 */
[TestClass]
[DoNotParallelize]
public class CarriedOwnershipTest
{
  private const double T0 = FixtureTime.Base;

  /*
   * One row of fight content: the same pet name attacking twice, once on a line that names its owner ("Fido" as Reisil's pet) and once on a line
   * that does not. The first is what teaches the walk; the second is placed by whatever has been learned when it arrives.
   */
  private static Fight PetFight(double teachAt, double bareAt, uint teachAmount, uint bareAmount)
  {
    var fight = new Fight
    {
      Id = 1,
      Name = "an ice lion",
      BeginDamageTime = Math.Min(teachAt, bareAt),
      LastDamageTime = Math.Max(teachAt, bareAt),
      DamageTotal = teachAmount + bareAmount,
      DamageHits = 2,
    };

    var claim = new ActionGroup { BeginTime = teachAt };
    claim.Actions.Add(new DamageRecord { Attacker = "Fido", AttackerOwner = "Reisil", Defender = "an ice lion", Total = teachAmount, Type = Labels.Melee, SubType = Labels.Melee });
    fight.DamageBlocks.Add(claim);

    var bare = new ActionGroup { BeginTime = bareAt };
    bare.Actions.Add(new DamageRecord { Attacker = "Fido", Defender = "an ice lion", Total = bareAmount, Type = Labels.Melee, SubType = Labels.Melee });
    fight.DamageBlocks.Add(bare);

    return fight;
  }

  private static CombinedStats Build(Fight fight)
  {
    var range = new TimeRange();
    range.Add(new TimeSegment(fight.BeginDamageTime, fight.LastDamageTime));

    var options = new GenerateStatsOptions { AllRanges = range, Source = "carried ownership test" };
    options.Npcs.Add(fight);

    DamageStatsBuilder.Instance.BuildTotalStats(options);
    var stats = DamageStatsBuilder.Instance.GetLastStats()?.CombinedStats;
    Assert.IsNotNull(stats, "the builder produced no board");
    return stats;
  }

  private static bool HasTopLevel(CombinedStats stats, string name) =>
    stats.StatsList.Any(s => s.Name == name);

  private static long TopTotal(CombinedStats stats, string name) =>
    stats.StatsList.First(s => s.Name == name).Total;

  /// <summary>The rows this law is about, as ONE comparable string — a List compared with AreEqual compares references, which passes on nothing.</summary>
  private static string PlacementOf(CombinedStats stats) =>
    string.Join("|", stats.StatsList
      .Where(x => x.Name.StartsWith("Fido", StringComparison.Ordinal) || x.Name == "Reisil +Pets")
      .Select(x => $"{x.Name}={x.Total}")
      .OrderBy(x => x, StringComparer.Ordinal));

  [TestMethod]
  public void AWalkThatSawTheOwnerClaimFoldsBothRecordsUnderTheOwner()
  {
    // Teaching record first: by the time the bare-name record arrives, `Fido` is known as Reisil's pet.
    var stats = Build(PetFight(T0, T0 + 5, 100, 50));

    Assert.IsTrue(HasTopLevel(stats, "Reisil +Pets"),
      $"expected the owner aggregate; board was [{string.Join(", ", stats.StatsList.Select(s => s.Name))}]");
    Assert.AreEqual(150L, TopTotal(stats, "Reisil +Pets"), "both records belong to the owner's row once ownership is known");
    Assert.IsFalse(HasTopLevel(stats, "Fido"), "a known pet does not get its own top-level row");
  }

  [TestMethod]
  public void AWalkThatMissedTheOwnerClaimPutsTheSameRecordOnThePetsOwnRow()
  {
    /*
     * The hazard in its real shape: not a cold START in the middle of a walk (that cannot happen inside one build, see below), but a selection that
     * never contains the teaching record — a meter window that begins after the owner claim scrolled out, a subset of rows, a cached cell built for a
     * narrower question. Same record as above, different row, and nothing throws: the board just credits a different line. This is why cell identity
     * carries the selection stamp rather than trusting a per-cell map.
     */
    var cold = new Fight
    {
      Id = 1,
      Name = "an ice lion",
      BeginDamageTime = T0 + 5,
      LastDamageTime = T0 + 5,
      DamageTotal = 50,
      DamageHits = 1,
    };
    var onlyBare = new ActionGroup { BeginTime = T0 + 5 };
    onlyBare.Actions.Add(new DamageRecord { Attacker = "Fido", Defender = "an ice lion", Total = 50, Type = Labels.Melee, SubType = Labels.Melee });
    cold.DamageBlocks.Add(onlyBare);

    var stats = Build(cold);

    Assert.IsTrue(HasTopLevel(stats, "Fido"), "with nothing learned, the pet forms its own row");
    Assert.AreEqual(50L, TopTotal(stats, "Fido"));
    Assert.IsFalse(HasTopLevel(stats, "Reisil +Pets"), "the owner cannot be credited a pet this walk never heard of");
  }

  [TestMethod]
  public void OrderInsideOneBuildDoesNotMoveDamageBecauseLearningIsAPrePass()
  {
    /*
     * This test was written to prove the opposite and FAILED into the finding: the same two records, claim first or claim second, produce the same
     * board — "Reisil +Pets = 150", no "Fido" row either way. The reason is in BuildTotalStatsCore: `UpdatePetMapping` runs during the GROUPING pass,
     * which sweeps every block of the selection before `ComputeDamageStats` counts a single record. So within one build the maps are complete before
     * placement happens, and record order cannot move damage.
     *
     * That is a law worth pinning in both directions:
     *   - if somebody moves learning INTO the counting walk (the obvious way to make a resumable cell), order starts mattering and this test breaks —
     *     loudly, on a fixture, instead of quietly splitting an owner's night across two rows;
     *   - and it tells the cache what to carry: NOT a per-cell snapshot of "what this prefix had learned", but the maps for the WHOLE selection,
     *     computed once (204 entries on a full raid night, measured) and stamped with the selection. A cell that carries its own partial map would be
     *     answering a question the live walk never asks.
     */
    var claimFirst = Build(PetFight(T0, T0 + 5, 100, 50));
    var claimLast = Build(PetFight(T0 + 10, T0 + 5, 100, 50));

    Assert.AreEqual(PlacementOf(claimFirst), PlacementOf(claimLast),
      $"learning is a pre-pass, so order must not matter: [{PlacementOf(claimFirst)}] vs [{PlacementOf(claimLast)}]");
    Assert.AreEqual("Reisil +Pets=150", PlacementOf(claimLast));
    Assert.IsFalse(HasTopLevel(claimLast, "Fido"));
  }

  [TestCleanup]
  public void Cleanup()
  {
    // The builder is a singleton; leave no board behind for the next test in the run.
    DamageStatsBuilder.Instance.BuildTotalStats(new GenerateStatsOptions { AllRanges = new TimeRange(), Source = "carried ownership cleanup" });
  }
}
