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
    private static List<DataPoint> DataPoints = new List<DataPoint>();
    private static bool running = false;
    private static DataPointComparer DataPointComparer = new DataPointComparer();

    public LineChart()
    {
      InitializeComponent();

      // reverse regular tooltip
      lvcChart.DataTooltip.Foreground = (SolidColorBrush) Application.Current.FindResource(AssetResourceKeys.ToolTipBackgroundNormalBrushKey);
      lvcChart.DataTooltip.Background = (SolidColorBrush) Application.Current.FindResource(AssetResourceKeys.ToolTipForegroundNormalBrushKey);
      lvcChart.ChartLegend.Foreground = (SolidColorBrush) Application.Current.FindResource(AssetResourceKeys.ToolTipBackgroundNormalBrushKey);
      lvcChart.ChartLegend.Background = (SolidColorBrush) Application.Current.FindResource(AssetResourceKeys.ToolTipForegroundNormalBrushKey);
    }

    public void Reset()
    {

      //LineSeries series = new LineSeries();
      //series.Values = new ChartValues<long>() { 0 };
      //SeriesCollection collection = new SeriesCollection();
      //collection.Add(series);
      //lvcChart.AxisX[0].Labels = new List<string>() { "Jan 01 12:00:00", "15", "30" };
      //lvcChart.AxisY[0].Labels = new List<string>() { "0", "100000", "200000", "300000" };
      //lvcChart.AxisY[0].MaxValue = 300000;
      //lvcChart.Series = collection;
    }

    public void Update(ChartData chartData)
    {
      //var series = Helpers.CreateLineChartSeries(chartData.Values);
      //Helpers.ChartResetView(lvcChart);
      //lvcChart.AxisX[0].Labels = chartData.XAxisLabels;
      //lvcChart.AxisY[0].Labels = null;
      //lvcChart.AxisY[0].MaxValue = double.NaN;
      //lvcChart.Series = series;
    }

    public void HandleDataPointEvent(object sender, DataPointEvent e)
    {
      switch (e.EventType)
      {
        case "RESET":
          lock (DataPoints)
          {
            DataPoints.Clear();
          }
          break;
        case "UPDATE":
          lock (DataPoints)
          {
            DataPoints.Add(e.Data);
          }
          break;
        case "DONE":
          Display(sender as PlayerStats);
          break;
      }
    }

    private void Display(PlayerStats raidStats)
    {
      if (running == false)
      {
        running = true;

        Task.Delay(5).ContinueWith(task =>
        {
          try
          {
            if (DataPoints.Count > 0)
            {
              DateTime lastTime = DateTime.MinValue;
              DateTime firstTime = DateTime.MinValue;
              Dictionary<string, DataPoint> playerData = new Dictionary<string, DataPoint>();
              Dictionary<string, ChartValues<DataPoint>> chartValues = new Dictionary<string, ChartValues<DataPoint>>();
              Dictionary<string, DataPoint> needAccounting = new Dictionary<string, DataPoint>();

              foreach (var dataPoint in DataPoints)
              {
                double diff = lastTime == DateTime.MinValue ? 1 : dataPoint.CurrentTime.Subtract(lastTime).TotalSeconds;
                lastTime = dataPoint.CurrentTime;

                DataPoint aggregate;
                if (!playerData.TryGetValue(dataPoint.Name, out aggregate))
                {
                  aggregate = new DataPoint() { Name = dataPoint.Name };
                  playerData[dataPoint.Name] = aggregate;
                }

                if (firstTime == DateTime.MinValue || diff > 30)
                {
                  firstTime = dataPoint.CurrentTime;
                  foreach (var value in playerData.Values)
                  {
                    value.Rolling = 0;
                  }
                }

                aggregate.Total += dataPoint.Total;
                aggregate.Rolling += dataPoint.Total;
                aggregate.BeginTime = firstTime;
                aggregate.CurrentTime = dataPoint.CurrentTime;

                if (diff >= 1)
                {
                  Insert(aggregate, chartValues);
                  UpdateRemaining(chartValues, needAccounting, firstTime, lastTime, aggregate.Name);
                }
                else
                {
                  needAccounting[aggregate.Name] = aggregate;
                }
              }

              UpdateRemaining(chartValues, needAccounting, firstTime, lastTime);

              var config = Mappers.Xy<DataPoint>()
                .X(dateModel => dateModel.CurrentTime.Ticks / TimeSpan.FromSeconds(1).Ticks)
                .Y(dateModel => dateModel.VPS);

              var sortedValues = chartValues.Values.OrderByDescending(values => values.Last().Total).Take(5);

              Dispatcher.InvokeAsync(() =>
              {
                Helpers.ChartResetView(lvcChart);

                SeriesCollection collection = new SeriesCollection(config);
                foreach (var value in sortedValues)
                {
                  var series = new LineSeries() { Title = value.First().Name, Values = value, PointGeometry = null };
                  collection.Add(series);
                }

                lvcChart.AxisX[0].Labels = null;
                lvcChart.AxisY[0].Labels = null;
                lvcChart.AxisY[0].MaxValue = double.NaN;
                lvcChart.AxisX[0].LabelFormatter = value => new DateTime((long) (value * TimeSpan.FromSeconds(1).Ticks)).ToString("T");
                lvcChart.Series = collection;
              });
            }
          }
          catch (Exception ex)
          {
            LOG.Debug(ex);
          }
          finally
          {
            running = false;
          }
        });
      }
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
  }
}
