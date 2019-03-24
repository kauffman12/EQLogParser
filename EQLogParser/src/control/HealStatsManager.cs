using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace EQLogParser
{
  class HealStatsManager
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    internal static event EventHandler<DataPointEvent> EventsUpdateDataPoint;
    internal static List<List<ActionBlock>> HealGroups = new List<List<ActionBlock>>();
    internal static bool IsAEHealingAvailable = false;

    private const int HEAL_OFFSET = 5; // additional # of seconds to count hilling after last damage is seen

    private static List<NonPlayer> Selected;
    private static string Title;

    internal static CombinedHealStats BuildTotalStats(string title, List<NonPlayer> selected, bool showAE)
    {
      CombinedHealStats combined = null;
      Selected = selected;
      Title = title;

      try
      {
        PlayerStats raidTotals = StatsUtil.CreatePlayerStats(Labels.RAID_PLAYER);

        HealGroups.Clear();

        selected.ForEach(npc => StatsUtil.UpdateTimeDiffs(raidTotals, npc, HEAL_OFFSET));
        raidTotals.TotalSeconds = raidTotals.TimeDiffs.Sum();

        if (raidTotals.BeginTimes.Count > 0 && raidTotals.BeginTimes.Count == raidTotals.LastTimes.Count)
        {
          for (int i = 0; i < raidTotals.BeginTimes.Count; i++)
          {
            HealGroups.Add(DataManager.Instance.GetHealsDuring(raidTotals.BeginTimes[i], raidTotals.LastTimes[i]));
          }

          combined = ComputeHealStats(raidTotals, showAE);
        }
        else
        {
          // send update
          DataPointEvent de = new DataPointEvent() { Action = "CLEAR" };
          EventsUpdateDataPoint?.Invoke(HealGroups, de);
        }
      }
      catch (Exception e)
      {
        LOG.Error(e);
      }

      return combined;
    }

    internal static CombinedHealStats ComputeHealStats(PlayerStats raidTotals, bool showAE)
    {
      CombinedHealStats combined = null;
      Dictionary<string, PlayerStats> individualStats = new Dictionary<string, PlayerStats>();

      // always start over
      raidTotals.Total = 0;
      IsAEHealingAvailable = false;

      try
      {
        // send update
        DataPointEvent de = new DataPointEvent() { Action = "UPDATE", Iterator = new HealGroupIterator(HealGroups, showAE) };
        EventsUpdateDataPoint?.Invoke(HealGroups, de);

        HealGroups.ForEach(group =>
        {
          // keep track of time range as well as the players that have been updated
          Dictionary<string, PlayerSubStats> allStats = new Dictionary<string, PlayerSubStats>();

          group.ForEach(block =>
          {
            block.Actions.ForEach(action =>
            {
              HealRecord record = action as HealRecord;

              if (IsValidHeal(record, showAE))
              {
                raidTotals.Total += record.Total;
                PlayerStats stats = StatsUtil.CreatePlayerStats(individualStats, record.Healer);

                StatsUtil.UpdateStats(stats, record, block.BeginTime);
                allStats[record.Healer] = stats;

                var spellStatName = record.SubType ?? Labels.UNKNOWN_SPELL;
                PlayerSubStats spellStats = StatsUtil.CreatePlayerSubStats(stats.SubStats, spellStatName, record.Type);
                StatsUtil.UpdateStats(spellStats, record, block.BeginTime);
                allStats[stats.Name + "=" + spellStatName] = spellStats;

                var healedStatName = record.Healed;
                if (stats.SubStats2 == null)
                {
                  stats.SubStats2 = new Dictionary<string, PlayerSubStats>();
                }

                PlayerSubStats healedStats = StatsUtil.CreatePlayerSubStats(stats.SubStats2, healedStatName, record.Type);
                StatsUtil.UpdateStats(healedStats, record, block.BeginTime);
                allStats[stats.Name + "=" + healedStatName] = healedStats;
              }
            });
          });

          Parallel.ForEach(allStats.Values, stats =>
          {
            stats.TotalSeconds += stats.LastTime - stats.BeginTime + 1;
            stats.BeginTime = double.NaN;
          });
        });

        raidTotals.DPS = (long) Math.Round(raidTotals.Total / raidTotals.TotalSeconds, 2);
        Parallel.ForEach(individualStats.Values, stats => StatsUtil.UpdateCalculations(stats, raidTotals));

        combined = new CombinedHealStats();
        combined.RaidStats = raidTotals;
        combined.UniqueClasses = new Dictionary<string, byte>();
        combined.StatsList = individualStats.Values.AsParallel().OrderByDescending(item => item.Total).ToList();
        combined.TargetTitle = (Selected.Count > 1 ? "Combined (" + Selected.Count + "): " : "") + Title;
        combined.TimeTitle = string.Format(StatsUtil.TIME_FORMAT, raidTotals.TotalSeconds);
        combined.TotalTitle = string.Format(StatsUtil.TOTAL_FORMAT, StatsUtil.FormatTotals(raidTotals.Total), " Heals ", StatsUtil.FormatTotals(raidTotals.DPS));
        combined.FullTitle = StatsUtil.FormatTitle(combined.TargetTitle, combined.TimeTitle, combined.TotalTitle);
        combined.ShortTitle = StatsUtil.FormatTitle(combined.TargetTitle, combined.TimeTitle, "");

        for (int i = 0; i < combined.StatsList.Count; i++)
        {
          combined.StatsList[i].Rank = Convert.ToUInt16(i + 1);
          combined.UniqueClasses[combined.StatsList[i].ClassName] = 1;
        }
      }
      catch(Exception ex)
      {
        LOG.Error(ex);
      }

      return combined;
    }

    internal static StatsSummary BuildSummary(CombinedStats currentStats, List<PlayerStats> selected, bool showTotals, bool rankPlayers)
    {
      List<string> list = new List<string>();

      string title = "";
      string details = "";

      if (currentStats != null)
      {
        if (selected != null)
        {
          foreach (PlayerStats stats in selected.OrderByDescending(item => item.Total))
          {
            string playerFormat = rankPlayers ? string.Format(StatsUtil.PLAYER_RANK_FORMAT, stats.Rank, stats.Name) : string.Format(StatsUtil.PLAYER_FORMAT, stats.Name);
            string damageFormat = string.Format(StatsUtil.TOTAL_ONLY_FORMAT, StatsUtil.FormatTotals(stats.Total));
            list.Add(playerFormat + damageFormat + " ");
          }
        }

        details = list.Count > 0 ? ", " + string.Join(" | ", list) : "";
        title = StatsUtil.FormatTitle(currentStats.TargetTitle, currentStats.TimeTitle, showTotals ? currentStats.TotalTitle : "");
      }

      return new StatsSummary() { Title = title, RankedPlayers = details, };
    }

    internal static void FireSelectionEvent(List<PlayerStats> selected)
    {
      // send update
      DataPointEvent de = new DataPointEvent() { Action = "SELECT", Selected = selected };
      EventsUpdateDataPoint?.Invoke(HealGroups, de);
    }

    internal static void FireUpdateEvent(bool showAE, List<PlayerStats> selected = null)
    {
      // send update
      DataPointEvent de = new DataPointEvent() { Action = "UPDATE", Selected = selected, Iterator = new DamageGroupIterator(HealGroups, showAE) };
      EventsUpdateDataPoint?.Invoke(HealGroups, de);
    }

    internal static bool IsValidHeal(HealRecord record, bool showAE)
    {
      bool valid = false;

      if (record != null && !DataManager.Instance.IsProbablyNotAPlayer(record.Healed))
      {
        valid = true;

        SpellData spellData = null;
        if (record.SubType != null && (spellData = DataManager.Instance.GetSpellByName(record.SubType)) != null)
        {
          if (spellData.Target == (byte) SpellTarget.TARGET_AE || spellData.Target == (byte) SpellTarget.NEARBY_PLAYERS_AE ||
            spellData.Target == (byte) SpellTarget.TARGET_RING_AE)
          {
            IsAEHealingAvailable = true;
            valid = showAE;
          }
        }
      }

      return valid;
    }
  }

  internal class HealParse : Parse
  {
    internal new StatsSummary Create(bool showTotals, bool rankPlayers)
    {
      return HealStatsManager.BuildSummary(Combined, Selected, showTotals, rankPlayers);
    }
  }
}
