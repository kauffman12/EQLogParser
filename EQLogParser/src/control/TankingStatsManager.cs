
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace EQLogParser
{
  class TankingStatsManager : ISummaryBuilder
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    internal static TankingStatsManager Instance = new TankingStatsManager();

    internal event EventHandler<DataPointEvent> EventsUpdateDataPoint;
    internal event EventHandler<StatsGenerationEvent> EventsGenerationStatus;

    internal Dictionary<string, byte> NpcNames = new Dictionary<string, byte>();
    internal List<List<ActionBlock>> TankingGroups = new List<List<ActionBlock>>();

    private const int TANK_OFFSET = 10; // additional # of seconds to count

    private PlayerStats RaidTotals;
    private List<NonPlayer> Selected;
    private string Title;

    internal TankingStatsManager()
    {
      lock (TankingGroups)
      {
        DataManager.Instance.EventsClearedActiveData += (object sender, bool e) =>
        {
          TankingGroups.Clear();
          RaidTotals = null;
          Selected = null;
          Title = "";
        };
      }
    }

    internal void BuildTotalStats(GenerateStatsOptions options)
    {
      lock (TankingGroups)
      {
        Selected = options.Npcs;
        Title = options.Name;

        try
        {
          FireNewStatsEvent(options);

          RaidTotals = StatsUtil.CreatePlayerStats(Labels.RAID);
          TankingGroups.Clear();
          NpcNames.Clear();

          Selected.ForEach(npc =>
          {
            StatsUtil.UpdateTimeDiffs(RaidTotals, npc, TANK_OFFSET);
            NpcNames[npc.Name] = 1;
          });

          RaidTotals.TotalSeconds = RaidTotals.TimeDiffs.Sum();

          if (RaidTotals.BeginTimes.Count > 0 && RaidTotals.BeginTimes.Count == RaidTotals.LastTimes.Count)
          {
            for (int i = 0; i < RaidTotals.BeginTimes.Count; i++)
            {
              var updatedDamages = new List<ActionBlock>();
              var damages = DataManager.Instance.GetDamageDuring(RaidTotals.BeginTimes[i], RaidTotals.LastTimes[i]);
              damages.ForEach(damage =>
              {
                var updatedDamage = new ActionBlock() { BeginTime = damage.BeginTime };
                updatedDamage.Actions.AddRange(damage.Actions.AsParallel().Where(item => item is DamageRecord record && IsValidDamage(record)));

                if (updatedDamage.Actions.Count > 0)
                {
                  updatedDamages.Add(updatedDamage);
                }
              });

              if (updatedDamages.Count > 0)
              {
                TankingGroups.Add(updatedDamages);
              }
            }

            ComputeTankingStats(options);
          }
          else
          {
            FireNoDataEvent(options);
          }
        }
        catch (ArgumentNullException ne)
        {
          LOG.Error(ne);
        }
        catch (NullReferenceException nr)
        {
          LOG.Error(nr);
        }
        catch (ArgumentOutOfRangeException aor)
        {
          LOG.Error(aor);
        }
        catch (ArgumentException ae)
        {
          LOG.Error(ae);
        }
      }
    }

    internal void RebuildTotalStats(GenerateStatsOptions options)
    {
      if (TankingGroups.Count > 0)
      {
        FireNewStatsEvent(options);
        ComputeTankingStats(options);
      }
    }

    internal void FireFilterEvent(GenerateStatsOptions options, Predicate<object> filter)
    {
      FireChartEvent(options, "FILTER", null, filter);
    }

    internal void FireSelectionEvent(GenerateStatsOptions options, List<PlayerStats> selected)
    {
      FireChartEvent(options, "SELECT", selected);
    }

    internal void FireUpdateEvent(GenerateStatsOptions options, List<PlayerStats> selected = null, Predicate<object> filter = null)
    {
      FireChartEvent(options, "UPDATE", selected, filter);
    }

    private void FireCompletedEvent(GenerateStatsOptions options, CombinedStats combined, List<List<ActionBlock>> groups)
    {
      if (options.RequestSummaryData)
      {
        // generating new stats
        var genEvent = new StatsGenerationEvent()
        {
          Type = Labels.TANKPARSE,
          State = "COMPLETED",
          CombinedStats = combined
        };

        genEvent.Groups.AddRange(groups);
        EventsGenerationStatus?.Invoke(this, genEvent);
      }
    }

    private void FireNewStatsEvent(GenerateStatsOptions options)
    {
      if (options.RequestSummaryData)
      {
        // generating new stats
        EventsGenerationStatus?.Invoke(this, new StatsGenerationEvent() { Type = Labels.TANKPARSE, State = "STARTED" });
      }
    }

    private void FireNoDataEvent(GenerateStatsOptions options)
    {
      if (options.RequestSummaryData)
      {
        // nothing to do
        EventsGenerationStatus?.Invoke(this, new StatsGenerationEvent() { Type = Labels.TANKPARSE, State = "NONPC" });
      }

      FireChartEvent(options, "CLEAR");
    }

    private void FireChartEvent(GenerateStatsOptions options, string action, List<PlayerStats> selected = null, Predicate<object> filter = null)
    {
      lock (TankingGroups)
      {
        if (options.RequestChartData)
        {
          // send update
          DataPointEvent de = new DataPointEvent() { Action = action, Iterator = new TankGroupCollection(TankingGroups), Filter = filter };

          if (selected != null)
          {
            de.Selected.AddRange(selected);
          }

          EventsUpdateDataPoint?.Invoke(TankingGroups, de);
        }
      }
    }

    internal bool IsValidDamage(DamageRecord record)
    {
      return record != null &&
        record.Defender == record.Attacker && PlayerManager.Instance.IsVerifiedPlayer(record.Attacker)
        || (NpcNames.ContainsKey(record.Attacker) && (Helpers.IsPossiblePlayerName(record.Defender) || PlayerManager.Instance.IsPetOrPlayer(record.Defender)));
    }

    private void ComputeTankingStats(GenerateStatsOptions options)
    {
      lock (TankingGroups)
      {
        CombinedStats combined = null;
        Dictionary<string, PlayerStats> individualStats = new Dictionary<string, PlayerStats>();

        if (RaidTotals != null)
        {
          // always start over
          RaidTotals.Total = 0;

          try
          {
            FireUpdateEvent(options);

            if (options.RequestSummaryData)
            {
              TankingGroups.ForEach(group =>
              {
                // keep track of time range as well as the players that have been updated
                Dictionary<string, PlayerSubStats> allStats = new Dictionary<string, PlayerSubStats>();

                group.ForEach(block =>
                {
                  block.Actions.ForEach(action =>
                  {
                    if (action is DamageRecord record)
                    {
                      RaidTotals.Total += record.Total;
                      PlayerStats stats = StatsUtil.CreatePlayerStats(individualStats, record.Defender);

                      StatsUtil.UpdateStats(stats, record, block.BeginTime);
                      allStats[record.Defender] = stats;

                      PlayerSubStats subStats = StatsUtil.CreatePlayerSubStats(stats.SubStats, record.SubType, record.Type);
                      UpdateSubStats(subStats, record, block.BeginTime);
                      allStats[stats.Name + "=" + record.SubType] = subStats;
                    }
                  });
                });

                Parallel.ForEach(allStats.Values, stats =>
                {
                  stats.TotalSeconds += stats.LastTime - stats.BeginTime + 1;
                  stats.BeginTime = double.NaN;
                });
              });

              RaidTotals.DPS = (long)Math.Round(RaidTotals.Total / RaidTotals.TotalSeconds, 2);
              Parallel.ForEach(individualStats.Values, stats => StatsUtil.UpdateCalculations(stats, RaidTotals));

              combined = new CombinedStats
              {
                RaidStats = RaidTotals,
                TargetTitle = (Selected.Count > 1 ? "Combined (" + Selected.Count + "): " : "") + Title,
                TimeTitle = string.Format(CultureInfo.CurrentCulture, StatsUtil.TIME_FORMAT, RaidTotals.TotalSeconds),
                TotalTitle = string.Format(CultureInfo.CurrentCulture, StatsUtil.TOTAL_FORMAT, StatsUtil.FormatTotals(RaidTotals.Total), " Tanked ", StatsUtil.FormatTotals(RaidTotals.DPS))
              };

              combined.StatsList.AddRange(individualStats.Values.AsParallel().OrderByDescending(item => item.Total));
              combined.FullTitle = StatsUtil.FormatTitle(combined.TargetTitle, combined.TimeTitle, combined.TotalTitle);
              combined.ShortTitle = StatsUtil.FormatTitle(combined.TargetTitle, combined.TimeTitle, "");

              for (int i = 0; i < combined.StatsList.Count; i++)
              {
                combined.StatsList[i].Rank = Convert.ToUInt16(i + 1);
                combined.UniqueClasses[combined.StatsList[i].ClassName] = 1;
              }
            }
          }
          catch (ArgumentNullException ane)
          {
            LOG.Error(ane);
          }
          catch (NullReferenceException nre)
          {
            LOG.Error(nre);
          }
          catch (ArgumentOutOfRangeException aro)
          {
            LOG.Error(aro);
          }

          FireCompletedEvent(options, combined, TankingGroups);
        }
      }
    }


    public StatsSummary BuildSummary(CombinedStats currentStats, List<PlayerStats> selected, bool showTotals, bool rankPlayers, bool _)
    {
      List<string> list = new List<string>();

      string title = "";
      string details = "";

      if (currentStats != null)
      {
        if (selected?.Count > 0)
        {
          foreach (PlayerStats stats in selected.OrderByDescending(item => item.Total))
          {
            string playerFormat = rankPlayers ? string.Format(CultureInfo.CurrentCulture, StatsUtil.PLAYER_RANK_FORMAT, stats.Rank, stats.Name) : string.Format(CultureInfo.CurrentCulture, StatsUtil.PLAYER_FORMAT, stats.Name);
            string damageFormat = string.Format(CultureInfo.CurrentCulture, StatsUtil.TOTAL_ONLY_FORMAT, StatsUtil.FormatTotals(stats.Total));
            list.Add(playerFormat + damageFormat + " ");
          }
        }

        details = list.Count > 0 ? ", " + string.Join(" | ", list) : "";
        title = StatsUtil.FormatTitle(currentStats.TargetTitle, currentStats.TimeTitle, showTotals ? currentStats.TotalTitle : "");
      }

      return new StatsSummary() { Title = title, RankedPlayers = details, };
    }

    private static void UpdateSubStats(PlayerSubStats subStats, DamageRecord record, double beginTime)
    {
      StatsUtil.UpdateStats(subStats, record, beginTime);
    }
  }
}
