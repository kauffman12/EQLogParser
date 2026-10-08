using Microsoft.VisualStudio.TestTools.UnitTesting;

using System;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser.Tests.Control.Builders
{

  /*
   * The group-AOE / MGB filter, and the two-pass shape it depends on.
   *
   * When "Count AoE healing" is OFF, a group heal that reached more than six distinct people inside its lookback window is a raid-wide
   * button-mash rather than six intentional casts, so the builder marks the (second, healer, spell) triples as ignored and drops them.
   * The marking happens in the FIRST pass (a sighting late in a segment reaches back over the earlier ones) and the dropping in the
   * SECOND — which is why the healing window is two passes and cannot be merged into one loop, however tempting the intermediate
   * `List<(time, record)>` makes it look. These tests exist because the golden board runs with AOE healing ON, so nothing else covered
   * the off path end to end after the per-record allocations around it were made conditional (`HealingValidator.TracksGroupAe`).
   *
   * `Ancestral Aid VI` is a real spells.txt row whose Target is Targetgroup(41) with the MGB flag set and negative damage — the exact
   * shape that gets counted, as opposed to a plain single-target heal (which is never marked) or an AE spell (which is skipped whole).
   */
  [TestClass]
  public class HealingGroupAeTest
  {
    private const string GroupHeal = "Ancestral Aid VI";
    private const uint PerHeal = 100;

    private Func<string, SpellData>? _originalHealingSpell;
    private bool _aoe;
    private bool _swarm;

    [TestInitialize]
    public void Setup()
    {
      PipelineHarness.EnsureDataStore();
      _originalHealingSpell = CombatRecordLookup.HealingSpellByName;
      CombatRecordLookup.HealingSpellByName = name => EQDataStore.Instance.GetHealingSpellByName(name);

      DamageLineParser.ResetProcessState();
      HealingLineParser.ClearCaches();
      RecordsStore.Instance.Clear(false);
      PlayerRegistry.Instance.Clear();
      HealRecordSource.Current = null;

      _aoe = AppSettings.IsAoEHealingEnabled;
      _swarm = AppSettings.IsHealingSwarmPetsEnabled;
      AppSettings.IsHealingSwarmPetsEnabled = true;
    }

    [TestCleanup]
    public void Cleanup()
    {
      AppSettings.IsAoEHealingEnabled = _aoe;
      AppSettings.IsHealingSwarmPetsEnabled = _swarm;
      CombatRecordLookup.HealingSpellByName = _originalHealingSpell ?? (name => null!);
      DamageLineParser.ResetProcessState();
      HealingLineParser.ClearCaches();
      RecordsStore.Instance.Clear(false);
      PlayerRegistry.Instance.Clear();
    }

    /*
     * One heal per second, distinct people, all from one healer with the group spell — seven seconds of it is what an MGB spam looks like.
     * The bookkeeping mirrors the builder's: a fresh spell-count dictionary per line, the previous one filed under its own second, history
     * older than seven seconds dropped.
     */
    private static List<(double Time, HealRecord Record)> GroupHeals(double firstSecond, int count)
    {
      var heals = new List<(double, HealRecord)>();
      for (var i = 0; i < count; i++)
      {
        var record = new HealRecord
        {
          Healer = "Clyer",
          Healed = HealedName(i),
          SubType = GroupHeal,
          Type = Labels.Heal,
          Total = PerHeal,
        };
        heals.Add((firstSecond + i, record));
      }

      return heals;
    }

    // Letters only: a name being counted at all has to pass the registry's person-shape gate, and these are not pets or mobs.
    private static string HealedName(int i) => new[] { "Alpha", "Betas", "Gamma", "Deltas", "Epona", "Fenka", "Ghadi", "Hellyr" }[i];

    [TestMethod]
    public void TrackingTheGroupCountIsWhatTheAoeSettingDecides()
    {
      // The gate the builder asks once per build, before allocating anything per record for this feature.
      Assert.IsTrue(new HealingValidator(false, true).TracksGroupAe, "AOE healing off is when the group counting runs");
      Assert.IsFalse(new HealingValidator(true, true).TracksGroupAe, "with AOE healing on, nothing writes the spell-count or ignore maps");
    }

    [TestMethod]
    public void AGroupHealReachingSevenPeopleIsMarkedOnlyWhenAoeHealingIsOff()
    {
      var off = MarkedSeconds(aoeEnabled: false, count: 7);
      Assert.IsTrue(off.Count > 0, "seven distinct people from one group cast has to be marked, or the filter does nothing");

      // The marking reaches back: the sighting itself plus the history seconds that carried this healer and spell.
      Assert.IsTrue(off.Contains(1_006d), "the second that hit the threshold is marked");
      Assert.IsTrue(off.Contains(1_000d), "and the earlier seconds of the same cast are marked with it — that is the pass-ordering law");

      var on = MarkedSeconds(aoeEnabled: true, count: 7);
      Assert.AreEqual(0, on.Count, "with AOE healing on, the whole feature stays asleep");
    }

    private static HashSet<double> MarkedSeconds(bool aoeEnabled, int count)
    {
      var validator = new HealingValidator(aoeEnabled, true);
      var ignores = new Dictionary<string, byte>();
      Dictionary<string, HashSet<string>>? current = null;
      Dictionary<double, Dictionary<string, HashSet<string>>>? previous = null;
      var currentTime = double.NaN;

      foreach (var (time, record) in GroupHeals(1_000d, count))
      {
        if (aoeEnabled)
        {
          // What the builder does when TracksGroupAe is false: the short overload, and no maps at all.
          Assert.IsTrue(validator.IsValid(time, record, ignores));
          continue;
        }

        current ??= [];
        previous ??= [];

        if (current.Count > 0) previous[currentTime] = current;
        currentTime = time;
        current = [];
        foreach (var key in previous.Keys.Where(k => !double.IsNaN(time) && time - k > 7).ToList()) previous.Remove(key);

        Assert.IsTrue(validator.IsValid(time, record, current, previous, ignores),
            "the filter marks rather than rejecting, so the record is accepted here and dropped by the second pass");
      }

      // The ignore keys are "<second>|<healer>|<spell>"; return the seconds so a failure names seconds, not opaque strings.
      return ignores.Keys.Select(k => double.Parse(k.Split('|')[0], System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
    }

    [TestMethod]
    public void TheBoardDropsTheMarkedCastAndKeepsAnOrdinaryHeal()
    {
      /*
       * End to end through the builder, because the drop lives in pass 2: a selection holding seven seconds of group healing costs
       * nothing when AOE healing is off, and everything when it is on. Six seconds is the control — under the threshold, both settings
       * count it, so a failure cannot be read as "the off path counts nothing".
       */
      Assert.AreEqual(7 * PerHeal, BoardTotal(count: 7, aoeEnabled: true), "AOE on keeps all seven heals");
      Assert.AreEqual(6 * PerHeal, BoardTotal(count: 6, aoeEnabled: true));
      Assert.AreEqual(6 * PerHeal, BoardTotal(count: 6, aoeEnabled: false), "six people is under the threshold; both settings count it");

      /*
       * ALL of it disappears, not just the seventh heal: every one of those seconds carried this healer and spell, so the threshold marks
       * them as one cast's history. Asserting zero is what makes a partial reach-back (a merged single pass, which would see only forward)
       * fail by name instead of reading as "a bit less healing".
       */
      Assert.AreEqual(0, BoardTotal(count: 7, aoeEnabled: false),
          "the marked cast must leave the board entirely — anything between 0 and 700 means the reach-back got shorter");
    }

    private static long BoardTotal(int count, bool aoeEnabled)
    {
      AppSettings.IsAoEHealingEnabled = aoeEnabled;

      var heals = GroupHeals(1_000d, count);
      var range = new TimeRange();
      range.Add(new TimeSegment(heals[0].Time, heals[^1].Time));

      StatsGenerationEvent? last = null;
      var builder = new HealingStatsBuilder();
      builder.EventsGenerationStatus += e => { if (e.CombinedStats is not null) last = e; };

      var options = new GenerateStatsOptions { AllRanges = range, Heals = heals };
      builder.BuildTotalStats(options);

      Assert.IsNotNull(last, "a board with heals has to be published");

      // Raid row plus per-healer rows: the sum moves whenever a heal is counted or dropped, which is the only question here.
      return last.CombinedStats.StatsList.Sum(s => s.Total);
    }
  }
}
