using System;
using System.Collections.Generic;
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
    var published = FullPassTimeline();
    var stamp = published.StateStamp();
    var carried = Census(published);                                // the pane reads this published instance
    var rebuilt = Census(FullPassTimeline());                       // an independent rule replay

    Assert.IsTrue(carried.Rows.Count > 0, "the fixture produced no census rows");
    CollectionAssert.AreEqual(Verdicts(rebuilt), Verdicts(carried), "every displayed name, kind and reason must agree");
    Assert.AreEqual(stamp, published.StateStamp(), "a display read must not mutate the published timeline");
  }

  [TestMethod]
  public void AnOverrideWrittenAfterThePass_WinsWithoutTouchingTheTimeline()
  {
    // Find a name the rules placed, then overrule it the way the Type dropdown does — writing the file only, never the timeline.
    var placed = Census(FullPassTimeline()).Rows.FirstOrDefault(r => r.Kind is IdentityKind.Player or IdentityKind.Npc);
    Assert.IsNotNull(placed, "the fixture's rules placed nobody, so there is nothing to overrule");

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
    Assert.IsNotNull(placed, "the fixture's rules placed nobody, so there is nothing to take back");

    IdentityOverrideStore.Instance.Set(placed.Name, IdentityKind.Pet);
    Assert.AreEqual(IdentityKind.Pet, Census(FullPassTimeline(applyOverrides: false)).Find(placed.Name)!.Kind);

    IdentityOverrideStore.Instance.Remove(placed.Name);
    var back = Census(FullPassTimeline(applyOverrides: false)).Find(placed.Name);
    Assert.IsNotNull(back);
    Assert.AreEqual(placed.Kind, back.Kind, "clearing a claim left the row on the override instead of the rules");
    Assert.AreEqual(placed.Reason, back.Reason);
  }

}
