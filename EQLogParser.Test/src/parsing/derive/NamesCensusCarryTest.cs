using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

using EQLogParser;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParserTests.src.parsing.derive;

/*
 * The Names pane's census reads the timeline the last expensive derive pass published, instead of running the rule book a second time
 * for itself (DeriveEngine.BuildNameCensus). Re-measured cost of the duplicate: 186 ms on eqlog_Kizant_xegony.txt, 261 ms on
 * eqlog_Incogitable_xegony.txt — and with the tab open the pane refreshes on every derive, floored at 2 s, so a farm night paid that twice
 * a second for verdicts that were already in hand.
 *
 * Reuse is only legitimate if it answers the same questions, which is what these tests hold:
 *   - the verdict set is identical to a whole rule run (a census is a DISPLAY of a timeline, so two timelines built from one capture
 *     must say the same thing; if they ever differ the pane has been showing one build's answer next to another's);
 *   - an operator's own write still wins WITHOUT being replayed into the published instance — ClassificationReport asks the override
 *     store directly, because mutating a shared timeline would move its state stamp and buy a full rebuild with readers inside it;
 *   - taking a claim back falls back to what the rules saw, not to nothing.
 */
[TestClass]
public class NamesCensusCarryTest
{
  private static PipelineHarness.DeriveRunResult? _capture;

  private string _savedConfigDir = "";
  private string _savedServerName = "";
  private string _savedPlayerName = "";
  private string _tempDir = "";

  private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "mini-data", "derive", "mini-fight.txt");

  [TestInitialize]
  public void Setup()
  {
    _savedConfigDir = ConfigUtil.ConfigDir;
    _savedServerName = ConfigUtil.ServerName;
    _savedPlayerName = ConfigUtil.PlayerName;

    _tempDir = Path.Combine(Path.GetTempPath(), "carry-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(_tempDir);
    ConfigUtil.ConfigDir = _tempDir;
    ConfigUtil.ServerName = "Census Carry";
    ConfigUtil.PlayerName = "Carrytester";

    PlayerRegistry.Instance.Clear();
    IdentityOverrideStore.Instance.Init("Census Carry");
    IdentityPriorStore.Instance.Init("Census Carry");
  }

  [TestCleanup]
  public void Cleanup()
  {
    PlayerRegistry.Instance.Clear();
    IdentityOverrideStore.Instance.Init("carry-cleanup-" + Guid.NewGuid().ToString("N"));
    IdentityPriorStore.Instance.Init("carry-cleanup-" + Guid.NewGuid().ToString("N"));

    ConfigUtil.ConfigDir = _savedConfigDir;
    ConfigUtil.ServerName = _savedServerName;
    ConfigUtil.PlayerName = _savedPlayerName;
  }

  private static PipelineHarness.DeriveRunResult Capture() =>
    _capture ??= PipelineHarness.RunFileDerived(FixturePath);

  private static double LogStartS() => Capture().Facts.FactCount > 0 ? Capture().Facts.Facts[0].TimeS : double.NaN;

  private static double LogEndS()
  {
    var facts = Capture().Facts;
    return facts.FactCount > 0 ? facts.Facts[facts.FactCount - 1].TimeS : double.NaN;
  }

  /*
   * What DeriveEngine.Classify does for one expensive pass: seed, rules, operator file last. `applyOverrides: false` yields the state
   * of a pass that ran BEFORE the operator's latest click — which is exactly what the pane holds between writes now that it carries the
   * published instance instead of rebuilding it. Without that switch this helper replayed the file every time, and the override test
   * below "passed" its first assertion for the wrong reason: the timeline already said Manual.
   */
  private static EntityTimeline FullPassTimeline(bool applyOverrides = true)
  {
    var timeline = new EntityTimeline();
    RegistrySeed.Apply(timeline, Capture().Facts, LogStartS(), LogEndS());
    ClassificationRules.Apply(Capture().Facts, timeline, Capture().HealFacts);
    if (applyOverrides) IdentityOverrideStore.Instance.Apply(timeline);
    return timeline;
  }

  private static ClassificationReport Census(EntityTimeline? timeline) =>
    ClassificationReport.Build(timeline, Capture().Facts, Capture().HealFacts, IdentityOverrideStore.Instance,
                              PlayerRegistry.Instance, IdentityPriorStore.Instance);

  // Row-by-row comparison of the two columns a reader actually sees: kind and the reason word beside it.
  private static List<string> Verdicts(ClassificationReport report) =>
    report.Rows.Select(r => $"{r.Name}|{r.Kind}|{r.Reason}").ToList();

  [TestMethod]
  public void ACensusOverTheCarriedTimeline_AgreesWithAWholeRuleRun()
  {
    var carried = Census(FullPassTimeline());                       // what the pane reads now
    var rebuilt = Census(FullPassTimeline());                       // what it used to compute for itself

    Assert.IsTrue(carried.Rows.Count > 0, "the fixture produced no census rows");
    CollectionAssert.AreEqual(Verdicts(rebuilt), Verdicts(carried));
  }

  [TestMethod]
  public void AnOverrideWrittenAfterThePass_WinsWithoutTouchingTheTimeline()
  {
    // Find a name the rules placed, then overrule it the way the Type dropdown does — writing the file only, never the timeline.
    var placed = Census(FullPassTimeline()).Rows.FirstOrDefault(r => r.Kind is IdentityKind.Player or IdentityKind.Npc);
    if (placed is null) Assert.Inconclusive("the fixture's rules placed nobody, so there is nothing to overrule");

    var other = placed.Kind == IdentityKind.Player ? IdentityKind.Npc : IdentityKind.Player;
    IdentityOverrideStore.Instance.Set(placed.Name, other);

    // A timeline from a pass that predates the write — nothing replayed into it. The census must still show the operator.
    var stale = FullPassTimeline(applyOverrides: false);
    Assert.AreEqual(placed.Kind, stale.IdentityWithSource(placed.Name, out _), "the fixture's rules stopped placing the name");

    var after = Census(stale).Find(placed.Name);
    Assert.IsNotNull(after);
    Assert.AreEqual(other, after.Kind, "the rules' stale verdict outranked the player's own click");
    Assert.AreEqual("Manual", after.Reason);

    // And that published instance was not edited to get there: it still says what the rules said.
    Assert.AreEqual(placed.Kind, stale.IdentityWithSource(placed.Name, out _));
  }

  [TestMethod]
  public void TakingTheClaimBack_FallsBackToWhatTheRulesSaw()
  {
    var placed = Census(FullPassTimeline()).Rows.FirstOrDefault(r => r.Kind is IdentityKind.Player or IdentityKind.Npc);
    if (placed is null) Assert.Inconclusive("the fixture's rules placed nobody");

    IdentityOverrideStore.Instance.Set(placed.Name, IdentityKind.Pet);
    Assert.AreEqual(IdentityKind.Pet, Census(FullPassTimeline(applyOverrides: false)).Find(placed.Name)!.Kind);

    IdentityOverrideStore.Instance.Remove(placed.Name);
    var back = Census(FullPassTimeline(applyOverrides: false)).Find(placed.Name);
    Assert.IsNotNull(back);
    Assert.AreEqual(placed.Kind, back.Kind, "clearing a claim left the row on the override instead of the rules");
    Assert.AreEqual(placed.Reason, back.Reason);
  }

  [TestMethod]
  public void TwoTimelinesFromOneCapture_AgreeOnEveryName()
  {
    // The law underneath the reuse: a census is a display of a timeline, so the rule book must be deterministic over one capture.
    // (This is also the guard against a rule that reads process state it should not — the pet-claim drift this project measured shows
    // up here as two runs disagreeing, which is exactly why the comparison is over Kind AND reason rather than a count.)
    var a = Census(FullPassTimeline());
    var b = Census(FullPassTimeline());

    var firstOnly = a.Rows.Where(r => b.Find(r.Name)?.Kind != r.Kind).ToList();
    Assert.AreEqual(0, firstOnly.Count,
                    "two rule runs disagree: " + string.Join(", ", firstOnly.Take(8).Select(r => $"{r.Name} {r.Kind} vs {b.Find(r.Name)?.Kind}")));
  }

  /*
   * The cost the pane stopped paying, on a real capture. Gated like the other probes (EQLP_NAMES_CENSUS_COST=<log>): run it with
   * EQLP_EMU=1 for an EMU capture. It ASSERTS the agreement on real data and prints the two timings, so the number in DesignNotes has a
   * command behind it rather than a memory.
   */
  [TestMethod]
  public void OnARealCapture_CarriedCensusMatchesARuleRunAndIsTheCheaperHalf()
  {
    var path = Environment.GetEnvironmentVariable("EQLP_NAMES_CENSUS_COST");
    if (string.IsNullOrEmpty(path)) Assert.Inconclusive("set EQLP_NAMES_CENSUS_COST=<log> to run this over a real capture");
    if (!File.Exists(path)) Assert.Inconclusive($"no such file: {path}");

    var run = PipelineHarness.RunFileDerived(path);
    var facts = run.Facts;
    double start = facts.FactCount > 0 ? facts.Facts[0].TimeS : double.NaN;
    double end = facts.FactCount > 0 ? facts.Facts[facts.FactCount - 1].TimeS : double.NaN;

    EntityTimeline Build()
    {
      var timeline = new EntityTimeline();
      RegistrySeed.Apply(timeline, facts, start, end);
      ClassificationRules.Apply(facts, timeline, run.HealFacts);
      IdentityOverrideStore.Instance.Apply(timeline);
      return timeline;
    }

    var ruleBook = Stopwatch.StartNew();
    var carried = Build();
    ruleBook.Stop();
    var censusOnly = Stopwatch.StartNew();
    var reportOverCarried = ClassificationReport.Build(carried, facts, run.HealFacts, IdentityOverrideStore.Instance,
                                                      PlayerRegistry.Instance, IdentityPriorStore.Instance);
    censusOnly.Stop();

    var fresh = Build();
    var reportFresh = ClassificationReport.Build(fresh, facts, run.HealFacts, IdentityOverrideStore.Instance,
                                                PlayerRegistry.Instance, IdentityPriorStore.Instance);

    Assert.AreEqual(reportFresh.Rows.Count, reportOverCarried.Rows.Count);
    var disagreements = reportFresh.Rows.Where(r => reportOverCarried.Find(r.Name)?.Kind != r.Kind).ToList();
    Assert.AreEqual(0, disagreements.Count,
                    "real capture: rule runs disagree for " + string.Join(", ", disagreements.Take(8).Select(r => r.Name)));

    Console.WriteLine($"[census-cost] {Path.GetFileName(path)}: rule book (the half the pane no longer pays) {ruleBook.ElapsedMilliseconds} ms; "
                    + $"report over the carried timeline {censusOnly.ElapsedMilliseconds} ms; rows {reportOverCarried.Rows.Count}");
  }
}
