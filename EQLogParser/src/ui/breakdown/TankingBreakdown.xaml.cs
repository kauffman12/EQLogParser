using Syncfusion.UI.Xaml.TreeGrid;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for TankingBreakdown.xaml
  /// </summary>
  public partial class TankingBreakdown
  {
    private DamageBreakdownGroups _groups;

    public TankingBreakdown()
    {
      InitializeComponent();
      SyncFusionUtil.ConfigureFrozenCellAnimations(dataGrid);
      dataGrid.IsEnabled = false;
      UiElementUtil.SetEnabled(controlPanel.Children, false);
      InitBreakdownTable(titleLabel, dataGrid, selectedColumns);
    }

    internal void Init(CombinedStats currentStats, List<PlayerStats> selectedStats)
    {
      if (currentStats is null) return;

      Task.Delay(100).ContinueWith(_ =>
      {
        var groups = new DamageBreakdownGroups(currentStats.RaidStats, selectedStats);
        Dispatcher.InvokeAsync(() =>
        {
          _groups = groups;
          titleLabel.Content = currentStats.ShortTitle;
          dataGrid.ItemsSource = groups.Players;
          dataGrid.IsEnabled = true;
          UiElementUtil.SetEnabled(controlPanel.Children, true);
        });
      });
    }

    private void RequestTreeItems(object sender, TreeGridRequestTreeItemsEventArgs e)
    {
      e.ChildItems = _groups?.ChildrenOf(e.ParentItem) ?? [];
    }
  }
}
