namespace EQLogParser
{
  internal class HealingColumnChart : ColumnChart
  {
    protected override IStatsBuilder StatsManager => HealingStatsManagerAdapter.Instance;

    protected override string ChartTitle => "Players vs Top Performer (Percent of Total Healing)";
  }
}
