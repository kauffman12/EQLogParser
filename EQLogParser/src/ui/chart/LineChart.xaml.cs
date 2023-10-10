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

    private readonly Dictionary<string, List<DataPoint>> PlayerPetValues = new();
    private readonly Dictionary<string, List<DataPoint>> PlayerValues = new();
    private readonly Dictionary<string, List<DataPoint>> PetValues = new();
    private readonly Dictionary<string, List<DataPoint>> RaidValues = new();
    private readonly Dictionary<string, Dictionary<string, byte>> HasPets = new();
    private readonly static Dictionary<string, bool> MissTypes = new()
    { { Labels.ABSORB, true }, { Labels.BLOCK, true } , { Labels.DODGE, true }, { Labels.PARRY, true }, { Labels.INVULNERABLE, true }, { Labels.MISS, true } };

    private string CurrentChoice = "";
    private string CurrentPetOrPlayerOption;
    private List<PlayerStats> LastSelected;

    public LineChart(List<string> choices, bool includePets = false)
    {
      InitializeComponent();

      choicesList.ItemsSource = choices;
      choicesList.SelectedIndex = 0;
      CurrentChoice = choicesList.SelectedValue as string;
      dateLabel.FontSize = MainWindow.CurrentFontSize - 1;
      numLabel.FontSize = MainWindow.CurrentFontSize;

      DataManager.Instance.EventsClearedActiveData += EventsClearedActiveData;
      (Application.Current.MainWindow as MainWindow).EventsThemeChanged += EventsThemeChanged;

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

    private void EventsThemeChanged(string _)
    {
      dateLabel.FontSize = MainWindow.CurrentFontSize - 1;
      numLabel.FontSize = MainWindow.CurrentFontSize;
    }

    private void UpdateTimes(string name, DataPoint dataPoint, Dictionary<string, double> diffs, Dictionary<string, double> lastTimes)
    {
      if (lastTimes.TryGetValue(name, out var lastTime))
      {
        diffs[name] = dataPoint.CurrentTime - lastTime;
      }
      else
      {
        diffs[name] = 0;
      }
    }

    private void AddDataPoints(RecordGroupCollection recordIterator, List<PlayerStats> selected = null)
    {
      var diffs = new Dictionary<string, double>();
      var lastTimes = new Dictionary<string, double>();
      var timeRanges = new Dictionary<string, TimeRange>();
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
        var raidName = "Raid";
        var playerName = dataPoint.PlayerName ?? dataPoint.Name;
        var totalName = playerName + " +Pets";

        UpdateTimes(dataPoint.Name, dataPoint, diffs, lastTimes);
        UpdateTimes(raidName, dataPoint, diffs, lastTimes);
        UpdateTimes(totalName, dataPoint, diffs, lastTimes);

        if (!raidData.TryGetValue(raidName, out var raidAggregate))
        {
          raidAggregate = new DataPoint { Name = raidName };
          raidData[raidName] = raidAggregate;
        }

        Aggregate(RaidValues, needRaidAccounting, dataPoint, raidAggregate, lastTimes, timeRanges, diffs);

        if (!totalPlayerData.TryGetValue(totalName, out var totalAggregate))
        {
          totalAggregate = new DataPoint { Name = totalName, PlayerName = playerName };
          totalPlayerData[totalName] = totalAggregate;
        }

        Aggregate(PlayerPetValues, needTotalAccounting, dataPoint, totalAggregate, lastTimes, timeRanges, diffs);

        if (dataPoint.PlayerName == null)
        {
          if (!playerData.TryGetValue(dataPoint.Name, out var aggregate))
          {
            aggregate = new DataPoint { Name = dataPoint.Name, PlayerName = dataPoint.Name };
            playerData[dataPoint.Name] = aggregate;
          }

          Aggregate(PlayerValues, needPlayerAccounting, dataPoint, aggregate, lastTimes, timeRanges, diffs);
        }
        else if (dataPoint.PlayerName != null)
        {
          if (!HasPets.ContainsKey(totalName))
          {
            HasPets[totalName] = new Dictionary<string, byte>();
          }

          HasPets[totalName][dataPoint.Name] = 1;
          if (!petData.TryGetValue(dataPoint.Name, out var petAggregate))
          {
            petAggregate = new DataPoint() { Name = dataPoint.Name, PlayerName = playerName };
            petData[dataPoint.Name] = petAggregate;
          }

          Aggregate(PetValues, needPetAccounting, dataPoint, petAggregate, lastTimes, timeRanges, diffs);
        }

        lastTimes[dataPoint.Name] = dataPoint.CurrentTime;
        lastTimes[raidName] = dataPoint.CurrentTime;
        lastTimes[totalName] = dataPoint.CurrentTime;
      }

      UpdateRemaining(RaidValues, needRaidAccounting, lastTimes, timeRanges);
      UpdateRemaining(PlayerPetValues, needTotalAccounting, lastTimes, timeRanges);
      UpdateRemaining(PlayerValues, needPlayerAccounting, lastTimes, timeRanges);
      UpdateRemaining(PetValues, needPetAccounting, lastTimes, timeRanges);

      PopulateRolling(RaidValues);
      PopulateRolling(PlayerPetValues);
      PopulateRolling(PlayerValues);
      PopulateRolling(PetValues);

      Plot(selected);
    }

    private void PopulateRolling(Dictionary<string, List<DataPoint>> data)
    {
      foreach (ref var points in data.Values.ToArray().AsSpan())
      {
        for (var i = 0; i < points.Count; i++)
        {
          var count = 0;
          var total = 0L;
          var beginTime = points[i].CurrentTime;
          for (var j = i; j >= 0; j--)
          {
            if ((beginTime - points[j].CurrentTime) > 5)
            {
              break;
            }

            count++;
            total += points[j].TotalPerSecond;
          }

          points[i].RollingTotal = total;

          if (count > 0)
          {
            points[i].RollingDps = total / count;
          }
        }
      }
    }

    private void Plot(List<PlayerStats> selected = null)
    {
      LastSelected = selected;

      Dictionary<string, List<DataPoint>> workingData = null;

      var selectedLabel = "Selected Player(s)";
      var nonSelectedLabel = " Player(s)";
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
        var names = selected.Select(stats => stats.OrigName).ToList();
        sortedValues = workingData.Values.Where(values =>
        {
          var pass = false;
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

      var yPath = "Avg";
      switch (CurrentChoice)
      {
        case "Aggregate DPS":
        case "Aggregate HPS":
          yPath = "ValuePerSecond";
          break;
        case "Aggregate Damage":
        case "Aggregate Damaged":
        case "Aggregate Healing":
          yPath = "Total";
          break;
        case "Aggregate Av Hit":
        case "Aggregate Av Heal":
          yPath = "Avg";
          break;
        case "Aggregate Crit Rate":
          yPath = "CritRate";
          break;
        case "Aggregate Twincast Rate":
          yPath = "TcRate";
          break;
        case "DPS":
        case "HPS":
          yPath = "TotalPerSecond";
          break;
        case "Rolling DPS":
        case "Rolling HPS":
          yPath = "RollingDps";
          break;
        case "Rolling Damage":
        case "Rolling Healing":
          yPath = "RollingTotal";
          break;
        case "# Attempts":
          yPath = "AttemptsPerSecond";
          break;
        case "# Crits":
          yPath = "CritsPerSecond";
          break;
        case "# Hits":
        case "# Heals":
          yPath = "HitsPerSecond";
          break;
        case "# Twincasts":
          yPath = "TcPerSecond";
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

    private void CreateImageClick(object sender, RoutedEventArgs e) => UIElementUtil.CreateImage(Dispatcher, sfLineChart, titleLabel);

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
          var data = new List<List<object>>();
          var header = new List<string> { "Seconds", choicesList.SelectedValue as string, "Name" };

          foreach (var series in sfLineChart.Series)
          {
            if (series.ItemsSource is List<DataPoint> dataPoints)
            {
              foreach (ref var chartData in dataPoints.ToArray().AsSpan())
              {
                double chartValue = 0;
                switch (CurrentChoice)
                {
                  case "Aggregate DPS":
                  case "Aggregate HPS":
                    chartValue = chartData.ValuePerSecond;
                    break;
                  case "Aggregate Damage":
                  case "Aggregate Damaged":
                  case "Aggregate Healing":
                    chartValue = chartData.Total;
                    break;
                  case "Aggregate Av Hit":
                  case "Aggregate Av Heal":
                    chartValue = chartData.Avg;
                    break;
                  case "Aggregate Crit Rate":
                    chartValue = chartData.CritRate;
                    break;
                  case "Aggregate Twincast Rate":
                    chartValue = chartData.TcRate;
                    break;
                  case "DPS":
                  case "HPS":
                    chartValue = chartData.TotalPerSecond;
                    break;
                  case "Rolling DPS":
                  case "Rolling HPS":
                    chartValue = chartData.RollingDps;
                    break;
                  case "Rolling Damage":
                  case "Rolling Healing":
                    chartValue = chartData.RollingTotal;
                    break;
                  case "# Attempts":
                    chartValue = chartData.AttemptsPerSecond;
                    break;
                  case "# Crits":
                    chartValue = chartData.CritsPerSecond;
                    break;
                  case "# Hits":
                  case "# Heals":
                    chartValue = chartData.HitsPerSecond;
                    break;
                  case "# Twincasts":
                    chartValue = chartData.TcPerSecond;
                    break;
                }

                data.Add(new List<object> { chartData.CurrentTime, Math.Round(chartValue, 2), chartData.Name });
              }
            }
          }

          Clipboard.SetDataObject(TextUtils.BuildCsv(header, data, titleLabel.Content as string));
        }
        catch (ExternalException ex)
        {
          LOG.Error(ex);
        }
      }
    }

    private static void Aggregate(Dictionary<string, List<DataPoint>> theValues,
      Dictionary<string, DataPoint> needAccounting, DataPoint dataPoint, DataPoint aggregate,
      Dictionary<string, double> lastTimes, Dictionary<string, TimeRange> timeRanges, Dictionary<string, double> diffs)
    {
      lastTimes.TryGetValue(aggregate.Name, out var lastTime);

      if (!timeRanges.ContainsKey(aggregate.Name))
      {
        timeRanges[aggregate.Name] = new TimeRange(new TimeSegment(dataPoint.CurrentTime, dataPoint.CurrentTime));
      }

      var diff = diffs[aggregate.Name];
      if (diff > DataManager.FIGHTTIMEOUT)
      {
        timeRanges[aggregate.Name].Add(new TimeSegment(dataPoint.CurrentTime, dataPoint.CurrentTime));
        Insert(aggregate, theValues, timeRanges);
        aggregate.CritsPerSecond = 0;
        aggregate.TcPerSecond = 0;
        aggregate.AttemptsPerSecond = 0;
        aggregate.HitsPerSecond = 0;
        aggregate.TotalPerSecond = 0;

        // is this good? i dont know
        // trying to insert null for time not seen
        var noData = new DataPoint
        {
          Name = aggregate.Name,
          PlayerName = aggregate.PlayerName,
          CurrentTime = lastTime + 6
        };

        Insert(noData, theValues, timeRanges);
        noData.CurrentTime = dataPoint.CurrentTime - 6;
        Insert(noData, theValues, timeRanges);
      }
      else if (diff >= 1)
      {
        timeRanges[aggregate.Name].Add(new TimeSegment(aggregate.CurrentTime, dataPoint.CurrentTime));
        Insert(aggregate, theValues, timeRanges);
        aggregate.CritsPerSecond = 0;
        aggregate.TcPerSecond = 0;
        aggregate.AttemptsPerSecond = 0;
        aggregate.HitsPerSecond = 0;
        aggregate.TotalPerSecond = 0;
      }
      else
      {
        needAccounting[aggregate.Name] = aggregate;
      }

      aggregate.CurrentTime = dataPoint.CurrentTime;
      aggregate.CritsPerSecond += LineModifiersParser.IsCrit(dataPoint.ModifiersMask) ? (uint)1 : 0;
      aggregate.TcPerSecond += LineModifiersParser.IsTwincast(dataPoint.ModifiersMask) ? (uint)1 : 0;
      aggregate.AttemptsPerSecond += 1;
      aggregate.HitsPerSecond += MissTypes.ContainsKey(dataPoint.Type) ? 0 : 1;
      aggregate.TotalPerSecond += dataPoint.Total;
      aggregate.Total += dataPoint.Total;
      aggregate.FightTotal += dataPoint.Total;
      aggregate.FightHits += 1;
      aggregate.FightCritHits += LineModifiersParser.IsCrit(dataPoint.ModifiersMask) ? (uint)1 : 0;
      aggregate.FightTcHits += LineModifiersParser.IsTwincast(dataPoint.ModifiersMask) ? (uint)1 : 0;
    }

    private static void UpdateRemaining(Dictionary<string, List<DataPoint>> chartValues, Dictionary<string, DataPoint> needAccounting,
      Dictionary<string, double> lastTimes, Dictionary<string, TimeRange> timeRanges)
    {
      foreach (ref var remaining in needAccounting.Values.ToArray().AsSpan())
      {
        var lastTime = lastTimes[remaining.Name];
        var lastSegment = timeRanges[remaining.Name].TimeSegments.Last();
        timeRanges[remaining.Name].Add(new TimeSegment(lastSegment.BeginTime, lastTime));
        remaining.CurrentTime = lastTime;
        Insert(remaining, chartValues, timeRanges);
      }

      needAccounting.Clear();
    }

    private static void Insert(DataPoint aggregate, Dictionary<string, List<DataPoint>> chartValues,
      Dictionary<string, TimeRange> timeRanges)
    {
      var newEntry = new DataPoint
      {
        Name = aggregate.Name,
        PlayerName = aggregate.PlayerName,
        CurrentTime = aggregate.CurrentTime,
        Total = aggregate.Total,
        CritsPerSecond = aggregate.CritsPerSecond,
        TcPerSecond = aggregate.TcPerSecond,
        AttemptsPerSecond = aggregate.AttemptsPerSecond,
        HitsPerSecond = aggregate.HitsPerSecond,
        TotalPerSecond = aggregate.TotalPerSecond,
        DateTime = DateUtil.FromDouble(aggregate.CurrentTime)
      };

      var totalSeconds = timeRanges[aggregate.Name].GetTotal();
      newEntry.ValuePerSecond = (long)Math.Round(aggregate.FightTotal / totalSeconds, 2);

      if (aggregate.FightHits > 0)
      {
        newEntry.Avg = (long)Math.Round(Convert.ToDecimal(aggregate.FightTotal) / aggregate.FightHits, 2);
        newEntry.CritRate = Math.Round(Convert.ToDouble(aggregate.FightCritHits) / aggregate.FightHits * 100, 2);
        newEntry.TcRate = Math.Round(Convert.ToDouble(aggregate.FightTcHits) / aggregate.FightHits * 100, 2);
      }

      if (!chartValues.TryGetValue(aggregate.Name, out var playerValues))
      {
        playerValues = new List<DataPoint>();
        chartValues[aggregate.Name] = playerValues;
      }

      if (playerValues.Count > 0 && playerValues.Last() is { } test && test != null)
      {
        if (test.CurrentTime == newEntry.CurrentTime)
        {
          playerValues[^1] = newEntry;
        }
        else if (newEntry.CurrentTime > test.CurrentTime)
        {
          playerValues.Add(newEntry);
        }
      }
      else
      {
        playerValues.Add(newEntry);
      }
    }

    #region IDisposable Support
    private bool disposedValue; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        (Application.Current.MainWindow as MainWindow).EventsThemeChanged -= EventsThemeChanged;
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
