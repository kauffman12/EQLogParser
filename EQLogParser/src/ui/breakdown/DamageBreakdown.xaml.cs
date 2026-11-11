using Syncfusion.UI.Xaml.TreeGrid;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for DamageBreakdown.xaml
  /// </summary>
  public partial class DamageBreakdown
  {
    private bool _currentShowPets = true;
    private DamageBreakdownGroups _groups;

    public DamageBreakdown()
    {
      InitializeComponent();
      SyncFusionUtil.ConfigureFrozenCellAnimations(dataGrid);
      dataGrid.IsEnabled = false;
      UiElementUtil.SetEnabled(controlPanel.Children, false);
      InitBreakdownTable(titleLabel, dataGrid, selectedColumns);
    }

    internal void Init(CombinedStats currentStats, List<PlayerStats> selectedStats)
    {
      var title = currentStats.ShortTitle;
      var raidStats = currentStats.RaidStats;
      var childStats = currentStats.Children;
      var list = new List<PlayerStats>();
      var pets = showPets.IsEnabled;

      Task.Delay(100).ContinueWith(_ =>
      {
        foreach (var stats in CollectionsMarshal.AsSpan(selectedStats))
        {
          if (!pets && !(PlayerRegistry.IsPossiblePlayerName(stats.Name) && !PlayerRegistry.Instance.IsVerifiedPet(stats.Name)))
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
              }
            }
          }
          else if (!list.Contains(stats))
          {
            list.Add(stats);
          }
        }

        var groups = new DamageBreakdownGroups(raidStats, list);
        Dispatcher.InvokeAsync(() =>
        {
          _groups = groups;
          titleLabel.Content = title;
          dataGrid.ItemsSource = groups.Players;
          dataGrid.IsEnabled = true;
          UiElementUtil.SetEnabled(controlPanel.Children, true);
          showPets.IsEnabled = pets;
        });
      });
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
            result = _currentShowPets || (PlayerRegistry.IsPossiblePlayerName(stats.Name) && !PlayerRegistry.Instance.IsVerifiedPet(stats.Name));
          }
          return result;
        };

        dataGrid.View.RefreshFilter();
      }
    }

    private void OptionsChanged(object sender, RoutedEventArgs e)
    {
      // check if call is during initialization
      if (dataGrid?.View != null && _currentShowPets != showPets.IsChecked == true)
      {
        _currentShowPets = showPets.IsChecked == true;
        dataGrid.View.RefreshFilter();
      }
    }

    private void RequestTreeItems(object sender, TreeGridRequestTreeItemsEventArgs e)
    {
      e.ChildItems = _groups?.ChildrenOf(e.ParentItem) ?? [];
    }
  }
}
