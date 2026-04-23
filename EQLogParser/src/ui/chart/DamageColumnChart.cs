namespace EQLogParser
{
  internal class DamageColumnChart : ColumnChart
  {
    protected override IStatsBuilder StatsManager => DamageStatsManagerAdapter.Instance;

    protected override string ChartTitle => "Players vs Top Performer (Percent of Total Damage)";
  }
}
