using EQLogParser;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser.Wpf.Test;

[TestClass]
public sealed class QueuedSummarySnapshotTest
{
  [TestMethod]
  public void AQueuedSelectionMaterializesAgainstTheIndexThatSuppliedItsRows()
  {
    var facts = new DamageFactTable(0);
    var attacker = facts.InternName("Rune");
    var defender = facts.InternName("A test boss");
    const long time = 63_800_000_000L;
    facts.AddFact(new DamageFact(1, time, attacker, defender, 123, LabelTypes.Melee, 0, 0,
      facts.InternSubtype("slash")));
    var timeline = new EntityTimeline();
    timeline.SetIdentity("Rune", IdentityKind.Player, 100, "test");
    timeline.SetIdentity("A test boss", IdentityKind.Npc, 100, "test");
    var oldIndex = new FightFactIndex(timeline);
    var selected = FightProjection.Build(facts, timeline, oldIndex.OnFact);
    var snapshot = new DerivedSnapshot { Facts = facts, DamageIndex = oldIndex, Timeline = timeline, AllFights = selected,
      Heals = new HealFactTable(facts, 0) };
    var scheduled = new Queue<Action>();
    var gate = new SummaryBuildGate(scheduled.Enqueue);
    SummaryInput? answer = null;
    gate.Request(1, () => true);
    gate.Request(2, () => { answer = DeriveEngine.BuildSummaryInput(snapshot, selected); return true; });

    // A full projection replaced both row instances and index while this request waited.
    var replacement = new FightFactIndex(timeline);
    var fresh = FightProjection.Build(facts, timeline, replacement.OnFact);
    Assert.AreNotSame(selected[0], fresh[0]);
    Assert.AreEqual(0, replacement.DamageOrdinalsFor(selected[0]).Count, "the wrong index would silently answer empty");
    while (scheduled.TryDequeue(out var work)) work();
    Assert.IsNotNull(answer);
    Assert.AreEqual(1, answer.Fights.Count);
    Assert.AreEqual(123L, answer.Fights[0].DamageTotal);
  }

  [TestMethod]
  public void AManualRefreshOrClassEditIsNotDedupedAgainstThePreviousAnswer()
  {
    var old = MainWindow.SummaryKeyFor(1, [], 100, 0, gestureGeneration: 1);
    var refresh = MainWindow.SummaryKeyFor(1, [], 100, 0, gestureGeneration: 2);
    Assert.AreNotEqual(old, refresh);
  }
}
