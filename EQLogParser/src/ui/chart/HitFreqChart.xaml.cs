using Syncfusion.UI.Xaml.Charts;
using System;
using System.Collections.Generic;
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
    private Dictionary<string, List<HitFreqChartData>> PlayerData = null;
    private readonly List<string> MinFreqs = new List<string>() { "Any Freq", "Freq > 1", "Freq > 2", "Freq > 3", "Freq > 4" };
    private int PageSize = 9;
    private static bool Updating = false;
    private List<ColumnData> Columns = new List<ColumnData>();

    public HitFreqChart()
    {
      InitializeComponent();
      minFreqList.ItemsSource = MinFreqs;
      minFreqList.SelectedIndex = 0;
    }

    internal void Update(PlayerStats playerStats, CombinedStats combined)
    {
      PlayerData = GetHitFreqValues(playerStats, combined);
      List<string> players = PlayerData.Keys.ToList();
      playerList.ItemsSource = players;
      playerList.SelectedIndex = 0; // triggers event
    }

    private void CopyCsvClick(object sender, RoutedEventArgs e)
    {
      try
      {
        var header = new List<string> { "Hit Value", "Frequency", "Difference" };

        List<List<object>> data = new List<List<object>>();
        foreach (var column in Columns.ToList())
        {
          data.Add(new List<object> { column.XLongValue, column.Y, column.Diff });
        }

        Clipboard.SetDataObject(TextFormatUtils.BuildCsv(header, data));
      }
      catch (ExternalException ex)
      {
        LOG.Error(ex);
      }
    }

    private void CreateImageClick(object sender, RoutedEventArgs e) => Helpers.CreateImage(Dispatcher, sfChart);

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
              if (playerList.SelectedItem is string player && hitTypeList.SelectedItem is string type &&
              critTypeList.SelectedItem is string critType && player.Length > 0 && type.Length > 0 && critType.Length > 0)
              {
                var data = PlayerData[player];
                int minFreq = GetMinFreq();
                HitFreqChartData first = data.Find(d => d.HitType == type);
                Columns.Clear();

                if (critType == CRIT_HITTYPE)
                {
                  for (int i = 0; i < first.CritYValues.Count; i++)
                  {
                    if (first.CritYValues[i] > minFreq)
                    {
                      var diff = (i > 0) ? (first.CritXValues[i] - first.CritXValues[i - 1]) : 0;
                      var diffString = (diff == 0) ? "" : "+" + diff;
                      Columns.Add(new ColumnData
                      {
                        Diff = diff,
                        Y = first.CritYValues[i],
                        XLongValue = first.CritXValues[i],
                        X = first.CritXValues[i] + "\n" + diffString
                      });
                    }
                  }
                }
                else
                {
                  for (int i = 0; i < first.NonCritYValues.Count; i++)
                  {
                    if (first.NonCritYValues[i] > minFreq)
                    {
                      var diff = (i > 0) ? (first.NonCritXValues[i] - first.NonCritXValues[i - 1]) : 0;
                      var diffString = (diff == 0) ? "" : "+" + diff;
                      Columns.Add(new ColumnData
                      {
                        Diff = diff,
                        Y = first.NonCritYValues[i],
                        XLongValue = first.NonCritXValues[i],
                        X = first.NonCritXValues[i] + "\n" + diffString
                      });
                    }
                  }
                }
              }

              pageSlider.Value = 0;
              UpdatePageSize();
              DisplayPage();
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

    private void DisplayPage()
    {
      if (Columns.Count > 0)
      {
        int page = (int)pageSlider.Value;
        List<ColumnData> onePage = new List<ColumnData>();
        for (int i = page; i < page + PageSize && i < Columns.Count; i++)
        {
          onePage.Add(Columns[i]);
        }

        var collection = new ChartSeriesCollection();
        var series = new FastColumnBitmapSeries { XBindingPath = "X", YBindingPath = "Y", ItemsSource = onePage };
        var adornment = new ChartAdornmentInfo
        {
          ShowLabel = true,
          ShowMarker = false,
          LabelPosition = AdornmentsLabelPosition.Outer,
          FontSize = 20,
          Foreground = Application.Current.Resources["ContentForeground"] as SolidColorBrush,
          Background = new SolidColorBrush(Colors.Transparent)
        };
        series.AdornmentsInfo = adornment;
        ChartSeriesBase.SetSpacing(series, 0.5);
        collection.Add(series);
        sfChart.Series = collection;
      }
    }

    private void UpdatePageSize()
    {
      if (Columns.Count > 0)
      {
        PageSize = (int)Math.Round(sfChart.ActualWidth / 60);
        pageSlider.Minimum = 0;
        int max = Columns.Count <= PageSize ? 0 : Columns.Count - PageSize;
        // happens after resize
        if (pageSlider.Value > max)
        {
          pageSlider.Value = max;
        }
        pageSlider.Maximum = max;
        pageSlider.IsEnabled = (pageSlider.Maximum > 0);
        if (pageSlider.IsEnabled)
        {
          pageSlider.Focus();
        }
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

      if (PlayerData.ContainsKey(player))
      {
        var data = PlayerData[player];

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
      if (!string.IsNullOrEmpty(player) && PlayerData.ContainsKey(player))
      {
        var data = PlayerData[player];
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

    private void sfChart_SizeChanged(object sender, SizeChangedEventArgs e)
    {
      UpdatePageSize();
      DisplayPage();
    }

    private void sfChart_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
      if (e.Delta < 0 && pageSlider.Value < pageSlider.Maximum)
      {
        pageSlider.Value++;
      }
      else if (e.Delta > 0 && pageSlider.Value > 0)
      {
        pageSlider.Value--;
      }
    }

    private static Dictionary<string, List<HitFreqChartData>> GetHitFreqValues(PlayerStats selected, CombinedStats damageStats)
    {
      Dictionary<string, List<HitFreqChartData>> results = new Dictionary<string, List<HitFreqChartData>>();

      // get chart data for player and pets if available
      if (damageStats?.Children.ContainsKey(selected.Name) == true)
      {
        damageStats?.Children[selected.Name].ForEach(stats => AddStats(stats));
      }
      else
      {
        AddStats(selected);
      }

      return results;

      void AddStats(PlayerStats stats)
      {
        results[stats.Name] = new List<HitFreqChartData>();
        foreach (ref var subStat in stats.SubStats.ToArray().AsSpan())
        {
          HitFreqChartData chartData = new HitFreqChartData { HitType = subStat.Name };

          // add crits
          chartData.CritXValues.AddRange(subStat.CritFreqValues.Keys.OrderBy(key => key));
          foreach (ref var damage in chartData.CritXValues.ToArray().AsSpan())
          {
            chartData.CritYValues.Add(subStat.CritFreqValues[damage]);
          }

          // add non crits
          chartData.NonCritXValues.AddRange(subStat.NonCritFreqValues.Keys.OrderBy(key => key));
          foreach (ref var damage in chartData.NonCritXValues.ToArray().AsSpan())
          {
            chartData.NonCritYValues.Add(subStat.NonCritFreqValues[damage]);
          }

          results[stats.Name].Add(chartData);
        }
      }
    }


    private class ColumnData
    {
      public string X { get; set; }
      public int Y { get; set; }
      public long Diff { get; set; }
      public long XLongValue { get; set; }
    }

    private class HitFreqChartData
    {
      public string HitType { get; set; }
      public List<int> CritYValues { get; } = new List<int>();
      public List<long> CritXValues { get; } = new List<long>();
      public List<int> NonCritYValues { get; } = new List<int>();
      public List<long> NonCritXValues { get; } = new List<long>();
    }

    /*
      private class DIValue
      {
        public int DI { get; set; }
        public long Diff { get; set; }
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
      */
  }
}
