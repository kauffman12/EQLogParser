using ActiproSoftware.Windows.Themes;
using LiveCharts;
using LiveCharts.Wpf;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for DPSChart.xaml
  /// </summary>
  public partial class DPSChart : UserControl
  {
    public DPSChart()
    {
      InitializeComponent();

      // reverse regular tooltip
      lvcChart.DataTooltip.Foreground = (SolidColorBrush) Application.Current.FindResource(AssetResourceKeys.ToolTipBackgroundNormalBrushKey);
      lvcChart.DataTooltip.Background = (SolidColorBrush) Application.Current.FindResource(AssetResourceKeys.ToolTipForegroundNormalBrushKey);
      lvcChart.ChartLegend.Foreground = (SolidColorBrush) Application.Current.FindResource(AssetResourceKeys.ToolTipBackgroundNormalBrushKey);
      lvcChart.ChartLegend.Background = (SolidColorBrush) Application.Current.FindResource(AssetResourceKeys.ToolTipForegroundNormalBrushKey);

      Reset();
    }

    public void Reset()
    {

      LineSeries series = new LineSeries();
      series.Values = new ChartValues<long>() { 0 };
      SeriesCollection collection = new SeriesCollection();
      collection.Add(series);
      lvcChart.AxisX[0].Labels = new List<string>() { "Jan 01 12:00:00", "15", "30" };
      lvcChart.AxisY[0].Labels = new List<string>() { "0", "100000", "200000", "300000" };
      lvcChart.AxisY[0].MaxValue = 300000;
      lvcChart.Series = collection;
    }

    public void Update(ChartData chartData)
    {
      var series = Helpers.CreateLineChartSeries(chartData.Values);
      Helpers.ChartResetView(lvcChart);
      lvcChart.AxisX[0].Labels = chartData.XAxisLabels;
      lvcChart.AxisY[0].Labels = null;
      lvcChart.AxisY[0].MaxValue = double.NaN;
      lvcChart.Series = series;
    }

    private void Chart_DoubleClick(object sender, MouseButtonEventArgs e)
    {
      Helpers.ChartResetView(lvcChart);
    }
  }
}
