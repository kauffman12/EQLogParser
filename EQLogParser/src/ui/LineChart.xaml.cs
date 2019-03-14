using ActiproSoftware.Windows.Themes;
using LiveCharts;
using LiveCharts.Configurations;
using LiveCharts.Wpf;
using System;
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
     .X(dateModel => dateModel.CurrentTime)
     .Y(dateModel => dateModel.VPS);
    private static CartesianMapper<DataPoint> CONFIG_TOTAL = Mappers.Xy<DataPoint>()
     .X(dateModel => dateModel.CurrentTime)
     .Y(dateModel => dateModel.Total);

    private DateTime ChartModifiedTime;
    Dictionary<string, ChartValues<DataPoint>> ChartValues = null;
    private CartesianMapper<DataPoint> CurrentConfig;
    private List<PlayerStats> LastSelected = null;

    public LineChart(List<string> choices)
    {
      InitializeComponent();

      lvcChart.Hoverable = false;
      lvcChart.DisableAnimations = true;
      lvcChart.DataTooltip = null;

      // reverse regular tooltip
      //lvcChart.DataTooltip.Foreground = (SolidColorBrush) Application.Current.FindResource(AssetResourceKeys.ToolTipBackgroundNormalBrushKey);
      //lvcChart.DataTooltip.Background = (SolidColorBrush) Application.Current.FindResource(AssetResourceKeys.ToolTipForegroundNormalBrushKey);
      lvcChart.ChartLegend.Foreground = (SolidColorBrush) Application.Current.FindResource(AssetResourceKeys.ToolTipBackgroundNormalBrushKey);
      lvcChart.ChartLegend.Background = (SolidColorBrush) Application.Current.FindResource(AssetResourceKeys.ToolTipForegroundNormalBrushKey);

      CurrentConfig = CONFIG_VPS;
      choicesList.ItemsSource = choices;
      choicesList.SelectedIndex = 0;
      Reset();
    }

    public void Clear()
    {
      ChartValues = null;
      Reset();
    }

    public void AddDataPoints(RecordGroupIterator recordIterator, List<PlayerStats> selected = null)
    {
      DateTime newTaskTime = DateTime.Now;

      Task.Run(() =>
      {
        double lastTime = double.NaN;
        double firstTime = double.NaN;
        Dictionary<string, DataPoint> playerData = new Dictionary<string, DataPoint>();
        Dictionary<string, DataPoint> needAccounting = new Dictionary<string, DataPoint>();
        Dictionary<string, ChartValues<DataPoint>> theValues = new Dictionary<string, ChartValues<DataPoint>>();

        foreach (var dataPoint in recordIterator)
        {
          double diff = double.IsNaN(lastTime) ? 1 : dataPoint.CurrentTime - lastTime;

          DataPoint aggregate;
          if (!playerData.TryGetValue(dataPoint.Name, out aggregate))
          {
            aggregate = new DataPoint() { Name = dataPoint.Name };
            playerData[dataPoint.Name] = aggregate;
          }

          if (double.IsNaN(firstTime) || diff > 30)
          {
            firstTime = dataPoint.CurrentTime;

            if (diff >= 30)
            {
              UpdateRemaining(theValues, needAccounting, firstTime, lastTime);
              foreach (var value in playerData.Values)
              {
                value.Rolling = 0;
                value.CurrentTime = lastTime + 6;
                Insert(value, theValues);
                value.CurrentTime = firstTime - 6;
                Insert(value, theValues);
              }
            }
          }

          aggregate.Total += dataPoint.Total;
          aggregate.Rolling += dataPoint.Total;
          aggregate.BeginTime = firstTime;
          aggregate.CurrentTime = dataPoint.CurrentTime;
          lastTime = dataPoint.CurrentTime;

          if (diff >= 1)
          {
            Insert(aggregate, theValues);
            UpdateRemaining(theValues, needAccounting, firstTime, lastTime, aggregate.Name);
          }
          else
          {
            needAccounting[aggregate.Name] = aggregate;
          }
        }

        UpdateRemaining(theValues, needAccounting, firstTime, lastTime);
        Plot(theValues, newTaskTime, selected);
      });
    }

    private string GetLabelFormat(double value)
    {
      string dateTimeString;
      DateTime dt = value > 0 ? new DateTime((long) (value * TimeSpan.FromSeconds(1).Ticks)) : new DateTime();
      dateTimeString = dt.ToString("HH:mm:ss");
      return dateTimeString;
    }

    public void Plot(List<PlayerStats> selected)
    {
      if (ChartValues != null)
      {
        // handling case where chart can be updated twice
        // when toggling bane and selection is lost
        if (!(selected.Count == 0 && LastSelected == null))
        {
          Plot(ChartValues, DateTime.Now, selected);
        }
      }
      else
      {
        Reset();
      }
    }

    public void Plot(Dictionary<string, ChartValues<DataPoint>> theValues, DateTime requestTime, List<PlayerStats> selected = null)
    {
      LastSelected = selected;

      string label;
      List<ChartValues<DataPoint>> sortedValues;
      if (selected == null || selected.Count == 0)
      {
        sortedValues = theValues.Values.OrderByDescending(values => values.Last().Total).Take(5).ToList();
        label = sortedValues.Count > 0 ? "Top " + sortedValues.Count + " Player(s)" : "No Data";
      }
      else
      {
        List<string> names = selected.Select(stats => stats.OrigName).Take(10).ToList();
        sortedValues = theValues.Values.Where(values => names.Contains(values.First().Name)).ToList();
        label = sortedValues.Count > 0 ? "Selected Player(s)" : "No Data";
      }

      Dispatcher.InvokeAsync(() =>
      {
        if (ChartModifiedTime < requestTime)
        {
          ChartValues = theValues;
          ChartModifiedTime = requestTime;
          Reset();

          titleLabel.Content = label;
          SeriesCollection collection = new SeriesCollection(CurrentConfig);
          bool fixStillNeeded = true;

          foreach (var value in sortedValues)
          {
            var series = new LineSeries() { Title = value.First().Name, Values = value };

            if (value.Count > 1)
            {
              series.PointGeometry = null;
              fixStillNeeded = false;
            }
            else if (value.Count == 1 && fixStillNeeded) // handles if everything is 1 point
            {
              if (!double.IsNaN(lvcChart.AxisX[0].MinValue))
              {
                lvcChart.AxisX[0].MinValue = Math.Min(lvcChart.AxisX[0].MinValue, value[0].CurrentTime - 3.0);
              }
              else
              {
                lvcChart.AxisX[0].MinValue = value[0].CurrentTime - 3.0;
              }

              if (!double.IsNaN(lvcChart.AxisX[0].MaxValue))
              {
                lvcChart.AxisX[0].MaxValue = Math.Max(lvcChart.AxisX[0].MaxValue, value[0].CurrentTime + 3.0);
              }
              else
              {
                lvcChart.AxisX[0].MaxValue = value[0].CurrentTime + 3.0;
              }
            }
            else
            {
              fixStillNeeded = false;
            }

            if (!fixStillNeeded)
            {
              lvcChart.AxisX[0].MinValue = double.NaN;
              lvcChart.AxisX[0].MaxValue = double.NaN;
            }

            collection.Add(series);
          }

          lvcChart.Series = collection;
        }
      });
    }

    private void Reset()
    {
      Helpers.ChartResetView(lvcChart);
      lvcChart.AxisX[0].LabelFormatter = GetLabelFormat;
      lvcChart.Series = null;
      titleLabel.Content = "No Data";
    }

    private void UpdateRemaining(Dictionary<string, ChartValues<DataPoint>> chartValues, Dictionary<string, DataPoint> needAccounting, 
      double firstTime, double currentTime, string ignore = null)
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

      double totalSeconds = aggregate.CurrentTime - aggregate.BeginTime + 1;
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
      if (ChartValues != null)
      {
        CurrentConfig = choicesList.SelectedIndex == 0 ? CONFIG_VPS : CONFIG_TOTAL;
        Plot(ChartValues, DateTime.Now, LastSelected);
      }
    }
  }
}
