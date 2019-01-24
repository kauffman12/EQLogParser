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
    private bool CurrentGroupSpellsSetting = true;
    private bool CurrentShowPets = true;
    private Dictionary<string, List<PlayerSubStats>> WithGroupedSpells = new Dictionary<string, List<PlayerSubStats>>();
    private Dictionary<string, List<PlayerSubStats>> WithoutGroupedSpells = new Dictionary<string, List<PlayerSubStats>>();
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
                    var all = childStat.SubStats.Values.ToList();
                    WithoutGroupedSpells[childStat.Name] = all;
                    WithGroupedSpells[childStat.Name] = BuildWithGroupedSpells(childStat, all);
                  }
                }
                else
                {
                  PlayerStats.Add(playerStat);
                  var all = playerStat.SubStats.Values.ToList();
                  WithoutGroupedSpells[playerStat.Name] = all;
                  WithGroupedSpells[playerStat.Name] = BuildWithGroupedSpells(playerStat, all);
                }
              }
            }

            if (PlayerStats != null)
            {
              var filtered = CurrentShowPets ? PlayerStats : PlayerStats.Where(playerStats => DataManager.Instance.CheckNameForPlayer(playerStats.Name));
              foreach (var playerStat in SortSubStats(filtered.ToList()))
              {
                Dispatcher.InvokeAsync(() =>
                {
                  list.Add(playerStat);
                  var optionalList = CurrentGroupSpellsSetting ? WithGroupedSpells[playerStat.Name] : WithoutGroupedSpells[playerStat.Name];
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

    private List<PlayerSubStats> BuildWithGroupedSpells(PlayerStats playerStats, List<PlayerSubStats> all)
    {
      List<PlayerSubStats> list = new List<PlayerSubStats>();
      PlayerSubStats dots = new PlayerSubStats() { Name = Labels.DOT_TYPE };

      all.ForEach(sub =>
      {
        if (sub.Type != "DoT")
        {
          list.Add(sub);
        }
        else
        {
          dots.TotalDamage += sub.TotalDamage;
          dots.TotalCritDamage += sub.TotalCritDamage;
          dots.TotalLuckyDamage += sub.TotalLuckyDamage;
          dots.Hits += sub.Hits;
          dots.CritHits += sub.CritHits;
          dots.LuckyHits += sub.LuckyHits;
          dots.TwincastHits += sub.TwincastHits;
          dots.Max = (sub.Max < dots.Max) ? dots.Max : sub.Max;
          dots.TotalSeconds = Math.Max(dots.TotalSeconds, sub.TotalSeconds);
        }
      });

      if (dots.TotalDamage > 0)
      {
        dots.DPS = (long) Math.Round(dots.TotalDamage / dots.TotalSeconds);
        dots.Avg = (long) Math.Round(Convert.ToDecimal(dots.TotalDamage) / dots.Hits);
        dots.CritRate = Math.Round(Convert.ToDecimal(dots.CritHits) / dots.Hits * 100, 1);
        dots.LuckRate = Math.Round(Convert.ToDecimal(dots.LuckyHits) / dots.Hits * 100, 1);
        dots.TwincastRate = Math.Round(Convert.ToDecimal(dots.TwincastHits) / dots.Hits * 100, 1);
        dots.Percent = Math.Round(playerStats.Percent / 100 * ((decimal) dots.TotalDamage / playerStats.TotalDamage) * 100, 2);
        dots.PercentString = dots.Percent.ToString();
        list.Add(dots);
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
        CurrentGroupSpellsSetting = groupSpellDamage.IsChecked.Value;
        CurrentShowPets = showPets.IsChecked.Value;
        Display();
      }
    }
  }
}
