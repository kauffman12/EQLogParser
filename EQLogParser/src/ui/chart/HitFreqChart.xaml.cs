using log4net;
using Syncfusion.UI.Xaml.Charts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for HitFreqChart.xaml
  /// </summary>
  public partial class HitFreqChart : UserControl, IDisposable
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private const string CritHittype = "Critical";
    private const string NonCritHittype = "Non-Critical";
    private Dictionary<string, List<HitFreqChartData>> _playerData;
    private readonly List<string> _minFreqs = ["Any Frequency", "Frequency > 1", "Frequency > 2", "Frequency > 3", "Frequency > 4", "Frequency > 5"];
    private int _pageSize = 9;
    private readonly List<ColumnData> _columns = [];

    public HitFreqChart()
    {
      InitializeComponent();
      minFreqList.ItemsSource = _minFreqs;
      minFreqList.SelectedIndex = 0;
      MainActions.EventsThemeChanged += EventsThemeChanged;
    }

    internal void Update(PlayerStats playerStats, CombinedStats combined)
    {
      _playerData = GetHitFreqValues(playerStats, combined);
      var players = _playerData.Keys.ToList();
      playerList.ItemsSource = players;
      playerList.SelectedIndex = 0; // triggers event
    }

    private void CopyCsvClick(object sender, RoutedEventArgs e)
    {
      try
      {
        var header = new List<string> { "Hit Value", "Frequency", "Difference" };

        var data = new List<List<object>>();
        foreach (var column in CollectionsMarshal.AsSpan(_columns))
        {
          data.Add([column.XLongValue, column.Y, column.Diff]);
        }

        Clipboard.SetDataObject(TextUtils.BuildCsv(header, data));
      }
      catch (ExternalException ex)
      {
        Log.Error(ex);
      }
    }

    private void CreateImageClick(object sender, RoutedEventArgs e) => UiElementUtil.CreateImage(Dispatcher, sfChart);

    private void EventsThemeChanged(string _)
    {
      if (sfChart?.Series is { Count: > 0 } collection)
      {
        if (collection[0] is FastColumnBitmapSeries series)
        {
          // this object doesn't have setResource
          series.AdornmentsInfo.FontSize = MainWindow.CurrentFontSize + 4;
          series.AdornmentsInfo.Foreground = Application.Current.Resources["ContentForeground"] as SolidColorBrush;
        }

        // not sure why dynamic resource wasnt working in xaml
        catLabel.FontSize = MainWindow.CurrentFontSize;
        numLabel.FontSize = MainWindow.CurrentFontSize;
      }
    }

    private void UserSelectionChanged()
    {
      try
      {
        if (playerList.SelectedItem is string player && hitTypeList.SelectedItem is string type &&
        critTypeList.SelectedItem is string critType && player.Length > 0 && type.Length > 0 && critType.Length > 0)
        {
          var data = _playerData[player];
          var minFreq = minFreqList.SelectedIndex > -1 ? minFreqList.SelectedIndex : 0;
          var first = data.Find(d => d.HitType == type);
          _columns.Clear();

          if (critType == CritHittype)
          {
            for (var i = 0; i < first.CritYValues.Count; i++)
            {
              if (first.CritYValues[i] > minFreq)
              {
                var diff = (i > 0) ? (first.CritXValues[i] - first.CritXValues[i - 1]) : 0;
                var diffString = (diff == 0) ? "" : "+" + diff;
                _columns.Add(new ColumnData
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
            for (var i = 0; i < first.NonCritYValues.Count; i++)
            {
              if (first.NonCritYValues[i] > minFreq)
              {
                var diff = (i > 0) ? (first.NonCritXValues[i] - first.NonCritXValues[i - 1]) : 0;
                var diffString = (diff == 0) ? "" : "+" + diff;
                _columns.Add(new ColumnData
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
      }
      catch (ArgumentNullException ex)
      {
        Log.Error(ex);
      }
      catch (InvalidOperationException ioe)
      {
        Log.Error(ioe);
      }
    }

    private void DisplayPage()
    {
      if (_columns.Count > 0)
      {
        var page = (int)pageSlider.Value;
        var onePage = new List<ColumnData>();
        for (var i = page; i < page + _pageSize && i < _columns.Count; i++)
        {
          onePage.Add(_columns[i]);
        }

        var collection = new ChartSeriesCollection();
        var series = new FastColumnBitmapSeries { XBindingPath = "X", YBindingPath = "Y", ItemsSource = onePage };

        var adornment = new ChartAdornmentInfo
        {
          ShowLabel = true,
          ShowMarker = false,
          LabelPosition = AdornmentsLabelPosition.Outer,
          FontSize = MainWindow.CurrentFontSize + 4,
          Foreground = Application.Current.Resources["ContentForeground"] as SolidColorBrush,
          Background = new SolidColorBrush(Colors.Transparent)
        };

        catLabel.FontSize = MainWindow.CurrentFontSize;
        numLabel.FontSize = MainWindow.CurrentFontSize;
        series.AdornmentsInfo = adornment;
        ChartSeriesBase.SetSpacing(series, 0.5);
        collection.Add(series);
        sfChart.Series = collection;
      }
    }

    private void UpdatePageSize()
    {
      if (_columns.Count > 0)
      {
        _pageSize = (int)Math.Round(sfChart.ActualWidth / 60);
        pageSlider.Minimum = 0;
        var max = _columns.Count <= _pageSize ? 0 : _columns.Count - _pageSize;
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

    private void ListSelectionChanged(object sender, SelectionChangedEventArgs e) => UserSelectionChanged();

    private void CritTypeListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (critTypeList.SelectedItem is string selectedCritType)
      {
        UpdateSelectedHitTypes(selectedCritType == NonCritHittype);
        UserSelectionChanged();
      }
    }

    private void PlayerListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (playerList?.SelectedItem is string player)
      {
        if (_playerData.TryGetValue(player, out var data))
        {
          var canUseCrit = false;
          var playerCritTypes = new List<string>();
          if (data.Any(d => d.CritYValues.Count > 0))
          {
            playerCritTypes.Add(CritHittype);
            canUseCrit = true;
          }

          if (data.Any(d => d.NonCritYValues.Count > 0))
          {
            playerCritTypes.Add(NonCritHittype);
          }

          critTypeList.ItemsSource = playerCritTypes;
          critTypeList.SelectedIndex = playerCritTypes.Count == 1 ? 0 : canUseCrit ? 1 : 0;
          UpdateSelectedHitTypes((critTypeList?.SelectedItem as string) == NonCritHittype);
          UserSelectionChanged();
        }
      }
    }

    private void UpdateSelectedHitTypes(bool useNonCrit)
    {
      var player = playerList?.SelectedItem as string;
      if (!string.IsNullOrEmpty(player) && _playerData.TryGetValue(player, out var data))
      {
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
        else if (hitTypes.Count > 0 && hitTypeList != null)
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

    private void sfChart_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
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
      var results = new Dictionary<string, List<HitFreqChartData>>();

      // get chart data for player and pets if available
      if (damageStats?.Children.ContainsKey(selected.Name) == true)
      {
        damageStats.Children[selected.Name].ForEach(AddStats);
      }
      else
      {
        AddStats(selected);
      }

      return results;

      void AddStats(PlayerStats stats)
      {
        results[stats.Name] = [];
        foreach (var subStat in CollectionsMarshal.AsSpan(stats.SubStats))
        {
          var chartData = new HitFreqChartData { HitType = subStat.Name };

          // add crits
          chartData.CritXValues.AddRange(subStat.CritFreqValues.Keys.OrderBy(key => key));
          foreach (var damage in CollectionsMarshal.AsSpan(chartData.CritXValues))
          {
            chartData.CritYValues.Add(subStat.CritFreqValues[damage]);
          }

          // add non crits
          chartData.NonCritXValues.AddRange(subStat.NonCritFreqValues.Keys.OrderBy(key => key));
          foreach (var damage in CollectionsMarshal.AsSpan(chartData.NonCritXValues))
          {
            chartData.NonCritYValues.Add(subStat.NonCritFreqValues[damage]);
          }

          results[stats.Name].Add(chartData);
        }
      }
    }

    #region IDisposable Support
    private bool _disposedValue; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!_disposedValue)
      {
        MainActions.EventsThemeChanged -= EventsThemeChanged;
        sfChart?.Dispose();
        _disposedValue = true;
      }
    }

    // This code added to correctly implement the disposable pattern.
    public void Dispose()
    {
      // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
      Dispose(true);
      // TODO: uncomment the following line if the finalizer is overridden above.
      GC.SuppressFinalize(this);
    }
    #endregion

    private class ColumnData
    {
      public string X { get; set; }
      public int Y { get; init; }
      public long Diff { get; init; }
      public long XLongValue { get; init; }
    }

    private class HitFreqChartData
    {
      public string HitType { get; init; }
      public List<int> CritYValues { get; } = [];
      public List<long> CritXValues { get; } = [];
      public List<int> NonCritYValues { get; } = [];
      public List<long> NonCritXValues { get; } = [];
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
