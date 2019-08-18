using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
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
    private Dictionary<string, PlayerSubStats> GroupedDD = new Dictionary<string, PlayerSubStats>();
    private Dictionary<string, PlayerSubStats> GroupedDoT = new Dictionary<string, PlayerSubStats>();
    private Dictionary<string, PlayerSubStats> GroupedProcs = new Dictionary<string, PlayerSubStats>();
    private Dictionary<string, PlayerSubStats> GroupedResisted = new Dictionary<string, PlayerSubStats>();
    private Dictionary<string, List<PlayerSubStats>> UnGroupedDD = new Dictionary<string, List<PlayerSubStats>>();
    private Dictionary<string, List<PlayerSubStats>> UnGroupedDoT = new Dictionary<string, List<PlayerSubStats>>();
    private Dictionary<string, List<PlayerSubStats>> UnGroupedProcs = new Dictionary<string, List<PlayerSubStats>>();
    private Dictionary<string, List<PlayerSubStats>> UnGroupedResisted = new Dictionary<string, List<PlayerSubStats>>();
    private Dictionary<string, List<PlayerSubStats>> OtherDamage = new Dictionary<string, List<PlayerSubStats>>();
    private static bool Running = false;

    public DamageBreakdown(CombinedStats currentStats)
    {
      InitializeComponent();
      titleLabel.Content = currentStats.ShortTitle;
      RaidStats = currentStats.RaidStats;
      ChildStats = currentStats.Children;
    }

    public void Show(List<PlayerStats> selectedStats)
    {
      if (selectedStats != null)
      {
        Display(selectedStats);
      }
    }

    private new void Display(List<PlayerStats> selectedStats = null)
    {
      if (Running == false && ChildStats != null && RaidStats != null)
      {
        Running = true;
        Dispatcher.InvokeAsync(() => (Application.Current.MainWindow as MainWindow)?.Busy(true));

        Task.Delay(5).ContinueWith(task =>
        {
          try
          {
            ObservableCollection<PlayerSubStats> list = new ObservableCollection<PlayerSubStats>();

            // initial load
            if (selectedStats != null)
            {
              PlayerStats = new List<PlayerSubStats>();
              foreach (var playerStat in selectedStats.AsParallel().OrderByDescending(stats => GetSortValue(stats)))
              {
                if (ChildStats.ContainsKey(playerStat.Name))
                {
                  foreach (var childStat in ChildStats[playerStat.Name])
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
                  PlayerStats.Add(playerStat);
                  BuildGroups(playerStat, playerStat.SubStats.Values.ToList());
                }
              }
            }

            if (PlayerStats != null)
            {
              var filtered = CurrentShowPets ? PlayerStats : PlayerStats.Where(playerStats => !DataManager.Instance.CheckNameForPet(playerStats.Name));
              foreach (var playerStat in SortSubStats(filtered.ToList()))
              {
                Dispatcher.InvokeAsync(() =>
                {
                  list.Add(playerStat);
                  var optionalList = GetSubStats(playerStat.Name);
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
            Dispatcher.InvokeAsync(() => (Application.Current.MainWindow as MainWindow)?.Busy(false));
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

        if (stats != null)
        {
          stats.Total += sub.Total;
          stats.TotalCrit += sub.TotalCrit;
          stats.TotalLucky += sub.TotalLucky;
          stats.Hits += sub.Hits;
          stats.Resists += sub.Resists;
          stats.CritHits += sub.CritHits;
          stats.LuckyHits += sub.LuckyHits;
          stats.TwincastHits += sub.TwincastHits;
          stats.Max = (sub.Max < stats.Max) ? stats.Max : sub.Max;
          stats.TotalSeconds = Math.Max(stats.TotalSeconds, sub.TotalSeconds);
        }
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

    private List<PlayerSubStats> GetSubStats(string name)
    {
      List<PlayerSubStats> list = new List<PlayerSubStats>();

      if (OtherDamage.ContainsKey(name))
      {
        list.AddRange(OtherDamage[name]);
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
            list.AddRange(UnGroupedDD[name]);
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
            list.AddRange(UnGroupedDoT[name]);
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
            list.AddRange(UnGroupedProcs[name]);
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
            list.AddRange(UnGroupedResisted[name]);
          }

        }
      }

      return list;
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
