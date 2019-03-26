using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace EQLogParser
{
  class HealingStatsManager : SummaryBuilder
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    internal static HealingStatsManager Instance = new HealingStatsManager();

    internal event EventHandler<DataPointEvent> EventsUpdateDataPoint;
    internal event EventHandler<StatsGenerationEvent> EventsGenerationStatus;

    internal List<List<ActionBlock>> HealingGroups = new List<List<ActionBlock>>();
    internal bool IsAEHealingAvailable = false;

    private const int HEAL_OFFSET = 10; // additional # of seconds to count hilling after last damage is seen

    private PlayerStats RaidTotals;
    private List<NonPlayer> Selected;
    private string Title;

    internal HealingStatsManager()
    {
      DataManager.Instance.EventsClearedActiveData += (object sender, bool e) =>
      {
        HealingGroups.Clear();
        RaidTotals = null;
        Selected = null;
        Title = "";
      };
    }

    internal void BuildTotalStats(HealingStatsOptions options)
    {
      Selected = options.Npcs;
      Title = options.Name;

      try
      {
        FireNewStatsEvent(options);

        RaidTotals = StatsUtil.CreatePlayerStats(Labels.RAID_PLAYER);
        HealingGroups.Clear();

        Selected.ForEach(npc => StatsUtil.UpdateTimeDiffs(RaidTotals, npc, HEAL_OFFSET));
        RaidTotals.TotalSeconds = RaidTotals.TimeDiffs.Sum();

        if (RaidTotals.BeginTimes.Count > 0 && RaidTotals.BeginTimes.Count == RaidTotals.LastTimes.Count)
        {
          for (int i = 0; i < RaidTotals.BeginTimes.Count; i++)
          {
            HealingGroups.Add(DataManager.Instance.GetHealsDuring(RaidTotals.BeginTimes[i], RaidTotals.LastTimes[i]));
          }

          ComputeHealStats(options);
        }
        else
        {
          FireNoDataEvent(options);
        }
      }
      catch (Exception e)
      {
        LOG.Error(e);
      }
    }

    internal void RebuildTotalStats(HealingStatsOptions options)
    {
      FireNewStatsEvent(options);
      ComputeHealStats(options);
    }

    internal bool IsValidHeal(HealRecord record, bool showAE)
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

    internal void FireSelectionEvent(HealingStatsOptions options, List<PlayerStats> selected)
    {
      if (options.RequestChartData)
      {
        // send update
        DataPointEvent de = new DataPointEvent() { Action = "SELECT", Selected = selected };
        EventsUpdateDataPoint?.Invoke(HealingGroups, de);
      }
    }

    internal void FireUpdateEvent(HealingStatsOptions options, List<PlayerStats> selected = null)
    {
      if (options.RequestChartData)
      {
        // send update
        DataPointEvent de = new DataPointEvent() { Action = "UPDATE", Selected = selected, Iterator = new HealGroupIterator(HealingGroups, options.IsAEHealingEanbled) };
        EventsUpdateDataPoint?.Invoke(HealingGroups, de);
      }
    }

    private void FireCompletedEvent(HealingStatsOptions options, CombinedHealStats combined)
    {
      if (options.RequestSummaryData)
      {
        // generating new stats
        EventsGenerationStatus?.Invoke(this, new StatsGenerationEvent()
        {
          Type = Labels.HEAL_PARSE,
          State = "COMPLETED",
          CombinedStats = combined,
          IsAEHealingAvailable = IsAEHealingAvailable
        });
      }
    }

    private void FireNewStatsEvent(HealingStatsOptions options)
    {
      if (options.RequestSummaryData)
      {
       // generating new stats
        EventsGenerationStatus?.Invoke(this, new StatsGenerationEvent() { Type = Labels.HEAL_PARSE, State = "STARTED" });
      }
    }

    private void FireNoDataEvent(HealingStatsOptions options)
    {
      if (options.RequestSummaryData)
      {
        // nothing to do
        EventsGenerationStatus?.Invoke(this, new StatsGenerationEvent() { Type = Labels.HEAL_PARSE, State = "NONPC" });
      }

      if (options.RequestChartData)
      {
        // send update
        DataPointEvent de = new DataPointEvent() { Action = "CLEAR" };
        EventsUpdateDataPoint?.Invoke(HealingGroups, de);
      }
    }

    private void ComputeHealStats(HealingStatsOptions options)
    {
      CombinedHealStats combined = null;
      Dictionary<string, PlayerStats> individualStats = new Dictionary<string, PlayerStats>();

      // always start over
      RaidTotals.Total = 0;
      IsAEHealingAvailable = false;

      try
      {
        FireUpdateEvent(options);

        HealingGroups.ForEach(group =>
        {
          // keep track of time range as well as the players that have been updated
          Dictionary<string, PlayerSubStats> allStats = new Dictionary<string, PlayerSubStats>();

          group.ForEach(block =>
          {
            block.Actions.ForEach(action =>
            {
              HealRecord record = action as HealRecord;

              if (IsValidHeal(record, options.IsAEHealingEanbled))
              {
                RaidTotals.Total += record.Total;
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

        RaidTotals.DPS = (long) Math.Round(RaidTotals.Total / RaidTotals.TotalSeconds, 2);
        Parallel.ForEach(individualStats.Values, stats => StatsUtil.UpdateCalculations(stats, RaidTotals));

        combined = new CombinedHealStats();
        combined.RaidStats = RaidTotals;
        combined.UniqueClasses = new Dictionary<string, byte>();
        combined.StatsList = individualStats.Values.AsParallel().OrderByDescending(item => item.Total).ToList();
        combined.TargetTitle = (Selected.Count > 1 ? "Combined (" + Selected.Count + "): " : "") + Title;
        combined.TimeTitle = string.Format(StatsUtil.TIME_FORMAT, RaidTotals.TotalSeconds);
        combined.TotalTitle = string.Format(StatsUtil.TOTAL_FORMAT, StatsUtil.FormatTotals(RaidTotals.Total), " Heals ", StatsUtil.FormatTotals(RaidTotals.DPS));
        combined.FullTitle = StatsUtil.FormatTitle(combined.TargetTitle, combined.TimeTitle, combined.TotalTitle);
        combined.ShortTitle = StatsUtil.FormatTitle(combined.TargetTitle, combined.TimeTitle, "");

        for (int i = 0; i < combined.StatsList.Count; i++)
        {
          combined.StatsList[i].Rank = Convert.ToUInt16(i + 1);
          combined.UniqueClasses[combined.StatsList[i].ClassName] = 1;
        }
      }
      catch (Exception ex)
      {
        LOG.Error(ex);
      }

      FireCompletedEvent(options, combined);
    }

    public StatsSummary BuildSummary(CombinedStats currentStats, List<PlayerStats> selected, bool showTotals, bool rankPlayers)
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
  }
}
