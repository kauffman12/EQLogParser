using Syncfusion.UI.Xaml.Charts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for LineChart.xaml
  /// </summary>
  public partial class LineChart : UserControl, IDisposable
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private readonly Dictionary<string, List<DataPoint>> PlayerPetValues = new Dictionary<string, List<DataPoint>>();
    private readonly Dictionary<string, List<DataPoint>> PlayerValues = new Dictionary<string, List<DataPoint>>();
    private readonly Dictionary<string, List<DataPoint>> PetValues = new Dictionary<string, List<DataPoint>>();
    private readonly Dictionary<string, List<DataPoint>> RaidValues = new Dictionary<string, List<DataPoint>>();
    private readonly Dictionary<string, Dictionary<string, byte>> HasPets = new Dictionary<string, Dictionary<string, byte>>();

    private string CurrentChoice = "";
    private int CurrentConfig;
    private string CurrentPetOrPlayerOption;
    private List<PlayerStats> LastSelected = null;

    public LineChart(List<string> choices, bool includePets = false)
    {
      InitializeComponent();

      CurrentConfig = 0;
      choicesList.ItemsSource = choices;
      choicesList.SelectedIndex = 0;
      CurrentChoice = choicesList.SelectedValue as string;
      DataManager.Instance.EventsClearedActiveData += EventsClearedActiveData;

      if (includePets)
      {
        petOrPlayerList.ItemsSource = new List<string> { Labels.PETPLAYEROPTION, Labels.PLAYEROPTION, Labels.PETOPTION, Labels.RAIDOPTION };
      }
      else
      {
        petOrPlayerList.ItemsSource = new List<string> { Labels.PLAYEROPTION, Labels.RAIDOPTION };
      }

      petOrPlayerList.SelectedIndex = 0;
      CurrentPetOrPlayerOption = petOrPlayerList.SelectedValue as string;
      Reset();
    }

    private void EventsClearedActiveData(object sender, bool cleared) => Clear();

    internal void Clear()
    {
      PlayerPetValues.Clear();
      PlayerValues.Clear();
      PetValues.Clear();
      RaidValues.Clear();
      HasPets.Clear();
      Reset();
    }

    internal void HandleUpdateEvent(DataPointEvent e)
    {
      switch (e.Action)
      {
        case "CLEAR":
          Clear();
          break;
        case "UPDATE":
          Clear();
          AddDataPoints(e.Iterator, e.Selected);
          break;
        case "SELECT":
          PlotSelected(e.Selected);
          break;
      }
    }

    private void UpdateTimes(string name, DataPoint dataPoint, Dictionary<string, double> diffs, Dictionary<string, double> firstTimes,
      Dictionary<string, double> lastTimes)
    {
      double diff = lastTimes.TryGetValue(name, out double lastTime) ? dataPoint.CurrentTime - lastTime : 1;
      diffs[name] = diff;

      if (!firstTimes.TryGetValue(name, out double _) || diff > DataManager.FIGHTTIMEOUT)
      {
        firstTimes[name] = dataPoint.CurrentTime;
      }
    }

    private void AddDataPoints(RecordGroupCollection recordIterator, List<PlayerStats> selected = null)
    {
      var diffs = new Dictionary<string, double>();
      var firstTimes = new Dictionary<string, double>();
      var lastTimes = new Dictionary<string, double>();
      var petData = new Dictionary<string, DataPoint>();
      var playerData = new Dictionary<string, DataPoint>();
      var totalPlayerData = new Dictionary<string, DataPoint>();
      var raidData = new Dictionary<string, DataPoint>();
      var needTotalAccounting = new Dictionary<string, DataPoint>();
      var needPlayerAccounting = new Dictionary<string, DataPoint>();
      var needPetAccounting = new Dictionary<string, DataPoint>();
      var needRaidAccounting = new Dictionary<string, DataPoint>();

      foreach (var dataPoint in recordIterator)
      {
        UpdateTimes(dataPoint.Name, dataPoint, diffs, firstTimes, lastTimes);

        var raidName = "Raid";
        UpdateTimes(raidName, dataPoint, diffs, firstTimes, lastTimes);

        if (!raidData.TryGetValue(raidName, out DataPoint raidAggregate))
        {
          raidAggregate = new DataPoint() { Name = raidName };
          raidData[raidName] = raidAggregate;
        }

        Aggregate(RaidValues, needRaidAccounting, dataPoint, raidAggregate, firstTimes, lastTimes, diffs);

        var playerName = dataPoint.PlayerName ?? dataPoint.Name;
        var petName = dataPoint.PlayerName == null ? null : dataPoint.Name;
        var totalName = playerName + " +Pets";
        UpdateTimes(totalName, dataPoint, diffs, firstTimes, lastTimes);
        if (!totalPlayerData.TryGetValue(totalName, out DataPoint totalAggregate))
        {
          totalAggregate = new DataPoint() { Name = totalName, PlayerName = playerName };
          totalPlayerData[totalName] = totalAggregate;
        }

        Aggregate(PlayerPetValues, needTotalAccounting, dataPoint, totalAggregate, firstTimes, lastTimes, diffs);

        if (dataPoint.PlayerName == null)
        {
          if (!playerData.TryGetValue(dataPoint.Name, out DataPoint aggregate))
          {
            aggregate = new DataPoint() { Name = dataPoint.Name };
            playerData[dataPoint.Name] = aggregate;
          }

          Aggregate(PlayerValues, needPlayerAccounting, dataPoint, aggregate, firstTimes, lastTimes, diffs);
        }
        else if (dataPoint.PlayerName != null)
        {
          if (!HasPets.ContainsKey(totalName))
          {
            HasPets[totalName] = new Dictionary<string, byte>();
          }

          HasPets[totalName][petName] = 1;
          if (!petData.TryGetValue(petName, out DataPoint petAggregate))
          {
            petAggregate = new DataPoint() { Name = petName, PlayerName = playerName };
            petData[petName] = petAggregate;
          }

          Aggregate(PetValues, needPetAccounting, dataPoint, petAggregate, firstTimes, lastTimes, diffs);
        }

        lastTimes[dataPoint.Name] = dataPoint.CurrentTime;
        lastTimes[raidName] = dataPoint.CurrentTime;
      }

      UpdateRemaining(RaidValues, needRaidAccounting, firstTimes, lastTimes);
      UpdateRemaining(PlayerPetValues, needTotalAccounting, firstTimes, lastTimes);
      UpdateRemaining(PlayerValues, needPlayerAccounting, firstTimes, lastTimes);
      UpdateRemaining(PetValues, needPetAccounting, firstTimes, lastTimes);
      Plot(selected);
    }

    private void Plot(List<PlayerStats> selected = null)
    {
      LastSelected = selected;

      Dictionary<string, List<DataPoint>> workingData = null;

      string selectedLabel = "Selected Player(s)";
      string nonSelectedLabel = " Player(s)";
      switch (CurrentPetOrPlayerOption)
      {
        case Labels.PETPLAYEROPTION:
          workingData = PlayerPetValues;
          selectedLabel = "Selected Player +Pets(s)";
          nonSelectedLabel = " Player +Pets(s)";
          break;
        case Labels.PLAYEROPTION:
          workingData = PlayerValues;
          break;
        case Labels.PETOPTION:
          workingData = PetValues;
          selectedLabel = "Selected Pet(s)";
          nonSelectedLabel = " Pet(s)";
          break;
        case Labels.RAIDOPTION:
          workingData = RaidValues;
          break;
        default:
          workingData = new Dictionary<string, List<DataPoint>>();
          break;
      }

      string label;
      List<List<DataPoint>> sortedValues;
      if (CurrentPetOrPlayerOption == Labels.RAIDOPTION)
      {
        sortedValues = workingData.Values.ToList();
        label = sortedValues.Count > 0 ? "Raid" : Labels.NODATA;
      }
      else if (selected == null || selected.Count == 0)
      {
        sortedValues = workingData.Values.OrderByDescending(values => values.Last().Total).Take(5).ToList();
        label = sortedValues.Count > 0 ? "Top " + sortedValues.Count + nonSelectedLabel : Labels.NODATA;
      }
      else
      {
        List<string> names = selected.Select(stats => stats.OrigName).ToList();
        sortedValues = workingData.Values.Where(values =>
        {
          bool pass = false;
          var first = values.First();
          if (CurrentPetOrPlayerOption == Labels.PETPLAYEROPTION)
          {
            pass = names.Contains(first.PlayerName) || (HasPets.ContainsKey(first.Name) &&
            names.FirstOrDefault(name => HasPets[first.Name].ContainsKey(name)) != null);
          }
          else if (CurrentPetOrPlayerOption == Labels.PLAYEROPTION)
          {
            pass = names.Contains(first.Name);
          }
          else if (CurrentPetOrPlayerOption == Labels.PETOPTION)
          {
            pass = names.Contains(first.Name) || names.Contains(first.PlayerName);
          }
          return pass;
        }).Take(10).ToList();

        label = sortedValues.Count > 0 ? selectedLabel : Labels.NODATA;
      }

      if (label != Labels.NODATA)
      {
        label += " " + CurrentChoice;
      }

      Reset();
      titleLabel.Content = label;
      sfLineChart.Series = BuildCollection(sortedValues);
    }

    private ChartSeriesCollection BuildCollection(List<List<DataPoint>> sortedValues)
    {
      var collection = new ChartSeriesCollection();

      string yPath = "Avg";
      switch (CurrentConfig)
      {
        case 0:
          yPath = "Vps";
          break;
        case 1:
          yPath = "Total";
          break;
        case 2:
          yPath = "Avg";
          break;
        case 3:
          yPath = "CritRate";
          break;
      }

      foreach (ref var value in sortedValues.ToArray().AsSpan())
      {
        var name = value.First().Name;
        name = ((CurrentPetOrPlayerOption == Labels.PETPLAYEROPTION) && !HasPets.ContainsKey(name)) ? name.Split(' ')[0] : name;
        var series = new FastLineSeries
        {
          Label = name,
          XBindingPath = "DateTime",
          YBindingPath = yPath,
          ItemsSource = value,
          ShowTooltip = false,
        };

        collection.Add(series);
      }

      return collection;
    }

    private void CreateImageClick(object sender, RoutedEventArgs e) => Helpers.CopyImage(Dispatcher, sfLineChart, titleLabel);

    private void PlotSelected(List<PlayerStats> selected)
    {
      if (RaidValues.Count > 0)
      {
        // handling case where chart can be updated twice
        // when toggling bane and selection is lost
        if (!(selected.Count == 0 && LastSelected == null))
        {
          Plot(selected);
        }
      }
      else
      {
        Reset();
      }
    }

    private void Reset()
    {
      sfLineChart.Series.Clear();
      titleLabel.Content = Labels.NODATA;
    }

    private void ListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (PlayerPetValues.Count > 0)
      {
        CurrentChoice = choicesList.SelectedValue as string;
        CurrentConfig = choicesList.SelectedIndex;
        CurrentPetOrPlayerOption = petOrPlayerList.SelectedValue as string;
        Plot(LastSelected);
      }
    }

    private void CopyCsvClick(object sender, RoutedEventArgs e)
    {
      if (sfLineChart.Series.Count > 0)
      {
        try
        {
          List<string> header = new List<string> { "Seconds", choicesList.SelectedValue as string, "Name" };

          var data = new List<List<object>>();
          foreach (var series in sfLineChart.Series)
          {
            if (series.ItemsSource is List<DataPoint> dataPoints)
            {
              foreach (ref var chartData in dataPoints.ToArray().AsSpan())
              {
                double chartValue = 0;
                if (CurrentConfig == 2)
                {
                  chartValue = chartData.Avg;
                }
                else if (CurrentConfig == 3)
                {
                  chartValue = chartData.CritRate;
                }
                else if (CurrentConfig == 1)
                {
                  chartValue = chartData.Total;
                }
                else if (CurrentConfig == 0)
                {
                  chartValue = chartData.Vps;
                }

                data.Add(new List<object> { chartData.CurrentTime, Math.Round(chartValue, 2), chartData.Name });
              }
            }
          }

          Clipboard.SetDataObject(TextFormatUtils.BuildCsv(header, data, titleLabel.Content as string));
        }
        catch (ExternalException ex)
        {
          LOG.Error(ex);
        }
      }
    }

    private static void Aggregate(Dictionary<string, List<DataPoint>> theValues,
      Dictionary<string, DataPoint> needAccounting, DataPoint dataPoint, DataPoint aggregate,
      Dictionary<string, double> firstTimes, Dictionary<string, double> lastTimes, Dictionary<string, double> diffs)
    {
      var diff = diffs[aggregate.Name];
      var firstTime = firstTimes[aggregate.Name];
      lastTimes.TryGetValue(aggregate.Name, out double lastTime);

      if (diff > DataManager.FIGHTTIMEOUT)
      {
        aggregate.RollingTotal = 0;
        aggregate.RollingCritHits = 0;
        aggregate.RollingHits = 0;
        aggregate.CurrentTime = lastTime + 6;
        aggregate.DateTime = DateUtil.FromDouble(aggregate.CurrentTime);
        Insert(aggregate, theValues);
        aggregate.CurrentTime = firstTime - 6;
        aggregate.DateTime = DateUtil.FromDouble(aggregate.CurrentTime);
        Insert(aggregate, theValues);
      }

      aggregate.Total += dataPoint.Total;
      aggregate.RollingTotal += dataPoint.Total;
      aggregate.RollingHits += 1;
      aggregate.RollingCritHits += LineModifiersParser.IsCrit(dataPoint.ModifiersMask) ? (uint)1 : 0;
      aggregate.BeginTime = firstTime;
      aggregate.CurrentTime = dataPoint.CurrentTime;
      aggregate.DateTime = DateUtil.FromDouble(dataPoint.CurrentTime);

      if (diff >= 1)
      {
        Insert(aggregate, theValues);
      }
      else
      {
        needAccounting[aggregate.Name] = aggregate;
      }
    }

    private static void UpdateRemaining(Dictionary<string, List<DataPoint>> chartValues, Dictionary<string, DataPoint> needAccounting,
      Dictionary<string, double> firstTimes, Dictionary<string, double> lastTimes, string ignore = null)
    {
      foreach (ref var remaining in needAccounting.Values.ToArray().AsSpan())
      {
        if (ignore != remaining.Name)
        {
          var firstTime = firstTimes[remaining.Name];
          var lastTime = lastTimes[remaining.Name];
          if (remaining.BeginTime != firstTime)
          {
            remaining.BeginTime = firstTime;
            remaining.Total = 0;
            remaining.RollingTotal = 0;
            remaining.RollingHits = 0;
            remaining.RollingCritHits = 0;
          }

          remaining.CurrentTime = lastTime;
          remaining.DateTime = DateUtil.FromDouble(lastTime);
          Insert(remaining, chartValues);
        }
      }

      needAccounting.Clear();
    }

    private static void Insert(DataPoint aggregate, Dictionary<string, List<DataPoint>> chartValues)
    {
      var newEntry = new DataPoint
      {
        Name = aggregate.Name,
        PlayerName = aggregate.PlayerName,
        CurrentTime = aggregate.CurrentTime,
        DateTime = DateUtil.FromDouble(aggregate.CurrentTime),
        Total = aggregate.Total
      };

      var totalSeconds = aggregate.CurrentTime - aggregate.BeginTime + 1;
      newEntry.Vps = (long)Math.Round(aggregate.RollingTotal / totalSeconds, 2);

      if (aggregate.RollingHits > 0)
      {
        newEntry.Avg = (long)Math.Round(Convert.ToDecimal(aggregate.RollingTotal) / aggregate.RollingHits, 2);
        newEntry.CritRate = Math.Round(Convert.ToDouble(aggregate.RollingCritHits) / aggregate.RollingHits * 100, 2);
      }

      if (!chartValues.TryGetValue(aggregate.Name, out List<DataPoint> playerValues))
      {
        playerValues = new List<DataPoint>();
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

    #region IDisposable Support
    private bool disposedValue = false; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        DataManager.Instance.EventsClearedActiveData -= EventsClearedActiveData;
        sfLineChart.Dispose();
        disposedValue = true;
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
  }
}
