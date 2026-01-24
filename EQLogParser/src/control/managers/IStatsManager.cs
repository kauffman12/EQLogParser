using System;

namespace EQLogParser
{
  public interface IStatsManager
  {
    internal event Action<StatsGenerationEvent> EventsGenerationStatus;
    internal StatsGenerationEvent GetLastStats();
    internal void RebuildTotalStats(GenerateStatsOptions options);
  }

  internal class DamageStatsManagerAdapter : IStatsManager
  {
    public static readonly DamageStatsManagerAdapter Instance = new();
    private DamageStatsManagerAdapter() { }

    public event Action<StatsGenerationEvent> EventsGenerationStatus
    {
      add => DamageStatsManager.Instance.EventsGenerationStatus += value;
      remove => DamageStatsManager.Instance.EventsGenerationStatus -= value;
    }

    public StatsGenerationEvent GetLastStats() => DamageStatsManager.Instance.GetLastStats();
    public void RebuildTotalStats(GenerateStatsOptions options) => DamageStatsManager.Instance.RebuildTotalStats(options);
  }

  internal class HealingStatsManagerAdapter : IStatsManager
  {
    public static readonly HealingStatsManagerAdapter Instance = new();
    private HealingStatsManagerAdapter() { }

    public event Action<StatsGenerationEvent> EventsGenerationStatus
    {
      add => HealingStatsManager.Instance.EventsGenerationStatus += value;
      remove => HealingStatsManager.Instance.EventsGenerationStatus -= value;
    }

    public StatsGenerationEvent GetLastStats() => HealingStatsManager.Instance.GetLastStats();
    public void RebuildTotalStats(GenerateStatsOptions options) => HealingStatsManager.Instance.RebuildTotalStats(options);
  }
}
