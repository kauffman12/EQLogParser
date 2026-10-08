using FontAwesome5;
using log4net;
using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace EQLogParser
{
  public partial class HealingSummary : IDocumentContent
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private readonly DispatcherTimer _selectionTimer;
    private bool _ready;

    public HealingSummary()
    {
      InitializeComponent();

      // One builder for the four identity words, shared with the other summaries and the fight list (IdentityVerdictMenu).
      IdentityVerdictMenu.Populate(menuItemSetKind, ApplyVerdict);

      var list = EQDataStore.Instance.GetClassList();
      list.Insert(0, Resource.ANY_CLASS);
      classesList.ItemsSource = list;
      classesList.SelectedIndex = 0;

      CreateSpellCountMenuItems(menuItemShowSpellCounts, DataGridSpellCountsByClassClick, DataGridShowSpellCountsClick);
      CreateClassMenuItems(menuItemShowSpellCasts, DataGridSpellCastsByClassClick, false, DataGridShowSpellCastsClick);
      CreateClassMenuItems(menuItemShowBreakdown, DataGridShowBreakdownByClassClick, false, DataGridShowBreakdownClick);
      CreateClassMenuItems(menuItemSetPlayerClass, DataGridSetPlayerClassClick, true);

      // call after everything else is initialized
      InitSummaryTable(title, dataGrid, selectedColumns, classesList);
      dataGrid.GridCopyContent += DataGridCopyContent;

      _selectionTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 500) };
      _selectionTimer.Tick += (_, _) =>
      {
        if (prog.Icon == EFontAwesomeIcon.Solid_HourglassStart)
        {
          prog.Icon = EFontAwesomeIcon.Solid_HourglassHalf;
        }
        else if (prog.Icon == EFontAwesomeIcon.Solid_HourglassHalf)
        {
          prog.Icon = EFontAwesomeIcon.Solid_HourglassEnd;
        }
        else if (prog.Icon == EFontAwesomeIcon.Solid_HourglassEnd)
        {
          prog.Visibility = Visibility.Hidden;
          EventsHealingSummaryOptionsChanged("hourglass after time change");
          _selectionTimer.Stop();
        }
      };
    }

    internal override void ShowBreakdown(List<PlayerStats> selected)
    {
      if (selected?.Count > 0)
      {
        if (SyncFusionUtil.OpenWindow(out var window, typeof(HealBreakdown), "healingBreakdownWindow", "Healing Breakdown") && window.Content is HealBreakdown { } breakdown)
        {
          breakdown.Init(CurrentStats, selected);
        }
      }
    }

    internal override void UpdateDataGridMenuItems()
    {
      var selectedName = "Unknown";

      Dispatcher.InvokeAsync(() =>
      {
        if (CurrentStats != null && CurrentStats.StatsList.Count > 0 && dataGrid.View != null)
        {
          menuItemShowSpellCasts.IsEnabled = menuItemShowBreakdown.IsEnabled = menuItemShowSpellCounts.IsEnabled = true;
          menuItemShowHealingLog.IsEnabled = dataGrid.SelectedItems.Count == 1;
          copyHealParseToEQClick.IsEnabled = copyOptions.IsEnabled = true;
          copyTopHealsParseToEQClick.IsEnabled = (dataGrid.SelectedItems.Count == 1) && (dataGrid.SelectedItem as PlayerStats)?.SubStats?.Count > 0;
          menuItemShowHealingTimeline.IsEnabled = dataGrid.SelectedItems.Count == 1 || dataGrid.SelectedItems.Count == 2;

          // default before making check
          menuItemShowDeathLog.IsEnabled = false;
          // The Assign item and the Set cascade are enabled per selection (DamageSummary says the law: exactly one row, or they grey out).

          if (dataGrid.SelectedItem is PlayerStats playerStats && dataGrid.SelectedItems.Count == 1)
          {
            /*
             * Third pane, same two questions through the same seam (docs/DesignNotes.md → "The one seam that answers").
             * No IsOneOfUsOrMerc here: this menu has no "assign as pet of" item, so the pair question is not asked — and
             * it is NOT replaced with the plain one either, which would have quietly treated mercs as players somewhere
             * nobody requested.
             */
            menuItemShowDeathLog.IsEnabled = !string.IsNullOrEmpty(playerStats.Special) && playerStats.Special.Contains("X");
            selectedName = playerStats.OrigName;
          }

          EnableClassMenuItems(menuItemShowBreakdown, dataGrid, CurrentStats.UniqueClasses);
          EnableClassMenuItems(menuItemShowSpellCasts, dataGrid, CurrentStats?.UniqueClasses);
          EnableClassMenuItems(menuItemShowSpellCounts, dataGrid, CurrentStats?.UniqueClasses);
        }
        else
        {
          menuItemShowBreakdown.IsEnabled = copyOptions.IsEnabled =
          menuItemShowHealingLog.IsEnabled = menuItemShowSpellCounts.IsEnabled = copyHealParseToEQClick.IsEnabled =
            menuItemShowSpellCasts.IsEnabled = menuItemShowHealingTimeline.IsEnabled = false;
        }

        menuItemSetPlayerClass.Header = $"Assign Default Class for {selectedName}";

        // Exactly one selected row, or these verbs sleep (IdentityVerdictMenu.Present); the class-assign item follows it.
        var verdictName = SelectedVerdictName();
        menuItemSetPlayerClass.IsEnabled = verdictName != null;
        IdentityVerdictMenu.Present(menuItemSetKind, verdictName);
      });
    }

    private void CopyToEqClick(object sender, RoutedEventArgs e) => MainActions.CopyToEqClick(Labels.HealParse);
    private void CopyTopHealsToEqClick(object sender, RoutedEventArgs e) => MainActions.CopyToEqClick(Labels.TopHealParse);
    private void DataGridSelectionChanged(object sender, GridSelectionChangedEventArgs e) => DataGridSelectionChanged();

    /*
     * The ONE name the identity verbs act on: exactly one selected player row (group headers select nothing), otherwise null — which is what
     * greys the menu. Refusing a multi-selection is the operator's rule, not a missing feature: a verdict re-routes sides, rows and +Pets
     * folding for every name it covers, so a ctrl-click block would rewrite routing across rows nobody inspected — and there is no undo,
     * identity-overrides.txt keeps only the latest word. No batch path is kept "for later" either: an unreachable one drifts into reachable.
     */
    private string SelectedVerdictName()
      => dataGrid.SelectedItems.Count == 1 && dataGrid.SelectedItem is PlayerStats stats && stats is not GroupEntry && !string.IsNullOrEmpty(stats.OrigName)
        ? stats.OrigName
        : null;

    /*
     * The cascade's four picks (greyed unless exactly one row is selected). Written to identity-overrides.txt + the ledger row dropped, then
     * re-derived (IdentityVerdictMenu.Write): a verdict is a new READING of the facts, so the grid repaints from the next pass rather than
     * being patched here.
     */
    private void ApplyVerdict(IdentityKind kind) => IdentityVerdictMenu.Write(SelectedVerdictName(), kind);

    private void DataGridCopyContent(object sender, GridCopyPasteEventArgs e)
    {
      if (AppSettings.IsMapSendToEqEnabled && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.IsKeyDown(Key.C))
      {
        e.Handled = true;
        CopyToEqClick(sender, null);
      }
    }

    private async void DataGridHealingLogClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItems?.Count > 0)
      {
        if (SyncFusionUtil.OpenWindow(out var log, typeof(HitLogViewer), "healingLogWindow", "Healing Log") && log.Content is HitLogViewer { } viewer)
        {
          await viewer.InitAsync(CurrentStats, dataGrid.SelectedItems.Cast<PlayerStats>().First(), CurrentGroups);
        }
      }
    }

    private void DataGridDeathLogClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItems?.Count > 0)
      {
        if (SyncFusionUtil.OpenWindow(out var log, typeof(DeathLogViewer), "deathLogWindow", "Death Log"))
        {
          (log.Content as DeathLogViewer)?.Init(CurrentStats, dataGrid.SelectedItems.Cast<PlayerStats>().First());
        }
      }
    }

    private void DataGridHealingTimelineClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItems.Count > 0)
      {
        if (SyncFusionUtil.OpenWindow(out var timeline, typeof(Timeline), "healingTimeline", "Healing Timeline"))
        {
          ((Timeline)timeline.Content).Init(CurrentStats, [.. dataGrid.SelectedItems.Cast<PlayerStats>()], CurrentGroups, 2);
        }
      }
    }

    private void EventsClearedActiveData(bool cleared) => ClearData();

    private void ClearData()
    {
      CurrentStats = null;
      dataGrid.ItemsSource = NoResultsList;
      title.Content = Labels.NoNpcs;
    }

    private void EventsGenerationStatus(StatsGenerationEvent e)
    {
      Dispatcher.InvokeAsync(() =>
      {
        switch (e.State)
        {
          case "STARTED":
            title.Content = "Calculating HPS...";
            dataGrid.ItemsSource = NoResultsList;
            break;
          case "COMPLETED":
            CurrentStats = e.CombinedStats;
            CurrentGroups = e.Groups;

            if (CurrentStats == null)
            {
              title.Content = Labels.NoData;
              maxTimeChooser.MaxValue = 0;
              minTimeChooser.MaxValue = 0;
            }
            else
            {
              // update min/max time
              maxTimeChooser.MaxValue = Convert.ToInt64(CurrentStats.RaidStats.MaxTime);
              if (maxTimeChooser.MaxValue > 0)
              {
                maxTimeChooser.MinValue = 1;
              }
              maxTimeChooser.Value = Convert.ToInt64(CurrentStats.RaidStats.TotalSeconds + CurrentStats.RaidStats.MinTime);
              minTimeChooser.MaxValue = Convert.ToInt64(CurrentStats.RaidStats.MaxTime);
              minTimeChooser.Value = Convert.ToInt64(CurrentStats.RaidStats.MinTime);

              title.Content = CurrentStats.FullTitle;
              dataGrid.ItemsSource = CurrentStats.StatsList;
            }

            if (e.Limited)
            {
              title.Content += " (Not All Healing Opts Chosen)";
            }

            UpdateDataGridMenuItems();
            break;
          case "NONPC":
          case "NODATA":
            CurrentStats = null;
            maxTimeChooser.MaxValue = 0;
            minTimeChooser.MaxValue = 0;
            title.Content = e.State == "NONPC" ? Labels.NoNpcs : Labels.NoData;
            UpdateDataGridMenuItems();
            break;
        }

        // always stop
        _selectionTimer.Stop();
        prog.Visibility = Visibility.Hidden;
      });
    }

    private void ItemsSourceChanged(object sender, GridItemsSourceChangedEventArgs e)
    {
      if (dataGrid.View != null)
      {
        dataGrid.View.Filter = stats =>
        {
          string className = null;
          if (stats is PlayerStats playerStats)
          {
            className = playerStats.ClassName;
          }

          return SelectedClasses.Count == 16 || SelectedClasses.Contains(className);
        };

        if (dataGrid.SelectedItems.Count > 0)
        {
          dataGrid.SelectedItems.Clear();
        }

        dataGrid.View.RefreshFilter();
      }
    }

    private void TimeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
      if (dataGrid.ItemsSource != null)
      {
        _selectionTimer.Stop();
        _selectionTimer.Start();

        prog.Icon = EFontAwesomeIcon.Solid_HourglassStart;
        prog.Visibility = Visibility.Visible;
      }
    }

    private void EventsChartOpened(string name)
    {
      if (name == "Healing")
      {
        var selected = GetSelectedStats();
        HealingStatsBuilder.Instance.FireChartEvent("UPDATE", selected);
      }
    }

    internal override void FireSelectionChangedEvent(List<PlayerStats> selected)
    {
      Dispatcher.InvokeAsync(() =>
      {
        var selectionChanged = new PlayerStatsSelectionChangedEventArgs();
        selectionChanged.Selected.AddRange(selected);
        selectionChanged.CurrentStats = CurrentStats;
        MainActions.FireHealingSelectionChanged(selectionChanged);
      });
    }

    /*
     * This pane's own door into the healing builder, bypassing MainWindow's single-flight gate like the other summary panes'
     * (see StatsBuildTrace: the label is what makes a duplicated build attributable). Note what a rebuild WITHOUT options.Heals means:
     * the whole record store, not the clicked selection — so when this line names a door, the healing board on screen stopped describing
     * the selection. That trade is legacy's and unchanged here; the label only makes it visible.
     */
    private void EventsHealingSummaryOptionsChanged(string option = null)
    {
      var statOptions = new GenerateStatsOptions
      {
        Source = $"healing pane options [{option ?? "time window"}]",
        MinSeconds = (long)minTimeChooser.Value,
        MaxSeconds = ((long)maxTimeChooser.Value > 0) ? (long)maxTimeChooser.Value : -1
      };

      if (statOptions.MinSeconds < statOptions.MaxSeconds || statOptions.MaxSeconds == -1)
      {
        _ = Task.Run(() => HealingStatsBuilder.Instance.RebuildTotalStats(statOptions)).ContinueWith(t =>
          Log.Error($"Problem building healing stats.", t.Exception), TaskContinuationOptions.OnlyOnFaulted);
      }
    }

    private void ContentLoaded(object sender, RoutedEventArgs e)
    {
      if (VisualParent != null && !_ready)
      {
        HealingStatsBuilder.Instance.EventsGenerationStatus += EventsGenerationStatus;
        CombatEvents.ActiveDataCleared += EventsClearedActiveData;
        MainActions.EventsChartOpened += EventsChartOpened;
        MainActions.EventsHealingSummaryOptionsChanged += EventsHealingSummaryOptionsChanged;
        EventsHealingSummaryOptionsChanged("pane shown");
        _ready = true;
      }
    }

    public void HideContent()
    {
      HealingStatsBuilder.Instance.EventsGenerationStatus -= EventsGenerationStatus;
      CombatEvents.ActiveDataCleared -= EventsClearedActiveData;
      MainActions.EventsChartOpened -= EventsChartOpened;
      ClearData();

      // healing always rebuilds and doesn't have a simple way to reset to all data
      _ = Task.Run(() => HealingStatsBuilder.Instance.RebuildTotalStats(
        new GenerateStatsOptions { Source = "healing pane hidden" })).ContinueWith(t =>
        Log.Error("Problem building healing stats", t.Exception), TaskContinuationOptions.OnlyOnFaulted);
      _ready = false;
    }
  }
}
