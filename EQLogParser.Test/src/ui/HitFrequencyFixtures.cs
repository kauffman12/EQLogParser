namespace EQLogParser;

internal static class HitFrequencyFixtures
{
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
