using Syncfusion.UI.Xaml.TreeGrid;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for DamageBreakdownTable.xaml
  /// </summary>
  public partial class DamageBreakdown
  {
    private PlayerStats _raidStats;
    private string _title;
    private bool _currentShowPets = true;
    private readonly Dictionary<string, PlayerSubStats> _groupedDd = [];
    private readonly Dictionary<string, PlayerSubStats> _groupedDoT = [];
    private readonly Dictionary<string, PlayerSubStats> _groupedProcs = [];
    private readonly Dictionary<string, List<PlayerSubStats>> _otherDamage = [];

    public DamageBreakdown()
    {
      InitializeComponent();
      dataGrid.IsEnabled = false;
      UiElementUtil.SetEnabled(controlPanel.Children, false);
      InitBreakdownTable(titleLabel, dataGrid, selectedColumns);
    }

    internal void Init(CombinedStats currentStats, List<PlayerStats> selectedStats)
    {
      _title = currentStats.ShortTitle;
      _raidStats = currentStats.RaidStats;
      var childStats = currentStats.Children;
      var list = new List<PlayerStats>();
      var pets = showPets.IsEnabled;

      Task.Delay(100).ContinueWith(_ =>
      {
        foreach (var stats in CollectionsMarshal.AsSpan(selectedStats))
        {
          if (!pets && !(PlayerManager.IsPossiblePlayerName(stats.Name) && !PlayerManager.Instance.IsVerifiedPet(stats.Name)))
          {
            pets = true;
          }

          if (childStats.TryGetValue(stats.Name, out var stat))
          {
            foreach (var childStat in CollectionsMarshal.AsSpan(stat))
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
          titleLabel.Content = _title;
          showPets.IsEnabled = pets;
          dataGrid.ItemsSource = list;
          dataGrid.IsEnabled = true;
          UiElementUtil.SetEnabled(controlPanel.Children, true);
        });
      });
    }

    private void BuildGroups(PlayerStats playerStats, List<PlayerSubStats> all)
    {
      var list = new List<PlayerSubStats>();
      var dots = new SubStatsBreakdown { Name = Labels.Dot, Type = Labels.Dot };
      var dds = new SubStatsBreakdown { Name = Labels.Dd, Type = Labels.Dd };
      var procs = new SubStatsBreakdown { Name = Labels.Proc, Type = Labels.Proc };

      foreach (var sub in CollectionsMarshal.AsSpan(all))
      {
        PlayerSubStats stats = null;

        switch (sub.Type)
        {
          case Labels.Dot:
            stats = dots;
            dots.Children.Add(sub);
            break;
          case Labels.Dd:
          case Labels.Bane:
            stats = dds;
            dds.Children.Add(sub);
            break;
          case Labels.Proc:
            stats = procs;
            procs.Children.Add(sub);
            break;
          default:
            list.Add(sub);
            break;
        }

        StatsUtil.MergeStats(stats, sub);
      }

      StatsUtil.CalculateRates(dots, _raidStats, playerStats);
      StatsUtil.CalculateRates(dds, _raidStats, playerStats);
      StatsUtil.CalculateRates(procs, _raidStats, playerStats);
      _groupedDd[playerStats.Name] = dds;
      _groupedDoT[playerStats.Name] = dots;
      _groupedProcs[playerStats.Name] = procs;
      _otherDamage[playerStats.Name] = list;
    }

    private IEnumerable<PlayerSubStats> GetSubStats(PlayerStats playerStats)
    {
      var name = playerStats.Name;
      var list = new List<PlayerSubStats>();
      _otherDamage[name].ForEach(list.Add);

      if (_groupedDd.TryGetValue(name, out var dds))
      {
        if (dds.Total > 0)
        {
          list.Add(dds);
        }
      }

      if (_groupedDoT.TryGetValue(name, out var dots))
      {
        if (dots.Total > 0)
        {
          list.Add(dots);
        }
      }

      if (_groupedProcs.TryGetValue(name, out var proc))
      {
        if (proc.Total > 0)
        {
          list.Add(proc);
        }
      }

      return list.OrderByDescending(stats => stats.Total);
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
            result = _currentShowPets || (PlayerManager.IsPossiblePlayerName(stats.Name) && !PlayerManager.Instance.IsVerifiedPet(stats.Name));
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
        _currentShowPets = showPets.IsChecked == true;
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
