using System.Collections.Generic;
using System.Threading.Tasks;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for TankingBreakdown.xaml
  /// </summary>
  public partial class TankingBreakdown
  {
    public TankingBreakdown()
    {
      InitializeComponent();
      dataGrid.IsEnabled = false;
      UIElementUtil.SetEnabled(controlPanel.Children, false);
      InitBreakdownTable(titleLabel, dataGrid, selectedColumns);
    }

    internal void Init(CombinedStats currentStats, List<PlayerStats> selectedStats)
    {
      Task.Delay(100).ContinueWith(_ =>
      {
        Dispatcher.InvokeAsync(() =>
        {
          titleLabel.Content = currentStats?.ShortTitle;
          dataGrid.ItemsSource = selectedStats;
          dataGrid.IsEnabled = true;
          UIElementUtil.SetEnabled(controlPanel.Children, true);
        });
      });
    }
  }
}
