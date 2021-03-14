using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for DamageBreakdownTable.xaml
  /// </summary>
  public partial class DamageBreakdown : BreakdownTable
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private List<PlayerSubStats> PlayerStats;
    private PlayerStats RaidStats;
    private Dictionary<string, List<PlayerStats>> ChildStats;
    private bool CurrentGroupDDSetting = true;
    private bool CurrentGroupDoTSetting = true;
    private bool CurrentGroupProcsSetting = true;
    private bool CurrentGroupResistedSetting = true;
    private bool CurrentShowPets = true;
    private static bool Running = false;
    private Dictionary<string, PlayerSubStats> GroupedDD = new Dictionary<string, PlayerSubStats>();
    private Dictionary<string, PlayerSubStats> GroupedDoT = new Dictionary<string, PlayerSubStats>();
    private Dictionary<string, PlayerSubStats> GroupedProcs = new Dictionary<string, PlayerSubStats>();
    private Dictionary<string, PlayerSubStats> GroupedResisted = new Dictionary<string, PlayerSubStats>();
    private Dictionary<string, List<PlayerSubStats>> UnGroupedDD = new Dictionary<string, List<PlayerSubStats>>();
    private Dictionary<string, List<PlayerSubStats>> UnGroupedDoT = new Dictionary<string, List<PlayerSubStats>>();
    private Dictionary<string, List<PlayerSubStats>> UnGroupedProcs = new Dictionary<string, List<PlayerSubStats>>();
    private Dictionary<string, List<PlayerSubStats>> UnGroupedResisted = new Dictionary<string, List<PlayerSubStats>>();
    private Dictionary<string, List<PlayerSubStats>> OtherDamage = new Dictionary<string, List<PlayerSubStats>>();

    internal DamageBreakdown(CombinedStats currentStats)
    {
      InitializeComponent();
      titleLabel.Content = currentStats?.ShortTitle;
      RaidStats = currentStats.RaidStats;
      ChildStats = currentStats.Children;
    }

    internal void Show(List<PlayerStats> selectedStats)
    {
      if (selectedStats != null)
      {
        Display(selectedStats);
      }
    }

    override internal void Display(List<PlayerStats> selectedStats = null)
    {
      if (Running == false && ChildStats != null && RaidStats != null)
      {
        Running = true;
        groupDirectDamage.IsEnabled = groupDoT.IsEnabled = groupProcs.IsEnabled = groupResisted.IsEnabled = showPets.IsEnabled = false;

        Task.Delay(10).ContinueWith(task =>
        {
          try
          {
            ObservableCollection<PlayerSubStats> list = new ObservableCollection<PlayerSubStats>();

            // initial load
            if (selectedStats != null)
            {
              PlayerStats = new List<PlayerSubStats>();
              foreach (var playerStats in selectedStats.AsParallel().OrderByDescending(stats => GetSortValue(stats)))
              {
                if (ChildStats.ContainsKey(playerStats.Name))
                {
                  foreach (var childStat in ChildStats[playerStats.Name])
                  {
                    PlayerStats.Add(childStat);
                    BuildGroups(childStat, childStat.SubStats.Values.ToList());
                  }

                  Dispatcher.InvokeAsync(() =>
                  {
                    if (!showPets.IsEnabled)
                    {
                      showPets.IsEnabled = true;
                    }
                  });
                }
                else
                {
                  PlayerStats.Add(playerStats);
                  BuildGroups(playerStats, playerStats.SubStats.Values.ToList());
                }
              }
            }

            if (PlayerStats != null)
            {
              var filtered = CurrentShowPets ? PlayerStats : PlayerStats.Where(playerStats => !PlayerManager.Instance.IsVerifiedPet(playerStats.Name));
              foreach (var playerStats in SortSubStats(filtered.ToList()))
              {
                Dispatcher.InvokeAsync(() =>
                {
                  list.Add(playerStats);
                  var optionalList = GetSubStats(playerStats as PlayerStats);
                  SortSubStats(optionalList).ForEach(subStat => list.Add(subStat));
                });
              }

              Dispatcher.InvokeAsync(() => playerDamageDataGrid.ItemsSource = list);

              if (CurrentColumn != null)
              {
                Dispatcher.InvokeAsync(() => CurrentColumn.SortDirection = CurrentSortDirection);
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
          finally
          {
            Dispatcher.InvokeAsync(() => groupDirectDamage.IsEnabled = groupDoT.IsEnabled = groupProcs.IsEnabled = groupResisted.IsEnabled = showPets.IsEnabled = true);
            Running = false;
          }
        }, TaskScheduler.Default);
      }
    }

    private void BuildGroups(PlayerStats playerStats, List<PlayerSubStats> all)
    {
      List<PlayerSubStats> list = new List<PlayerSubStats>();
      PlayerSubStats dots = new PlayerSubStats() { Name = Labels.DOT, Type = Labels.DOT };
      PlayerSubStats dds = new PlayerSubStats() { Name = Labels.DD, Type = Labels.DD };
      PlayerSubStats procs = new PlayerSubStats() { Name = Labels.PROC, Type = Labels.PROC };
      PlayerSubStats resisted = new PlayerSubStats() { Name = Labels.RESIST, Type = Labels.RESIST, ResistRate = 100 };
      List<PlayerSubStats> allDots = new List<PlayerSubStats>();
      List<PlayerSubStats> allDds = new List<PlayerSubStats>();
      List<PlayerSubStats> allProcs = new List<PlayerSubStats>();
      List<PlayerSubStats> allResisted = new List<PlayerSubStats>();

      all.ForEach(sub =>
      {
        PlayerSubStats stats = null;

        switch (sub.Type)
        {
          case Labels.DOT:
            stats = dots;
            allDots.Add(sub);
            break;
          case Labels.DD:
          case Labels.BANE:
            stats = dds;
            allDds.Add(sub);
            break;
          case Labels.PROC:
            stats = procs;
            allProcs.Add(sub);
            break;
          case Labels.RESIST:
            stats = resisted;
            allResisted.Add(sub);
            break;
          default:
            list.Add(sub);
            break;
        }

        StatsUtil.MergeStats(stats, sub);
      });

      foreach (var stats in new PlayerSubStats[] { dots, dds, procs, resisted })
      {
        StatsUtil.CalculateRates(stats, RaidStats, playerStats);
      }

      UnGroupedDD[playerStats.Name] = allDds;
      UnGroupedDoT[playerStats.Name] = allDots;
      UnGroupedProcs[playerStats.Name] = allProcs;
      UnGroupedResisted[playerStats.Name] = allResisted;
      GroupedDD[playerStats.Name] = dds;
      GroupedDoT[playerStats.Name] = dots;
      GroupedProcs[playerStats.Name] = procs;
      GroupedResisted[playerStats.Name] = resisted;
      OtherDamage[playerStats.Name] = list;

      Dispatcher.InvokeAsync(() =>
      {
        if (allDds.Count > 0 && !groupDirectDamage.IsEnabled)
        {
          groupDirectDamage.IsEnabled = true;
        }

        if (allProcs.Count > 0 && !groupProcs.IsEnabled)
        {
          groupProcs.IsEnabled = true;
        }

        if (allDots.Count > 0 && !groupDoT.IsEnabled)
        {
          groupDoT.IsEnabled = true;
        }

        if (allResisted.Count > 0 && !groupResisted.IsEnabled)
        {
          groupResisted.IsEnabled = true;
        }
      });
    }

    private List<PlayerSubStats> GetSubStats(PlayerStats playerStats)
    {
      var name = playerStats.Name;
      List<PlayerSubStats> list = new List<PlayerSubStats>();

      if (OtherDamage.ContainsKey(name))
      {
        AddToList(playerStats, list, OtherDamage[name]);
      }

      if (GroupedDD.ContainsKey(name))
      {
        PlayerSubStats dds = GroupedDD[name];
        if (dds.Total > 0)
        {
          if (CurrentGroupDDSetting)
          {
            list.Add(dds);
          }
          else
          {
            AddToList(playerStats, list, UnGroupedDD[name]);
          }
        }
      }

      if (GroupedDoT.ContainsKey(name))
      {
        PlayerSubStats dots = GroupedDoT[name];
        if (dots.Total > 0)
        {
          if (CurrentGroupDoTSetting)
          {
            list.Add(dots);
          }
          else
          {
            AddToList(playerStats, list, UnGroupedDoT[name]);
          }
        }
      }

      if (GroupedProcs.ContainsKey(name))
      {
        PlayerSubStats procs = GroupedProcs[name];
        if (procs.Total > 0)
        {
          if (CurrentGroupProcsSetting)
          {
            list.Add(procs);
          }
          else
          {
            AddToList(playerStats, list, UnGroupedProcs[name]);
          }
        }
      }

      if (GroupedResisted.ContainsKey(name))
      {
        if (UnGroupedResisted[name].Count > 0)
        {
          if (CurrentGroupResistedSetting)
          {
            list.Add(GroupedResisted[name]);
          }
          else
          {
            AddToList(playerStats, list, UnGroupedResisted[name]);
          }
        }
      }

      return list;
    }

    private void AddToList(PlayerStats playerStats, List<PlayerSubStats> list, List<PlayerSubStats> additionalStats)
    {
      additionalStats.ForEach(stats =>
      {
        if (list.Find(item => item.Name == stats.Name) is PlayerSubStats found)
        {
          var combined = new PlayerSubStats() { Name = found.Name };
          StatsUtil.MergeStats(combined, found);
          StatsUtil.MergeStats(combined, stats);
          StatsUtil.CalculateRates(combined, RaidStats, playerStats);
          list.Remove(found);
          list.Add(combined);
        }
        else
        {
          list.Add(stats);
        }
      });
    }

    private void OptionsChange(object sender, RoutedEventArgs e)
    {
      // check if call is during initialization
      if (PlayerStats != null)
      {
        CurrentGroupDDSetting = groupDirectDamage.IsChecked.Value;
        CurrentGroupDoTSetting = groupDoT.IsChecked.Value;
        CurrentGroupProcsSetting = groupProcs.IsChecked.Value;
        CurrentGroupResistedSetting = groupResisted.IsChecked.Value;
        CurrentShowPets = showPets.IsChecked.Value;
        Display();
      }
    }
  }
}
