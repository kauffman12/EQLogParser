namespace EQLogParser;

public partial class DamageMeterCalculationTest
{
  [TestMethod]
  public void CancellationAfterCountingRejectsPublicationAndTheNextUpdateRecovers()
  {
    var saved = IdentityLookup.LiveOwner;
    using var cancel = new CancellationTokenSource();
    try
    {
      var log = new Capture();
      log.Add("Illuminai", "a rat", 0, 100);
      log.Add("a rat", "Bithika", 0, 10);
      log.Check(1);
      var frozen = log.Last;
      log.Add("Illuminai", "a rat", 2, 900);
      log.Add("a rat", "Bithika", 2, 90);
      var rows = log.Project();
      var input = log.Cache.Index.CaptureMeterInput(rows, log.Facts, double.NegativeInfinity, double.PositiveInfinity);
      var lookups = 0;
      // Fallback owners are read during publication, after both direction cursors have advanced.
      IdentityLookup.LiveOwner = _ =>
      {
        lookups++;
        cancel.Cancel();
        return null;
      };
      Assert.ThrowsExactly<OperationCanceledException>(() => log.Calculation.Update(input, cancel.Token));
      Assert.IsGreaterThan(0, lookups);
      IdentityLookup.LiveOwner = saved;
      log.Check(3, compareFresh: true);
      Assert.AreEqual(1000L, log.Last!.DamageStats.RaidStats.Total);
      Assert.AreEqual(100L, log.Last.TankStats.RaidStats.Total);
      Assert.AreEqual(100L, frozen!.DamageStats.RaidStats.Total, "a cancelled publication cannot mutate old rows");
      log.Add("Illuminai", "a rat", 3, 50);
      log.Check(3, compareFresh: true);
    }
    finally
    {
      IdentityLookup.LiveOwner = saved;
    }
  }

  [TestMethod]
  public void CancellationBetweenReboundLivesDoesNotCommitAHalfReboundIndex()
  {
    var log = new Capture();
    log.Add("Illuminai", "a rat", 0, 100);
    log.Add("Bithika", "a bat", 0, 200);
    log.Check(1);
    var oldIndex = log.Cache.Index;
    log.Timeline.SetIdentity("Unrelated", IdentityKind.Player, RuleStrength.Manual, "manual");
    var rows = log.Project();
    Assert.AreNotSame(oldIndex, log.Cache.Index);
    var input = log.Cache.Index.CaptureMeterInput(rows, log.Facts, double.NegativeInfinity, double.PositiveInfinity);
    Assert.HasCount(2, input.Lives);
    using var cancel = new CancellationTokenSource();
    var lives = new CancellingLifeList(input.Lives, cancel);
    var interrupted = input with { Lives = lives };
    Assert.ThrowsExactly<OperationCanceledException>(() => log.Calculation.Update(interrupted, cancel.Token));
    Assert.AreEqual(2, lives.Yielded, "cancellation must stop rebind, not be noticed by a later counting loop");
    log.Check(1, compareFresh: true);
    log.Add("Bithika", "a bat", 2, 300);
    log.Check(3, compareFresh: true);
  }

  [TestMethod]
  public void OrdinalPrefixComparisonHonorsCancellationEvenForAnEmptyPrefix()
  {
    var previous = new FightFactIndex();
    var current = new FightFactIndex();
    var life = new DerivedFight { Name = "a rat" };
    using var cancel = new CancellationTokenSource();
    cancel.Cancel();
    Assert.ThrowsExactly<OperationCanceledException>(() =>
      current.MeterPrefixMatches(previous, life, life, false, 0, cancel.Token));
  }

  [TestMethod]
  [DataRow(391, 0)]
  [DataRow(1729, 0)]
  [DataRow(391, 90)]
  [DataRow(1729, 90)]
  public void SeededMixedUpdatesMatchFreshCompactAndFullCalculations(int seed, int mode)
  {
    var previousBane = AppSettings.IsBaneDamageEnabled;
    var previousOwner = IdentityLookup.LiveOwner;
    try
    {
      var random = new Random(seed);
      var log = new Capture { Timeline = MeterTimeline("Illuminai") };
      var fallbackOwner = "Illuminai";
      IdentityLookup.LiveOwner = name => name == "Fluffy" ? fallbackOwner : null;
      string[] attackers = ["Illuminai", "Bithika", "Illuminai`s pet", "Fluffy", "a hound"];
      byte[] types = [LabelTypes.Melee, LabelTypes.Dd, LabelTypes.Bane, LabelTypes.Miss];
      var second = 0;
      for (var step = 0; step < 50; step++)
      {
        second += random.Next(0, 5);
        if (step % 11 == 10)
        {
          second += mode == 0 ? 40 : 100;
          log.Check(second, mode, compareFresh: true);
        }
        if (step % 9 == 0) AppSettings.IsBaneDamageEnabled = !AppSettings.IsBaneDamageEnabled;
        if (step % 7 == 0) fallbackOwner = fallbackOwner == "Illuminai" ? "Bithika" : "Illuminai";
        if (step is 18 or 37) log.Timeline = MeterTimeline(step == 18 ? "Bithika" : "Illuminai");
        if (step % 10 == 0)
          log.Timeline.SetIdentity("Unrelated" + step, IdentityKind.Player, RuleStrength.Manual, "manual");
        if (step is 13 or 31) log.Lifecycle.Reset(1, log.Facts.FactCount);

        var target = random.Next(2) == 0 ? "a rat" : "a bat";
        var hits = random.Next(1, 8);
        for (var i = 0; i < hits; i++)
        {
          var attacker = attackers[random.Next(attackers.Length)];
          var type = types[random.Next(types.Length)];
          var amount = type == LabelTypes.Miss ? 0u : (uint)random.Next(1, 5) * 50;
          log.Add(attacker, target, second, amount, type,
            attacker == "Illuminai`s pet" ? DamageFact.FlagOwnerInLine : (byte)0);
          log.Add(target, random.Next(2) == 0 ? "Illuminai" : "Bithika", second, amount / 10, type);
        }
        if (step % 4 == 3)
          log.Facts.AddDeath(new DeathFact(step, (long)(T0 + second), log.Facts.InternName(target), -1));
        log.Check(second, mode, compareFresh: true);
        log.Check(second, mode, compareFresh: true);
      }
    }
    finally
    {
      AppSettings.IsBaneDamageEnabled = previousBane;
      IdentityLookup.LiveOwner = previousOwner;
    }
  }

  private static EntityTimeline MeterTimeline(string charmOwner)
  {
    var timeline = new EntityTimeline();
    foreach (var name in new[] { "Illuminai", "Bithika" })
      timeline.SetIdentity(name, IdentityKind.Player, RuleStrength.Manual, "manual");
    foreach (var name in new[] { "a rat", "a bat", "a hound" })
      timeline.SetIdentity(name, IdentityKind.Npc, RuleStrength.Manual, "manual");
    foreach (var name in new[] { "Illuminai`s pet", "Fluffy" })
      timeline.SetIdentity(name, IdentityKind.Pet, RuleStrength.Manual, "manual");
    timeline.AddAffiliation(AffiliationKind.Friendly, "a hound", T0, T0 + 10000, 9, "R9-charm", charmOwner);
    return timeline;
  }

  private sealed class CancellingLifeList(IReadOnlyList<MeterLifeInput> lives, CancellationTokenSource cancellation)
    : IReadOnlyList<MeterLifeInput>
  {
    public int Count => lives.Count;
    public MeterLifeInput this[int index] => lives[index];
    internal int Yielded { get; private set; }

    public IEnumerator<MeterLifeInput> GetEnumerator()
    {
      for (var i = 0; i < lives.Count; i++)
      {
        if (i == 1) cancellation.Cancel();
        Yielded++;
        yield return lives[i];
      }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
  }
}
