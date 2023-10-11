using Syncfusion.UI.Xaml.TreeGrid;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for DamageBreakdownTable.xaml
  /// </summary>
  public partial class DamageBreakdown
  {
    private PlayerStats RaidStats;
    private string Title;
    private bool CurrentShowPets = true;
    private readonly Dictionary<string, PlayerSubStats> GroupedDd = new();
    private readonly Dictionary<string, PlayerSubStats> GroupedDoT = new();
    private readonly Dictionary<string, PlayerSubStats> GroupedProcs = new();
    private readonly Dictionary<string, List<PlayerSubStats>> OtherDamage = new();

    public DamageBreakdown()
    {
      InitializeComponent();
      dataGrid.IsEnabled = false;
      UIElementUtil.SetEnabled(controlPanel.Children, false);
      InitBreakdownTable(titleLabel, dataGrid, selectedColumns);
    }

    internal void Init(CombinedStats currentStats, List<PlayerStats> selectedStats)
    {
      Title = currentStats?.ShortTitle;
      RaidStats = currentStats.RaidStats;
      var childStats = currentStats.Children;
      var list = new List<PlayerStats>();
      var pets = showPets.IsEnabled;

      Task.Delay(100).ContinueWith(task =>
      {
        foreach (ref var stats in selectedStats.ToArray().AsSpan())
        {
          if (!pets && !(PlayerManager.IsPossiblePlayerName(stats.Name) && !PlayerManager.Instance.IsVerifiedPet(stats.Name)))
          {
            pets = true;
          }

          if (childStats.TryGetValue(stats.Name, out var stat))
          {
            foreach (ref var childStat in stat.ToArray().AsSpan())
            {
              // Damage Summary is a Tree which can have child and parent selected so check that we haven't
              // already added the entry
              if (!list.Contains(childStat))
              {
                list.Add(childStat);
                BuildGroups(childStat, childStat.SubStats);
              }
            }
          }
          else if (!list.Contains(stats))
          {
            list.Add(stats);
            BuildGroups(stats, stats.SubStats);
          }
        }

        Dispatcher.InvokeAsync(() =>
        {
          titleLabel.Content = Title;
          showPets.IsEnabled = pets;
          dataGrid.ItemsSource = list;
          dataGrid.IsEnabled = true;
          UIElementUtil.SetEnabled(controlPanel.Children, true);
        });
      });
    }

    private void BuildGroups(PlayerStats playerStats, List<PlayerSubStats> all)
    {
      var list = new List<PlayerSubStats>();
      var dots = new SubStatsBreakdown { Name = Labels.DOT, Type = Labels.DOT };
      var dds = new SubStatsBreakdown { Name = Labels.DD, Type = Labels.DD };
      var procs = new SubStatsBreakdown { Name = Labels.PROC, Type = Labels.PROC };

      all.ForEach(sub =>
      {
        PlayerSubStats stats = null;

        switch (sub.Type)
        {
          case Labels.DOT:
            stats = dots;
            dots.Children.Add(sub);
            break;
          case Labels.DD:
          case Labels.BANE:
            stats = dds;
            dds.Children.Add(sub);
            break;
          case Labels.PROC:
            stats = procs;
            procs.Children.Add(sub);
            break;
          default:
            list.Add(sub);
            break;
        }

        StatsUtil.MergeStats(stats, sub);
      });

      foreach (var stats in new PlayerSubStats[] { dots, dds, procs })
      {
        StatsUtil.CalculateRates(stats, RaidStats, playerStats);
      }

      GroupedDd[playerStats.Name] = dds;
      GroupedDoT[playerStats.Name] = dots;
      GroupedProcs[playerStats.Name] = procs;
      OtherDamage[playerStats.Name] = list;
    }

    private List<PlayerSubStats> GetSubStats(PlayerStats playerStats)
    {
      var name = playerStats.Name;
      var list = new List<PlayerSubStats>();

      OtherDamage[name].ForEach(stats => list.Add(stats));

      if (GroupedDd.TryGetValue(name, out var dds))
      {
        if (dds.Total > 0)
        {
          list.Add(dds);
        }
      }

      if (GroupedDoT.TryGetValue(name, out var dots))
      {
        if (dots.Total > 0)
        {
          list.Add(dots);
        }
      }

      if (GroupedProcs.TryGetValue(name, out var proc))
      {
        if (proc.Total > 0)
        {
          list.Add(proc);
        }
      }

      return list.OrderByDescending(stats => stats.Total).ToList();
    }

    private void ItemsSourceChanged(object sender, TreeGridItemsSourceChangedEventArgs e)
    {
      if (dataGrid.View != null)
      {
        dataGrid.View.Filter = value =>
        {
          var result = true;
          if (value is PlayerStats stats)
          {
            result = CurrentShowPets || (PlayerManager.IsPossiblePlayerName(stats.Name) && !PlayerManager.Instance.IsVerifiedPet(stats.Name));
          }
          return result;
        };

        dataGrid.View.RefreshFilter();
      }
    }

    private void OptionsChange(object sender, RoutedEventArgs e)
    {
      // check if call is during initialization
      if (dataGrid?.View != null)
      {
        CurrentShowPets = showPets.IsChecked.Value;
        dataGrid.View.RefreshFilter();
      }
    }

    private void RequestTreeItems(object sender, TreeGridRequestTreeItemsEventArgs e)
    {
      if (dataGrid.ItemsSource is List<PlayerStats> list)
      {
        if (e.ParentItem == null)
        {
          e.ChildItems = list;
        }
        else if (e.ParentItem is PlayerStats stats)
        {
          e.ChildItems = GetSubStats(stats);
        }
        else if (e.ParentItem is SubStatsBreakdown breakdown)
        {
          e.ChildItems = breakdown.Children;
        }
        else
        {
          e.ChildItems = new List<PlayerStats>();
        }
      }
    }
  }
}
