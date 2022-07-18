using System;
using System.Collections.Generic;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for TankingBreakdown.xaml
  /// </summary>
  public partial class TankingBreakdown : BreakdownTable
  {
    public TankingBreakdown()
    {
      InitializeComponent();
      InitBreakdownTable(titleLabel, dataGrid, selectedColumns);
    }

    internal void Init(CombinedStats currentStats, List<PlayerStats> selectedStats)
    {
      titleLabel.Content = currentStats?.ShortTitle;
      dataGrid.ItemsSource = selectedStats;
    }
  }
}
