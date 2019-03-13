using ActiproSoftware.Windows.Themes;
using LiveCharts;
using LiveCharts.Configurations;
using LiveCharts.Wpf;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for DPSChart.xaml
  /// </summary>
  public partial class LineChart : UserControl
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    public static List<string> DAMAGE_CHOICES = new List<string>() { "DPS Over Time", "Total Damage" };
    public static List<string> HEALING_CHOICES = new List<string>() { "HPS Over Time", "Total Healing" };

    private static CartesianMapper<DataPoint> CONFIG_VPS = Mappers.Xy<DataPoint>()
        .X(dateModel => dateModel.CurrentTime.Ticks / TimeSpan.FromSeconds(1).Ticks)
        .Y(dateModel => dateModel.VPS);
    private static CartesianMapper<DataPoint> CONFIG_TOTAL = Mappers.Xy<DataPoint>()
        .X(dateModel => dateModel.CurrentTime.Ticks / TimeSpan.FromSeconds(1).Ticks)
        .Y(dateModel => dateModel.Total);

    private DateTime LastTime;
    private DateTime FirstTime;
    private Dictionary<string, DataPoint> PlayerData;
    private Dictionary<string, ChartValues<DataPoint>> ChartValues;
    private Dictionary<string, DataPoint> NeedAccounting;
    private CartesianMapper<DataPoint> CurrentConfig;
    private List<PlayerStats> LastSelected = null;

    public LineChart(List<string> choices)
    {
      InitializeComponent();

      // reverse regular tooltip
      lvcChart.DataTooltip.Foreground = (SolidColorBrush) Application.Current.FindResource(AssetResourceKeys.ToolTipBackgroundNormalBrushKey);
      lvcChart.DataTooltip.Background = (SolidColorBrush) Application.Current.FindResource(AssetResourceKeys.ToolTipForegroundNormalBrushKey);
      lvcChart.ChartLegend.Foreground = (SolidColorBrush) Application.Current.FindResource(AssetResourceKeys.ToolTipBackgroundNormalBrushKey);
      lvcChart.ChartLegend.Background = (SolidColorBrush) Application.Current.FindResource(AssetResourceKeys.ToolTipForegroundNormalBrushKey);

      CurrentConfig = CONFIG_VPS;
      choicesList.ItemsSource = choices;
      choicesList.SelectedIndex = 0;
      Reset();
    }

    public void Reset()
    {
      Helpers.ChartResetView(lvcChart);
      lvcChart.AxisX[0].LabelFormatter = GetLabelFormat;
      lvcChart.Series = new SeriesCollection();
    }

    public void AddDataPoints(RecordGroupIterator recordIterator)
    {
      Task.Run(() =>
      {
        InitDataPoints();

        foreach (var dataPoint in recordIterator)
        {
          double diff = LastTime == DateTime.MinValue ? 1 : dataPoint.CurrentTime.Subtract(LastTime).TotalSeconds;
          LastTime = dataPoint.CurrentTime;

          DataPoint aggregate;
          if (!PlayerData.TryGetValue(dataPoint.Name, out aggregate))
          {
            aggregate = new DataPoint() { Name = dataPoint.Name };
            PlayerData[dataPoint.Name] = aggregate;
          }

          if (FirstTime == DateTime.MinValue || diff > 30)
          {
            FirstTime = dataPoint.CurrentTime;
            foreach (var value in PlayerData.Values)
            {
              value.Rolling = 0;
            }
          }

          aggregate.Total += dataPoint.Total;
          aggregate.Rolling += dataPoint.Total;
          aggregate.BeginTime = FirstTime;
          aggregate.CurrentTime = dataPoint.CurrentTime;

          if (diff >= 1)
          {
            Insert(aggregate, ChartValues);
            UpdateRemaining(ChartValues, NeedAccounting, FirstTime, LastTime, aggregate.Name);
          }
          else
          {
            NeedAccounting[aggregate.Name] = aggregate;
          }
        }

        Plot();
      });
    }

    private string GetLabelFormat(double value)
    {
      string dateTimeString;
      DateTime dt = value > 0 ? new DateTime((long) (value * TimeSpan.FromSeconds(1).Ticks)) : new DateTime();
      dateTimeString = dt.ToString("T");
      return dateTimeString;
    }

    private void InitDataPoints()
    {
      LastTime = DateTime.MinValue;
      FirstTime = DateTime.MinValue;
      PlayerData = new Dictionary<string, DataPoint>();
      ChartValues = new Dictionary<string, ChartValues<DataPoint>>();
      NeedAccounting = new Dictionary<string, DataPoint>();
    }

    public void Plot(List<PlayerStats> selected = null)
    {
      UpdateRemaining(ChartValues, NeedAccounting, FirstTime, LastTime);
      LastSelected = selected;

      string label;
      List<ChartValues<DataPoint>> sortedValues;
      if (selected == null || selected.Count == 0)
      {
        sortedValues = ChartValues.Values.OrderByDescending(values => values.Last().Total).Take(5).ToList();
        label = sortedValues.Count > 0 ? "Top " + sortedValues.Count + " Player(s)" : "";
      }
      else
      {
        List<string> names = selected.Select(stats => stats.OrigName).Take(10).ToList();
        sortedValues = ChartValues.Values.Where(values => names.Contains(values.First().Name)).ToList();
        label = sortedValues.Count > 0 ? "Selected Players" : "";
      }

      Dispatcher.InvokeAsync(() =>
      {
        Reset();

        titleLabel.Content = label;
        SeriesCollection collection = new SeriesCollection(CurrentConfig);
        foreach (var value in sortedValues)
        {
          var series = new LineSeries() { Title = value.First().Name, Values = value, PointGeometry = null };
          collection.Add(series);
        }

        lvcChart.Series = collection;
      });
    }

    private void UpdateRemaining(Dictionary<string, ChartValues<DataPoint>> chartValues, Dictionary<string, DataPoint> needAccounting, 
      DateTime firstTime, DateTime currentTime, string ignore = null)
    {
      foreach (var remaining in needAccounting.Values)
      {
        if (ignore != remaining.Name)
        {
          if (remaining.BeginTime != firstTime)
          {
            remaining.BeginTime = firstTime;
            remaining.Rolling = 0;
          }

          remaining.CurrentTime = currentTime;
          Insert(remaining, chartValues);
        }
      }

      needAccounting.Clear();
    }

    private void Insert(DataPoint aggregate, Dictionary<string, ChartValues<DataPoint>> chartValues)
    {
      DataPoint newEntry = new DataPoint();
      newEntry.Name = aggregate.Name;
      newEntry.CurrentTime = aggregate.CurrentTime;
      newEntry.Total = aggregate.Total;

      double totalSeconds = (aggregate.CurrentTime - aggregate.BeginTime).TotalSeconds + 1;
      newEntry.VPS = (long) Math.Round(aggregate.Rolling / totalSeconds, 2);

      ChartValues<DataPoint> playerValues;
      if (!chartValues.TryGetValue(aggregate.Name, out playerValues))
      {
        playerValues = new ChartValues<DataPoint>();
        chartValues[aggregate.Name] = playerValues;
      }

      DataPoint test;
      if (playerValues.Count > 0 && (test = playerValues.Last()) != null && test.CurrentTime == newEntry.CurrentTime)
      {
        playerValues[playerValues.Count - 1] = newEntry;
      }
      else
      {
        playerValues.Add(newEntry);
      }
    }

    private void Chart_DoubleClick(object sender, MouseButtonEventArgs e)
    {
      Helpers.ChartResetView(lvcChart);
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (PlayerData != null)
      {
        CurrentConfig = choicesList.SelectedIndex == 0 ? CONFIG_VPS : CONFIG_TOTAL;
        Plot(LastSelected);
      }
    }
  }
}
