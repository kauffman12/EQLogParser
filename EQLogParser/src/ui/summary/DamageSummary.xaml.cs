using FontAwesome5;
using Syncfusion.UI.Xaml.Grid;
using Syncfusion.UI.Xaml.TreeGrid;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;

namespace EQLogParser
{
  public partial class DamageSummary : IDocumentContent
  {
    public static readonly List<int> GroupNumbers = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

    private const int DEFAULT_VIEW = 0;
    private const int GROUP_VIEW = 4;
    private readonly DispatcherTimer _selectionTimer;
    private int _currentGroupCount;
    private int _currentPetOrPlayerOption;
    private bool _ready;

    // Group view tracking for incremental updates
    private List<GroupEntry> _groupEntries;

    public DamageSummary()
    {
      InitializeComponent();
      petOrPlayerList.ItemsSource = new List<string>
      {
        Labels.PetPlayerOption,   // 0: Players + Pets
        Labels.PlayerOption,      // 1: Players
        Labels.PetOption,         // 2: Pets
        Labels.AllOption,         // 3: Uncategorized
        Labels.ByGroupOption      // 4: By Group (NEW)
      };

      petOrPlayerList.SelectedIndex = 0;

      CreateSpellCountMenuItems(menuItemShowSpellCounts, DataGridSpellCountsByClassClick, DataGridShowSpellCountsClick);
      CreateClassMenuItems(menuItemShowSpellCasts, DataGridSpellCastsByClassClick, false, DataGridShowSpellCastsClick);
      CreateClassMenuItems(menuItemShowBreakdown, DataGridShowBreakdownByClassClick, false, DataGridShowBreakdownClick);
      CreateClassMenuItems(menuItemSetPlayerClass, DataGridSetPlayerClassClick, true);

      // call after everything else is initialized
      InitSummaryTable(title, dataGrid, selectedColumns, classesList);
      dataGrid.CopyContent += DataGridCopyContent;

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
          EventsDamageSummaryOptionsChanged();
          _selectionTimer.Stop();
        }
      };
    }

    internal override void ShowBreakdown(List<PlayerStats> selected)
    {
      if (selected?.Count > 0)
      {
        if (SyncFusionUtil.OpenWindow(out var breakdown, typeof(DamageBreakdown),
          "damageBreakdownWindow", "DPS Breakdown"))
        {
          (breakdown.Content as DamageBreakdown)?.Init(CurrentStats, selected);
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
          menuItemShowDamageLog.IsEnabled = menuItemShowHitFreq.IsEnabled = dataGrid.SelectedItems.Count == 1;
          menuItemShowAdpsTimeline.IsEnabled = dataGrid.SelectedItems.Count is 1 or 2 && _currentGroupCount == 1;
          copyDamageParseToEQClick.IsEnabled = copyOptions.IsEnabled = true;

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

          EnableClassMenuItems(menuItemShowBreakdown, dataGrid, CurrentStats?.UniqueClasses);
          EnableClassMenuItems(menuItemShowSpellCasts, dataGrid, CurrentStats?.UniqueClasses);
          EnableClassMenuItems(menuItemShowSpellCounts, dataGrid, CurrentStats?.UniqueClasses);
        }
        else
        {
          menuItemShowBreakdown.IsEnabled = menuItemShowDamageLog.IsEnabled =
          menuItemSetAsPet.IsEnabled = menuItemShowSpellCounts.IsEnabled = menuItemShowHitFreq.IsEnabled = copyDamageParseToEQClick.IsEnabled =
          copyOptions.IsEnabled = menuItemShowAdpsTimeline.IsEnabled = menuItemShowSpellCasts.IsEnabled = menuItemSetPlayerClass.IsEnabled =
          menuItemShowDeathLog.IsEnabled = false;
        }

        menuItemSetAsPet.Header = $"Assign {selectedName} as Pet of";
        menuItemSetPlayerClass.Header = $"Assign Default Class for {selectedName}";
      });
    }

    private void CopyToEqClick(object sender, RoutedEventArgs e) => MainActions.CopyToEqClick(Labels.DamageParse);
    internal override bool IsPetsCombined() => _currentPetOrPlayerOption == DEFAULT_VIEW;
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

    private void ListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      var needUpdate = _currentPetOrPlayerOption != petOrPlayerList.SelectedIndex;
      _currentPetOrPlayerOption = petOrPlayerList.SelectedIndex;

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

        switch (_currentPetOrPlayerOption)
        {
          case 0: // Players + Pets
            dataGrid.ItemsSource = UpdateRank(CurrentStats.StatsList);
            break;
          case 1: // Players
          case 2: // Pets
          case 3: // Uncategorized
            dataGrid.ItemsSource = UpdateRank(CurrentStats.ExpandedStatsList);
            break;
          case 4: // NEW: By Group
            InitializeGroupTracking();
            var groupedPlayers = BuildGroupedPlayers();
            dataGrid.ItemsSource = UpdateRankGrouped(groupedPlayers);
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

    /// <summary>
    /// Builds grouped players for Group View mode. Creates group headers with aggregated stats and merges time ranges for accurate TotalSeconds calculation.
    /// </summary>
    /// <returns>List of GroupEntry objects representing groups, sorted by total damage descending.</returns>
    private List<GroupEntry> BuildGroupedPlayers()
    {
      if (CurrentStats == null)
      {
        return [];
      }

      var nonEmptyGroups = new List<GroupEntry>();

      foreach (var groupEntry in _groupEntries)
      {
        if (groupEntry.Members.Count == 0)
        {
          continue;
        }

        // Sort players within each group by Total descending
        groupEntry.Members.Sort((a, b) => b.Total.CompareTo(a.Total));

        // Mark players so RequestTreeItems knows not to expand further
        foreach (var player in groupEntry.Members)
        {
          player.IsTopLevel = true;
        }

        // Reset and recalculate stats
        StatsUtil.ResetPlayerStats(groupEntry);

        // Merge time segments for accurate TotalSeconds
        var mergedRange = new TimeRange();
        mergedRange.Add(groupEntry.TimeSegments);
        groupEntry.TotalSeconds = mergedRange.GetTotal();

        // Merge all player stats into group entry
        foreach (var player in groupEntry.Members)
        {
          StatsUtil.MergeStats(groupEntry, player);
        }

        // Re-calculate rates based on the correct aggregated totals and TotalSeconds
        StatsUtil.CalculateRates(groupEntry, CurrentStats.RaidStats, null);

        // Set children for TreeGrid expansion
        groupEntry.Children = [.. groupEntry.Members];

        // Default: collapsed state for all groups (user expands as needed)
        groupEntry.IsExpanded = false;

        nonEmptyGroups.Add(groupEntry);
      }

      // Return non-empty groups sorted by aggregate Total descending
      return [.. nonEmptyGroups.OrderBy(g => g.Total).Reverse()];
    }

    private string GetGroupName(int groupId)
    {
      return groupId == 0 ? "Unassigned Group" : $"Group {groupId}";
    }

    private void InitializeGroupTracking()
    {
      _groupEntries = [];

      // Create group entries for each group ID
      for (var i = 0; i <= 12; i++)
      {
        _groupEntries.Add(new GroupEntry
        {
          Name = GetGroupName(i),
          OrigName = GetGroupName(i),
          GroupId = i,
          IsTopLevel = true,
          ClassName = null,
          AssignedGroup = i,
          IsExpanded = false,
          IsGroupHeader = true
        });
      }

      // Assign players to groups and collect time segments
      foreach (var stats in CurrentStats.StatsList)
      {
        if (!IsPlayerVisible(stats)) continue;

        var groupId = Math.Clamp(stats.AssignedGroup, 0, 12);
        var groupEntry = _groupEntries[groupId];

        groupEntry.Members.Add(stats);

        if (stats.Ranges?.TimeSegments != null)
        {
          groupEntry.TimeSegments.AddRange(stats.Ranges.TimeSegments);
        }
      }
    }

    private void CleanupGroupTracking()
    {
      _groupEntries?.Clear();
    }

    private async void PlayerGroup_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (e.AddedItems.Count == 0) return;

      var player = (sender as ComboBox)?.DataContext as PlayerStats;
      if (player == null) return;

      var newGroupId = (int)e.AddedItems[0];

      // If we're in Group View, use incremental update with progress indicator
      if (_currentPetOrPlayerOption == GROUP_VIEW)
      {
        // Skip if value hasn't actually changed
        if (player.AssignedGroup == newGroupId) return;

        var oldGroupId = player.AssignedGroup;
        if (oldGroupId == newGroupId) return;

        // IMMEDIATELY persist the change to player object BEFORE any rebuild
        player.AssignedGroup = newGroupId;

        // Show progress indicator
        prog.Icon = EFontAwesomeIcon.Solid_HourglassStart;
        prog.Visibility = Visibility.Visible;

        await Task.Run(() =>
        {
          // Background work: Update group membership and time segments
          UpdateGroupMembership(player, oldGroupId, newGroupId);
        });

        // Force full ItemsSource replacement on UI thread
        await Dispatcher.InvokeAsync(() =>
        {
          try
          {
            // 1. CAPTURE current expanded group names BEFORE changing ItemsSource
            var expandedGroups = new List<int>();
            if (dataGrid.View?.Nodes != null)
            {
              foreach (var node in dataGrid.View.Nodes)
              {
                var dataItem = node.Item as GroupEntry;
                if (dataItem?.IsExpanded == true)
                {
                  expandedGroups.Add(dataItem.GroupId);
                }
              }
            }

            // 2. DO THE ItemsSource replacement
            var newList = BuildGroupedPlayers();
            dataGrid.ItemsSource = null;
            dataGrid.ItemsSource = UpdateRankGrouped(newList);

            // 3. RESTORE expansion state after rebuild
            if (dataGrid.View?.Nodes != null)
            {
              foreach (var groupId in expandedGroups)
              {
                var entry = _groupEntries.FirstOrDefault(e => e.GroupId == groupId);
                if (entry != null)
                {
                  var node = dataGrid.View.Nodes.GetNode(entry);
                  if (node != null)
                  {
                    dataGrid.ExpandNode(node);
                  }
                }
              }
            }
          }
          finally
          {
            prog.Icon = EFontAwesomeIcon.Solid_HourglassEnd;
            prog.Visibility = Visibility.Hidden;
          }
        });
      }
      else
      {
        // Not in Group View - just update the value directly
        player.AssignedGroup = newGroupId;
      }
    }

    private void UpdateGroupMembership(PlayerStats player, int oldGroupId, int newGroupId)
    {
      var oldGroup = _groupEntries[oldGroupId];
      var newGroup = _groupEntries[newGroupId];

      // Remove from old group
      oldGroup.Members.Remove(player);
      if (player.Ranges?.TimeSegments != null)
      {
        oldGroup.TimeSegments.RemoveAll(t => player.Ranges.TimeSegments.Contains(t));
      }

      // Add to new group
      newGroup.Members.Add(player);
      if (player.Ranges?.TimeSegments != null)
      {
        newGroup.TimeSegments.AddRange(player.Ranges.TimeSegments);
      }
    }

    private void CleanupEmptyGroups()
    {
      // Remove empty groups' children and time segments
      foreach (var entry in _groupEntries)
      {
        if (entry.Members.Count == 0)
        {
          entry.Children.Clear();
          entry.TimeSegments.Clear();
        }
      }
    }

    /// <summary>
    /// Checks if player should be visible based on class filter selection.
    /// </summary>
    private bool IsPlayerVisible(PlayerStats player)
    {
      if (SelectedClasses.Count > 0 && SelectedClasses.Count < 16)
      {
        var className = player.ClassName;

        // For Player +Pets entries, className should already be set correctly
        if (className != null && !SelectedClasses.Contains(className))
        {
          return false;
        }
      }

      return true;
    }

    private void DataGridCopyContent(object sender, GridCopyPasteEventArgs e)
    {
      if (MainWindow.IsMapSendToEqEnabled && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.IsKeyDown(Key.C))
      {
        e.Handled = true;
        CopyToEqClick(sender, null);
      }
    }

    private async void DataGridDamageLogClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItems?.Count > 0)
      {
        if (SyncFusionUtil.OpenWindow(out var log, typeof(HitLogViewer), "damageLogWindow", "DPS Log") && log.Content is HitLogViewer { } viewer)
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

    private void DataGridHitFreqClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItems.Count == 1)
      {
        if (SyncFusionUtil.OpenWindow(out var hitFreq, typeof(HitFreqChart), "damageFreqChart", "Damage Hit Frequency"))
        {
          (hitFreq.Content as HitFreqChart)?.Update(dataGrid.SelectedItems.Cast<PlayerStats>().First(), CurrentStats);
        }
      }
    }

    private void DataGridAdpsTimelineClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItems.Count > 0)
      {
        if (SyncFusionUtil.OpenWindow(out var timeline, typeof(Timeline), "adpsTimeline", "ADPS Timeline"))
        {
          ((Timeline)timeline.Content).Init(CurrentStats, [.. dataGrid.SelectedItems.Cast<PlayerStats>()], CurrentGroups, 1);
        }
      }
    }

    private void EventsClearedActiveData(bool cleared)
    {
      if (cleared && _currentPetOrPlayerOption == GROUP_VIEW)
      {
        CleanupGroupTracking();
      }
      ClearData();
    }

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
            title.Content = "Calculating DPS...";
            dataGrid.ItemsSource = NoResultsList;
            break;
          case "COMPLETED":
            CurrentStats = e.CombinedStats;
            CurrentGroups = e.Groups;
            _currentGroupCount = e.UniqueGroupCount;

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
            title.Content = e.State == "NONPC" ? Labels.NoNpcs : Labels.NoData;
            CreatePetOwnerMenu();
            UpdateDataGridMenuItems();
            break;
        }

        // always stop
        _selectionTimer.Stop();
        prog.Visibility = Visibility.Hidden;
      });
    }

    private void ItemsSourceChanged(object sender, TreeGridItemsSourceChangedEventArgs e)
    {
      if (dataGrid.View != null)
      {
        dataGrid.View.Filter = stats =>
        {
          // Always show group entries in group view
          if (stats is GroupEntry)
          {
            return true;
          }

          if (!(stats is PlayerStats playerStats)) return false;

          var name = playerStats.Name;
          var className = playerStats.ClassName;
          var isPet = PlayerManager.Instance.IsVerifiedPet(name);

          if (isPet)
          {
            var ownerName = PlayerManager.Instance.GetPlayerFromPet(name);
            if (!string.IsNullOrEmpty(ownerName) && ownerName != Labels.Unassigned)
            {
              var owner = CurrentStats?.ExpandedStatsList.FirstOrDefault(s => s.Name == ownerName);
              if (owner != null)
              {
                className = owner.ClassName;
              }
            }
          }

          var classMatches = SelectedClasses.Count == 16 || (className != null && SelectedClasses.Contains(className));

          return _currentPetOrPlayerOption switch
          {
            1 => !isPet && classMatches,
            2 => isPet && classMatches,
            4 => true,  // Group View - pre-filtered in BuildGroupedPlayers
            _ => classMatches
          };
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

    private void RequestTreeItems(object sender, TreeGridRequestTreeItemsEventArgs e)
    {
      if (dataGrid.ItemsSource is IList list)
      {
        var isInGroupView = list.Count > 0 && list[0] is GroupEntry;

        if (e.ParentItem == null)
        {
          e.ChildItems = list;
        }
        else if (isInGroupView && e.ParentItem is GroupEntry groupEntry)
        {
          // Group view: return the group's children
          e.ChildItems = groupEntry.Children;
        }
        else if (e.ParentItem is PlayerStats parentStats)
        {
          if (isInGroupView)
          {
            // Level 2: Player +Pets - DON'T expand further, return empty list
            e.ChildItems = new List<PlayerStats>();
          }
          else
          {
            // Regular view: Expand to show pets
            if (CurrentStats.Children.TryGetValue(parentStats.Name, out var childPets))
            {
              e.ChildItems = childPets;
            }
            else
            {
              e.ChildItems = new List<PlayerStats>();
            }
          }
        }
      }
    }

    private void EventsChartOpened(string name)
    {
      if (name == "Damage")
      {
        var selected = GetSelectedStats();
        DamageStatsManager.Instance.FireChartEvent("UPDATE", selected);
      }
    }

    private static List<PlayerStats> UpdateRank(List<PlayerStats> list)
    {
      var rank = 1;
      foreach (var stats in list.OrderByDescending(stats => stats.Total))
      {
        stats.Rank = (ushort)rank++;
      }

      return list;
    }

    private static List<GroupEntry> UpdateRankGrouped(List<GroupEntry> list)
    {
      var rank = 1;
      foreach (var entry in list.OrderByDescending(e => e.Total))
      {
        entry.Rank = (ushort)rank++;
      }

      return list;
    }

    internal override void FireSelectionChangedEvent(List<PlayerStats> selected)
    {
      Dispatcher.InvokeAsync(() =>
      {
        var selectionChanged = new PlayerStatsSelectionChangedEventArgs();
        selectionChanged.Selected.AddRange(selected);
        selectionChanged.CurrentStats = CurrentStats;
        MainActions.FireDamageSelectionChanged(selectionChanged);
      });
    }

    private void EventsDamageSummaryOptionsChanged(string option = null)
    {
      var statOptions = new GenerateStatsOptions
      {
        MinSeconds = (long)minTimeChooser.Value,
        MaxSeconds = ((long)maxTimeChooser.Value > 0) ? (long)maxTimeChooser.Value : -1
      };

      if (statOptions.MinSeconds < statOptions.MaxSeconds || statOptions.MaxSeconds == -1)
      {
        Task.Run(() => DamageStatsManager.Instance.RebuildTotalStats(statOptions));
      }
    }

    private void ContentLoaded(object sender, RoutedEventArgs e)
    {
      if (VisualParent != null && !_ready)
      {
        DamageStatsManager.Instance.EventsGenerationStatus += EventsGenerationStatus;
        DataManager.Instance.EventsClearedActiveData += EventsClearedActiveData;
        MainActions.EventsChartOpened += EventsChartOpened;
        MainActions.EventsDamageSummaryOptionsChanged += EventsDamageSummaryOptionsChanged;

        if (DamageStatsManager.Instance.GetLastStats() is { } stats)
        {
          EventsGenerationStatus(stats);
        }
        else
        {
          EventsDamageSummaryOptionsChanged();
        }

        _ready = true;
      }
    }

    public void HideContent()
    {
      DamageStatsManager.Instance.EventsGenerationStatus -= EventsGenerationStatus;
      DataManager.Instance.EventsClearedActiveData -= EventsClearedActiveData;
      MainActions.EventsDamageSummaryOptionsChanged -= EventsDamageSummaryOptionsChanged;
      MainActions.EventsChartOpened -= EventsChartOpened;

      if (_currentPetOrPlayerOption == GROUP_VIEW)
      {
        CleanupGroupTracking();
      }

      ClearData();

      if ((long)minTimeChooser.Value != 0 || (long)maxTimeChooser.Value != (long)maxTimeChooser.MaxValue)
      {
        DamageStatsManager.Instance.RebuildTotalStats(new GenerateStatsOptions(), true);
      }

      _ready = false;
    }

    private void ImageAwesome_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
      if (sender is ImageAwesome ia && ia.Parent is Grid grid)
      {
        foreach (var child in grid.Children)
        {
          if (child is ComboBox combo)
          {
            combo.Visibility = Visibility.Visible;
            Dispatcher.InvokeAsync(() =>
            {
              combo.IsDropDownOpen = true;
            }, DispatcherPriority.Background);
            dataGrid.SelectionChanging += ContextChangeHandler;
          }
          else if (child is FrameworkElement fe)
          {
            fe.Visibility = Visibility.Collapsed;
          }
        }
      }

      void ContextChangeHandler(object s, GridSelectionChangingEventArgs args)
      {
        foreach (var child in grid.Children)
        {
          if (child is ComboBox combo)
          {
            combo.Visibility = Visibility.Collapsed;
            combo.IsDropDownOpen = false;
          }
          else if (child is FrameworkElement fe)
          {
            fe.Visibility = Visibility.Visible;
          }
        }
        dataGrid.SelectionChanging -= ContextChangeHandler;
      }
    }
  }
}
