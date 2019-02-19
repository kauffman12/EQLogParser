using ActiproSoftware.Windows.Themes;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for DamageBreakdownTable.xaml
  /// </summary>
  public partial class DamageTable : UserControl
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private MainWindow TheMainWindow;
    private List<PlayerSubStats> PlayerStats;
    private CombinedStats CurrentStats;
    private string CurrentSortKey = "TotalDamage";
    private ListSortDirection CurrentSortDirection = ListSortDirection.Descending;
    private DataGridTextColumn CurrentColumn = null;
    private bool CurrentGroupDDSetting = true;
    private bool CurrentGroupDoTSetting = true;
    private bool CurrentGroupProcsSetting = true;
    private bool CurrentShowPets = true;
    private Dictionary<string, PlayerSubStats> GroupedDD = new Dictionary<string, PlayerSubStats>();
    private Dictionary<string, PlayerSubStats> GroupedDoT = new Dictionary<string, PlayerSubStats>();
    private Dictionary<string, PlayerSubStats> GroupedProcs = new Dictionary<string, PlayerSubStats>();
    private Dictionary<string, List<PlayerSubStats>> UnGroupedDD = new Dictionary<string, List<PlayerSubStats>>();
    private Dictionary<string, List<PlayerSubStats>> UnGroupedDoT = new Dictionary<string, List<PlayerSubStats>>();
    private Dictionary<string, List<PlayerSubStats>> UnGroupedProcs = new Dictionary<string, List<PlayerSubStats>>();
    private Dictionary<string, List<PlayerSubStats>> OtherDamage = new Dictionary<string, List<PlayerSubStats>>();
    private static bool running = false;

    public DamageTable(MainWindow mainWindow, StatsSummary summary)
    {
      InitializeComponent();
      TheMainWindow = mainWindow;
      title.Content = summary.ShortTitle;
    }

    public void ShowDamage(List<PlayerStats> selectedStats, CombinedStats currentStats)
    {
      if (selectedStats != null && currentStats != null)
      {
        CurrentStats = currentStats;
        Display(selectedStats);
      }
    }

    public void Display(List<PlayerStats> selectedStats = null)
    {
      if (running == false)
      {
        running = true;
        Dispatcher.InvokeAsync(() => TheMainWindow.Busy(true));

        Task.Delay(20).ContinueWith(task =>
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

    private void BuildGroups(PlayerStats playerStats, List<PlayerSubStats> all)
    {
      List<PlayerSubStats> list = new List<PlayerSubStats>();
      PlayerSubStats dots = new PlayerSubStats() { Name = Labels.DOT_TYPE };
      PlayerSubStats dds = new PlayerSubStats() { Name = Labels.DD_TYPE };
      PlayerSubStats procs = new PlayerSubStats() { Name = Labels.PROC_TYPE };
      List<PlayerSubStats> allDots = new List<PlayerSubStats>();
      List<PlayerSubStats> allDds = new List<PlayerSubStats>();
      List<PlayerSubStats> allProcs = new List<PlayerSubStats>();

      all.ForEach(sub =>
      {
        PlayerSubStats stats = null;

        switch(sub.Type)
        {
          case "DoT":
            stats = dots;
            allDots.Add(sub);
            break;
          case "DD":
            stats = dds;
            allDds.Add(sub);
            break;
          case "Proc":
            stats = procs;
            allProcs.Add(sub);
            break;
          default:
            list.Add(sub);
            break;
        }

        if (stats != null)
        {
          stats.TotalDamage += sub.TotalDamage;
          stats.TotalCritDamage += sub.TotalCritDamage;
          stats.TotalLuckyDamage += sub.TotalLuckyDamage;
          stats.Hits += sub.Hits;
          stats.Resists += sub.Resists;
          stats.CritHits += sub.CritHits;
          stats.LuckyHits += sub.LuckyHits;
          stats.TwincastHits += sub.TwincastHits;
          stats.Max = (sub.Max < stats.Max) ? stats.Max : sub.Max;
          stats.TotalSeconds = Math.Max(stats.TotalSeconds, sub.TotalSeconds);
        }
      });

      foreach(var stats in new PlayerSubStats[] { dots, dds, procs })
      {
        if (stats.Hits > 0)
        {
          stats.DPS = (long) Math.Round(stats.TotalDamage / stats.TotalSeconds, 2);
          stats.Avg = (long) Math.Round(Convert.ToDecimal(stats.TotalDamage) / stats.Hits, 2);

          if (stats.CritHits > 0)
          {
            stats.AvgCrit = (long) Math.Round(Convert.ToDecimal(stats.TotalCritDamage) / stats.CritHits, 2);
          }

          if (stats.LuckyHits > 0)
          {
            stats.AvgLucky = (long) Math.Round(Convert.ToDecimal(stats.TotalLuckyDamage) / stats.LuckyHits, 2);
          }

          stats.CritRate = Math.Round(Convert.ToDecimal(stats.CritHits) / stats.Hits * 100, 2);
          stats.LuckRate = Math.Round(Convert.ToDecimal(stats.LuckyHits) / stats.Hits * 100, 2);
          stats.TwincastRate = Math.Round(Convert.ToDecimal(stats.TwincastHits) / stats.Hits * 100, 2);
          stats.Percent = Math.Round(playerStats.Percent / 100 * ((decimal) stats.TotalDamage / playerStats.TotalDamage) * 100, 2);
          stats.ResistRate = Math.Round(Convert.ToDecimal(stats.Resists) / (stats.Hits + stats.Resists) * 100, 2);
        }
      }

      UnGroupedDD[playerStats.Name] = allDds;
      UnGroupedDoT[playerStats.Name] = allDots;
      UnGroupedProcs[playerStats.Name] = allProcs;
      GroupedDD[playerStats.Name] = dds;
      GroupedDoT[playerStats.Name] = dots;
      GroupedProcs[playerStats.Name] = procs;
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
        if (dds.TotalDamage > 0)
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
        if (dots.TotalDamage > 0)
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
        if (procs.TotalDamage > 0)
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

      return list;
    }

    private List<PlayerSubStats> SortSubStats(List<PlayerSubStats> subStats)
    {
      OrderedParallelQuery<PlayerSubStats> query;
      if (CurrentSortDirection == ListSortDirection.Ascending)
      {
        query = subStats.AsParallel().OrderBy(subStat => GetSortValue(subStat));
      }
      else
      {
        query = subStats.AsParallel().OrderByDescending(subStat => GetSortValue(subStat));
      }
      return query.ToList();
    }

    private object GetSortValue(PlayerSubStats sub)
    {
      return sub.GetType().GetProperty(CurrentSortKey).GetValue(sub, null);
    }

    private void Custom_Sorting(object sender, DataGridSortingEventArgs e)
    {
      var column = e.Column as DataGridTextColumn;
      if (column != null)
      {
        // prevent the built-in sort from sorting
        e.Handled = true;

        var binding = column.Binding as Binding;
        if (binding != null && binding.Path != null && binding.Path.Path != "PercentString") // dont sort on percent total, its not useful
        {
          CurrentSortKey = binding.Path.Path;
          CurrentColumn = column;

          if (column.Header.ToString() != "Name" && column.SortDirection == null)
          {
            CurrentSortDirection = ListSortDirection.Descending;
          }
          else
          {
            CurrentSortDirection = (column.SortDirection != ListSortDirection.Ascending) ? ListSortDirection.Ascending : ListSortDirection.Descending;
          }

          Display();
        }
      }
    }

    private void Loading_Row(object sender, DataGridRowEventArgs e)
    {
      if (e.Row.DataContext is PlayerStats)
      {
        e.Row.Style = Application.Current.FindResource(DataGridResourceKeys.DataGridRowStyleKey) as Style;
      }
      else
      {
        e.Row.Style = Application.Current.Resources["DetailsDataGridRowSyle"] as Style;
      }
    }

    private void OptionsChange(object sender, RoutedEventArgs e)
    {
      // check if call is during initialization
      if (PlayerStats != null)
      {
        CurrentGroupDDSetting = groupDirectDamage.IsChecked.Value;
        CurrentGroupDoTSetting = groupDoT.IsChecked.Value;
        CurrentGroupProcsSetting = groupProcs.IsChecked.Value;
        CurrentShowPets = showPets.IsChecked.Value;
        Display();
      }
    }
  }
}
