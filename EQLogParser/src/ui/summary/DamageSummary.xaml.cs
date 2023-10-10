using Syncfusion.UI.Xaml.Grid;
using Syncfusion.UI.Xaml.TreeGrid;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Keyboard = System.Windows.Input.Keyboard;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for DamageSummary.xaml
  /// </summary>
  public partial class DamageSummary : SummaryTable, IDisposable
  {
    private string CurrentClass = null;
    private int CurrentGroupCount = 0;
    private int CurrentPetOrPlayerOption = 0;
    private readonly DispatcherTimer SelectionTimer;

    public DamageSummary()
    {
      InitializeComponent();

      var list = PlayerManager.Instance.GetClassList();
      list.Insert(0, Resource.ANY_CLASS);
      classesList.ItemsSource = list;
      classesList.SelectedIndex = 0;

      petOrPlayerList.ItemsSource = new List<string> { Labels.PETPLAYEROPTION, Labels.PLAYEROPTION, Labels.PETOPTION, Labels.EVERYTHINGOPTION };
      petOrPlayerList.SelectedIndex = 0;

      CreateClassMenuItems(menuItemShowSpellCounts, DataGridShowSpellCountsClick, DataGridSpellCountsByClassClick);
      CreateClassMenuItems(menuItemShowSpellCasts, DataGridShowSpellCastsClick, DataGridSpellCastsByClassClick);
      CreateClassMenuItems(menuItemShowBreakdown, DataGridShowBreakdownClick, DataGridShowBreakdownByClassClick);

      // call after everything else is initialized
      InitSummaryTable(title, dataGrid, selectedColumns);
      DamageStatsManager.Instance.EventsGenerationStatus += EventsGenerationStatus;
      DataManager.Instance.EventsClearedActiveData += EventsClearedActiveData;
      dataGrid.CopyContent += DataGridCopyContent;

      SelectionTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 500) };
      SelectionTimer.Tick += (sender, e) =>
      {
        if (prog.Icon == FontAwesome5.EFontAwesomeIcon.Solid_HourglassStart)
        {
          prog.Icon = FontAwesome5.EFontAwesomeIcon.Solid_HourglassHalf;
        }
        else if (prog.Icon == FontAwesome5.EFontAwesomeIcon.Solid_HourglassHalf)
        {
          prog.Icon = FontAwesome5.EFontAwesomeIcon.Solid_HourglassEnd;
        }
        else if (prog.Icon == FontAwesome5.EFontAwesomeIcon.Solid_HourglassEnd)
        {
          prog.Visibility = Visibility.Hidden;

          if (minTimeChooser.Value < maxTimeChooser.Value)
          {
            var damageOptions = new GenerateStatsOptions { MaxSeconds = (long)maxTimeChooser.Value, MinSeconds = (long)minTimeChooser.Value };
            Task.Run(() => DamageStatsManager.Instance.RebuildTotalStats(damageOptions));
          }

          SelectionTimer.Stop();
        }
      };
    }

    internal override void ShowBreakdown(List<PlayerStats> selected)
    {
      if (selected?.Count > 0)
      {
        var main = Application.Current.MainWindow as MainWindow;
        if (SyncFusionUtil.OpenWindow(main.dockSite, null, out var breakdown, typeof(DamageBreakdown),
          "damageBreakdownWindow", "DPS Breakdown"))
        {
          (breakdown.Content as DamageBreakdown).Init(CurrentStats, selected);
        }
      }
    }

    override internal void UpdateDataGridMenuItems()
    {
      var selectedName = "Unknown";

      Dispatcher.InvokeAsync(() =>
      {
        if (CurrentStats != null && CurrentStats.StatsList.Count > 0 && dataGrid.View != null)
        {
          menuItemShowSpellCasts.IsEnabled = menuItemShowBreakdown.IsEnabled = menuItemShowSpellCounts.IsEnabled = true;
          menuItemShowDamageLog.IsEnabled = menuItemShowHitFreq.IsEnabled = dataGrid.SelectedItems.Count == 1;
          menuItemShowAdpsTimeline.IsEnabled = (dataGrid.SelectedItems.Count == 1 || dataGrid.SelectedItems.Count == 2) && CurrentGroupCount == 1;
          copyDamageParseToEQClick.IsEnabled = copyOptions.IsEnabled = true;

          // default before making check
          menuItemSetAsPet.IsEnabled = false;
          menuItemShowDeathLog.IsEnabled = false;

          if (dataGrid.SelectedItem is PlayerStats playerStats && dataGrid.SelectedItems.Count == 1)
          {
            menuItemSetAsPet.IsEnabled = playerStats.OrigName != Labels.UNK && playerStats.OrigName != Labels.RS &&
            !PlayerManager.Instance.IsVerifiedPlayer(playerStats.OrigName) && !PlayerManager.Instance.IsMerc(playerStats.OrigName);
            selectedName = playerStats.OrigName;
            menuItemShowDeathLog.IsEnabled = !string.IsNullOrEmpty(playerStats.Special) && playerStats.Special.Contains("X");
          }

          EnableClassMenuItems(menuItemShowBreakdown, dataGrid, CurrentStats?.UniqueClasses);
          EnableClassMenuItems(menuItemShowSpellCasts, dataGrid, CurrentStats?.UniqueClasses);
          EnableClassMenuItems(menuItemShowSpellCounts, dataGrid, CurrentStats?.UniqueClasses);
        }
        else
        {
          menuItemShowBreakdown.IsEnabled = menuItemShowDamageLog.IsEnabled =
            menuItemSetAsPet.IsEnabled = menuItemShowSpellCounts.IsEnabled = menuItemShowHitFreq.IsEnabled = copyDamageParseToEQClick.IsEnabled =
            copyOptions.IsEnabled = menuItemShowAdpsTimeline.IsEnabled = menuItemShowSpellCasts.IsEnabled = menuItemShowDeathLog.IsEnabled = false;
        }

        menuItemSetAsPet.Header = string.Format(CultureInfo.CurrentCulture, "Assign {0} as Pet to", selectedName);
      });
    }

    private void CopyToEQClick(object sender, RoutedEventArgs e) => (Application.Current.MainWindow as MainWindow).CopyToEQClick(Labels.DAMAGEPARSE);
    internal override bool IsPetsCombined() => CurrentPetOrPlayerOption == 0;
    private void DataGridSelectionChanged(object sender, GridSelectionChangedEventArgs e) => DataGridSelectionChanged();

    private void ClassSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
      var update = classesList.SelectedIndex <= 0 ? null : classesList.SelectedValue.ToString();
      var needUpdate = CurrentClass != update;
      CurrentClass = update;

      if (needUpdate)
      {
        dataGrid.SelectedItems.Clear();
        dataGrid.View?.RefreshFilter();
      }
    }

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

    private void ListSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
      var needUpdate = CurrentPetOrPlayerOption != petOrPlayerList.SelectedIndex;
      CurrentPetOrPlayerOption = petOrPlayerList.SelectedIndex;

      if (needUpdate)
      {
        UpdateList();
      }
    }

    private void UpdateList()
    {
      if (CurrentStats != null)
      {
        var beforeList = dataGrid.ItemsSource;
        switch (CurrentPetOrPlayerOption)
        {
          case 0:
            dataGrid.ItemsSource = UpdateRank(CurrentStats.StatsList);
            break;
          case 1:
          case 2:
          case 3:
            dataGrid.ItemsSource = UpdateRank(CurrentStats.ExpandedStatsList);
            break;
        }

        // if list stayed the same then update the filter
        if (beforeList == dataGrid.ItemsSource)
        {
          dataGrid.SelectedItems.Clear();
          dataGrid.View.RefreshFilter();
        }
      }
    }

    private List<PlayerStats> UpdateRank(List<PlayerStats> list)
    {
      var rank = 1;
      foreach (ref var stats in list.OrderByDescending(stats => stats.Total).ToArray().AsSpan())
      {
        stats.Rank = (ushort)rank++;
      }

      return list;
    }

    private void DataGridCopyContent(object sender, GridCopyPasteEventArgs e)
    {
      if (MainWindow.IsMapSendToEQEnabled && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.IsKeyDown(Key.C))
      {
        e.Handled = true;
        CopyToEQClick(sender, null);
      }
    }

    private void DataGridDamageLogClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItems?.Count > 0)
      {
        var main = Application.Current.MainWindow as MainWindow;
        if (SyncFusionUtil.OpenWindow(main.dockSite, null, out var log, typeof(HitLogViewer), "damageLogWindow", "DPS Log"))
        {
          (log.Content as HitLogViewer).Init(CurrentStats, dataGrid.SelectedItems.Cast<PlayerStats>().First(), CurrentGroups);
        }
      }
    }

    private void DataGridDeathLogClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItems?.Count > 0)
      {
        var main = Application.Current.MainWindow as MainWindow;
        if (SyncFusionUtil.OpenWindow(main.dockSite, null, out var log, typeof(DeathLogViewer), "deathLogWindow", "Death Log"))
        {
          (log.Content as DeathLogViewer).Init(CurrentStats, dataGrid.SelectedItems.Cast<PlayerStats>().First());
        }
      }
    }

    private void DataGridHitFreqClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItems.Count == 1)
      {
        var main = Application.Current.MainWindow as MainWindow;
        if (SyncFusionUtil.OpenWindow(main.dockSite, null, out var hitFreq, typeof(HitFreqChart), "damageFreqChart", "Damage Hit Frequency"))
        {
          (hitFreq.Content as HitFreqChart).Update(dataGrid.SelectedItems.Cast<PlayerStats>().First(), CurrentStats);
        }
      }
    }

    private void DataGridAdpsTimelineClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItems.Count > 0)
      {
        var main = Application.Current.MainWindow as MainWindow;
        if (SyncFusionUtil.OpenWindow(main.dockSite, null, out var timeline, typeof(GanttChart), "adpsTimeline", "ADPS Timeline"))
        {
          ((GanttChart)timeline.Content).Init(CurrentStats, dataGrid.SelectedItems.Cast<PlayerStats>().ToList(), CurrentGroups, 1);
        }
      }
    }

    private void EventsClearedActiveData(object sender, bool cleared)
    {
      CurrentStats = null;
      dataGrid.ItemsSource = NoResultsList;
      title.Content = DEFAULT_TABLE_LABEL;
    }

    private void EventsGenerationStatus(object sender, StatsGenerationEvent e)
    {
      Dispatcher.InvokeAsync(() =>
      {
        switch (e.State)
        {
          case "STARTED":
            title.Content = "Calculating DPS...";
            dataGrid.ItemsSource = NoResultsList;
            break;
          case "COMPLETED":
            CurrentStats = e.CombinedStats;
            CurrentGroups = e.Groups;
            CurrentGroupCount = e.UniqueGroupCount;

            if (CurrentStats == null)
            {
              title.Content = NODATA_TABLE_LABEL;
              maxTimeChooser.MaxValue = 0;
              minTimeChooser.MaxValue = 0;
            }
            else
            {
              title.Content = CurrentStats.FullTitle;
              maxTimeChooser.MaxValue = Convert.ToInt64(CurrentStats.RaidStats.MaxTime);
              if (maxTimeChooser.MaxValue > 0)
              {
                maxTimeChooser.MinValue = 1;
              }
              maxTimeChooser.Value = Convert.ToInt64(CurrentStats.RaidStats.TotalSeconds + CurrentStats.RaidStats.MinTime);
              minTimeChooser.MaxValue = Convert.ToInt64(CurrentStats.RaidStats.MaxTime);
              minTimeChooser.Value = Convert.ToInt64(CurrentStats.RaidStats.MinTime);
              UpdateList();
            }

            if (e.Limited)
            {
              title.Content += " (Not All Damage Opts Chosen)";
            }

            CreatePetOwnerMenu();
            UpdateDataGridMenuItems();
            break;
          case "NONPC":
          case "NODATA":
            CurrentStats = null;
            maxTimeChooser.MaxValue = 0;
            minTimeChooser.MaxValue = 0;
            title.Content = e.State == "NONPC" ? DEFAULT_TABLE_LABEL : NODATA_TABLE_LABEL;
            CreatePetOwnerMenu();
            UpdateDataGridMenuItems();
            break;
        }

        // always stop
        SelectionTimer.Stop();
        prog.Visibility = Visibility.Hidden;
      });
    }

    private void ItemsSourceChanged(object sender, TreeGridItemsSourceChangedEventArgs e)
    {
      if (dataGrid.View != null)
      {
        dataGrid.View.Filter = (stats) =>
        {
          var name = "";
          var className = "";
          if (stats is PlayerStats playerStats)
          {
            name = playerStats.Name;
            className = playerStats.ClassName;
          }
          else if (stats is string playerName)
          {
            name = playerName;
            className = PlayerManager.Instance.GetPlayerClass(name);
          }

          var result = false;
          if (CurrentPetOrPlayerOption == 1)
          {
            result = !PlayerManager.Instance.IsVerifiedPet(name) && (string.IsNullOrEmpty(CurrentClass) || CurrentClass == className);
          }
          else if (CurrentPetOrPlayerOption == 2)
          {
            result = PlayerManager.Instance.IsVerifiedPet(name);
          }
          else
          {
            result = string.IsNullOrEmpty(CurrentClass) || CurrentClass == className;
          }

          return result;
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
        SelectionTimer.Stop();
        SelectionTimer.Start();

        prog.Icon = FontAwesome5.EFontAwesomeIcon.Solid_HourglassStart;
        prog.Visibility = Visibility.Visible;
      }
    }

    private void RequestTreeItems(object sender, TreeGridRequestTreeItemsEventArgs e)
    {
      if (dataGrid.ItemsSource is List<PlayerStats>)
      {
        if (e.ParentItem == null)
        {
          e.ChildItems = dataGrid.ItemsSource as List<PlayerStats>;
        }
        else if (e.ParentItem is PlayerStats stats && CurrentStats.Children.TryGetValue(stats.Name, out var childs))
        {
          e.ChildItems = childs;
        }
        else
        {
          e.ChildItems = new List<PlayerStats>();
        }
      }
    }

    #region IDisposable Support
    private bool disposedValue = false; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        SummaryCleanup();
        DamageStatsManager.Instance.FireChartEvent(new GenerateStatsOptions { MaxSeconds = long.MinValue }, "UPDATE");
        DamageStatsManager.Instance.EventsGenerationStatus -= EventsGenerationStatus;
        DataManager.Instance.EventsClearedActiveData -= EventsClearedActiveData;
        dataGrid.CopyContent -= DataGridCopyContent;
        CurrentStats = null;
        dataGrid.Dispose();
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
