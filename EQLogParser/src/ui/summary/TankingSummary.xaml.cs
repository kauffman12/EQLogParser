using FontAwesome5;
using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace EQLogParser
{
  public partial class TankingSummary : IDocumentContent
  {
    // Made property since it's used outside this class
    public int DamageType { get; set; }

    private bool _currentPetValue;
    private int _currentGroupCount;
    private readonly DispatcherTimer _selectionTimer;
    private bool _ready;

    public TankingSummary()
    {
      InitializeComponent();

      // if pets are shown
      showPets.IsChecked = _currentPetValue = ConfigUtil.IfSet("TankingSummaryShowPets", true);

      // default damage types to display
      var damageType = ConfigUtil.GetSetting("TankingSummaryDamageType");
      if (!string.IsNullOrEmpty(damageType) && int.TryParse(damageType, out var type) && type is > -1 and < 3)
      {
        damageTypes.SelectedIndex = type;
      }
      else
      {
        damageTypes.SelectedIndex = 0;
      }

      DamageType = damageTypes.SelectedIndex;
      var list = PlayerManager.Instance.GetClassList();
      list.Insert(0, Resource.ANY_CLASS);
      classesList.ItemsSource = list;
      classesList.SelectedIndex = 0;

      CreateSpellCountMenuItems(menuItemShowSpellCounts, DataGridSpellCountsByClassClick, DataGridShowSpellCountsClick);
      CreateClassMenuItems(menuItemShowSpellCasts, DataGridSpellCastsByClassClick, false, DataGridShowSpellCastsClick);
      CreateClassMenuItems(menuItemShowTankingBreakdown, DataGridShowBreakdownByClassClick, false, DataGridShowBreakdownClick);
      CreateClassMenuItems(menuItemShowHealingBreakdown, DataGridShowBreakdown2ByClassClick, true, DataGridShowBreakdown2Click);
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
          EventsTankingSummaryOptionsChanged();
          _selectionTimer.Stop();
        }
      };
    }

    internal override void ShowBreakdown(List<PlayerStats> selected)
    {
      if (selected?.Count > 0)
      {
        if (SyncFusionUtil.OpenWindow(out var window, typeof(TankingBreakdown), "tankingBreakdownWindow", "Tanking Breakdown")
          && window.Content is TankingBreakdown { } breakdown)
        {
          breakdown.Init(CurrentStats, selected);
        }
      }
    }

    internal override void ShowBreakdown2(List<PlayerStats> selected)
    {
      if (selected?.Count > 0)
      {
        if (SyncFusionUtil.OpenWindow(out var window, typeof(HealBreakdown), "receivedHealingWindow", "Received Healing Breakdown")
          && window.Content is HealBreakdown { } breakdown)
        {
          // healing stats on the tank is stored in MoreStats property
          // it's like another player stat based around healing
          var selectedHealing = selected.Where(stats => stats.MoreStats != null).Select(stats => stats.MoreStats).ToList();
          breakdown.Init(CurrentStats, selectedHealing, true);
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
          menuItemShowSpellCasts.IsEnabled = menuItemShowHealingBreakdown.IsEnabled = menuItemShowTankingBreakdown.IsEnabled =
            menuItemShowSpellCounts.IsEnabled = true;
          menuItemShowTankingLog.IsEnabled = menuItemShowHitFreq.IsEnabled = dataGrid.SelectedItems.Count == 1;
          copyTankingParseToEQClick.IsEnabled = copyOptions.IsEnabled = true;
          copyReceivedHealingParseToEQClick.IsEnabled = (dataGrid.SelectedItems.Count == 1) &&
            (dataGrid.SelectedItem as PlayerStats)?.MoreStats != null;
          menuItemShowDefensiveTimeline.IsEnabled = dataGrid.SelectedItems.Count is 1 or 2 && _currentGroupCount == 1;

          // default before making check
          menuItemShowDeathLog.IsEnabled = false;
          menuItemSetPlayerClass.IsEnabled = false;
          menuItemSetAsPet.IsEnabled = false;

          if (dataGrid.SelectedItem is PlayerStats playerStats && dataGrid.SelectedItems.Count == 1)
          {
            menuItemSetPlayerClass.IsEnabled = PlayerManager.Instance.IsVerifiedPlayer(playerStats.OrigName);
            menuItemSetAsPet.IsEnabled = playerStats.OrigName != Labels.Unk && playerStats.OrigName != Labels.Rs &&
            !PlayerManager.Instance.IsVerifiedPlayer(playerStats.OrigName) && !PlayerManager.Instance.IsMerc(playerStats.OrigName);
            selectedName = playerStats.OrigName;
            menuItemShowDeathLog.IsEnabled = !string.IsNullOrEmpty(playerStats.Special) && playerStats.Special.Contains('X');
          }

          EnableClassMenuItems(menuItemShowHealingBreakdown, dataGrid, CurrentStats?.UniqueClasses);
          EnableClassMenuItems(menuItemShowTankingBreakdown, dataGrid, CurrentStats?.UniqueClasses);
          EnableClassMenuItems(menuItemShowSpellCasts, dataGrid, CurrentStats?.UniqueClasses);
          EnableClassMenuItems(menuItemShowSpellCounts, dataGrid, CurrentStats?.UniqueClasses);
        }
        else
        {
          menuItemShowHealingBreakdown.IsEnabled = menuItemShowTankingBreakdown.IsEnabled =
          menuItemShowTankingLog.IsEnabled = menuItemSetAsPet.IsEnabled = menuItemShowSpellCounts.IsEnabled = copyTankingParseToEQClick.IsEnabled =
          copyOptions.IsEnabled = copyReceivedHealingParseToEQClick.IsEnabled = menuItemShowSpellCasts.IsEnabled = menuItemShowHitFreq.IsEnabled =
          menuItemSetPlayerClass.IsEnabled = menuItemShowDefensiveTimeline.IsEnabled = false;
        }

        menuItemSetAsPet.Header = $"Assign {selectedName} as Pet of";
        menuItemSetPlayerClass.Header = $"Assign {selectedName} to Class";
      });
    }

    private void CopyToEqClick(object sender, RoutedEventArgs e) => MainActions.CopyToEqClick(Labels.TankParse);
    private void CopyReceivedHealingToEqClick(object sender, RoutedEventArgs e) => MainActions.CopyToEqClick(Labels.ReceivedHealParse);
    private void DataGridSelectionChanged(object sender, GridSelectionChangedEventArgs e) => DataGridSelectionChanged();

    private void CreatePetOwnerMenu()
    {
      menuItemPetOptions.Children.Clear();
      if (CurrentStats != null)
      {
        foreach (var stats in CurrentStats.StatsList.Where(stats => PlayerManager.Instance.IsVerifiedPlayer(stats.OrigName)).OrderBy(stats => stats.OrigName))
        {
          var item = new MenuItem { IsEnabled = true, Header = stats.OrigName };
          item.Click += AssignOwnerClick;
          menuItemPetOptions.Children.Add(item);
        }
      }
    }

    private void AssignOwnerClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItem is PlayerStats stats && sender is MenuItem item)
      {
        PlayerManager.Instance.AddPetToPlayer(stats.OrigName, item.Header as string);
        PlayerManager.Instance.AddVerifiedPet(stats.OrigName);
      }
    }

    private void DataGridCopyContent(object sender, GridCopyPasteEventArgs e)
    {
      if (MainWindow.IsMapSendToEqEnabled && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.IsKeyDown(Key.C))
      {
        e.Handled = true;
        CopyToEqClick(sender, null);
      }
    }

    private async void DataGridTankingLogClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItems?.Count > 0)
      {
        if (SyncFusionUtil.OpenWindow(out var log, typeof(HitLogViewer), "tankingLogWindow", "Tanking Log") && log.Content is HitLogViewer { } viewer)
        {
          await viewer.InitAsync(CurrentStats, dataGrid.SelectedItems.Cast<PlayerStats>().First(), CurrentGroups, true);
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

    private void DataGridHitFreqClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItems.Count == 1)
      {
        if (SyncFusionUtil.OpenWindow(out var hitFreq, typeof(HitFreqChart), "tankHitFreqChart", "Tanking Hit Frequency"))
        {
          (hitFreq.Content as HitFreqChart)?.Update(dataGrid.SelectedItems.Cast<PlayerStats>().First(), CurrentStats);
        }
      }
    }

    private void DataGridDefensiveTimelineClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItems.Count > 0)
      {
        if (SyncFusionUtil.OpenWindow(out var timeline, typeof(Timeline), "defensiveTimeline", "Defensive Timeline"))
        {
          ((Timeline)timeline.Content).Init(CurrentStats, [.. dataGrid.SelectedItems.Cast<PlayerStats>()], CurrentGroups, 0);
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
        if (e.Type == Labels.HealParse && e.State == "COMPLETED")
        {
          if (CurrentStats != null)
          {
            HealingStatsManager.Instance.PopulateHealing(CurrentStats);
            dataGrid.SelectedItems.Clear();
            dataGrid.View?.RefreshFilter();

            if (e.Limited)
            {
              title.Content = CurrentStats.FullTitle + " (Not All Healing Opts Chosen)";
            }
            else
            {
              title.Content = CurrentStats.FullTitle;
            }
          }
        }
        else if (e.Type == Labels.TankParse)
        {
          switch (e.State)
          {
            case "STARTED":
              title.Content = "Calculating Tanking DPS...";
              dataGrid.ItemsSource = NoResultsList;
              break;
            case "COMPLETED":
              CurrentStats = e.CombinedStats;
              CurrentGroups = e.Groups;
              _currentGroupCount = e.UniqueGroupCount;

              var isHealingLimited = false;
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
                isHealingLimited = HealingStatsManager.Instance.PopulateHealing(CurrentStats);
                dataGrid.ItemsSource = CurrentStats.StatsList;
              }

              if (isHealingLimited)
              {
                title.Content += " (Not All Healing Opts Chosen)";
              }

              CreatePetOwnerMenu();
              UpdateDataGridMenuItems();
              break;
            case "NONPC":
            case "NODATA":
              CurrentStats = null;
              maxTimeChooser.MaxValue = 0;
              minTimeChooser.MaxValue = 0;
              title.Content = e.State == "NONPC" ? Labels.NoNpcs : Labels.NoData;
              CreatePetOwnerMenu();
              UpdateDataGridMenuItems();
              break;
          }

          // always stop
          _selectionTimer.Stop();
          prog.Visibility = Visibility.Hidden;
        }
      });
    }

    private void ItemsSourceChanged(object sender, GridItemsSourceChangedEventArgs e)
    {
      if (dataGrid.View != null)
      {
        dataGrid.View.Filter = stats =>
        {
          string className = null;
          string name = null;

          if (stats is PlayerStats playerStats)
          {
            name = playerStats.Name;
            className = playerStats.ClassName;
          }
          else if (stats is string dataPointName)
          {
            name = dataPointName;
            className = PlayerManager.Instance.GetPlayerClass(name);
          }

          var isPet = PlayerManager.Instance.IsVerifiedPet(name);
          if (isPet && _currentPetValue == false)
          {
            return false;
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

    private void OptionsChanged(object sender, RoutedEventArgs e)
    {
      if (dataGrid?.View != null)
      {
        var needRequery = DamageType != damageTypes.SelectedIndex;
        _currentPetValue = showPets.IsChecked == true;
        DamageType = damageTypes.SelectedIndex;
        ConfigUtil.SetSetting("TankingSummaryShowPets", _currentPetValue);
        ConfigUtil.SetSetting("TankingSummaryDamageType", DamageType);

        dataGrid.SelectedItems.Clear();
        dataGrid.View.RefreshFilter();

        if (needRequery)
        {
          var tankingOptions = new GenerateStatsOptions { DamageType = DamageType, MaxSeconds = (long)maxTimeChooser.Value, MinSeconds = (long)minTimeChooser.Value };
          Task.Run(() => TankingStatsManager.Instance.RebuildTotalStats(tankingOptions));
        }
      }
    }

    private void EventsChartOpened(string name)
    {
      if (name == "Tanking")
      {
        var selected = GetSelectedStats();
        TankingStatsManager.Instance.FireChartEvent("UPDATE", DamageType, selected);
      }
    }

    internal override void FireSelectionChangedEvent(List<PlayerStats> selected)
    {
      Dispatcher.InvokeAsync(() =>
      {
        var selectionChanged = new PlayerStatsSelectionChangedEventArgs();
        selectionChanged.Selected.AddRange(selected);
        selectionChanged.CurrentStats = CurrentStats;
        MainActions.FireTankingSelectionChanged(selectionChanged);
      });
    }

    private void EventsTankingSummaryOptionsChanged()
    {
      var statOptions = new GenerateStatsOptions
      {
        MinSeconds = (long)minTimeChooser.Value,
        MaxSeconds = ((long)maxTimeChooser.Value > 0) ? (long)maxTimeChooser.Value : -1,
        DamageType = DamageType
      };

      if (statOptions.MinSeconds < statOptions.MaxSeconds || statOptions.MaxSeconds == -1)
      {
        Task.Run(() => TankingStatsManager.Instance.RebuildTotalStats(statOptions));
      }
    }

    private void ContentLoaded(object sender, RoutedEventArgs e)
    {
      if (VisualParent != null && !_ready)
      {
        TankingStatsManager.Instance.EventsGenerationStatus += EventsGenerationStatus;
        HealingStatsManager.Instance.EventsGenerationStatus += EventsGenerationStatus;
        DataManager.Instance.EventsClearedActiveData += EventsClearedActiveData;
        MainActions.EventsChartOpened += EventsChartOpened;
        MainActions.EventsTankingSelectionChanged += EventsTankingSelectionChanged;
        EventsTankingSummaryOptionsChanged();
        _ready = true;
      }
    }

    private void EventsTankingSelectionChanged(PlayerStatsSelectionChangedEventArgs data)
    {
      TankingStatsManager.Instance.FireChartEvent("SELECT", DamageType, data.Selected);
    }

    public void HideContent()
    {
      TankingStatsManager.Instance.EventsGenerationStatus -= EventsGenerationStatus;
      HealingStatsManager.Instance.EventsGenerationStatus -= EventsGenerationStatus;
      DataManager.Instance.EventsClearedActiveData -= EventsClearedActiveData;
      MainActions.EventsChartOpened -= EventsChartOpened;
      MainActions.EventsTankingSelectionChanged -= EventsTankingSelectionChanged;
      ClearData();

      // window is close so reset
      TankingStatsManager.Instance.FireChartEvent("UPDATE", DamageType, null, true);
      _ready = false;
    }
  }
}
