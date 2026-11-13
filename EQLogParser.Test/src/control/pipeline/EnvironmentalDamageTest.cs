using System.Collections.Concurrent;
using System.Threading;

namespace EQLogParser;

[TestClass]
public class EnvironmentalDamageTest
{
  private const string BareHit = "You were hit by non-melee for 16 damage.";
  private const string Falling = "YOU were injured by falling.";
  private bool _priorEmu;
  private string _priorPlayerName = "";

  private sealed class NullSinks : IChatSink, ITriggerHook
  {
    public void Init() { }
    public void Add(ChatType chat) { }
    public void CheckQuickShare(ChatType chat, string action, double beginTime) { }
  }

  [TestInitialize]
  public void Setup()
  {
    _priorEmu = AppSettings.IsEmuParsingEnabled;
    _priorPlayerName = ConfigUtil.PlayerName;
    ConfigUtil.PlayerName = "Testplayer";
    PipelineHarness.EnsureDataStore();
  }

  [TestCleanup]
  public void Cleanup()
  {
    AppSettings.IsEmuParsingEnabled = _priorEmu;
    ConfigUtil.PlayerName = _priorPlayerName;
  }

  [TestMethod]
  [DataRow(false, Falling, 0)]
  [DataRow(false, "YOU are burning!", 0)]
  [DataRow(false, "YOU are freezing!", 0)]
  [DataRow(false, "YOU were crushed.", 0)]
  [DataRow(false, "YOU were sliced by a pendulum.", 0)]
  [DataRow(false, "Anything else.", 0)]
  [DataRow(true, Falling, 0)]
  [DataRow(true, "YOU are burning!", 1)]
  [DataRow(true, "YOU are freezing!", 1)]
  [DataRow(true, "YOU were crushed.", 1)]
  [DataRow(true, "YOU were sliced by a pendulum.", 1)]
  [DataRow(true, "You was burned.", 1)]
  [DataRow(true, "Anything else.", 1)]
  public void OnlyConfirmedFallsAreDroppedOnEmu(bool emu, string cause, int expected)
  {
    var (hits, _) = Feed(emu, [BareHit, cause]);
    Assert.HasCount(expected, hits);
    if (expected == 0) return;
    Assert.AreEqual(Labels.Unk, hits[0].Record.Attacker);
    Assert.AreEqual("Testplayer", hits[0].Record.Defender);
    Assert.AreEqual(16L, hits[0].Record.Total);
    Assert.AreEqual(Labels.Dd, hits[0].Record.Type);
    Assert.AreEqual(BareHit, hits[0].Action);
    Assert.AreEqual(FixtureTime.Base, hits[0].BeginTime);
    Assert.IsTrue(hits[0].IsMonitor);
  }

  [TestMethod]
  public void FallingPairCrossesBatchesAndTheOneObservedStunMessage()
  {
    var (hits, _) = Feed(true, [BareHit, "You are no longer stunned.", Falling]);
    Assert.HasCount(0, hits);
  }

  [TestMethod]
  public void AnUnconfirmedHitAtEndOfInputIsKeptOnlyOnEmu()
  {
    Assert.HasCount(0, Feed(false, [BareHit]).Hits);
    Assert.HasCount(1, Feed(true, [BareHit]).Hits);
  }

  [TestMethod]
  public void AnInterveningLineOrLateCauseCannotEraseAnEmuHit()
  {
    Assert.HasCount(1, Feed(true, [BareHit, "Anything else.", Falling]).Hits);
    Assert.HasCount(1, Feed(true, [BareHit, "Testplayer says, 'hello'", Falling]).Hits);
    Assert.HasCount(1, Feed(true, [BareHit, Falling], causeDelay: 2).Hits);
    Assert.HasCount(1, Feed(true, [BareHit, "You are no longer stunned.", Falling], causeDelay: 2).Hits);
  }

  [TestMethod]
  public void NonFallingEmuHitsKeepTheirOrderAndTheSentenceBeforeDeath()
  {
    const string stun = "You are no longer stunned.";
    const string death = "Testplayer has been slain by a gnawed ghoul.";
    var (hits, deaths) = Feed(true, [BareHit, stun, death, "You were hit by non-melee for 23 damage."]);
    CollectionAssert.AreEqual(new long[] { 16, 23 }, hits.Select(h => h.Record.Total).ToArray());
    Assert.HasCount(1, deaths);
    Assert.AreEqual(stun, deaths[0].Record.Previous);
  }

  [TestMethod]
  public void ANamedEmuHitIsKeptEvenWhenFollowedByFalling()
  {
    const string namedHit = "Bulron was hit by non-melee for 10 damage.";
    var (hits, _) = Feed(true, [namedHit, Falling]);
    Assert.HasCount(1, hits);
    Assert.AreEqual("Bulron", hits[0].Record.Defender);
    Assert.AreEqual(Labels.Unk, hits[0].Record.Attacker);
    Assert.AreEqual(10L, hits[0].Record.Total);
  }

  [TestMethod]
  public void AHeldEmuHitIsPublishedBeforeTheUnrelatedHitThatReleasesIt()
  {
    const string nextHit = "Demonstrated Depletion was hit by non-melee for 6734 points of damage.";
    var (hits, _) = Feed(true, [BareHit, nextHit]);
    CollectionAssert.AreEqual(new long[] { 16, 6734 }, hits.Select(h => h.Record.Total).ToArray());
    CollectionAssert.AreEqual(new[] { BareHit, nextHit }, hits.Select(h => h.Action).ToArray());
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void PointsOfDamageAndDamageShieldsAreUntouched(bool emu)
  {
    var (hits, _) = Feed(emu,
    [
      "Demonstrated Depletion was hit by non-melee for 6734 points of damage.",
      "A knight of loathing is pierced by Testplayer's thorns for 210301 points of non-melee damage.",
      "YOU are burned by A knight of loathing's flames for 120 points of non-melee damage.",
      "YOU are chilled to the bone for 2700 points of non-melee damage!"
    ]);
    CollectionAssert.AreEqual(new long[] { 6734, 210301, 120, 2700 }, hits.Select(h => h.Record.Total).ToArray());
    Assert.AreEqual("Testplayer", hits[1].Record.Attacker);
    Assert.AreEqual(Labels.Ds, hits[1].Record.Type);
    Assert.AreEqual("A knight of loathing", hits[2].Record.Attacker);
    Assert.AreEqual(Labels.Rs, hits[3].Record.Attacker);
  }

  [TestMethod]
  public void GluedLinesStillConfirmAnEmuFall()
  {
    var (hits, _) = Feed(true, [BareHit + "[Wed Jan 01 00:00:00 2025] " + Falling]);
    Assert.HasCount(0, hits);
  }

  [TestMethod]
  public void AnIdleMonitorPublishesUnconfirmedDamageWithoutWaitingForAnotherLine()
  {
    AppSettings.IsEmuParsingEnabled = true;
    using var seen = new ManualResetEventSlim();
    var lane = new ParseLane();
    lane.DamageProcessed += _ => seen.Set();
    using var items = new BlockingCollection<LogReaderItem[]>();
    using var processor = new LogProcessor(new NullSinks(), new NullSinks(), lane);
    processor.LinkTo(items);
    items.Add([Item(BareHit)]);
    try
    {
      Assert.IsTrue(seen.Wait(TimeSpan.FromSeconds(10)), "an idle EMU tail lost its unconfirmed hit");
    }
    finally
    {
      items.CompleteAdding();
      Assert.IsTrue(processor.Completion.Wait(TimeSpan.FromSeconds(10)));
    }
    Assert.AreEqual(1L, processor.ProcessedLines);
  }

  private static (List<DamageProcessedEvent> Hits, List<DeathEvent> Deaths) Feed(bool emu, string[] actions,
    int causeDelay = 0)
  {
    AppSettings.IsEmuParsingEnabled = emu;
    var hits = new List<DamageProcessedEvent>();
    var deaths = new List<DeathEvent>();
    var lane = new ParseLane();
    lane.DamageProcessed += hits.Add;
    lane.NewDeath += deaths.Add;
    using var items = new BlockingCollection<LogReaderItem[]>();
    using var processor = new LogProcessor(new NullSinks(), new NullSinks(), lane);
    // Queue first so the test's scheduling cannot turn an immediate cause into an idle timeout.
    foreach (var action in actions) items.Add([Item(action, action == Falling ? causeDelay : 0)]);
    items.CompleteAdding();
    processor.LinkTo(items);
    Assert.IsTrue(processor.Completion.Wait(TimeSpan.FromSeconds(10)));
    Assert.AreEqual((long)actions.Length, processor.ProcessedLines);
    return (hits, deaths);
  }

  private static LogReaderItem Item(string action, int seconds = 0)
    => new("[Wed Jan 01 00:00:00 2025] " + action, FixtureTime.Base + seconds, true);
}
