using System.Diagnostics;

using EQLogParser.Mirror;

namespace EQLogParser.Wpf.Test
{
  /// <summary>
  /// What one refresh of the fight list's own row-building costs — the half of the cheap lane (docs/DesignNotes.md -> "How long a
  /// meter update takes") that lives on this side of the seam, where the display rows are made.
  ///
  /// The reason it needs its own number: the live cadence now runs a projection-only pass every half-second in between expensive
  /// ones, and that pass does not stop at the projection — <see cref="DerivedFightRows.Build"/> walks every row the capture has
  /// accumulated and formats four columns each. Early in a pull that is nothing; at the end of a raid night (measured: 4,644 rows on
  /// one capture) it is work repeated twice a second, forever, because rows expire from the board long after they stop being listed
  /// here. If this ever grows into the tens of milliseconds the cheap lane needs widening (throttle it when the row count is high),
  /// and this is the test that would notice rather than a player watching the meter hesitate.
  ///
  /// Synthetic on purpose: the shape being timed is "N display rows, each with an identity lookup and three formatters", which needs
  /// no log file to be representative. The timeline is empty so identity lookups are misses, which under-counts slightly against a
  /// classified night; the bound below leaves room for that.
  /// </summary>
  [TestClass]
  public class DerivedSnapshotCostTest
  {
    private const int NightRows = 5_000;

    public TestContext? TestContext { get; set; }

    [TestMethod]
    public void RowBuildingForARaidNightStaysInsideAHeartbeat()
    {
      var facts = new DamageFactTable(16);
      var timeline = new EntityTimeline();
      var index = new FightFactIndex();
      var fights = new List<DerivedFight>(NightRows);

      // Spread across an evening with the gaps Sectionizer turns into "Inactivity" dividers, so the divider path is timed too.
      for (var i = 0; i < NightRows; i++)
      {
        var begin = 1_000 + i * 7.0;
        fights.Add(new DerivedFight
        {
          Id = i + 1, Name = $"A corrupted skeleton {i}", BeginTime = begin, LastTime = begin + 4,
          DamageTotal = 125_000 + i, DamageHits = (uint)(40 + (i % 7)), Dead = i % 3 == 0,
        });
      }

      var warm = DerivedFightRows.Build(fights, timeline, 0, facts, index);

      var sw = Stopwatch.StartNew();
      var snapshot = DerivedFightRows.Build(fights, timeline, 0, facts, index);
      sw.Stop();

      TestContext?.WriteLine($"[snapshot] {NightRows:N0} fights -> {snapshot.Rows.Count:N0} display rows " +
                             $"({snapshot.FightCount:N0} listed rows, {snapshot.Rows.Count - snapshot.FightCount:N0} dividers; " +
                             $"warm-up listed {warm.FightCount:N0}): " +
                             $"{sw.Elapsed.TotalMilliseconds:N1} ms per refresh");

      Assert.IsTrue(sw.Elapsed.TotalMilliseconds < 60,
          $"row-building takes {sw.Elapsed.TotalMilliseconds:N1} ms and the cheap lane runs it twice a second — " +
          "see DeriveCadence.FastFloorSeconds before widening that cadence past this");
    }
  }
}
