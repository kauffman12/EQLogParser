using Microsoft.VisualStudio.TestTools.UnitTesting;

using System.Collections.Concurrent;
using System.Text;

namespace EQLogParser;

/*
 * Three boards, one shared materialized selection, three workers — and the SAME answer a line of three builds gives
 * (EQLogParser.Core/src/ui/BoardFanout.cs, docs/improve-stat-building §2/§6).
 *
 * This is the equivalence half of the fan-out's safety case, and it is deliberately NOT the goldens: those freeze what a board
 * displays against a file, one board per fixture, on the serial path they have always used. What this test adds is the comparison the
 * goldens cannot make — the same three builders over the SAME `SummaryInput` object (the one MainWindow hands all three boards of a
 * click: the same Fights list, the same `AllRanges` window, the same heal list), once in a line and then fanned out over real pool
 * threads, rendered with the goldens' own row formatter so the comparison covers every column a grid binds.
 *
 * Why the sharing is what to test: `FightSummarySource.Build` materializes fresh legacy-shaped `Fight`s per request, but it hands ONE
 * copy to all three builders. Damage and tanking each sort their own list of that fight's blocks; the healing builder reads only the
 * selection's name and count; and the one place any build touches a shared object is `TimeRange.Add(options.AllRanges.TimeSegments)`,
 * which ADOPTS the segment objects it is given (the "merging copies, adding adopts" law) — so a build that later ADDED to a range it
 * adopted would weld seconds inside an object two other boards were reading. Today none does; this test is what notices if one starts,
 * along with any future writer into the materialized rows.
 *
 * Two assertions per round, because they catch different things and only one of them is probabilistic:
 *
 *   - the ANSWER: rendered board text identical to the serial reference (every column the goldens freeze, plus the completion state,
 *     so a board that took a different path — NODATA vs COMPLETED, a different group count — shows up even when its totals survive);
 *   - the INPUT's shape: `ShapeOf` snapshots the shared window's spans and every row's block and record counts before any build and
 *     re-reads them after each one. That one is order-independent, so it catches a build editing what it was handed even when the edit
 *     happens not to move a number (verified: a probe that inserted a span into `options.AllRanges` from inside the damage build left
 *     every compared column unchanged on these fixtures and was caught here, by span count).
 *
 * What neither can do: prove the absence of a race on fixtures this small. The invariant the design leans on — no build ever ADDS to a
 * `TimeRange` it adopted, because adding welds into the objects it adopted — is stated where the adoption happens (BoardFanout's header,
 * TimeRange's "merging copies, adding adopts"), and this file makes an edit of the shared selection loud rather than merely unlucky.
 */
[TestClass]
public sealed class ParallelBoardEquivalenceTest
{
  // The three golden fixtures, one per board's shape: a fanned round over each covers every builder over a selection it likes.
  private const string DamageFixture = "damage-board.txt";
  private const string TankingFixture = "tanking-board.txt";
  private const string HealingFixture = "healing-board.txt";

  private static readonly string[] Boards = ["damage", "tanking", "healing"];

  // Fanned rounds per fixture. Small, because this runs in seconds; enough that an order-dependent answer would show up.
  private const int FannedRounds = 5;

  private bool _ass, _bane, _ds, _fb, _hs, _su, _aoe, _swarm;
  private Func<string, bool>? _originalIsValidClass;

  [TestInitialize]
  public void Setup()
  {
    // The same process-state hygiene the goldens use — these builders are singletons and the parser caches are process statics.
    PipelineHarness.EnsureDataStore();
    _originalIsValidClass = CombatRecordLookup.IsValidClassName;
    CombatRecordLookup.IsValidClassName = name => EQDataStore.Instance.IsValidClassName(name);

    DamageLineParser.ResetProcessState();
    HealingLineParser.ClearCaches();
    RecordsStore.Instance.Clear(false);
    PlayerRegistry.Instance.Clear();
    HealRecordSource.Current = null;

    // Every filter ON: the six damage knobs and the two healing ones are read fresh per build, so a round that ran with a
    // different set than its neighbour would compare two different questions rather than two schedules of one question.
    (_ass, _bane, _ds, _fb, _hs, _su) = (AppSettings.IsAssassinateDamageEnabled, AppSettings.IsBaneDamageEnabled,
        AppSettings.IsDamageShieldDamageEnabled, AppSettings.IsFinishingBlowDamageEnabled,
        AppSettings.IsHeadshotDamageEnabled, AppSettings.IsSlayUndeadDamageEnabled);
    AppSettings.IsAssassinateDamageEnabled = true;
    AppSettings.IsBaneDamageEnabled = true;
    AppSettings.IsDamageShieldDamageEnabled = true;
    AppSettings.IsFinishingBlowDamageEnabled = true;
    AppSettings.IsHeadshotDamageEnabled = true;
    AppSettings.IsSlayUndeadDamageEnabled = true;

    (_aoe, _swarm) = (AppSettings.IsAoEHealingEnabled, AppSettings.IsHealingSwarmPetsEnabled);
    AppSettings.IsAoEHealingEnabled = true;
    AppSettings.IsHealingSwarmPetsEnabled = true;
  }

  [TestCleanup]
  public void Cleanup()
  {
    AppSettings.IsAssassinateDamageEnabled = _ass;
    AppSettings.IsBaneDamageEnabled = _bane;
    AppSettings.IsDamageShieldDamageEnabled = _ds;
    AppSettings.IsFinishingBlowDamageEnabled = _fb;
    AppSettings.IsHeadshotDamageEnabled = _hs;
    AppSettings.IsSlayUndeadDamageEnabled = _su;
    AppSettings.IsAoEHealingEnabled = _aoe;
    AppSettings.IsHealingSwarmPetsEnabled = _swarm;

    CombatRecordLookup.IsValidClassName = _originalIsValidClass ?? (_ => false);
    DamageLineParser.ResetProcessState();
    HealingLineParser.ClearCaches();
    RecordsStore.Instance.Clear(false);
    PlayerRegistry.Instance.Clear();
    HealRecordSource.Current = null;
  }

  /*
   * The production door for a click, including the healing half: classify → project → stamp sections → materialize → window the
   * heals by the selection's own span. One SummaryInput per fixture, shared by all three boards and by both schedules.
   */
  private static SummaryInput InputOf(string fixture)
  {
    var run = PipelineHarness.RunFileDerived(BoardGolden.FixturePath(fixture));

    ClassificationRules.Apply(run.Facts, run.Timeline);
    var index = new FightFactIndex();
    var rows = FightProjection.Build(run.Facts, run.Timeline, index.OnFact);
    Sectionizer.StampGroupIds(rows);

    var input = FightSummarySource.Build(rows, index, run.Facts);
    return input with { Heals = HealSummarySource.Materialize(run.HealFacts, input.AllRanges) };
  }

  [TestMethod]
  public void TheFannedOutBoardsAnswerExactlyWhatTheSerialOnesDo()
  {
    foreach (var fixture in new[] { DamageFixture, TankingFixture, HealingFixture })
    {
      var input = InputOf(fixture);
      Assert.IsTrue(input.Fights.Count > 0 && input.AllRanges.TimeSegments.Count > 0,
          $"{fixture}: nothing materialized, so this would compare two silences");

      var shapeBefore = ShapeOf(input);
      var serial = BuildAll(input, parallel: false);
      Assert.AreEqual(shapeBefore, ShapeOf(input), $"{fixture}: a SERIAL build already edits the selection it was handed");

      // The reference has to be an answer, not an empty string that matches an equally empty fanned one.
      foreach (var board in Boards)
      {
        Assert.IsTrue(serial.ContainsKey(board), $"{fixture}: the serial {board} board never reported back");
        Assert.IsTrue(serial[board].Length > 0, $"{fixture}: the serial {board} board rendered nothing to compare");
      }

      for (var round = 1; round <= FannedRounds; round++)
      {
        var fanned = BuildAll(input, parallel: true);

        foreach (var board in Boards)
        {
          Assert.IsTrue(fanned.ContainsKey(board),
              $"{fixture} round {round}: the {board} board never reported back — a worker that dies silently is the failure this change must not add");
          Assert.AreEqual(serial[board], fanned[board],
              $"{fixture} round {round}: the {board} board answered differently when its request ran on three workers over the same materialized selection");

          // The law a parallel build can break in ways a serial one cannot: reading the shared window while a sibling edits it.
          Assert.AreEqual(shapeBefore, ShapeOf(input),
              $"{fixture} round {round}: a board build changed the shared selection/window its siblings read (span, block or record counts)");
        }
      }
    }
  }

  /*
   * Build all three boards over ONE input, serially or through BoardFanout, and return each board's rendered answer.
   *
   * The rendering happens inside the completion handler (on whichever thread built it) because a CombinedStats is replaced by the
   * next build of that builder — and in the parallel case "the next build" is another board's business only in the sense that matters:
   * nothing here may assume it is still holding a live object after RunParallel returns.
   */
  private static ConcurrentDictionary<string, string> BuildAll(SummaryInput input, bool parallel)
  {
    var answer = new ConcurrentDictionary<string, string>();
    var group = StatsBuildTrace.NextGroupId();   // the three boards of one request are siblings (StatsBuildTrace rule 2)

    void OnDamage(StatsGenerationEvent e) => Capture("damage", e);
    void OnTanking(StatsGenerationEvent e) => Capture("tanking", e);
    void OnHealing(StatsGenerationEvent e) => Capture("healing", e);

    DamageStatsBuilder.Instance.EventsGenerationStatus += OnDamage;
    TankingStatsBuilder.Instance.EventsGenerationStatus += OnTanking;
    HealingStatsBuilder.Instance.EventsGenerationStatus += OnHealing;

    var damageJob = new BoardFanout.BoardJob("damage", "parallel equivalence", static () => true,
        () => DamageStatsBuilder.Instance.BuildTotalStats(OptionsFor(input, group)));
    var tankingJob = new BoardFanout.BoardJob("tanking", "parallel equivalence", static () => true,
        () => TankingStatsBuilder.Instance.BuildTotalStats(OptionsFor(input, group)));
    var healingJob = new BoardFanout.BoardJob("healing", "parallel equivalence", static () => true, () =>
    {
      // Healing is the one board with an extra input, and it is the SAME list object for all rounds: null would send it to the
      // record store instead, which is a different question rather than the same one scheduled differently.
      var healingOptions = OptionsFor(input, group);
      healingOptions.Heals = input.Heals;
      HealingStatsBuilder.Instance.BuildTotalStats(healingOptions);
    });

    try
    {
      if (parallel)
      {
        BoardFanout.RunParallel([damageJob, tankingJob, healingJob]);
      }
      else
      {
        damageJob.Build();
        tankingJob.Build();
        healingJob.Build();
      }
    }
    finally
    {
      DamageStatsBuilder.Instance.EventsGenerationStatus -= OnDamage;
      TankingStatsBuilder.Instance.EventsGenerationStatus -= OnTanking;
      HealingStatsBuilder.Instance.EventsGenerationStatus -= OnHealing;
    }

    return answer;

    void Capture(string board, StatsGenerationEvent e)
    {
      // The last terminal event of a build is the answer (COMPLETED / NODATA / NONPC), which is what the panes paint from too.
      if (e.State is not ("COMPLETED" or "NODATA" or "NONPC")) return;

      var sb = new StringBuilder();
      sb.Append("state=").Append(e.State).Append("|groups=").Append(e.Groups.Count).Append('\n');
      if (e.CombinedStats is null)
      {
        answer[board] = sb.ToString();
        return;
      }

      var c = e.CombinedStats;
      sb.Append("title=").Append(c.TimeTitle).Append("|raid=").Append(c.RaidStats?.Total ?? 0)
        .Append("|unique=").Append(string.Join(",", c.UniqueClasses)).Append('\n');
      if (c.PlayerClasses is not null)
      {
        foreach (var kv in c.PlayerClasses.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
          sb.Append("class=").Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
        }
      }

      foreach (var row in c.StatsList) AppendRow(sb, "S", row);
      foreach (var row in c.ExpandedStatsList) AppendRow(sb, "E", row);
      foreach (var child in c.Children.OrderBy(kv => kv.Key, StringComparer.Ordinal))
      {
        foreach (var row in child.Value) AppendRow(sb, $"C:{child.Key}", row);
      }

      answer[board] = sb.ToString();
    }

    void AppendRow(StringBuilder sb, string tag, PlayerStats row)
    {
      sb.Append(BoardGolden.StatsLine(tag, row)).Append('\n');
      foreach (var sub in row.SubStats) sb.Append(BoardGolden.SubLine("sub", row.Name, sub)).Append('\n');
      foreach (var sub in row.SubStats2) sb.Append(BoardGolden.SubLine("sub2", row.Name, sub)).Append('\n');
    }
  }

  /*
   * The shared input's shape as one string: every span of the window all three boards read (count included, so an INSERT is visible
   * and not merely a widened value), plus each materialized row's block and record counts on both sides, plus the heal count.
   * Cheap enough to take before and after every round, and it says the one thing a builder must never be able to say about a selection
   * it was handed for reading: "I changed it".
   */
  private static string ShapeOf(SummaryInput input)
  {
    var sb = new StringBuilder();
    sb.Append("spans=").Append(input.AllRanges.TimeSegments.Count).Append(':');
    foreach (var span in input.AllRanges.TimeSegments)
    {
      sb.Append('[').Append(BoardGolden.F(span.BeginTime)).Append(',').Append(BoardGolden.F(span.EndTime)).Append(']');
    }

    sb.Append("|heals=").Append(input.Heals?.Count ?? -1).Append("|rows=").Append(input.Fights.Count);
    foreach (var row in input.Fights)
    {
      sb.Append("\n").Append(row.Id).Append(':').Append(row.Name)
        .Append(" damageBlocks=").Append(row.DamageBlocks.Count)
        .Append('/').Append(row.DamageBlocks.Sum(static b => b.Actions.Count))
        .Append(" tankBlocks=").Append(row.TankingBlocks.Count)
        .Append('/').Append(row.TankingBlocks.Sum(static b => b.Actions.Count))
        .Append(" segs=").Append(row.DamageSegments.Count).Append('+').Append(row.TankSegments.Count)
        .Append(" total=").Append(row.DamageTotal).Append('/').Append(row.TankTotal);
    }

    return sb.ToString();
  }

  /*
   * Exactly what MainWindow.BuildBoards hands each board: the SAME Fights list and the SAME AllRanges object (that sharing is the
   * thing under test), one options instance per board, MinSeconds 0 and the tanking dial at its default "all".
   */
  private static GenerateStatsOptions OptionsFor(SummaryInput input, int group)
  {
    var options = new GenerateStatsOptions
    {
      Source = "parallel equivalence",
      BuildGroup = group,
      AllRanges = input.AllRanges,
      MinSeconds = 0,
      DamageType = 0
    };

    options.Npcs.AddRange(input.Fights);
    return options;
  }
}
