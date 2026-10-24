using System;
using System.Reflection;

#nullable enable annotations

namespace EQLogParser;

[TestClass]
public sealed class MeterReceiptVerificationTest
{
  private string? _savedMode;

  [TestInitialize]
  public void Setup()
  {
    _savedMode = ConfigUtil.GetSetting("OverlayDamageMode");
    RepeatCap.Reset();
  }

  [TestCleanup]
  public void Cleanup()
  {
    if (_savedMode is null) ConfigUtil.RemoveSetting("OverlayDamageMode");
    else ConfigUtil.SetSetting("OverlayDamageMode", _savedMode);
    RepeatCap.Reset();
  }

  [TestMethod]
  [DataRow("-1", 0)]
  [DataRow("101", 0)]
  [DataRow("2147483647", 0)]
  [DataRow("not a number", 0)]
  [DataRow("0", 0)]
  [DataRow("30", 30)]
  [DataRow("100", 100)]
  public void AutoOpenAndTheWindowReadTheSameValidatedResetMode(string stored, int expected)
  {
    ConfigUtil.SetSetting("OverlayDamageMode", stored);
    Assert.AreEqual(expected, DamageMeterConfigState.LoadResetMode());
    Assert.AreEqual(expected, DamageMeterConfigState.Load().DamageResetMode);
    Assert.AreEqual(stored, ConfigUtil.GetSetting("OverlayDamageMode"), "reading must not rewrite the saved setting");
  }

  [TestMethod]
  public void AMissingResetModeMeansOnKill()
  {
    ConfigUtil.RemoveSetting("OverlayDamageMode");
    Assert.AreEqual(0, DamageMeterConfigState.LoadResetMode());
    Assert.AreEqual(0, DamageMeterConfigState.Load().DamageResetMode);
  }

  [TestMethod]
  public void AReceiptFailureReturnsNoActivitySpeaksOnceAndCanRecover()
  {
    MeterArrival first = default;
    MeterArrival repeated = default;
    MeterArrival recovered = default;
    var sightings = 0;
    var silenced = 0;
    Sta.Run(() =>
    {
      using var session = new DeriveEngine();
      var facts = Field<DamageFactTable>(session, "_facts");
      var arrivals = Field<MeterArrivals>(session, "_meterArrivals");
      var attacker = facts.InternName("Illuminai");
      var defender = facts.InternName("a rat");
      facts.AddFact(new DamageFact(0, 63_871_286_400L, attacker, defender, 500,
        LabelTypes.Melee, 0, 0, ushort.MaxValue));
      arrivals.Record(1, 100);
      var timeline = new EntityTimeline();
      timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Manual, "manual");
      timeline.Freeze();

      // Force a real receipt-verification exception after decoding: auto-open must not fail the surrounding derive pass.
      first = session.VerifyMeterArrival(timeline, MeterReceiptRuns.MaxMode + 1, MeterPanels.Both);
      repeated = session.VerifyMeterArrival(timeline, MeterReceiptRuns.MaxMode + 1, MeterPanels.Both);
      sightings = RepeatCap.SightingsOf($"derive meter receipts {typeof(ArgumentOutOfRangeException).FullName}");
      silenced = RepeatCap.Silenced;
      recovered = session.VerifyMeterArrival(timeline, 0, MeterPanels.Both);
    });

    Assert.AreEqual(MeterArrival.None, first);
    Assert.AreEqual(MeterArrival.None, repeated);
    Assert.AreEqual(2, sightings);
    Assert.AreEqual(1, silenced, "repeated scan failures must not flood a live session's log");
    Assert.AreEqual(new MeterArrival(1, 100), recovered,
      "a failed scan must neither discard its receipt prefix nor prevent a later successful scan");
  }

  private static T Field<T>(DeriveEngine session, string name) =>
    typeof(DeriveEngine).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(session) is T value
      ? value : throw new InvalidOperationException($"Missing engine field {name}");
}
