using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using EQLogParser;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParserTests.src.parsing.derive;

/*
 * Same input, same answer — inside one process.
 *
 * This is the assertion that was missing while "one real-log capture per process" was written down as a measurement rule. Running
 * `eqlog_Incogitable_xegony.txt` twice in one test process used to route 102,420 facts tanking-side on the first parse and 104,126 on the
 * second (verified pets 182 → 178: Squirticus 1,625 facts, Plimpy 61, Stormclaw 20 read Pet/RegistrySeed once and Unknown the next time),
 * with identical fact counts and identical rows — only the split moved, because `IsRaidVictimAt` answers raid-side for an unplaced name and
 * not for a pet. The cause was one store missing from `PipelineHarness`'s reset list: `EQDataStore.FindPreviousCast` resolves ambiguous
 * spell-name matches by asking `RecordsStore.GetCastsBySpellName`, so an earlier parse's leftover cast records changed how many rows a line
 * resolved to, and `CastLineParser` registers a custom-named pet only when that resolution yields exactly one row. Production never had the
 * gap (RecordsStore registers with LifecycleManager and clears when a log closes/another opens). Full numbers: docs/DesignNotes.md →
 * "Pet-ness is a parse side effect…".
 */
[TestClass]
public class CaptureReproducibilityTest
{
  private string _savedConfigDir = "";
  private string _savedServerName = "";
  private string _savedPlayerName = "";

  private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "mini-data", "derive", "mini-fight.txt");

  [TestInitialize]
  public void Setup()
  {
    _savedConfigDir = ConfigUtil.ConfigDir;
    _savedServerName = ConfigUtil.ServerName;
    _savedPlayerName = ConfigUtil.PlayerName;
    ConfigUtil.ServerName = "Reproducibility Test";
  }

  [TestCleanup]
  public void Cleanup()
  {
    ConfigUtil.ConfigDir = _savedConfigDir;
    ConfigUtil.ServerName = _savedServerName;
    ConfigUtil.PlayerName = _savedPlayerName;
  }

  // Everything a surface reads that could move if parse order mattered: who the registry calls a pet, how many rows the projection
  // makes, which side each fact was routed to, and what every name was decided to be.
  private sealed record Outcome(string Pets, int Rows, long TankHits, long Unrouted, long DamageToOwner, long DamageByOwner,
                                string Verdicts);

  private static Outcome MeasureOnce(string path)
  {
    var run = PipelineHarness.RunFileDerived(path);
    var facts = run.Facts;

    var pets = string.Join(",", PlayerRegistry.Instance.GetVerifiedPets().OrderBy(n => n, StringComparer.Ordinal));

    var timeline = new EntityTimeline();
    RegistrySeed.Apply(timeline, facts, LogStart(facts), LogEnd(facts));
    ClassificationRules.Apply(facts, timeline, run.HealFacts, new ClassificationState());

    var verdicts = string.Join(";", facts.InternedNames
      .Where(n => !string.IsNullOrEmpty(n))
      .OrderBy(n => n, StringComparer.Ordinal)
      .Select(n => $"{n}={timeline.IdentityWithSource(n, out var src)}:{src}"));

    // Rows and index from one walk, the way FightProjectionCache does it: the index has to see the same sink call the rows split by.
    var index = new FightFactIndex(timeline);
    var rows = FightProjection.Build(facts, timeline, (fact, ordinal, owner, target) => index.OnFact(fact, ordinal, owner, target));

    long tankHits = 0, damageToOwner = 0, damageByOwner = 0;
    foreach (var fight in rows)
    {
      tankHits += index.TankingOrdinalsFor(fight).Count;
      damageToOwner += (long)fight.DamageToOwner;
      damageByOwner += (long)fight.DamageByOwner;
    }

    return new Outcome(pets, rows.Count, tankHits, index.UnroutedFactCount, damageToOwner, damageByOwner, verdicts);
  }

  private static double LogStart(DamageFactTable facts) => facts.FactCount > 0 ? facts.Facts[0].TimeS : double.NaN;

  private static double LogEnd(DamageFactTable facts) =>
    facts.FactCount > 0 ? facts.Facts[facts.FactCount - 1].TimeS : double.NaN;

  [TestMethod]
  public void TheSameCaptureTwiceInOneProcess_GivesTheSameAnswer()
  {
    Assert.IsTrue(File.Exists(FixturePath), $"missing fixture: {FixturePath}");

    var first = MeasureOnce(FixturePath);
    var second = MeasureOnce(FixturePath);

    // Compare field by field so a failure names WHICH number moved instead of dumping two megabyte strings.
    Assert.AreEqual(first.Rows, second.Rows, "row count moved between two parses of one file");
    Assert.AreEqual(first.Pets, second.Pets, "the verified-pet set moved — the exact drift this test exists for");
    Assert.AreEqual(first.TankHits, second.TankHits, "tanking-side routing moved");
    Assert.AreEqual(first.Unrouted, second.Unrouted, "the unrouted drop moved");
    Assert.AreEqual(first.DamageToOwner, second.DamageToOwner);
    Assert.AreEqual(first.DamageByOwner, second.DamageByOwner);
    Assert.AreEqual(first.Verdicts, second.Verdicts, "a name's verdict or provenance moved between parses of one file");
  }

  /*
   * The same law on a real capture — the only place the drift ever appeared (it needs hundreds of ambiguous spell names and a long
   * enough cast stream for the previous-cast lookup to matter). Gated `EQLP_REPRODUCIBLE=<log>`; add EQLP_EMU=1 for an EMU capture.
   * This parses the file TWICE, so it is slow by design: the second parse is the point.
   */
  [TestMethod]
  public void OnARealCapture_TheSecondParseInTheSameProcess_AgreesLineForLine()
  {
    var path = Environment.GetEnvironmentVariable("EQLP_REPRODUCIBLE");
    if (string.IsNullOrEmpty(path)) Assert.Inconclusive("set EQLP_REPRODUCIBLE=<log> to run this over a real capture");
    if (!File.Exists(path)) Assert.Inconclusive($"no such file: {path}");

    var first = MeasureOnce(path);
    var second = MeasureOnce(path);

    Console.WriteLine($"[repro] {Path.GetFileName(path)}: rows {first.Rows} vs {second.Rows}; pets "
                    + $"{first.Pets.Split(',').Length} vs {second.Pets.Split(',').Length}; tank hits {first.TankHits:N0} vs {second.TankHits:N0}; "
                    + $"unrouted {first.Unrouted:N0} vs {second.Unrouted:N0}");

    Assert.AreEqual(first.Pets, second.Pets, "verified-pet set moved between parses of the same file");
    Assert.AreEqual(first.Rows, second.Rows);
    Assert.AreEqual(first.TankHits, second.TankHits, "tanking-side routing moved — this is the 102,420 vs 104,126 defect");
    Assert.AreEqual(first.Unrouted, second.Unrouted);
    Assert.AreEqual(first.DamageToOwner, second.DamageToOwner);
    Assert.AreEqual(first.DamageByOwner, second.DamageByOwner);

    if (first.Verdicts != second.Verdicts)
    {
      var a = first.Verdicts.Split(';');
      var b = second.Verdicts.Split(';');
      var moved = Enumerable.Range(0, Math.Min(a.Length, b.Length)).Where(i => a[i] != b[i]).Take(12).Select(i => a[i] + " -> " + b[i]);
      Assert.Fail("verdicts moved between parses: " + string.Join(", ", moved));
    }
  }
}
