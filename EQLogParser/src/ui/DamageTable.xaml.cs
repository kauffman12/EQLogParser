using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for DamageBreakdownTable.xaml
  /// </summary>
  public partial class DamageTable : BreakdownTable
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private List<PlayerSubStats> PlayerStats;
    private CombinedDamageStats CurrentStats;
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
    private Dictionary<string, string> SpellTypeCache = new Dictionary<string, string>();
    private static bool running = false;

    public DamageTable(MainWindow mainWindow, string title)
    {
      InitializeComponent();
      TheMainWindow = mainWindow;
      titleLabel.Content = title;
    }

    public void ShowDamage(List<PlayerStats> selectedStats, CombinedDamageStats currentStats)
    {
      if (selectedStats != null && currentStats != null)
      {
        CurrentStats = currentStats;
        Display(selectedStats);
      }
    }

    private new void Display(List<PlayerStats> selectedStats = null)
    {
      if (running == false)
      {
        running = true;
        Dispatcher.InvokeAsync(() => TheMainWindow.Busy(true));

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
                if (CurrentStats.Children.ContainsKey(playerStat.Name))
                {
                  foreach (var childStat in CurrentStats.Children[playerStat.Name])
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

              // no longer needed
              SpellTypeCache.Clear();
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
          catch (Exception ex)
          {
            LOG.Error(ex);
          }
          finally
          {
            Dispatcher.InvokeAsync(() => TheMainWindow.Busy(false));
            running = false;
          }
        });
      }
    }

    private string CheckSpellType(string name, string type)
    {
      string result = type;

      if (type == Labels.DD_NAME || type == Labels.DOT_NAME)
      {
        if (!SpellTypeCache.TryGetValue(name, out result))
        {
          string spellName = Helpers.AbbreviateSpellName(name);
          SpellData data = DataManager.Instance.GetSpellByAbbrv(spellName);
          result = (data != null && data.IsProc) ? Labels.PROC_NAME : type;
          SpellTypeCache[name] = result;
        }
      }

      return result;
    }

    private void BuildGroups(PlayerStats playerStats, List<PlayerSubStats> all)
    {
      List<PlayerSubStats> list = new List<PlayerSubStats>();
      PlayerSubStats dots = new PlayerSubStats() { Name = Labels.DOT_NAME, Type = Labels.DOT_NAME };
      PlayerSubStats dds = new PlayerSubStats() { Name = Labels.DD_NAME, Type = Labels.DD_NAME };
      PlayerSubStats procs = new PlayerSubStats() { Name = Labels.PROC_NAME, Type = Labels.PROC_NAME };
      PlayerSubStats resisted = new PlayerSubStats() { Name = Labels.RESIST_NAME, Type = Labels.RESIST_NAME, ResistRate = 100 };
      List<PlayerSubStats> allDots = new List<PlayerSubStats>();
      List<PlayerSubStats> allDds = new List<PlayerSubStats>();
      List<PlayerSubStats> allProcs = new List<PlayerSubStats>();
      List<PlayerSubStats> allResisted = new List<PlayerSubStats>();

      all.ForEach(sub =>
      {
        PlayerSubStats stats = null;

        switch(CheckSpellType(sub.Name, sub.Type))
        {
          case Labels.DOT_NAME:
            stats = dots;
            allDots.Add(sub);
            break;
          case Labels.DD_NAME:
            stats = dds;
            allDds.Add(sub);
            break;
          case Labels.PROC_NAME:
            stats = procs;
            allProcs.Add(sub);
            break;
          case Labels.RESIST_NAME:
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

      foreach(var stats in new PlayerSubStats[] { dots, dds, procs, resisted })
      {
        if (stats.Hits > 0)
        {
          stats.DPS = (long) Math.Round(stats.Total / stats.TotalSeconds, 2);
          stats.SDPS = (long) Math.Round(stats.Total / CurrentStats.RaidStats.TotalSeconds, 2);
          stats.Avg = (long) Math.Round(Convert.ToDecimal(stats.Total) / stats.Hits, 2);

          if (stats.CritHits > 0)
          {
            stats.AvgCrit = (long) Math.Round(Convert.ToDecimal(stats.TotalCrit) / stats.CritHits, 2);
          }

          if (stats.LuckyHits > 0)
          {
            stats.AvgLucky = (long) Math.Round(Convert.ToDecimal(stats.TotalLucky) / stats.LuckyHits, 2);
          }

          stats.CritRate = Math.Round(Convert.ToDecimal(stats.CritHits) / stats.Hits * 100, 2);
          stats.LuckRate = Math.Round(Convert.ToDecimal(stats.LuckyHits) / stats.Hits * 100, 2);

          var tcMult = stats.Type == Labels.DOT_NAME ? 1 : 2;
          stats.TwincastRate = Math.Round(Convert.ToDecimal(stats.TwincastHits) / stats.Hits * tcMult * 100, 2);
          stats.Percent = Math.Round(playerStats.Percent / 100 * ((decimal) stats.Total / playerStats.Total) * 100, 2);
          stats.ResistRate = Math.Round(Convert.ToDecimal(stats.Resists) / (stats.Hits + stats.Resists) * 100, 2);
        }
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
