using LiveCharts;
using LiveCharts.Wpf;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
    private readonly List<string> MinFreqs = new List<string>() { "Any Freq", "Freq > 1", "Freq > 2", "Freq > 3", "Freq > 4" };
    private static bool Updating = false;
    private int PageSize = 24;
    private List<int> YValues;
    private List<long> XValues;
    private List<long> XValuesDiff;
    private Dictionary<long, DIValue> DIMap;

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

    private void CopyCsvClick(object sender, RoutedEventArgs e)
    {
      try
      {
        var header = new List<string> { "Hit Value", "Frequency", "Difference" };

        List<List<object>> data = new List<List<object>>();
        for (int i = 0; i < YValues.Count; i++)
        {
          data.Add(new List<object> { XValues[i], YValues[i], XValuesDiff[i] });
        }

        Clipboard.SetDataObject(TextFormatUtils.BuildCsv(header, data));
      }
      catch (ExternalException ex)
      {
        LOG.Error(ex);
      }
    }

    private void CreateImageClick(object sender, RoutedEventArgs e) => Helpers.CopyImage(Dispatcher, lvcChart);

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
              if (playerList.SelectedItem is string player && hitTypeList.SelectedItem is string type && critTypeList.SelectedItem is string critType && player.Length > 0 && type.Length > 0 && critType.Length > 0)
              {
                DIMap = null;
                var data = ChartData[player];
                int minFreq = GetMinFreq();
                HitFreqChartData first = data.Find(d => d.HitType == type);

                YValues = new List<int>();
                XValues = new List<long>();
                XValuesDiff = new List<long>();

                if (critType == CRIT_HITTYPE)
                {
                  for (int i = 0; i < first.CritYValues.Count; i++)
                  {
                    if (first.CritYValues[i] > minFreq)
                    {
                      YValues.Add(first.CritYValues[i]);
                      XValues.Add(first.CritXValues[i]);
                      XValuesDiff.Add(i > 0 ? Math.Abs(first.CritXValues[i] - first.CritXValues[i - 1]) : 0);
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
                      XValuesDiff.Add(i > 0 ? Math.Abs(first.NonCritXValues[i] - first.NonCritXValues[i - 1]) : 0);
                    }
                  }
                }

                pageSlider.Value = 0;
                UpdatePageSize();
                DisplayPage();
              }

              Updating = false;
            }
            catch (ArgumentNullException ex)
            {
              Updating = false;
              LOG.Error(ex);
            }
            catch (InvalidOperationException ioe)
            {
              Updating = false;
              LOG.Error(ioe);
            }
          });
        }, TaskScheduler.Default);
      }
    }

    private void LookForDIValues()
    {
      DictionaryAddHelper<long, int> addHelper = new DictionaryAddHelper<long, int>();
      Dictionary<long, int> counts = new Dictionary<long, int>();
      List<long> unique = XValuesDiff.ToList();
      while (unique.Count > 0)
      {
        long value = unique[0];
        unique.RemoveAt(0);
        unique.ForEach(damage =>
        {
          if (Math.Abs(value - damage) <= 5 && damage > 20)
          {
            addHelper.Add(counts, value, 1);
          }
        });
      }

      int max = -1;
      long foundKey = -1;
      foreach (long key in counts.Keys)
      {
        if (counts[key] > max)
        {
          max = counts[key];
          foundKey = key;
        }
      }

      if (foundKey > -1)
      {
        int diCount = 0;
        DIMap = new Dictionary<long, DIValue>();
        for (int i = 0; i < XValuesDiff.Count; i++)
        {
          if (Math.Abs(XValuesDiff[i] - foundKey) <= 4)
          {
            diCount++;
            if (diCount == 1)
            {
              DIMap[XValues[i]] = new DIValue() { DI = diCount };
            }
            else
            {
              DIMap[XValues[i]] = new DIValue() { DI = diCount, Diff = XValuesDiff[i] };
            }
          }
        };

        if (DIMap.Count > 5)
        {
        }
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
        int page = (int)pageSlider.Value;
        ChartValues<int> yChartValues = new ChartValues<int>();
        List<string> xChartValues = new List<string>();

        int maxY = 1;
        for (int i = page; i < page + PageSize && i < YValues.Count; i++)
        {
          maxY = Math.Max(maxY, YValues[i]);
          yChartValues.Add(YValues[i]);
          xChartValues.Add(XValues[i].ToString(CultureInfo.CurrentCulture) + (XValuesDiff[i] == 0 ? "" : " \n+" + XValuesDiff[i].ToString(CultureInfo.CurrentCulture)));
        }

        var series = new SeriesCollection();
        var firstSeries = new ColumnSeries
        {
          Values = yChartValues,
          DataLabels = true,
          LabelPoint = point => point.Y.ToString(CultureInfo.CurrentCulture),
          FontSize = 14,
          FontWeight = FontWeights.Bold,
          Foreground = new SolidColorBrush(Colors.White),
          MaxColumnWidth = 15,
          ColumnPadding = 8,
          ScalesXAt = 0
        };

        series.Add(firstSeries);

        lvcChart.DataTooltip = null;
        lvcChart.AxisX[0].Separator.StrokeThickness = 0;
        lvcChart.AxisX[0].Labels = xChartValues;
        lvcChart.AxisY[0].Labels = null;
        lvcChart.AxisY[0].Separator.Step = (maxY <= 10) ? 2 : double.NaN;
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

    private void ListSelectionChanged(object sender, SelectionChangedEventArgs e) => UserSelectionChanged();

    private void CritTypeListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (critTypeList.SelectedItem is string selectedCritType)
      {
        UpdateSelectedHitTypes(selectedCritType == NON_CRIT_HITTYPE);
        UserSelectionChanged();
      }
    }

    private void PlayerListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      string player = playerList?.SelectedItem as string;

      if (ChartData.ContainsKey(player))
      {
        var data = ChartData[player];

        bool canUseCrit = false;
        List<string> playerCritTypes = new List<string>();
        if (data.Any(d => d.CritYValues.Count > 0))
        {
          playerCritTypes.Add(CRIT_HITTYPE);
          canUseCrit = true;
        }

        if (data.Any(d => d.NonCritYValues.Count > 0))
        {
          playerCritTypes.Add(NON_CRIT_HITTYPE);
        }

        critTypeList.ItemsSource = playerCritTypes;
        critTypeList.SelectedIndex = playerCritTypes.Count == 1 ? 0 : canUseCrit ? 1 : 0;
        UpdateSelectedHitTypes(critTypeList?.SelectedItem as string == NON_CRIT_HITTYPE);
        UserSelectionChanged();
      }
    }

    private void UpdateSelectedHitTypes(bool useNonCrit)
    {
      string player = playerList?.SelectedItem as string;
      if (!string.IsNullOrEmpty(player) && ChartData.ContainsKey(player))
      {
        var data = ChartData[player];
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
        if (hitTypeList?.SelectedItem is string selectedHitType && hitTypes.Contains(selectedHitType))
        {
          hitTypeList.SelectedItem = selectedHitType;
        }
        else if (hitTypes.Count > 0)
        {
          hitTypeList.SelectedItem = hitTypes.First();
        }
      }
    }

    private void PageSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
      DisplayPage();
    }

    private void ChartSizeChanged(object sender, SizeChangedEventArgs e)
    {
      UpdatePageSize();
      DisplayPage();
    }

    private class DIValue
    {
      public int DI { get; set; }
      public long Diff { get; set; }
    }
  }
}
