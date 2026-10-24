using log4net;
using Syncfusion.UI.Xaml.Charts;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  public partial class LineChart
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly Dictionary<string, bool> MissTypes = new()
    {
      { Labels.Absorb, true },
      { Labels.Block, true } ,
      { Labels.Dodge, true },
      { Labels.Parry, true },
      { Labels.Riposte, true },
      { Labels.Invulnerable, true },
      { Labels.Miss, true }
    };

    /* A redraw over this much of a second is worth a line; a healthy one with the top five players takes tens of milliseconds. */
    private const double SlowUpdateMs = 300;

    /* What the last redraw drew. Only read to say "1,289 ms FOR what" on a slow pass - the milliseconds alone are not actionable. */
    private int _plotLines;
    private int _plotPoints;

    private readonly Dictionary<string, List<DataPoint>> _playerPetValues = [];
    private readonly Dictionary<string, List<DataPoint>> _playerValues = [];
    private readonly Dictionary<string, List<DataPoint>> _petValues = [];
    private readonly Dictionary<string, List<DataPoint>> _raidValues = [];
    /* Which pet names belong to which player, used only as a set - the byte it used to carry was never read. */
    private readonly Dictionary<string, HashSet<string>> _hasPets = [];

    /* "player +Pets" is a pure function of the player name, and this loop asked for it millions of times per redraw; see AddDataPoints. */
    private const string PetTotalSuffix = " +Pets";
    private string _petTotalKey;
    private string _petTotalName;
    private string _currentChoice;
    private string _currentViewOption;
    private int _currentTopCount = 5;
    private List<PlayerStats> _lastSelected;
    private List<GroupEntry> _selectedGroups;
    private readonly ViewOptionRegistry _viewOptions;

    public LineChart(IEnumerable<string> choices, bool includePets = false)
    {
      InitializeComponent();

      choicesList.ItemsSource = choices;
      choicesList.SelectedIndex = 0;
      _currentChoice = choicesList.SelectedValue as string;
      dateLabel.FontSize = ThemeConfig.CurrentFontSize - 1;
      numLabel.FontSize = ThemeConfig.CurrentFontSize;
      ThemeConfig.EventsThemeChanged += EventsThemeChanged;

      _viewOptions = new ViewOptionRegistry();
      if (includePets)
      {
        _viewOptions.AddOption(Labels.ByGroupOption, OnViewOptionChanged);
        _viewOptions.AddOption(Labels.PetPlayerOption, OnViewOptionChanged);
        _viewOptions.AddOption(Labels.PlayerOption, OnViewOptionChanged);
        _viewOptions.AddOption(Labels.PetOption, OnViewOptionChanged);
        _viewOptions.AddOption(Labels.RaidOption, OnViewOptionChanged);
      }
      else
      {
        _viewOptions.AddOption(Labels.PlayerOption, OnViewOptionChanged);
        _viewOptions.AddOption(Labels.RaidOption, OnViewOptionChanged);
      }
      petOrPlayerList.ItemsSource = _viewOptions.GetDisplayNames();
      petOrPlayerList.SelectedIndex = 0;

      // Load saved top count setting or default to 5
      _currentTopCount = ConfigUtil.GetSettingAsInteger($"Line{this.GetType().Name}TopCount", 5);
      topCountList.SelectedIndex = _currentTopCount == 5 ? 0 : 1;

      Reset();
    }

    internal void Clear()
    {
      _playerPetValues.Clear();
      _playerValues.Clear();
      _petValues.Clear();
      _raidValues.Clear();
      _hasPets.Clear();
      _selectedGroups = null;

      Reset();
    }

    /*
     * What the LAST applied UPDATE answered -- the key is built and compared by ChartUpdateQuestion (Core, where the terms of a question are
     * pinned by tests): data generation, selection, group selection, view option, top count. Nothing else enters a walk, so nothing else belongs
     * in a comparison; dropdown moves redraw on their own through ListSelectionChanged from aggregates already in hand, which is why a skipped
     * UPDATE can only ever have been asking to redraw what is already on screen.
     */
    private string _lastUpdateKey;

    /* True while the chart holds the aggregates that key describes: with no data behind it, there is nothing to skip on. */
    private bool _hasAppliedData;

    /* Set when a pass was dropped as a duplicate, so the completion line says why it drew nothing. */
    private bool _skippedDuplicate;

    internal void HandleUpdateEvent(DataPointEvent e)
    {
      var action = e.Action;
      var watch = Stopwatch.StartNew();

      _skippedDuplicate = false;
      _plotLines = 0;
      _plotPoints = 0;

      try
      {
        switch (action)
        {
          case "CLEAR":
            Clear();
            _lastUpdateKey = null;
            _hasAppliedData = false;
            break;
          case "UPDATE":
            /*
             * The expensive door. A summary pane fires "UPDATE" on its OWN interactions -- selecting rows, changing a view option -- so one
             * set of clicks can ask the same question several times over the same records: measured on a field run, two `DamageChart UPDATE`
             * passes over 4,660,915 records drawing the identical answer (5 lines, 36,163 points), 1,589 ms and 1,107 ms, with no stats build
             * between them. An unstamped event (generation < 0) is never skipped: unknown content always walks.
             */
            // The view option is resolved FIRST because it belongs to the question: a pass that auto-selects an option draws with it, so two
            // events must be compared on the option they would both end up using. Changing it redraws through ListSelectionChanged anyway --
            // from the aggregates already in hand -- so resolving it before a possible skip cannot leave the screen showing something else.
            _selectedGroups = e.SelectedGroups;
            AutoSelectViewOption(e.SelectedGroups, e.Selected);

            var key = ChartUpdateQuestion.KeyOf(e, _currentViewOption, _currentTopCount);
            if (ChartUpdateQuestion.IsRepeat(key, _lastUpdateKey, _hasAppliedData))
            {
              _skippedDuplicate = true;
              // Debug, not Info: a skip is the healthy case (a repeated gesture, a duplicate announce), so printing it at
              // all costs nothing and printing it in a player's log would teach them to wonder. The line exists for the
              // "three asks arrived" vs "one ask, three builds" question, which is asked with Debug on.
              Log.Debug($"chart.update skipped: {GetType().Name} was asked the question it just answered "
                       + $"(data generation {e.DataGeneration}, nothing rebuilt since)");
            }
            else
            {
              Clear();
              _selectedGroups = e.SelectedGroups;
              AddDataPoints(e.Iterator, e.Selected);
              _lastUpdateKey = key;
              _hasAppliedData = true;
            }
            break;
          case "SELECT":
            _selectedGroups = e.SelectedGroups;
            AutoSelectViewOption(e.SelectedGroups, e.Selected);
            PlotSelected(e.Selected);
            break;
        }
      }
      finally
      {
        /*
         * One line, and only for a pass a person could feel. The sizes go on it because "1,289 ms" is diagnostic only next to "…for 7 lines
         * and 84,000 points": the milliseconds say whether, the numbers say whether the fix is to draw less or to build somewhere else.
         * The per-phase breakdown this used to print went with the perf layer; what survives is the question it was assembled to answer.
         */
        if (watch.Elapsed.TotalMilliseconds >= SlowUpdateMs)
        {
          Log.Debug($"chart.redraw slow: {GetType().Name} {action} took {watch.Elapsed.TotalMilliseconds:F0} ms"
                    + $" -> {_plotLines} lines, {_plotPoints} points"
                    + (_skippedDuplicate ? " | duplicate of the pass already drawn - nothing re-walked" : ""));
        }
      }
    }

    private void AutoSelectViewOption(List<GroupEntry> selectedGroups, List<PlayerStats> selected)
    {
      var hasGroups = selectedGroups != null && selectedGroups.Count > 0;

      if (hasGroups)
      {
        // Groups selected — switch to group view
        // (selected may also have member players expanded from groups, so check groups first)
        var idx = _viewOptions.TrySetSelectedByName(Labels.ByGroupOption);
        if (idx >= 0) petOrPlayerList.SelectedIndex = idx;
      }
      else
      {
        // No groups — switch to player + pets view (whether players selected or nothing)
        var idx = _viewOptions.TrySetSelectedByName(Labels.PetPlayerOption);
        if (idx >= 0) petOrPlayerList.SelectedIndex = idx;
      }
    }

    private void EventsThemeChanged(string _)
    {
      dateLabel.FontSize = ThemeConfig.CurrentFontSize - 1;
      numLabel.FontSize = ThemeConfig.CurrentFontSize;
    }

    private void AddDataPoints(RecordGroupCollection recordIterator, List<PlayerStats> selected = null)
    {
      var petData = new Dictionary<string, ChartSeriesBuilder>();
      var playerData = new Dictionary<string, ChartSeriesBuilder>();
      var totalPlayerData = new Dictionary<string, ChartSeriesBuilder>();
      var raid = new ChartSeriesBuilder("Raid");

      foreach (var dataPoint in recordIterator)
      {
        var playerName = dataPoint.PlayerName ?? dataPoint.Name;

        /* Rebuilt only when the name changes rather than allocated per record: a redraw of a big parse made 4.66M of these strings, and the GC
           cost is charged to the whole application, not just to this walk. */
        if (_petTotalKey != playerName)
        {
          _petTotalKey = playerName;
          _petTotalName = playerName + PetTotalSuffix;
        }

        var totalName = _petTotalName;

        /* Tested once for the record instead of once per series: two bit tests and a string lookup that four series were each repeating. */
        var isCrit = LineModifiersParser.IsCrit(dataPoint.ModifiersMask);
        var isTwincast = LineModifiersParser.IsTwincast(dataPoint.ModifiersMask);
        var isHit = !MissTypes.ContainsKey(dataPoint.Type);

        raid.Add(dataPoint, isCrit, isTwincast, isHit);

        if (!totalPlayerData.TryGetValue(totalName, out var totalAggregate))
        {
          totalAggregate = new ChartSeriesBuilder(totalName, playerName);
          totalPlayerData[totalName] = totalAggregate;
        }

        totalAggregate.Add(dataPoint, isCrit, isTwincast, isHit);

        if (dataPoint.PlayerName == null)
        {
          if (!playerData.TryGetValue(dataPoint.Name, out var aggregate))
          {
            aggregate = new ChartSeriesBuilder(dataPoint.Name, dataPoint.Name);
            playerData[dataPoint.Name] = aggregate;
          }

          aggregate.Add(dataPoint, isCrit, isTwincast, isHit);
        }
        else
        {
          if (!_hasPets.TryGetValue(totalName, out var value))
          {
            value = [];
            _hasPets[totalName] = value;
          }

          value.Add(dataPoint.Name);
          if (!petData.TryGetValue(dataPoint.Name, out var petAggregate))
          {
            petAggregate = new ChartSeriesBuilder(dataPoint.Name, playerName);
            petData[dataPoint.Name] = petAggregate;
          }

          petAggregate.Add(dataPoint, isCrit, isTwincast, isHit);
        }
      }

      raid.Finish();
      if (raid.Points.Count > 0) _raidValues["Raid"] = raid.Points;
      FinishSeries(totalPlayerData, _playerPetValues);
      FinishSeries(playerData, _playerValues);
      FinishSeries(petData, _petValues);

      Plot(selected);
    }

    private void Plot(List<PlayerStats> selected)
    {
      _lastSelected = selected;

      Dictionary<string, List<DataPoint>> workingData;

      var selectedLabel = "Selected Player(s)";
      var nonSelectedLabel = " Player(s)";
      var selectedName = _viewOptions?.GetSelectedOptionName();
      switch (selectedName)
      {
        case Labels.PetPlayerOption:
          workingData = _playerPetValues;
          selectedLabel = "Selected Player +Pets(s)";
          nonSelectedLabel = " Player +Pets(s)";
          break;
        case Labels.PlayerOption:
          workingData = _playerValues;
          break;
        case Labels.PetOption:
          workingData = _petValues;
          selectedLabel = "Selected Pet(s)";
          nonSelectedLabel = " Pet(s)";
          break;
        case Labels.ByGroupOption:
          workingData = _playerPetValues;
          selectedLabel = "Selected Group(s)";
          break;
        case Labels.RaidOption:
          workingData = _raidValues;
          break;
        default:
          workingData = [];
          break;
      }

      string label;
      List<List<DataPoint>> sortedValues;
      if (selectedName == Labels.RaidOption)
      {
        sortedValues = [.. workingData.Values];
        label = sortedValues.Count > 0 ? "Raid" : Labels.NoData;
      }
      else if (selectedName == Labels.ByGroupOption)
      {
        sortedValues = AggregateGroups(workingData);
        label = sortedValues.Count > 0 ? selectedLabel : Labels.NoData;
      }
      else if (selected == null || selected.Count == 0)
      {
        sortedValues = [.. workingData.Values.OrderByDescending(values => values[^1].Total).Take(_currentTopCount)];
        label = sortedValues.Count > 0 ? "Top " + sortedValues.Count + nonSelectedLabel : Labels.NoData;
      }
      else
      {
        var names = selected.Select(stats => stats.OrigName).ToHashSet();
        sortedValues = workingData.Values.Where(values =>
        {
          var pass = false;
          var first = values.First();
          if (selectedName == Labels.PetPlayerOption)
          {
            pass = names.Contains(first.PlayerName) || (_hasPets.ContainsKey(first.Name) &&
            names.FirstOrDefault(name => _hasPets[first.Name].Contains(name)) is not null);
          }
          else if (selectedName == Labels.PlayerOption)
          {
            pass = names.Contains(first.Name);
          }
          else if (selectedName == Labels.PetOption)
          {
            pass = names.Contains(first.Name) || names.Contains(first.PlayerName);
          }
          return pass;
        }).Take(10).ToList();

        label = sortedValues.Count > 0 ? selectedLabel : Labels.NoData;
      }

      if (label != Labels.NoData)
      {
        label += " " + _currentChoice;
      }

      // Show/hide the top count dropdown based on whether data is selected
      topCountList.Visibility = (selected == null || selected.Count == 0) && selectedName != Labels.ByGroupOption
        ? Visibility.Visible : Visibility.Collapsed;

      Reset();

      titleLabel.Content = label;

      var series = BuildCollection(sortedValues);

      /* Handing the control a whole new collection is the one line in this file that is not ours. */
      sfLineChart.Series = series;
    }

    /// <summary>
    /// Aggregates per-player data series into group-level series.
    /// Each group becomes a single line with values summed across all members at each time tick.
    /// </summary>
    private List<List<DataPoint>> AggregateGroups(Dictionary<string, List<DataPoint>> playerData)
    {
      return ChartAggregation.AggregateGroups(playerData, _selectedGroups);
    }

    private ChartSeriesCollection BuildCollection(List<List<DataPoint>> sortedValues)
    {
      var collection = new ChartSeriesCollection();

      var yPath = "Avg";
      switch (_currentChoice)
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

      /* What the control was asked to draw, since line count and point count are what its own work scales with. */
      var points = 0;
      var rolling = yPath is "RollingDps" or "RollingTotal";
      var through = _raidValues.TryGetValue("Raid", out var raid) && raid.Count > 0
        ? raid[^1].CurrentTime : double.NegativeInfinity;

      foreach (var value in CollectionsMarshal.AsSpan(sortedValues))
      {
        // Rolling-only points show quiet tails through the selected data edge, never through the PC clock.
        var plotted = rolling ? ChartRollingSeries.Build(value, through) : value;
        points += plotted.Count;

        var name = value.First().Name;
        name = ((_currentViewOption == Labels.PetPlayerOption) && !_hasPets.ContainsKey(name)) ? name.Split(' ')[0] : name;
        var series = new FastLineSeries
        {
          Label = name,
          XBindingPath = "DateTime",
          YBindingPath = yPath,
          ItemsSource = plotted,
          ShowTooltip = false,
          /*
           * Nobody ever asked for a tween, and the default one is not free: during a live tail the chart's own data pass measured 2-6 ms
           * while the framework took **404-1,029 ms** (avg 427) with what it was handed, 3.6 times a second — a gap posted at ContextIdle is
           * the UI thread being busy (field log 2026-10-08: about one freeze per second, worst 547 ms, for a plot of 5 lines and 137 points).
           * Series are rebuilt on every update here, so the animation was replayed from scratch
           * each time. A chart whose numbers move is the one thing you do NOT want sliding into place twice a second.
           */
          EnableAnimation = false,
        };

        collection.Add(series);
      }

      _plotLines = collection.Count;
      _plotPoints = points;

      return collection;
    }

    private async void CreateImageClick(object sender, RoutedEventArgs e) => await UiElementUtil.CreateImage(Dispatcher, sfLineChart, titleLabel);

    private void PlotSelected(List<PlayerStats> selected)
    {
      if (_raidValues.Count > 0)
      {
        // handling case where chart can be updated twice
        // when toggling bane and selection is lost
        if (!(selected.Count == 0 && _lastSelected == null))
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
      titleLabel.Content = Labels.NoData;
    }

    private void ListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      _currentChoice = choicesList.SelectedValue as string;
      _currentViewOption = petOrPlayerList.SelectedValue as string ?? "";
      if (_viewOptions != null)
      {
        _viewOptions.OnSelectionChanged(petOrPlayerList.SelectedIndex);
      }

      if (_playerPetValues.Count == 0)
        return;

      Plot(_lastSelected);
    }

    private void OnViewOptionChanged(int index)
    {
      _currentViewOption = _viewOptions?.GetSelectedOptionName() ?? "";
    }

    private void TopCountSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      var current = _currentTopCount;
      if (topCountList.SelectedIndex == 0)
      {
        _currentTopCount = 5;
      }
      else
      {
        _currentTopCount = 10;
      }

      // save if changed
      if (current != _currentTopCount && _currentTopCount > 0)
      {
        ConfigUtil.SetSetting($"Line{GetType().Name}TopCount", _currentTopCount);
      }

      if (_playerPetValues.Count == 0)
        return;

      Plot(_lastSelected);
    }

    private void CopyCsvClick(object sender, RoutedEventArgs e)
    {
      if (sfLineChart.Series.Count > 0)
      {
        var data = new List<List<object>>();
        var header = new List<string> { "Seconds", choicesList.SelectedValue as string, "Name" };

        foreach (var series in sfLineChart.Series)
        {
          if (series.ItemsSource is List<DataPoint> dataPoints)
          {
            for (var i = 0; i < dataPoints.Count; i++)
            {
              var chartData = dataPoints[i];
              double chartValue = 0;
              switch (_currentChoice)
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

              data.Add([chartData.CurrentTime, Math.Round(chartValue, 2), chartData.Name]);
            }
          }
        }

        if (titleLabel.Content is string title)
        {
          UiUtil.SetClipboardText(TextUtils.BuildTsv(header, data, title));
        }
      }
    }

    private static void FinishSeries(Dictionary<string, ChartSeriesBuilder> builders,
      Dictionary<string, List<DataPoint>> values)
    {
      foreach (var (name, builder) in builders)
      {
        builder.Finish();
        values[name] = builder.Points;
      }
    }
  }
}
