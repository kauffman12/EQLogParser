using Syncfusion.UI.Xaml.Charts;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for DPSChart.xaml
  /// </summary>
  public partial class LineChart : UserControl
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
    private Predicate<object> LastFilter = null;
    private List<List<DataPoint>> LastSortedValues = null;

    public LineChart(List<string> choices, bool includePets = false)
    {
      InitializeComponent();

      CurrentConfig = 0;
      choicesList.ItemsSource = choices;
      choicesList.SelectedIndex = 0;
      CurrentChoice = choicesList.SelectedValue as string;

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
          AddDataPoints(e.Iterator, e.Selected, e.Filter);
          break;
        case "SELECT":
          Plot(e.Selected);
          break;
        case "FILTER":
          Plot(e.Filter);
          break;
      }
    }

    private void AddDataPoints(RecordGroupCollection recordIterator, List<PlayerStats> selected = null, Predicate<object> filter = null)
    {
      Task.Run(() =>
      {
        double lastTime = double.NaN;
        double firstTime = double.NaN;
        Dictionary<string, DataPoint> petData = new Dictionary<string, DataPoint>();
        Dictionary<string, DataPoint> playerData = new Dictionary<string, DataPoint>();
        Dictionary<string, DataPoint> totalPlayerData = new Dictionary<string, DataPoint>();
        Dictionary<string, DataPoint> raidData = new Dictionary<string, DataPoint>();
        Dictionary<string, DataPoint> needTotalAccounting = new Dictionary<string, DataPoint>();
        Dictionary<string, DataPoint> needPlayerAccounting = new Dictionary<string, DataPoint>();
        Dictionary<string, DataPoint> needPetAccounting = new Dictionary<string, DataPoint>();
        Dictionary<string, DataPoint> needRaidAccounting = new Dictionary<string, DataPoint>();

        foreach (var dataPoint in recordIterator)
        {
          double diff = double.IsNaN(lastTime) ? 1 : dataPoint.CurrentTime - lastTime;

          if (double.IsNaN(firstTime) || diff > DataManager.FIGHTTIMEOUT)
          {
            firstTime = dataPoint.CurrentTime;
          }

          var raidName = "Raid";
          if (!raidData.TryGetValue(raidName, out DataPoint raidAggregate))
          {
            raidAggregate = new DataPoint() { Name = raidName };
            raidData[raidName] = raidAggregate;
          }

          Aggregate(raidData, RaidValues, needRaidAccounting, dataPoint, raidAggregate, firstTime, lastTime, diff);

          var playerName = dataPoint.PlayerName ?? dataPoint.Name;
          var petName = dataPoint.PlayerName == null ? null : dataPoint.Name;
          var totalName = playerName + " +Pets";
          if (!totalPlayerData.TryGetValue(totalName, out DataPoint totalAggregate))
          {
            totalAggregate = new DataPoint() { Name = totalName, PlayerName = playerName };
            totalPlayerData[totalName] = totalAggregate;
          }

          Aggregate(totalPlayerData, PlayerPetValues, needTotalAccounting, dataPoint, totalAggregate, firstTime, lastTime, diff);

          if (dataPoint.PlayerName == null)
          {
            if (!playerData.TryGetValue(dataPoint.Name, out DataPoint aggregate))
            {
              aggregate = new DataPoint() { Name = dataPoint.Name };
              playerData[dataPoint.Name] = aggregate;
            }

            Aggregate(playerData, PlayerValues, needPlayerAccounting, dataPoint, aggregate, firstTime, lastTime, diff);
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

            Aggregate(petData, PetValues, needPetAccounting, dataPoint, petAggregate, firstTime, lastTime, diff);
          }

          lastTime = dataPoint.CurrentTime;
        }

        UpdateRemaining(RaidValues, needRaidAccounting, firstTime, lastTime);
        UpdateRemaining(PlayerPetValues, needTotalAccounting, firstTime, lastTime);
        UpdateRemaining(PlayerValues, needPlayerAccounting, firstTime, lastTime);
        UpdateRemaining(PetValues, needPetAccounting, firstTime, lastTime);
        Plot(selected, filter);
      });
    }

    private void Plot(List<PlayerStats> selected = null, Predicate<object> filter = null)
    {
      LastFilter = filter;
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
        sortedValues = workingData.Values.ToList().Where(values => PassFilter(filter, values)).OrderByDescending(values => values.Last().Total).Take(5).ToList();
        label = sortedValues.Count > 0 ? "Top " + sortedValues.Count + nonSelectedLabel : Labels.NODATA;
      }
      else
      {
        List<string> names = selected.Select(stats => stats.OrigName).ToList();
        sortedValues = workingData.Values.ToList().Where(values =>
        {
          bool pass = false;
          var first = values.First();
          if (CurrentPetOrPlayerOption == Labels.PETPLAYEROPTION)
          {
            pass = names.Contains(first.PlayerName) || (HasPets.ContainsKey(first.Name) && names.FirstOrDefault(name => HasPets[first.Name].ContainsKey(name)) != null);
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

      Dispatcher.InvokeAsync(() =>
      {
        Reset();
        titleLabel.Content = label;
        sfLineChart.Series = BuildCollection(sortedValues);
      });
    }

    private ChartSeriesCollection BuildCollection(List<List<DataPoint>> sortedValues)
    {
      ChartSeriesCollection collection = new ChartSeriesCollection();

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

      foreach (var value in sortedValues.ToList())
      {
        var name = value.First().Name;
        name = ((CurrentPetOrPlayerOption == Labels.PETPLAYEROPTION) && !HasPets.ContainsKey(name)) ? name.Split(' ')[0] : name;
        var series = new FastLineBitmapSeries
        {
          Label = name,
          XBindingPath = "DateTime",
          YBindingPath = yPath,
          ItemsSource = value,
          ShowTooltip = true,
          EnableAntiAliasing = true
        };
        collection.Add(series);
      }

      return collection;
    }

    private void CreateImageClick(object sender, RoutedEventArgs e) => Helpers.CopyImage(Dispatcher, sfLineChart, titleLabel);

    private bool PassFilter(Predicate<object> filter, List<DataPoint> values)
    {
      bool pass = filter == null;

      if (!pass)
      {
        var first = values.First();
        if (CurrentPetOrPlayerOption == Labels.PETPLAYEROPTION)
        {
          pass = filter(first.PlayerName) || (HasPets.ContainsKey(first.Name) && filter(HasPets[first.Name]));
        }
        else
        {
          pass = filter(first.Name);
        }
      }

      return pass;
    }

    private void Plot(Predicate<object> filter)
    {
      if (RaidValues.Count > 0)
      {
        Plot(LastSelected, filter);
      }
      else
      {
        Reset();
      }
    }

    private void Plot(List<PlayerStats> selected)
    {
      if (RaidValues.Count > 0)
      {
        // handling case where chart can be updated twice
        // when toggling bane and selection is lost
        if (!(selected.Count == 0 && LastSelected == null))
        {
          Plot(selected, LastFilter);
        }
      }
      else
      {
        Reset();
      }
    }

    private string GetLabelFormat(double value)
    {
      string dateTimeString;
      DateTime dt = value > 0 ? new DateTime((long)(value * TimeSpan.FromSeconds(1).Ticks)) : new DateTime();
      dateTimeString = dt.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
      return dateTimeString;
    }

    private void Reset() => titleLabel.Content = Labels.NODATA;

    private void ListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (PlayerPetValues.Count > 0)
      {
        CurrentChoice = choicesList.SelectedValue as string;
        CurrentConfig = choicesList.SelectedIndex;
        CurrentPetOrPlayerOption = petOrPlayerList.SelectedValue as string;
        Plot(LastSelected, LastFilter);
      }
    }

    private void CopyCsvClick(object sender, RoutedEventArgs e)
    {
      if (LastSortedValues != null)
      {
        try
        {
          List<string> header = new List<string> { "Seconds", choicesList.SelectedValue as string, "Name" };

          var data = new List<List<object>>();
          LastSortedValues.Where(values => LastFilter == null || LastFilter(values.First())).ToList().ForEach(sortedValue =>
          {
            foreach (var chartData in sortedValue)
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

              data.Add(new List<object> { chartData.CurrentTime, chartValue, chartData.Name });
              Clipboard.SetDataObject(TextFormatUtils.BuildCsv(header, data, titleLabel.Content as string));
            }
          });
        }
        catch (ExternalException ex)
        {
          LOG.Error(ex);
        }
      }
    }

    private static void Aggregate(Dictionary<string, DataPoint> playerData, Dictionary<string, List<DataPoint>> theValues,
      Dictionary<string, DataPoint> needAccounting, DataPoint dataPoint, DataPoint aggregate, double firstTime, double lastTime, double diff)
    {
      if (diff > DataManager.FIGHTTIMEOUT)
      {
        UpdateRemaining(theValues, needAccounting, firstTime, lastTime);
        foreach (var value in playerData.Values.ToList())
        {
          value.RollingTotal = 0;
          value.RollingCritHits = 0;
          value.RollingHits = 0;
          value.CurrentTime = lastTime + 6;
          Insert(value, theValues);
          value.CurrentTime = firstTime - 6;
          Insert(value, theValues);
          value.DateTime = DateUtil.FromDouble(value.CurrentTime);
        }
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
        UpdateRemaining(theValues, needAccounting, firstTime, dataPoint.CurrentTime, aggregate.Name);
      }
      else
      {
        needAccounting[aggregate.Name] = aggregate;
      }
    }

    private static void UpdateRemaining(Dictionary<string, List<DataPoint>> chartValues, Dictionary<string, DataPoint> needAccounting,
      double firstTime, double currentTime, string ignore = null)
    {
      foreach (ref var remaining in needAccounting.Values.ToArray().AsSpan())
      {
        if (ignore != remaining.Name)
        {
          if (remaining.BeginTime != firstTime)
          {
            remaining.BeginTime = firstTime;
            remaining.RollingTotal = 0;
            remaining.RollingHits = 0;
            remaining.RollingCritHits = 0;
          }

          remaining.CurrentTime = currentTime;
          remaining.DateTime = DateUtil.FromDouble(currentTime);
          Insert(remaining, chartValues);
        }
      }

      needAccounting.Clear();
    }

    private static void Insert(DataPoint aggregate, Dictionary<string, List<DataPoint>> chartValues)
    {
      DataPoint newEntry = new DataPoint
      {
        Name = aggregate.Name,
        PlayerName = aggregate.PlayerName,
        CurrentTime = aggregate.CurrentTime,
        DateTime = DateUtil.FromDouble(aggregate.CurrentTime),
        Total = aggregate.Total
      };

      double totalSeconds = aggregate.CurrentTime - aggregate.BeginTime + 1;
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
  }
}
