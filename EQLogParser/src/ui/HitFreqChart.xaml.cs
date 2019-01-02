using LiveCharts;
using LiveCharts.Wpf;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for HitFreqChart.xaml
  /// </summary>
  public partial class HitFreqChart : UserControl
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private const string CRIT_HITTYPE = "Critical";
    private const string NON_CRIT_HITTYPE = "Non-Critical";
    private Dictionary<string, List<HitFreqChartData>> ChartData = null;
    private List<string> MinFreqs = new List<string>() { "Any Freq", "Freq > 1", "Freq > 2", "Freq > 3", "Freq > 4" };
    private static bool Updating = false;
    private int PageSize = 24;
    private List<int> YValues;
    private List<long> XValues;

    internal HitFreqChart()
    {
      InitializeComponent();
      minFreqList.ItemsSource = MinFreqs;
      minFreqList.SelectedIndex = 0;
    }

    internal void Update(Dictionary<string, List<HitFreqChartData>> chartData)
    {
      ChartData = chartData;
      List<string> players = chartData.Keys.ToList();
      playerList.ItemsSource = players;
      playerList.SelectedIndex = 0; // triggers event
    }

    private void UserSelectionChanged()
    {
      if (!Updating)
      {
        Updating = true;

        Task.Delay(20).ContinueWith(task =>
        {
          Dispatcher.InvokeAsync(() =>
          {
            try
            {
              string player = playerList.SelectedItem as string;
              string type = hitTypeList.SelectedItem as string;
              string critType = critTypeList.SelectedItem as string;

              if (player != null && type != null && critType != null && player != "" && type != "" && critType != "")
              {
                var data = ChartData[player];
                int minFreq = GetMinFreq();
                HitFreqChartData first = data.Find(d => d.HitType == type);

                YValues = new List<int>();
                XValues = new List<long>();

                if (critType == CRIT_HITTYPE)
                {
                  for (int i = 0; i < first.CritYValues.Count; i++)
                  {
                    if (first.CritYValues[i] > minFreq)
                    {
                      YValues.Add(first.CritYValues[i]);
                      XValues.Add(first.CritXValues[i]);
                    }
                  }
                }
                else
                {
                  for (int i = 0; i < first.NonCritYValues.Count; i++)
                  {
                    if (first.NonCritYValues[i] > minFreq)
                    {
                      YValues.Add(first.NonCritYValues[i]);
                      XValues.Add(first.NonCritXValues[i]);
                    }
                  }
                }

                pageSlider.Value = 0;
                UpdatePageSize();
                DisplayPage();
              }

              Updating = false;
            }
            catch(Exception ex)
            {
              Updating = false;
              LOG.Error(ex);
            }
          });
        });
      }
    }

    private void UpdatePageSize()
    {
      if (YValues != null)
      {
        PageSize = (int)Math.Round(lvcChart.ActualWidth / 49);
        pageSlider.Minimum = 0;
        int max = YValues.Count <= PageSize ? 0 : YValues.Count - PageSize;
        // happens after resize
        if (pageSlider.Value > max)
        {
          pageSlider.Value = max;
        }
        pageSlider.Maximum = max;
        pageSlider.IsEnabled = pageSlider.Maximum > 0;
        if (pageSlider.IsEnabled)
        {
          pageSlider.Focus();
        }
      }
    }

    private void DisplayPage()
    {
      if (YValues != null)
      {
        int page = (int) pageSlider.Value;
        ChartValues<int> yChartValues = new ChartValues<int>();
        List<string> xChartValues = new List<string>();

        int maxY = 1;
        for (int i=page; i<page+PageSize && i<YValues.Count; i++)
        {
          maxY = Math.Max(maxY, YValues[i]);
          yChartValues.Add(YValues[i]);
          xChartValues.Add(XValues[i].ToString());
        }

        var series = new SeriesCollection();
        var firstSeries = new ColumnSeries();
        firstSeries.Values = yChartValues;
        firstSeries.DataLabels = true;
        firstSeries.LabelPoint = point => point.Y.ToString();
        firstSeries.FontSize = 14;
        firstSeries.FontWeight = FontWeights.Bold;
        firstSeries.Foreground = new SolidColorBrush(Colors.White);
        firstSeries.MaxColumnWidth = 15;
        firstSeries.ColumnPadding = 8;
        series.Add(firstSeries);

        lvcChart.DataTooltip = null;
        lvcChart.AxisX[0].Separator.StrokeThickness = 0;
        lvcChart.AxisX[0].Labels = xChartValues;
        lvcChart.AxisY[0].Labels = null;
        lvcChart.AxisY[0].Separator.Step = (maxY <= 6) ? 2 : double.NaN;
        lvcChart.Series = series;
        Helpers.ChartResetView(lvcChart);
      }
    }

    private int GetMinFreq()
    {
      int result = 1;
      string selected = minFreqList.SelectedItem as string;
      switch (selected)
      {
        case "Any Freq":
          result = 0;
          break;
        case "Freq > 1":
          result = 1;
          break;
        case "Freq > 2":
          result = 2;
          break;
        case "Freq > 3":
          result = 3;
          break;
        case "Freq > 4":
          result = 4;
          break;
      }
      return result;
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      UserSelectionChanged();
    }

    private void CritTypeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      string selectedCritType = critTypeList.SelectedItem as string;
      if (selectedCritType != null)
      {
        UpdateSelectedHitTypes(selectedCritType == NON_CRIT_HITTYPE);
        UserSelectionChanged();
      }
    }

    private void Chart_DoubleClick(object sender, MouseButtonEventArgs e)
    {
      Helpers.ChartResetView(lvcChart);
    }

    private void PlayerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      try
      {
        string selectedCritType = critTypeList.SelectedItem as string;
        string player = playerList.SelectedItem as string;
        var data = ChartData[player];

        bool useNonCrit = true;
        List<string> playerCritTypes = new List<string>();
        if (data.Any(d => d.CritYValues.Count > 0))
        {
          playerCritTypes.Add(CRIT_HITTYPE);
          if (selectedCritType != null && selectedCritType == CRIT_HITTYPE)
          {
            useNonCrit = false;
          }
        }

        if (data.Any(d => d.NonCritYValues.Count > 0))
        {
          playerCritTypes.Add(NON_CRIT_HITTYPE);
        }

        critTypeList.ItemsSource = playerCritTypes;
        critTypeList.SelectedItem = useNonCrit ? NON_CRIT_HITTYPE : CRIT_HITTYPE;
        UpdateSelectedHitTypes(useNonCrit);
        UserSelectionChanged();
      }
      catch (Exception ex)
      {
        LOG.Error(ex);
      }
    }

    private void UpdateSelectedHitTypes(bool useNonCrit)
    {
      try
      {
        string player = playerList.SelectedItem as string;
        if (player != null && player != "")
        {
          var data = ChartData[player];
          string selectedHitType = hitTypeList.SelectedItem as string;
          List<string> hitTypes;

          if (useNonCrit)
          {
            hitTypes = data.Where(d => d.NonCritYValues.Count > 0).Select(d => d.HitType).OrderBy(hitType => hitType).ToList();
          }
          else
          {
            hitTypes = data.Where(d => d.CritYValues.Count > 0).Select(d => d.HitType).OrderBy(hitType => hitType).ToList();
          }

          hitTypeList.ItemsSource = hitTypes;
          hitTypeList.SelectedItem = (selectedHitType != null && hitTypes.Contains(selectedHitType)) ? selectedHitType : hitTypes[0];
        }
      }
      catch (Exception ex)
      {
        LOG.Error(ex);
      }
    }

    private void PageSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
      DisplayPage();
    }

    private void Chart_SizeChanged(object sender, SizeChangedEventArgs e)
    {
      UpdatePageSize();
      DisplayPage();
    }
  }
}
