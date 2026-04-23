using System;

namespace EQLogParser
{
  public interface IStatsBuilder
  {
    internal event Action<StatsGenerationEvent> EventsGenerationStatus;
    internal StatsGenerationEvent GetLastStats();
    internal void RebuildTotalStats(GenerateStatsOptions options);
  }

  internal class DamageStatsManagerAdapter : IStatsBuilder
  {
    public static readonly DamageStatsManagerAdapter Instance = new();
    private DamageStatsManagerAdapter() { }

    public event Action<StatsGenerationEvent> EventsGenerationStatus
    {
      add => DamageStatsBuilder.Instance.EventsGenerationStatus += value;
      remove => DamageStatsBuilder.Instance.EventsGenerationStatus -= value;
    }

    public StatsGenerationEvent GetLastStats() => DamageStatsBuilder.Instance.GetLastStats();
    public void RebuildTotalStats(GenerateStatsOptions options) => DamageStatsBuilder.Instance.RebuildTotalStats(options);
  }

  internal class HealingStatsManagerAdapter : IStatsBuilder
  {
    public static readonly HealingStatsManagerAdapter Instance = new();
    private HealingStatsManagerAdapter() { }

    public event Action<StatsGenerationEvent> EventsGenerationStatus
    {
      add => HealingStatsBuilder.Instance.EventsGenerationStatus += value;
      remove => HealingStatsBuilder.Instance.EventsGenerationStatus -= value;
    }

    public StatsGenerationEvent GetLastStats() => HealingStatsBuilder.Instance.GetLastStats();
    public void RebuildTotalStats(GenerateStatsOptions options) => HealingStatsBuilder.Instance.RebuildTotalStats(options);
  }
}
