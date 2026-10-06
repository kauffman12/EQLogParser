using EQLogParser;

namespace EQLogParser;

/*
 * The census's answer to "why does that read as one of ours?" when the answer is HEALING. R15 places a name from the
 * breadth of its crowd — so many DISTINCT raid-side casters, not so many lines (docs/DesignNotes.md → "Breadth of evidence, measured") — and `Row.HealedByCasters` carries that number so the window can say "Healed by 20 Raiders" instead of
 * printing the rule code and sending the operator to read the source.
 *
 * Four laws, one per test:
 *   - THE COUNT IS THE RULE'S OWN CROWD. Same gates as R15: self-heals and healers whose own identity is not Strong and
 *     raid-side do not count. A tooltip that counted a mob's own cleric would report a bigger crowd than the rule
 *     required, which is worse than no number — it defends a verdict the evidence does not support.
 *   - ONLY A HEAL-BASED VERDICT ASKS. A name placed by something stronger keeps zero here, and zero means "not asked",
 *     not "nobody healed them" (that name may be the most petted thing in the log).
 *   - THE WALK IS LAZY: a capture with no R15 row pays nothing for the heal stream.
 *   - Distinct CASTERS, not lines: one player healing twenty times is one.
 */
[TestClass]
[DoNotParallelize]
public class CensusHealProofTest
{
  [TestInitialize]
  public void Setup() => PlayerRegistry.Instance.Clear();

  [TestCleanup]
  public void Cleanup() => PlayerRegistry.Instance.Clear();

  /*
   * The crowd R15 counted is the crowd the tooltip shows. Two casters, twelve lines: the number a reader needs is 2,
   * because 2 is what cleared the rule's minimum; 12 lines from one of them would have proved nothing (the rule rejects
   * that shape on purpose — see AHealEdgeNeedsVolumeTwoCastersAndNoSwingsBack).
   */
  [TestMethod]
  public void AHealedVerdictCarriesHowManyNamesHealedIt()
  {
    var report = Census(HealLines("Mercless", 12));

    Assert.AreEqual("R15-healed", report.Find("Mercless")!.Reason);
    Assert.AreEqual(2, report.Find("Mercless")!.HealedByCasters,
        "the number beside a Healed verdict is the distinct casters that placed it, not the line count");
  }

  // Distinct names, not lines: one caster repeated twenty times is still one name.
  [TestMethod]
  public void TheCountIsDistinctCastersNotHealLines()
  {
    var report = Census(HealLines("Solohealee", 18).Concat(HealerthreeLines("Solohealee", 2)));

    // Eighteen of the twenty lines come from two players and only two more from a third; the crowd is three, because
    // breadth is what the rule scores. A line count here would read 20 and imply a raid.
    Assert.AreEqual("R15-healed", report.Find("Solohealee")!.Reason);
    Assert.AreEqual(3, report.Find("Solohealee")!.HealedByCasters);
  }

  /*
   * THE SAME GATES, OR THE TOOLTIP DEFENDS A LIE. The fixture heals the target from raid-side names, from a name whose
   * only claim is Medium (the owner of a pet — R15 refuses to let that launder anybody, and neither may the tooltip),
   * from NPCs, and from the target itself. Only the Strong raid-side casters count.
   */
  [TestMethod]
  public void TheCrowdCountsTheCastersTheRuleWouldCount()
  {
    var lines = HealLines("Wardenly", 12).ToList();

    // A Medium healer: an owner named only by its pet's line. It heals the target plenty.
    lines.Add("[Mon May 04 18:50:02 2026] Ownerone`s pet hits a frostbound sentinel for 900 points of damage.");
    for (var i = 0; i < 8; i++)
    {
      lines.Add($"[{Timestamp(19, 40, i * 20)}] Ownerone healed Wardenly for 3000 hit points by Blessed Radiance Rk. II.");
    }

    // Mob healers — this is what a boss healed by its own clerk looks like from below.
    lines.Add("[Mon May 04 18:50:05 2026] A crypt nurse healed Wardenly for 2200 hit points by Circle of Health.");
    lines.Add("[Mon May 04 18:50:09 2026] A crypt nurse healed Wardenly for 2200 hit points by Circle of Health.");

    // And the target tending itself, which says nothing about who claims it.
    lines.Add("[Mon May 04 18:51:00 2026] Wardenly healed Wardenly for 900 hit points by Blessed Radiance Rk. II.");

    var report = Census(lines);

    Assert.AreEqual("R15-healed", report.Find("Wardenly")!.Reason);
    Assert.AreEqual(2, report.Find("Wardenly")!.HealedByCasters,
        "a Medium owner, a mob and the name itself all joined the crowd the tooltip reports");
  }

  /*
   * A name that gets the whole raid's attention AND a Certain verdict from somewhere else keeps 0 — which is the file's
   * way of saying "this was never about healing", not "nobody healed them". That distinction is why the field is an int
   * with a documented zero rather than a nullable: the window only prints it for a heal-based verdict.
   */
  [TestMethod]
  public void ANamePlacedBySomethingStrongerReportsNoCrowd()
  {
    var lines = new List<string> { "[Mon May 04 18:50:01 2026] Targeted (NPC): Gnosis Warden" };
    lines.AddRange(HealLines("Gnosis Warden", 12));

    var report = Census(lines);
    var row = report.Find("Gnosis Warden")!;

    Assert.AreEqual(IdentityKind.Npc, row.Kind);
    Assert.AreEqual("R1-target", row.Reason, "the target frame outranks the heal edge, as it must");
    Assert.AreEqual(0, row.HealedByCasters,
        "0 means this verdict did not come from healing — printing a crowd here would rewrite its provenance");
  }

  // ---- helpers (same shapes IdentityRuleExtensionsTest drives R15 with) ----

  private static ClassificationReport Census(IEnumerable<string> lines)
  {
    var run = RunDerive(lines.ToArray());
    var timeline = new EntityTimeline();
    ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);
    return ClassificationReport.Build(timeline, run.Facts, run.HealFacts,
                                      IdentityOverrideStore.Instance, PlayerRegistry.Instance);
  }

  // Two Strong casters healing `target` `count` times over six minutes.
  private static IEnumerable<string> HealLines(string target, int count)
  {
    yield return "[Mon May 04 18:50:01 2026] Targeted (Player): Healerone";
    yield return "[Mon May 04 18:50:01 2026] Targeted (Player): Healertwo";
    for (var i = 0; i < count; i++)
    {
      var caster = i % 2 == 0 ? "Healerone" : "Healertwo";
      yield return $"[{Timestamp(19, 0, i * 30)}] {caster} healed {target} for 5000 (9000) hit points by Blessed Radiance Rk. II.";
    }
  }

  private static IEnumerable<string> OneCasterLines(string target, int count)
  {
    yield return "[Mon May 04 18:50:01 2026] Targeted (Player): Healerone";
    for (var i = 0; i < count; i++)
    {
      yield return $"[{Timestamp(19, 0, i * 30)}] Healerone healed {target} for 5000 (9000) hit points by Blessed Radiance Rk. II.";
    }
  }

  private static IEnumerable<string> HealerthreeLines(string target, int count)
  {
    yield return "[Mon May 04 18:50:01 2026] Targeted (Player): Healerthree";
    for (var i = 0; i < count; i++)
    {
      yield return $"[{Timestamp(19, 20, i * 30)}] Healerthree healed {target} for 5000 (9000) hit points by Blessed Radiance Rk. II.";
    }
  }

  private static string Timestamp(int hour, int minute, int secondOffset)
  {
    var t = new DateTime(2026, 5, 4, hour, 0, 0, DateTimeKind.Utc).AddMinutes(minute).AddSeconds(secondOffset);
    return $"Mon May {t.Day:00} {t.Hour:00}:{t.Minute:00}:{t.Second:00} 2026";
  }

  private static PipelineHarness.DeriveRunResult RunDerive(params string[] lines)
  {
    var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "census-heal-" + Guid.NewGuid().ToString("N")));
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
