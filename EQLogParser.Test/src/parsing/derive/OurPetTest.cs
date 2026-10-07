using EQLogParser;

namespace EQLogParser;

/*
 * R18 and the ownership seam it rides in on: an NPC-verdict name that the raid heals from fifteen directions is
 * OURS, and its damage belongs on our side of the board (design doc §"Targeted (NPC) means not a player, never
 * 'not ours'").
 *
 * What these tests exist to hold, in order:
 *
 *   1. The fix does NOT touch identity. `Useless` carries `Targeted (NPC)` at Certain and the target frame is
 *      right - it is not a player. R18 writes an ownership INTERVAL instead of relabelling, so the identity
 *      assertion below still reads Npc/R1-target while the side flips. A version of this feature that "fixed" the
 *      name by demoting Certain evidence would break every other law in this directory.
 *   2. The gate is heal BREADTH (>= 15 distinct our-side casters), because that is the measured discriminator:
 *      pets take heals from 19-52 casters, every genuine hostile ever counted tops out at 10, and the crumb-heal
 *      shape (raid AoE ticking a mob for 1-2) sits at 8. Volume alone would flip bosses.
 *   3. A name that swings at us stays hostile however much healing it receives.
 *   4. A charm window explains heals instead of R18 - those names already belong to R9, and minting a whole-span
 *      ownership interval from heals cast during a charm would keep a hostile mob on our side outside the window.
 *   5. Ownership ends when the top-ups stop (+ tail), so a mob the raid later fights gets its row back.
 *   6. Damage our own side deals to our own pet earns NO credit and keys no row (policy, asked and answered
 *      2026-11: a meter must not pad a raider with a mechanic the raid chose to run - Elemental Conversion on
 *      their own wolf is not output). Only a CHARMED mob keeps its damage, because there the raid was genuinely
 *      fighting it; hiding-vs-counting still lives with CharmPetRows for those rows.
 *   7. petmapping.txt reaches the timeline at last, so `Dangle`'s damage folds under Strangle instead of
 *      sitting under a name no raid member owns.
 */
[TestClass]
public class OurPetTest
{
  private const string Pet = "Danglebait";
  private const string Mob = "a rune-etched warblade";

  // A raid-side healer roster big enough to clear the breadth gate, each with its own target-frame verdict.
  private static readonly string[] Casters =
  [
    "Healer01", "Healer02", "Healer03", "Healer04", "Healer05", "Healer06", "Healer07", "Healer08",
    "Healer09", "Healer10", "Healer11", "Healer12", "Healer13", "Healer14", "Healer15",
  ];

  // The util parses the bracketed form the log writes, so the helper adds them.
  private static double T(string bareStamp) => DateUtil.StandardDateToDotNetSeconds($"[{bareStamp}] x");

  private static DerivedFight? Row(List<DerivedFight> rows, string name)
    => rows.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

  [TestMethod]
  public void AnNpcTheWholeRaidHealsLosesItsEnemyRowAndKeepsItsVerdict()
  {
    var run = RunDerive(PetLines().ToArray());
    var timeline = new EntityTimeline();
    var outcome = ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);

    // The identity the log actually evidenced is untouched: Certain, from the target frame, still NPC.
    Assert.AreEqual(IdentityKind.Npc, timeline.IdentityWithSource(Pet, out var source));
    Assert.AreEqual("R1-target", source, "R18 relabelled the name instead of writing an interval");

    // ...and the interval says whose side it is on.
    Assert.IsTrue(timeline.IsOurPetAt(Pet, T(Timestamp(19, 5, 0))), "no ownership interval for a raid-healed NPC");
    CollectionAssert.Contains(outcome.OurPets, Pet);

    var rows = FightProjection.Build(run.Facts, timeline);

    // No enemy row carrying its name, and its swing landed inside the mob's row.
    Assert.IsNull(Row(rows, Pet), "our pet still keys a fight-list row of its own");
    var bear = Row(rows, "A rune-etched warblade");
    Assert.IsNotNull(bear, "the mob the raid and the pet were both hitting has no row");
    Assert.AreEqual(900 + 700, bear.DamageToOwner,
                    "the pet's damage did not fold in with the raid's");
  }

  /*
   * The two shapes that must NOT flip, both measured: the crumb-heal population (raid AoE ticking a hostile for
   * 1-2 hit points, `Hand of the King` at 8 casters) and the boss that answers for itself.
   */
  [TestMethod]
  public void CrumbHealsAndABossThatHitsBackStayHostile()
  {
    // Eight casters, generous amounts: under the breadth gate, so still the enemy's.
    var few = RunDerive(PetLines(casters: Casters[..8]).ToArray());
    var fewTimeline = new EntityTimeline();
    ClassificationRules.Apply(few.Facts, fewTimeline, few.HealFacts);
    Assert.IsFalse(fewTimeline.IsOurPetAt(Pet, T(Timestamp(19, 5, 0))), "8 casters cleared the breadth gate");

    // Its swings stay off our side: the mob's row counts the raid's 900 and nothing else, because a swing by an
    // NPC-side name at an NPC-side mob is not a raid fact at all (see FightProjection's both-hostile skip).
    var fewRows = FightProjection.Build(few.Facts, fewTimeline);
    Assert.AreEqual(900, Row(fewRows, "A rune-etched warblade")!.DamageToOwner,
                    "a name under the breadth gate folded its damage in with the raid's");

    // Fifteen casters AND it swings at the raid: the veto wins, because a pet hits mobs.
    var lines = PetLines().ToList();
    for (var i = 0; i < 10; i++)
      lines.Add($"[{Timestamp(19, 30, i * 6)}] {Pet} hits Healer01 for 4400 points of damage.");
    var boss = RunDerive(lines.ToArray());
    var bossTimeline = new EntityTimeline();
    ClassificationRules.Apply(boss.Facts, bossTimeline, boss.HealFacts);
    Assert.IsFalse(bossTimeline.IsOurPetAt(Pet, T(Timestamp(19, 5, 0))),
                   "the boss that got rained on by raid AoE became our pet");
  }

  [TestMethod]
  public void ACharmWindowExplainsThoseHealsInstead()
  {
    // The raid charms this mob and then keeps it topped up - fifteen casters of heals inside a window. R18 has
    // to keep out: the charm already explains the healing, and an ownership interval running the whole log would
    // hold a hostile mob on our side before the charm and after the wear-off.
    var lines = PetLines().ToList();
    lines.Add($"[{Timestamp(19, 2, 0)}] {Pet} has been charmed.");
    var run = RunDerive(lines.ToArray());

    var timeline = new EntityTimeline();
    var outcome = ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);

    Assert.AreEqual(1, outcome.Charms.Count, "the charm line produced no window");
    Assert.IsFalse(outcome.OurPets.Contains(Pet), "R18 minted ownership for a name a charm window owns");
    Assert.IsFalse(timeline.IsOurPetAt(Pet, T(Timestamp(19, 5, 0))), "the heals still minted an ownership interval");

    // Control: the window is there, so the skip was about who owns the name, not about a window that never opened.
    Assert.IsTrue(timeline.IsCharmedAt(Pet, T(Timestamp(19, 2, 30))), "the charm window itself did not open");
  }

  /*
   * Ownership is time-scoped. The top-ups stop (pet dismissed, owner logs off), and after the tail the name is
   * hostile again - observable as the raid's later swings on it keying a row of its own that is NOT hidden, as
   * opposed to the same swing inside the interval, which keys a hidden pet row.
   */
  [TestMethod]
  public void OwnershipEndsWhenTheHealingStops()
  {
    var lines = PetLines().ToList();

    // A raid swing an hour after the top-ups stopped.
    lines.Add($"[{Timestamp(20, 40, 0)}] You hit {Pet} for 4321 points of damage.");

    var run = RunDerive(lines.ToArray());
    var timeline = new EntityTimeline();
    ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);

    Assert.IsTrue(timeline.IsOurPetAt(Pet, T(Timestamp(19, 5, 0))), "ownership missing while it was being healed");
    Assert.IsFalse(timeline.IsOurPetAt(Pet, T(Timestamp(20, 40, 0))), "a pet nobody has healed for an hour is still ours");

    var rows = FightProjection.Build(run.Facts, timeline);
    var row = Row(rows, Pet);
    Assert.IsNotNull(row, "the hostile half after the tail has no row");
    Assert.IsFalse(row.RaidPet, "the post-tail row was hidden as a pet row: " + row.DamageTotal);
    Assert.AreEqual(4321, row.DamageTotal, "the wrong half of the name's history is on the row");
  }

  /*
   * The policy on our own side's damage to our own pet: no credit, no row. This is the one place the meter used to
   * pay out for a mechanic the raid ran on purpose - a raider's stray swing (or an Elemental Conversion burn) landing
   * on their own summon landed in their total. Friendly fire already dropped raider-on-raider and raider-on-mercenary;
   * a pet is the same case with a name the client prints as "NPC".
   *
   * What this does NOT do is lose the fight: the raid's real work on the mob stands untouched, and the exemption that
   * keeps damage on a CHARMED mob - a genuinely fought enemy the charm took off the target list - is asserted next
   * door in CharmRowProjectionTest, along with the hiding-is-display-only law that used to ride on this test.
   */
  [TestMethod]
  public void TheRaidSHitsOnTheirOwnPetEarnNoCreditAndKeyNoRow()
  {
    var lines = PetLines();

    // A stray swing inside the pull, plus one aimed by a spell that can only hit a pet (R24 proves THAT name).
    lines.Add($"[{Timestamp(19, 0, 7)}] You hit {Pet} for 1234 points of damage.");
    lines.Add($"[{Timestamp(19, 0, 8)}] Healer01 hit {Pet} for 4321 points of unresistable damage by Elemental Conversion VI.");
    var run = RunDerive(lines.ToArray());

    var timeline = new EntityTimeline();
    ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);
    var rows = FightProjection.Build(run.Facts, timeline);

    Assert.IsNull(Row(rows, Pet), "damage on our own pet still opened a row of its own");

    var mob = Row(rows, "A rune-etched warblade");
    Assert.IsNotNull(mob);
    Assert.AreEqual(900 + 700, mob.DamageToOwner,
                    "the raid's own pet-burn found its way onto the raid's damage: " + mob.DamageToOwner);

    // The facts themselves are not gone - a meter that never saw them could not say why nobody earned anything.
    var burned = 0;
    foreach (var f in run.Facts.Facts) if (f.Total == 4321) burned++;
    Assert.AreEqual(1, burned, "the pet-burn fact left the capture instead of going uncredited");
  }

  /*
   * petmapping.txt in the timeline (warm seeding). Before this, GetPetMappings() was read by the Pet Owners tab
   * and by nothing that classifies, so a mapped pet's damage reached a board under the PET's own name - and for
   * an NPC-verdict pet, on the enemy's side of it. OwnerOf is the field the roll-up folds on.
   */
  [TestMethod]
  public void AMappedPetIsOwnedInTheTimelineNotOnlyInTheGrid()
  {
    PlayerRegistry.Instance.Clear();
    try
    {
      // The harness clears the registry while parsing, so the operator's mapping goes in afterwards - which is
      // also the real order: the mapping was saved on an earlier session, this log is being reopened.
      var run = RunDerive(PetLines(casters: Casters[..3]).ToArray());
      PlayerRegistry.Instance.AddPetToPlayer(Pet, "Strangle");

      var timeline = new EntityTimeline();
      RegistrySeed.Apply(timeline, run.Facts, T(Timestamp(18, 50, 0)), T(Timestamp(19, 30, 0)));
      ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);

      Assert.AreEqual("Strangle", timeline.OwnerOf(Pet, T(Timestamp(19, 5, 0))),
                      "the operator's mapping never reached the timeline");
      Assert.IsTrue(timeline.IsOurPetAt(Pet, T(Timestamp(19, 5, 0))),
                    "a mapped pet with a target-frame NPC verdict is still on the enemy's side");

      // And it reaches the board: the pet's damage materializes as its owner's.
      var index = new FightFactIndex(timeline);
      var rows = FightProjection.Build(run.Facts, timeline, index.OnFact);
      var mob = Row(rows, "A rune-etched warblade");
      Assert.IsNotNull(mob);

      var records = FightSummarySource.Build([mob], index, run.Facts).Fights
          .SelectMany(f => f.DamageBlocks.SelectMany(b => b.Actions)).OfType<DamageRecord>().ToList();
      var petRecord = records.FirstOrDefault(r => r.Attacker == Pet);
      Assert.IsNotNull(petRecord, "the pet's damage never made it to the board");
      Assert.AreEqual("Strangle", petRecord.AttackerOwner, "the board does not know who owns that pet");
    }
    finally
    {
      PlayerRegistry.Instance.Clear();
    }
  }

  /*
   * The grid's "Unknown Pet Owner" rows (petmapping.txt holds them - `Squirticus` is one) still say the name is a
   * SUMMON, which is half the information and the half that moves the damage off the enemy column. What they must
   * not do is hand that damage to a person called "Unknown Pet Owner".
   */
  [TestMethod]
  public void AnUnmappedOwnerStillSaysTheNameIsOursButNobodySEarnsIt()
  {
    PlayerRegistry.Instance.Clear();
    try
    {
      var run = RunDerive(PetLines(casters: Casters[..3]).ToArray());
      PlayerRegistry.Instance.AddPetToPlayer(Pet, Labels.Unassigned);

      var timeline = new EntityTimeline();
      RegistrySeed.Apply(timeline, run.Facts, T(Timestamp(18, 50, 0)), T(Timestamp(19, 30, 0)));
      ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);

      Assert.IsTrue(timeline.IsOurPetAt(Pet, T(Timestamp(19, 5, 0))), "an unowned mapping said nothing");
      Assert.IsNull(timeline.OwnerOf(Pet, T(Timestamp(19, 5, 0))), "an invented raider owns the pet now");
    }
    finally
    {
      PlayerRegistry.Instance.Clear();
    }
  }

  // ---- helpers ----

  /*
   * A pull: the raid on a mob, the mob's own swings at a mob (never at us - that is the veto), and `count`
   * casters healing `Pet` twice each. The pet's verdict comes from the target frame, which is the case R15
   * cannot reach and therefore the case that has been keeping a row in the enemy column.
   */
  private static List<string> PetLines(string? target = null, string[]? casters = null)
  {
    var healedName = target ?? Pet;
    var who = casters ?? Casters;

    var lines = new List<string>();
    for (var i = 0; i < who.Length; i++)
      lines.Add($"[{Timestamp(18, 50, i)}] Targeted (Player): {who[i]}");
    lines.Add($"[{Timestamp(18, 52, 0)}] Targeted (NPC): {healedName}");

    /*
     * The fight the raid is actually in — and it has to stay INSIDE one engagement gap (30 s, the split that
     * starts a new fight row). Every assertion below reads a single mob row, so a pet's swing written a minute
     * after the raid's would be a second encounter rather than folded damage: correct behaviour, different test.
     */
    lines.Add($"[{Timestamp(19, 0, 0)}] You hit {Mob} for 900 points of damage.");
    lines.Add($"[{Timestamp(19, 0, 5)}] {Mob} hits You for 300 points of damage.");
    lines.Add($"[{Timestamp(19, 0, 20)}] {Pet} hits {Mob} for 700 points of damage.");

    for (var round = 0; round < 2; round++)
      for (var i = 0; i < who.Length; i++)
        lines.Add($"[{Timestamp(19, 2 + round, i * 3)}] {who[i]} healed {healedName} for 5000 (9000) hit points by Blessed Radiance Rk. II.");

    return lines;
  }

  private static string Timestamp(int hour, int minute, int secondOffset)
  {
    var t = new DateTime(2026, 5, 4, hour, 0, 0, DateTimeKind.Utc).AddMinutes(minute).AddSeconds(secondOffset);
    return $"Mon May {t.Day:00} {t.Hour:00}:{t.Minute:00}:{t.Second:00} 2026";
  }

  private static PipelineHarness.DeriveRunResult RunDerive(params string[] lines)
  {
    var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "derive-ourpet-" + Guid.NewGuid().ToString("N")));
    var log = Path.Combine(dir.FullName, "eqlog_Probeone_Eqgate.txt");   // filename seeds ConfigUtil.PlayerName
    try
    {
      File.WriteAllLines(log, lines);
      return PipelineHarness.RunFileDerived(log);
    }
    finally
    {
      try { Directory.Delete(dir.FullName, true); } catch (IOException) { }
    }
  }
}
