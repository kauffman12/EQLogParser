namespace EQLogParser;

internal static class HitFrequencyFixtures
{
  internal static HitFrequencyBar[] HazysongsPetHits()
  {
    long[] amounts = [1320, 1440, 1557, 1560, 1680, 1800, 1840, 1920, 2040, 2160, 2280, 2400, 2520,
      2640, 2760, 2880, 3000, 3115, 3120, 3240, 3360, 3398, 3480, 3540, 3600];
    int[] counts = [468, 29, 5, 29, 34, 38, 1, 32, 29, 27, 36, 32, 31, 30, 19, 16, 21, 2, 15, 19, 11, 1, 5, 2, 8];
    return HitFrequencySeries.Prepare(amounts.Select((amount, index) => (amount, count: counts[index]))
      .ToDictionary(pair => pair.amount, pair => pair.count));
  }

  // Keyce's four-fight critical selection: docs/DesignNotes.md → "DI inference: measured melee ladders, not automatic numbering".
  internal static HitFrequencyBar[] CombinedKeyceCriticals()
  {
    long[] amounts = [7313, 10524, 13739, 16955, 20170, 23380, 26596, 29812, 33022, 36237,
      39453, 42663, 45879, 49094, 52310, 52674, 55520, 58735, 61951, 65161, 68377];
    int[] counts = [26, 6, 6, 11, 16, 20, 20, 19, 20, 30, 28, 30, 36, 32, 28, 1, 35, 21, 22, 24, 288];
    return HitFrequencySeries.Prepare(amounts.Select((amount, index) => (amount, count: counts[index]))
      .ToDictionary(pair => pair.amount, pair => pair.count));
  }
}
