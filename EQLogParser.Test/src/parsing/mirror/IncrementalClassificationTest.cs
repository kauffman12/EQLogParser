using EQLogParser.Mirror;

namespace EQLogParser;

/*
 * Incremental classification (ClassificationState): the rule book's expensive walks resume where the last pass
 * stopped instead of re-reading every fact, and a test that no carried aggregate can lag knowledge it cannot
 * have rather than contradict it.
 *
 * The single law behind every assertion here: A CARRIED PASS AND A FROM-ZERO REPLAY OVER THE SAME CAPTURE BUILD
 * BYTE-IDENTICAL STORES - EntityTimeline.StateStamp is the whole-store digest, so one comparison pins identities,
 * sources, intervals and strengths at once. Two traps make it worth pinning that way rather than by spot-checks:
 *
 *   - a cursor that resumes when it should not (an upstream verdict moved; a walk's answer depends on it) leaves
 *     the store MISSING a conclusion - invisible to "totals still add up" checks, which is what "stale rows with
 *     plausible numbers" means; and
 *   - a rule that rebuilds more eagerly than its gate requires writes the SAME conclusions twice in a different
 *     insertion order - harmless to the digest (it is a commutative sum), which is exactly why equality, not a
 *     count of walks, is the thing to assert.
 *
 * The roster-join test is the negative control for the cursor design itself: evidence that was already in the
 * table on an earlier pass must become claimable when a verdict change makes it COUNT (a healer verified late
 * retroactively qualifies every heal they ever cast - identity intervals span the whole log). A "resume always"
 * implementation would hide those old heals forever.
 */
[TestClass]
public class IncrementalClassificationTest
{
    private const double T0 = 1_000_000;

    // Fifteen verified casters: above R18's breadth gate (15) at full strength, and enough distinct healers
    // for R15's floor from any two of them. Names avoid every shape the rules special-case.
    private static readonly string[] Casters =
    [
      "Alder", "Betna", "Corvin", "Deliya", "Everun", "Farrel", "Gysla", "Harkos",
      "Iveela", "Jomm", "Kelen", "Lyriss", "Maben", "Nefra", "Ondrik",
    ];

    private const string Healed = "Grisel";               // Unknown, raid-healed: R15's shape
    // R18's shape is a name an NPC VERDICT holds on the wrong side - article shape claims it (R14) long before
    // R18 reads it. (`X`s ward` would NOT work: R5 claims those Pet Certain, already raid-side, no interval to mint.)
    private const string Ward = "A mossdraped statue";
    private const string Mob = "A mossback brutor";       // fought by the raid: graph's shape

    [TestInitialize]
    public void Setup()
    {
      PlayerRegistry.Instance.Clear();
      ConfigUtil.PlayerName = null;                       // R0 must not claim a fixture name from process state

      // Stage failure counts and retirements are PROCESS state by design (a poison fact should stop being paid
      // for all session); a test that wants the full rule book - which is every test here - claims it back.
      ClassificationRules.ResetRuleHealth();
    }

    [TestCleanup]
    public void Cleanup() => PlayerRegistry.Instance.Clear();

    /*
     * Healers must reach STRONG as raid-side for R15/R18 (a seed-only name is strength 8 - a hint, deliberately
     * under the gate: "the registry happens to know a name" must not launder a verdict into counting other
     * verdicts). A raid-join line is the cheapest Strong evidence there is - and it is also how a real roster
     * join mid-log reaches the rules, which is what the late-healer test needs as its trigger.
     */
    private static void JoinRaid(DamageFactTable facts, ref int seq, string name)
      => facts.AddEvidence(new EvidenceFact(seq++, (long)(T0 + 1), facts.InternName(name), EvidenceFact.EvJoinedRaid));

    // One classifying pass exactly the way DeriveEngine.Classify runs one: seed, then rules with the carry.
    private static (EntityTimeline Timeline, ClassificationOutcome Outcome) Pass(
      DamageFactTable facts, HealFactTable heals, ClassificationState state)
    {
      var timeline = new EntityTimeline();
      RegistrySeed.Apply(timeline, facts, double.NaN, double.NaN);
      var outcome = ClassificationRules.Apply(facts, timeline, heals, state);
      Assert.IsTrue(outcome.FailedRules.Count == 0, "a rule stage threw: " + string.Join(", ", outcome.FailedRules));
      return (timeline, outcome);
    }

    // A from-zero replay: fresh store, no carried state. This is the reference answer.
    private static (EntityTimeline Timeline, ClassificationOutcome Outcome) Replay(DamageFactTable facts, HealFactTable heals)
    {
      var timeline = new EntityTimeline();
      RegistrySeed.Apply(timeline, facts, double.NaN, double.NaN);
      var replayOutcome = ClassificationRules.Apply(facts, timeline, heals);
      Assert.IsTrue(replayOutcome.FailedRules.Count == 0, "replay stage threw: " + string.Join(", ", replayOutcome.FailedRules));
      return (timeline, replayOutcome);
    }

    private static void SeedHit(DamageFactTable facts, ref int seq, string atk, string def, long amount, double t)
      => facts.AddFact(new DamageFact(seq++, (long)(T0 + t), facts.InternName(atk), facts.InternName(def),
                                      total: (uint)amount, typeId: 1, flags: 0, modMask: 0, subIdx: ushort.MaxValue));

    private static void SeedHeal(HealFactTable heals, ref int seq, string healer, string healed, uint amount, double t)
      => heals.AddHeal(new HealFact(seq++, (long)(T0 + t), heals.InternName(healer), heals.InternName(healed),
                                    amount, overTotal: 0, typeId: LabelTypes.Heal, flags: 0, modMask: 0,
                                    subIdx: HealFact.NoSpell));

    /*
     * The crown assertion in its everyday shape: one table and one heal stream grown across three carried passes
     * (raid whittling a mob, the raid healing two names on our side of the inference ladder), compared against a
     * from-zero replay of the same final tables. Contains claims that must exist by the end - R15's claim, R18's
     * interval, OurPets - so the equality cannot pass on two stores that are both empty.
     */
    [TestMethod]
    public void ACarriedPassAndAFromZeroReplayBuildTheSameStore()
    {
      var facts = new DamageFactTable(64);
      var heals = new HealFactTable(facts);
      var seq = 0;
      var state = new ClassificationState();

      // Pass one: half the raid is seen, both inference subjects already named.
      for (var i = 0; i < 7; i++) JoinRaid(facts, ref seq, Casters[i]);
      for (var i = 0; i < 7; i++)
      {
        SeedHit(facts, ref seq, Casters[i], Mob, 900, i * 3);
        SeedHit(facts, ref seq, Mob, Casters[i], 250, i * 3 + 1);
        SeedHeal(heals, ref seq, Casters[i], Healed, 400, i * 3 + 2);
      }
      for (var i = 0; i < 8; i++) SeedHeal(heals, ref seq, Casters[i], Ward, 500, i * 4);
      var first = Pass(facts, heals, state);

      // Pass two: the pull continues; more healers arrive.
      for (var i = 7; i < Casters.Length; i++) JoinRaid(facts, ref seq, Casters[i]);
      for (var i = 7; i < Casters.Length; i++)
      {
        SeedHit(facts, ref seq, Casters[i], Mob, 850, 100 + i * 2);
        SeedHeal(heals, ref seq, Casters[i], Healed, 380, 100 + i * 2 + 1);
      }
      for (var i = 8; i < Casters.Length; i++) SeedHeal(heals, ref seq, Casters[i], Ward, 460, 100 + i);
      var second = Pass(facts, heals, state);

      // Pass three: a second mob, nothing else new - the common live shape (the digest freezes or nearly so).
      for (var i = 0; i < 5; i++) SeedHit(facts, ref seq, Casters[i], "A cave lion", 700, 300 + i);
      var third = Pass(facts, heals, state);

      // The fixtures must be claiming something, or equality would prove nothing.
      Assert.IsTrue(second.Outcome.OurPets.Contains(Ward), "R18 minted no interval through the carried passes");
      Assert.AreEqual(IdentityKind.Player, third.Timeline.IdentityWithSource(Healed, out var healedSource),
                      "R15 never claimed the raid-healed name across the carried passes");
      Assert.AreEqual("R15-healed", healedSource);

      var replay = Replay(facts, heals);
      Assert.AreEqual(replay.Timeline.StateStamp(), third.Timeline.StateStamp(),
                      "carried passes and a from-zero replay built different stores");
      Assert.AreEqual(replay.Outcome.OurPets.Count, third.Outcome.OurPets.Count);
      CollectionAssert.AreEquivalent(replay.Outcome.Conflicts.ToList(), third.Outcome.Conflicts.ToList());

      // And the intermediate timelines are not the point: only the final one must match - asserted above.
      Assert.AreNotEqual(first.Timeline.StateStamp(), third.Timeline.StateStamp(), "the capture never grew");
    }

    /*
     * The negative control for resuming at all: a healer verified AFTER their heals were already walked.
     * Identity intervals span the whole log, so every older heal from that caster retroactively qualifies;
     * R15's boundary digest moves (the seed's claims changed) and its walk rebuilds - a "resume always"
     * cursor would keep the candidate one-healer-short forever and the replay here would disagree.
     */
    [TestMethod]
    public void ARosterJoinLateStillCountsEveryOlderHeal()
    {
      var facts = new DamageFactTable(64);
      var heals = new HealFactTable(facts);
      var seq = 0;
      JoinRaid(facts, ref seq, Casters[0]);                // one Strong caster is below R15's two-healer floor
      for (var i = 0; i < 6; i++) SeedHeal(heals, ref seq, Casters[0], Healed, 300, i * 5);
      for (var i = 0; i < 6; i++) SeedHeal(heals, ref seq, "Pellin", Healed, 320, i * 5 + 2);
      SeedHit(facts, ref seq, Casters[0], Mob, 900, 1);    // give the tables a damage stream too

      var state = new ClassificationState();
      var first = Pass(facts, heals, state);
      Assert.AreEqual(IdentityKind.Unknown, first.Timeline.IdentityWithSource(Healed, out _),
                      "one Strong caster plus one unverified caster cleared R15");

      // The mid-log roster join: a NEW evidence line - and the digest moves because the rules now read
      // Pellin Strong, which retroactively qualifies every heal they ever cast.
      JoinRaid(facts, ref seq, "Pellin");
      var second = Pass(facts, heals, state);
      Assert.AreEqual(IdentityKind.Player, second.Timeline.IdentityWithSource(Healed, out var source),
                      "the late-verified healer's OLD heals were never re-counted - a cursor outran the verdict");
      Assert.AreEqual("R15-healed", source);

      var replay = Replay(facts, heals);
      Assert.AreEqual(replay.Timeline.StateStamp(), second.Timeline.StateStamp(),
                      "the rebuild after a roster join stored something a replay would not");
    }

    /*
     * Reset means "the next pass is a session's first": back to byte-identical with a never-used state. If any
     * cursor, set or digest outlives the reset, two sessions reading the same capture would disagree - and the
     * disagreement direction (a carried claim surviving onto an unrelated timeline) is the poisonous one.
     */
    [TestMethod]
    public void AResetStateRebuildsExactlyLikeAFreshOne()
    {
      var facts = new DamageFactTable(64);
      var heals = new HealFactTable(facts);
      var seq = 0;
      for (var i = 0; i < Casters.Length; i++) JoinRaid(facts, ref seq, Casters[i]);
      for (var i = 0; i < Casters.Length; i++)
      {
        SeedHit(facts, ref seq, Casters[i], Mob, 800, i * 4);
        SeedHeal(heals, ref seq, Casters[i], Ward, 450, i * 4 + 1);
      }

      var state = new ClassificationState();
      Pass(facts, heals, state);
      for (var i = 0; i < 6; i++) SeedHit(facts, ref seq, Casters[i], Mob, 640, 400 + i);   // grow it a little
      Pass(facts, heals, state);

      state.Reset();
      var afterReset = Pass(facts, heals, state);
      var replay = Replay(facts, heals);
      Assert.AreEqual(replay.Timeline.StateStamp(), afterReset.Timeline.StateStamp(),
                      "a reset state did not rebuild like a never-used one");
    }

    /*
     * The carry against the real corpus: pass 1 full, pass 2 carried over an unchanged capture (the digest must
     * freeze every gated rule), then the from-zero replay - one store, three ways. Gated on
     * EQLP_MIRROR_INCREMENT=<log> like the benchmark; the night exercises every stream the carry reads (evidence,
     * charm text, pets, graph) at a size where the from-zero walk is known to cost ~300 ms.
     */
    [TestMethod]
    public void CarriedPassMatchesAFullReplayOnRealLog()
    {
      var path = Environment.GetEnvironmentVariable("EQLP_MIRROR_INCREMENT");
      if (string.IsNullOrEmpty(path) || !File.Exists(path))
      {
        Assert.Inconclusive("set EQLP_MIRROR_INCREMENT=<capture log>");
        return;
      }

      var run = PipelineHarness.RunFileDerived(path);
      var state = new ClassificationState();

      var first = Pass(run.Facts, run.HealFacts, state);
      var second = Pass(run.Facts, run.HealFacts, state);          // carried: nothing new since pass 1
      var replay = Replay(run.Facts, run.HealFacts);               // from zero, no state

      Assert.AreEqual(replay.Timeline.StateStamp(), second.Timeline.StateStamp(),
                      "carried passes and a from-zero replay disagree on a real capture");
      Assert.AreEqual(replay.Outcome.OurPets.Count, second.Outcome.OurPets.Count);
      Assert.AreEqual(replay.Outcome.Charms.Count, second.Outcome.Charms.Count);
      TestContext.WriteLine($"real-log carry: {run.Facts.FactCount:N0} facts, charms {second.Outcome.Charms.Count}, " +
                            $"our-pets {second.Outcome.OurPets.Count}, stamp 0x{second.Timeline.StateStamp():X16}");
    }

    public TestContext TestContext { get; set; }
}
