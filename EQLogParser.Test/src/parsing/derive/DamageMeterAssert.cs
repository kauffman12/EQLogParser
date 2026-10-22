namespace EQLogParser;

internal static class DamageMeterAssert
{
  internal static void Equal(DamageOverlayStats? expected, DamageOverlayStats? actual)
  {
    if (expected is null)
    {
      Assert.IsNull(actual);
      return;
    }
    Assert.IsNotNull(actual);
    Equal(expected.DamageStats, actual.DamageStats);
    Equal(expected.TankStats, actual.TankStats);
  }

  internal static void Equal(CombinedStats? expected, CombinedStats? actual)
  {
    if (expected is null)
    {
      Assert.IsNull(actual);
      return;
    }
    Assert.IsNotNull(actual);
    Assert.AreEqual(expected.TargetTitle, actual.TargetTitle);
    Assert.AreEqual(expected.TimeTitle, actual.TimeTitle);
    Assert.AreEqual(expected.TotalTitle, actual.TotalTitle);
    Assert.AreEqual(expected.RaidStats.Total, actual.RaidStats.Total, "raid total");
    Assert.AreEqual(expected.RaidStats.TotalSeconds, actual.RaidStats.TotalSeconds, "raid seconds");
    Assert.AreEqual(expected.RaidStats.Dps, actual.RaidStats.Dps, "raid DPS");
    Assert.AreEqual(expected.StatsList.Count, actual.StatsList.Count);
    for (var i = 0; i < expected.StatsList.Count; i++)
    {
      var a = expected.StatsList[i];
      var b = actual.StatsList[i];
      Assert.AreEqual(a.Name, b.Name, $"row {i}");
      Assert.AreEqual(a.Total, b.Total, a.Name);
      Assert.AreEqual(a.TotalSeconds, b.TotalSeconds, $"{a.Name} seconds");
      Assert.AreEqual(a.Dps, b.Dps, $"{a.Name} DPS");

      // Compared because two independent paths compute it: the board's DerivedTotals and the meter's own fold. A denominator that
      // moves on one of them (the raid's span versus the name's active span) is exactly the drift this harness exists to catch.
      Assert.AreEqual(a.Sdps, b.Sdps, $"{a.Name} SDPS");
      Assert.AreEqual(a.ClassName, b.ClassName, $"{a.Name} class");
      Assert.AreEqual(a.Rank, b.Rank, $"{a.Name} rank");
    }
  }
}
