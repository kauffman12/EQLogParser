namespace EQLogParser;

/// <summary>Presentation-only spell groups shared by outgoing and incoming damage breakdowns.</summary>
internal sealed class DamageBreakdownGroups
{
  private readonly Dictionary<PlayerStats, List<PlayerSubStats>> _children = [];

  internal DamageBreakdownGroups(PlayerStats raidStats, IEnumerable<PlayerStats> players)
  {
    Players = players.ToArray();
    foreach (var player in Players)
    {
      _children[player] = BuildGroups(player, raidStats);
    }
  }

  internal IReadOnlyList<PlayerStats> Players { get; }

  internal IEnumerable<PlayerSubStats> ChildrenOf(object parent) => parent switch
  {
    null => Players,
    PlayerStats player => _children.TryGetValue(player, out var children) ? children : [],
    SubStatsBreakdown group => group.Children,
    _ => []
  };

  private static List<PlayerSubStats> BuildGroups(PlayerStats player, PlayerStats raidStats)
  {
    var rows = new List<PlayerSubStats>();
    var dots = new SubStatsBreakdown { Name = Labels.Dot, Type = Labels.Dot };
    var dds = new SubStatsBreakdown { Name = Labels.Dd, Type = Labels.Dd };
    var procs = new SubStatsBreakdown { Name = Labels.Proc, Type = Labels.Proc };

    foreach (var sub in player.SubStats)
    {
      var group = sub.Type switch
      {
        Labels.Dot => dots,
        Labels.Dd or Labels.Bane => dds,
        Labels.Proc => procs,
        _ => null
      };

      if (group is null)
      {
        rows.Add(sub);
      }
      else
      {
        group.Children.Add(sub);
        StatsUtil.MergeStats(group, sub);
      }
    }

    // Keep the outgoing breakdown's category order for ties; the grid may apply its own sort afterward.
    foreach (var group in new[] { dds, dots, procs })
    {
      StatsUtil.CalculateRates(group, raidStats, player);
      if (group.Total > 0)
      {
        rows.Add(group);
      }
    }

    return rows.OrderByDescending(row => row.Total).ToList();
  }
}
