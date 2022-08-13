using Syncfusion.UI.Xaml.TreeGrid;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for DamageBreakdownTable.xaml
  /// </summary>
  public partial class DamageBreakdown : BreakdownTable
  {
    private List<PlayerStats> PlayerStats = new List<PlayerStats>();
    private PlayerStats RaidStats;
    private Dictionary<string, List<PlayerStats>> ChildStats;
    private bool CurrentGroupDDSetting = true;
    private bool CurrentGroupDoTSetting = true;
    private bool CurrentGroupProcsSetting = true;
    private bool CurrentShowPets = true;
    private readonly Dictionary<string, PlayerSubStats> GroupedDD = new Dictionary<string, PlayerSubStats>();
    private readonly Dictionary<string, PlayerSubStats> GroupedDoT = new Dictionary<string, PlayerSubStats>();
    private readonly Dictionary<string, PlayerSubStats> GroupedProcs = new Dictionary<string, PlayerSubStats>();
    private readonly Dictionary<string, List<PlayerSubStats>> UnGroupedDD = new Dictionary<string, List<PlayerSubStats>>();
    private readonly Dictionary<string, List<PlayerSubStats>> UnGroupedDoT = new Dictionary<string, List<PlayerSubStats>>();
    private readonly Dictionary<string, List<PlayerSubStats>> UnGroupedProcs = new Dictionary<string, List<PlayerSubStats>>();
    private readonly Dictionary<string, List<PlayerSubStats>> OtherDamage = new Dictionary<string, List<PlayerSubStats>>();

    public DamageBreakdown()
    {
      InitializeComponent();
      InitBreakdownTable(titleLabel, dataGrid, selectedColumns);
    }

    internal void Init(CombinedStats currentStats, List<PlayerStats> selectedStats)
    {
      titleLabel.Content = currentStats?.ShortTitle;
      RaidStats = currentStats.RaidStats;
      ChildStats = currentStats.Children;

      foreach (ref var stats in selectedStats.ToArray().AsSpan())
      {
        if (!showPets.IsEnabled && !(PlayerManager.IsPossiblePlayerName(stats.Name) && !PlayerManager.Instance.IsVerifiedPet(stats.Name)))
        {
          showPets.IsEnabled = true;
        }

        if (ChildStats.ContainsKey(stats.Name))
        {
          foreach (ref var childStat in ChildStats[stats.Name].ToArray().AsSpan())
          {
            // Damage Summary is a Tree which can have child and parent selected so check that we haven't
            // already added the entry
            if (!PlayerStats.Contains(childStat))
            {
              PlayerStats.Add(childStat);
              BuildGroups(childStat, childStat.SubStats);
            }
          }
        }
        else if (!PlayerStats.Contains(stats))
        {
          PlayerStats.Add(stats);
          BuildGroups(stats, stats.SubStats);
        }
      }

      Display();
    }

    internal void Display()
    {
      dataGrid.ItemsSource = null;
      dataGrid.ItemsSource = PlayerStats;
    }

    private void BuildGroups(PlayerStats playerStats, List<PlayerSubStats> all)
    {
      var list = new List<PlayerSubStats>();
      var dots = new PlayerSubStats() { Name = Labels.DOT, Type = Labels.DOT };
      var dds = new PlayerSubStats() { Name = Labels.DD, Type = Labels.DD };
      var procs = new PlayerSubStats() { Name = Labels.PROC, Type = Labels.PROC };
      var allDots = new List<PlayerSubStats>();
      var allDds = new List<PlayerSubStats>();
      var allProcs = new List<PlayerSubStats>();

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

      UnGroupedDD[playerStats.Name] = allDds;
      UnGroupedDoT[playerStats.Name] = allDots;
      UnGroupedProcs[playerStats.Name] = allProcs;
      GroupedDD[playerStats.Name] = dds;
      GroupedDoT[playerStats.Name] = dots;
      GroupedProcs[playerStats.Name] = procs;
      OtherDamage[playerStats.Name] = list;

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

    private void ItemsSourceChanged(object sender, TreeGridItemsSourceChangedEventArgs e)
    {
      if (dataGrid.View != null)
      {
        dataGrid.View.Filter = (value) =>
        {
          var result = true;
          if (value is PlayerStats stats)
          {
            result = CurrentShowPets || (PlayerManager.IsPossiblePlayerName(stats.Name) && !PlayerManager.Instance.IsVerifiedPet(stats.Name));
          }
          return result;
        };

        dataGrid.SelectedItems.Clear();
        dataGrid.View.RefreshFilter();
      }
    }

    private void OptionsChange(object sender, RoutedEventArgs e)
    {
      // check if call is during initialization
      if (dataGrid?.View != null)
      {
        CurrentGroupDDSetting = groupDirectDamage.IsChecked.Value;
        CurrentGroupDoTSetting = groupDoT.IsChecked.Value;
        CurrentGroupProcsSetting = groupProcs.IsChecked.Value;
        CurrentShowPets = showPets.IsChecked.Value;

        if (sender == showPets)
        {
          dataGrid.View.RefreshFilter();
          dataGrid.SelectedItems.Clear();
        }
        else
        {
          Dispatcher.InvokeAsync(() => Display(), DispatcherPriority.Background);
        }
      }
    }

    private void RequestTreeItems(object sender, TreeGridRequestTreeItemsEventArgs e)
    {
      if (dataGrid.ItemsSource is List<PlayerStats>)
      {
        if (e.ParentItem == null)
        {
          e.ChildItems = dataGrid.ItemsSource as List<PlayerStats>;
        }
        else if (e.ParentItem is PlayerStats stats)
        {
          e.ChildItems = GetSubStats(stats);
        }
      }
    }
  }
}
