namespace EQLogParser;

[TestClass]
public class CharmCastHistoryTest
{
  [TestInitialize]
  public void Initialize() => PipelineHarness.EnsureDataStore();

  [TestMethod]
  public void NewestTimestampWinsEvenWhenEligibleCastsArriveOutOfOrder()
  {
    var history = new CharmCastHistory();
    history.Add(Cast("Charmer", "Word of Terris"), 10);
    history.Add(Cast("Other", "Lifeshadow"), 9);
    history.Add(Cast("Future", "Lifeshadow"), 20);
    Assert.AreEqual("Charmer", history.OwnerFor("moans.", 12));
    Assert.IsNull(history.OwnerFor("moans.", 17), "a seven-second-old cast is not eligible");
  }

  [TestMethod]
  public void SameSecondArrivalOrderAndInterruptionCannotFallBackToAnOlderCharm()
  {
    var history = new CharmCastHistory();
    history.Add(Cast("Charmer", "Word of Terris"), 10);
    history.Add(Cast("Other", "Lifeshadow"), 10);
    Assert.IsNull(history.OwnerFor("moans.", 12));
    var latest = Cast("Latest", "Word of Terris");
    history.Add(latest, 10);
    Assert.AreEqual("Latest", history.OwnerFor("moans.", 12));
    latest.Interrupted = true;
    Assert.IsNull(history.OwnerFor("moans.", 12));
    history.Clear();
    Assert.IsNull(history.OwnerFor("moans.", 12));
  }

  [TestMethod]
  public void AnotherParseLaneCannotSupplyACharmer()
  {
    var first = new ParseLane();
    var second = new ParseLane();
    first.CharmCasts.Add(Cast("Charmer", "Word of Terris"), 10);
    Assert.IsNull(second.CharmCasts.OwnerFor("moans.", 12));
    Assert.AreEqual("Charmer", first.CharmCasts.OwnerFor("moans.", 12));
  }

  private static SpellCast Cast(string caster, string spell) => new() { Caster = caster, Spell = spell };
}
