using FontAwesome5;
using log4net;
using Syncfusion.UI.Xaml.Grid;
using Syncfusion.UI.Xaml.TreeGrid;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    public static readonly List<string> GroupNumbers = ["", "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12"];
    private static readonly List<string> NoRostersList = ["No Rosters Found"];
    private readonly ComboBoxItem _selectRosterItem = new() { Content = "Select Roster" };
    private readonly Dictionary<string, int> _manualGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _selectionTimer;

    /*
     * "Calculating DPS..." and the emptied grid, shown only if the build is still running when a short delay expires. Showing them the instant
     * a build starts cannot work: the answer is posted at the same dispatcher priority as the busy state, so on a fast build or a congested UI
     * thread the clear is filled back in before it ever reaches the screen. See DeferredBusyState.
     */
    private readonly DeferredBusyState _busyState = new();
    private int _currentGroupCount;
    private readonly ViewOptionRegistry _viewOptions;
    private bool _ready;
    private bool _updatingRosterSelector;

    // Group view tracking for incremental updates
    private List<GroupEntry> _groupEntries;
    /* The row the group popup is editing. It starts unset until a selection arrives (the handler below asks), and this
     * project builds with Nullable=disable, so no null-forgiving initializer is needed to satisfy anything — one would only
     * hide the real state from whoever reads it next (docs/CodingStandards.md → Nullable Reference Types). */
    private PlayerStats _currentEditPlayer;

    public DamageSummary()
    {
      InitializeComponent();
      SyncFusionUtil.ConfigureFrozenCellAnimations(dataGrid);

      // The four identity words come from one builder (IdentityVerdictMenu) rather than from markup, so the three summary panes and the
      // fight list cannot drift about their spelling or their write.
      IdentityVerdictMenu.Populate(menuItemSetKind, ApplyVerdict);

      _viewOptions = new ViewOptionRegistry();
      _viewOptions.AddOption(Labels.ByGroupOption, OnViewOptionChanged);
      _viewOptions.AddOption(Labels.PetPlayerOption, OnViewOptionChanged);
      _viewOptions.AddOption(Labels.PlayerOption, OnViewOptionChanged);
      _viewOptions.AddOption(Labels.PetOption, OnViewOptionChanged);
      _viewOptions.AddOption(Labels.AllOption, OnViewOptionChanged);
      petOrPlayerList.ItemsSource = _viewOptions.GetDisplayNames();
      petOrPlayerList.SelectedIndex = 1;

      CreateSpellCountMenuItems(menuItemShowSpellCounts, DataGridSpellCountsByClassClick, DataGridShowSpellCountsClick);
      CreateClassMenuItems(menuItemShowSpellCasts, DataGridSpellCastsByClassClick, false, DataGridShowSpellCastsClick);
      CreateClassMenuItems(menuItemShowBreakdown, DataGridShowBreakdownByClassClick, false, DataGridShowBreakdownClick);
      CreateClassMenuItems(menuItemSetPlayerClass, DataGridSetPlayerClassClick, true);

      // call after everything else is initialized
      InitSummaryTable(title, dataGrid, selectedColumns, classesList);
      dataGrid.CopyContent += DataGridCopyContent;
      PopulateRosterSelector();

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
          EventsDamageSummaryOptionsChanged("hourglass after time change");
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
          var hasPlayerSelection = dataGrid.SelectedItems.Any(s => s is PlayerStats && s is not GroupEntry);

          // Copy/send options always available
          copyDamageParseToEQClick.IsEnabled = copyOptions.IsEnabled = true;

          // Player-only options: disabled for group selections. The two Assign items and the Set cascade are NOT reset in this chain —
          // the block below this one enables them from the selection (exactly one row, or they grey out), which is the same answer the
          // fight list's menu gives (IdentityVerdictMenu.Present).
          menuItemShowSpellCasts.IsEnabled = false;
          menuItemShowSpellCounts.IsEnabled = false;
          menuItemShowBreakdown.IsEnabled = false;
          menuItemShowDamageLog.IsEnabled = false;
          menuItemShowHitFreq.IsEnabled = false;
          menuItemShowAdpsTimeline.IsEnabled = false;
          menuItemShowDeathLog.IsEnabled = false;

          if (hasPlayerSelection)
          {
            menuItemShowSpellCasts.IsEnabled = menuItemShowBreakdown.IsEnabled = menuItemShowSpellCounts.IsEnabled = true;
            menuItemShowDamageLog.IsEnabled = menuItemShowHitFreq.IsEnabled = dataGrid.SelectedItems.Count == 1;
            menuItemShowAdpsTimeline.IsEnabled = dataGrid.SelectedItems.Count is 1 or 2 && _currentGroupCount == 1;

            if (dataGrid.SelectedItem is PlayerStats playerStats && dataGrid.SelectedItems.Count == 1)
            {
              selectedName = playerStats.OrigName;
              menuItemShowDeathLog.IsEnabled = !string.IsNullOrEmpty(playerStats.Special) && playerStats.Special.Contains('X');
            }

            EnableClassMenuItems(menuItemShowBreakdown, dataGrid, CurrentStats?.UniqueClasses);
            EnableClassMenuItems(menuItemShowSpellCasts, dataGrid, CurrentStats?.UniqueClasses);
            EnableClassMenuItems(menuItemShowSpellCounts, dataGrid, CurrentStats?.UniqueClasses);
          }
        }
        else
        {
          menuItemShowBreakdown.IsEnabled = menuItemShowDamageLog.IsEnabled =
          menuItemShowSpellCounts.IsEnabled = menuItemShowHitFreq.IsEnabled = copyDamageParseToEQClick.IsEnabled =
          copyOptions.IsEnabled = menuItemShowAdpsTimeline.IsEnabled = menuItemShowSpellCasts.IsEnabled =
          menuItemShowDeathLog.IsEnabled = false;
        }

        /*
         * The two Assign items name their subject the way they always did ("Unknown" being the generic wording when nothing usable is
         * selected). The cascade says the same thing in its own shape and under the stricter law: `IdentityVerdictMenu.Present` takes ONE
         * name — the row a verdict can honestly be about — so a ctrl-clicked block greys the cascade out rather than heading it with a
         * count. That is not timidity: a ruling re-routes sides, rows and `+Pets` folding for every name it covers and identity-overrides.txt
         * keeps only the latest word, so there is no undo (AGENTS: force one selected row).
         */
        menuItemSetAsPet.Header = $"Assign {selectedName} as Pet of";
        menuItemSetPlayerClass.Header = $"Assign Default Class for {selectedName}";

        // Exactly one selected row, or these verbs sleep. The two Assign items follow the same law: "whose pet is this" is as much a
        // one-name judgement as "what is this", and a group header or a block of rows answers neither.
        var verdictName = SelectedVerdictName();
        menuItemSetAsPet.IsEnabled = menuItemSetPlayerClass.IsEnabled = verdictName != null;
        IdentityVerdictMenu.Present(menuItemSetKind, verdictName);
      });
    }

    private void CopyToEqClick(object sender, RoutedEventArgs e) => MainActions.CopyToEqClick(Labels.DamageParse);
    internal override bool IsPetsCombined() => _viewOptions?.GetSelectedOptionName() == Labels.PetPlayerOption;
    private void DataGridSelectionChanged(object sender, GridSelectionChangedEventArgs e) => DataGridSelectionChanged();

    private void CreatePetOwnerMenu()
    {
      menuItemPetOptions.Children.Clear();
      if (CurrentStats != null)
      {
        // The "assign owner" list is built from the same question, so the grid and this dropdown can never disagree
        // about who may own a pet — one seam, one answer (docs/DesignNotes.md → "Memory: one import, three files").
        foreach (var stats in CurrentStats.StatsList.Where(stats => IdentityLookup.IsOneOfUs(stats.OrigName)).OrderBy(stats => stats.OrigName))
        {
          var item = new MenuItem { IsEnabled = true, Header = stats.OrigName };
          item.Click += AssignOwnerClick;
          menuItemPetOptions.Children.Add(item);
        }
      }
    }

    private void AssignOwnerClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItem is PlayerStats stats && sender is MenuItem { Header: string header })
      {
        PetAssignment.Assign(stats.OrigName, header);
      }
    }

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

    private void ListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (_viewOptions != null)
      {
        _viewOptions.OnSelectionChanged(petOrPlayerList.SelectedIndex);
      }
    }

    private void OnViewOptionChanged(int index)
    {
      var groupView = _viewOptions.GetSelectedOptionName() == Labels.ByGroupOption;
      if (groupView)
      {
        PopulateRosterSelector();
        ApplySelectedRoster();
      }

      rosterSelector.Visibility = groupView ? Visibility.Visible : Visibility.Collapsed;
      UpdateList();
    }

    private void OnRosterUpdated(long captureVersion)
    {
      Dispatcher.InvokeAsync(() =>
      {
        if (!_ready || captureVersion != RaidRosterStore.Instance.CaptureVersion) return;
        PopulateRosterSelector(selectLatest: true);
        RefreshRosterGrouping();
      });
    }

    private void PopulateRosterSelector(bool selectLatest = false)
    {
      var rosters = RaidRosterStore.Instance.GetRosterRecords();
      var previousRoster = rosterSelector.SelectedItem is ComboBoxItem { Tag: WhoRosterRecord previous }
        ? previous : null;

      // Replacing ItemsSource raises selection events; apply only the final choice, never the transient empty selection.
      _updatingRosterSelector = true;
      try
      {
        if (rosters.Count == 0)
        {
          rosterSelector.ItemsSource = NoRostersList;
          rosterSelector.SelectedIndex = 0;
          rosterSelector.IsEnabled = false;
          rosterSelector.FontStyle = FontStyles.Italic;
          _selectRosterItem.Content = "Select Roster";
          if (previousRoster != null) _manualGroups.Clear();
          return;
        }

        var wasCleared = ReferenceEquals(rosterSelector.SelectedItem, _selectRosterItem);
        rosterSelector.IsEnabled = true;
        rosterSelector.FontStyle = FontStyles.Normal;
        var displayItems = rosters.Select(r => new ComboBoxItem
        {
          Content = DateUtil.FormatDotNetDateSeconds(DateUtil.TicksToDotNetSeconds(r.BeginTicks)),
          Tag = r
        }).ToList();

        displayItems.Insert(0, _selectRosterItem);
        rosterSelector.ItemsSource = displayItems;
        var previousTicks = previousRoster?.BeginTicks;
        var selectedIndex = selectLatest ? 1 : wasCleared ? 0
          : displayItems.FindIndex(i => i.Tag is WhoRosterRecord roster && roster.BeginTicks == previousTicks);
        if (selectedIndex < 0) selectedIndex = 1;
        rosterSelector.SelectedIndex = selectedIndex;
        var selectedRoster = displayItems[selectedIndex].Tag;
        if (selectLatest || !ReferenceEquals(previousRoster, selectedRoster)) _manualGroups.Clear();
        UpdateRosterActionLabel();
      }
      finally
      {
        _updatingRosterSelector = false;
      }
    }

    private void RosterSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (!_updatingRosterSelector && rosterSelector?.SelectedItem is ComboBoxItem)
      {
        _manualGroups.Clear();
        UpdateRosterActionLabel();
        RefreshRosterGrouping();
      }
    }

    private void UpdateRosterActionLabel()
    {
      _selectRosterItem.Content = rosterSelector.SelectedIndex == 0 ? "Select Roster" : "Clear Roster";
    }

    private void RefreshRosterGrouping()
    {
      // Only Group View needs a new grid list. The normal views keep their selected players.
      if (ApplySelectedRoster() && _viewOptions?.GetSelectedOptionName() == Labels.ByGroupOption)
      {
        var expandedGroups = CaptureExpandedGroups();
        UpdateList();
        RestoreExpandedGroups(expandedGroups);
      }
    }

    private bool ApplySelectedRoster()
    {
      if (CurrentStats?.StatsList == null) return false;

      var hasRosterChoice = rosterSelector.SelectedItem is ComboBoxItem;
      var roster = rosterSelector.SelectedItem is ComboBoxItem { Tag: WhoRosterRecord selected } ? selected : null;
      var changed = false;
      foreach (var stats in GroupRows())
      {
        if (stats?.OrigName == null) continue;

        if (_manualGroups.TryGetValue(stats.OrigName, out var manualGroup))
        {
          changed |= AssignGroup(stats, manualGroup);
        }
        else if (hasRosterChoice)
        {
          var groupId = roster != null && roster.Players.TryGetValue(stats.OrigName, out var group) ? group : 0;
          changed |= AssignGroup(stats, groupId);
        }
      }

      return changed;
    }

    private void UpdateList()
    {
      if (CurrentStats != null)
      {
        var beforeList = dataGrid.ItemsSource;
        var selectedName = _viewOptions?.GetSelectedOptionName();
        _groupEntries = [];

        switch (selectedName)
        {
          case Labels.ByGroupOption:
            InitializeGroupTracking();
            var groupedPlayers = BuildGroupedPlayers();
            dataGrid.ItemsSource = UpdateRankGrouped(groupedPlayers);
            break;
          case Labels.PetPlayerOption:
            dataGrid.ItemsSource = UpdateRank(CurrentStats.StatsList);
            break;
          case Labels.PlayerOption:
          case Labels.PetOption:
          case Labels.AllOption:
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

        // Uptime is the union of the members' own ranges, rebuilt from Members every pass so membership changes cannot leave stale seconds behind.
        groupEntry.TotalSeconds = StatsUtil.MergeMemberRanges(groupEntry.Members).GetTotal();

        // Merge all player stats into group entry
        foreach (var player in groupEntry.Members)
        {
          StatsUtil.MergeStats(groupEntry, player);
        }

        // Re-calculate rates based on the correct aggregated totals and TotalSeconds
        StatsUtil.CalculateRates(groupEntry, CurrentStats.RaidStats, null);
        StatsUtil.CalculatePercentOfRaid(groupEntry, CurrentStats.RaidStats);

        // Set children for TreeGrid expansion
        groupEntry.Children = [.. groupEntry.Members];

        // Default: collapsed state for all groups (user expands as needed)
        groupEntry.IsExpanded = false;

        nonEmptyGroups.Add(groupEntry);
      }

      // Return non-empty groups sorted by aggregate Total descending
      return [.. nonEmptyGroups.OrderBy(g => g.Total).Reverse()];
    }

    // A player's aggregate and individual rows can be separate objects with the same OrigName.
    private IEnumerable<PlayerStats> GroupRows() => CurrentStats.StatsList.Concat(CurrentStats.ExpandedStatsList);

    private void SetManualGroup(PlayerStats stats, int groupId)
    {
      if (string.IsNullOrEmpty(stats.OrigName)) return;

      _manualGroups[stats.OrigName] = groupId;
      foreach (var row in GroupRows())
      {
        if (string.Equals(row?.OrigName, stats.OrigName, StringComparison.OrdinalIgnoreCase))
        {
          AssignGroup(row, groupId);
        }
      }
    }

    private static bool AssignGroup(PlayerStats stats, int groupId)
    {
      if (stats.AssignedGroup != groupId)
      {
        stats.AssignedGroup = groupId;

        if (!string.IsNullOrEmpty(stats?.OrigName))
        {
          DamageStatsBuilder.Instance.SetPlayerAssignedGroup(stats.OrigName, groupId);
        }

        return true;
      }
      return false;
    }

    private static string GetGroupName(int groupId)
    {
      return groupId < 1 ? "Unassigned Group" : $"Raid Group {groupId}";
    }

    private void InitializeGroupTracking()
    {
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
      }
    }

    private void CleanupGroupTracking()
    {
      _groupEntries?.Clear();
    }

    /*
     * This pane's own door into the damage builder — separate from the fight list's selection, and it bypasses MainWindow's single-flight
     * gate entirely, which is why "the grid filled three times" was invisible until each door had a name. `option` says who asked: a view
     * dial from the toolbar, this pane being shown for the first time, or the hourglass timer finishing after a time-window change (which
     * fires on startup by itself, because setting the saved min/max values moves the dependency properties). StatsBuildTrace prints it.
     */
    private void EventsDamageSummaryOptionsChanged(string option = null)
    {
      var statOptions = new GenerateStatsOptions
      {
        Source = $"damage pane options [{option ?? "time window"}]",
        MinSeconds = (long)minTimeChooser.Value,
        MaxSeconds = ((long)maxTimeChooser.Value > 0) ? (long)maxTimeChooser.Value : -1
      };

      if (statOptions.MinSeconds < statOptions.MaxSeconds || statOptions.MaxSeconds == -1)
      {
        _ = Task.Run(() => DamageStatsBuilder.Instance.RebuildTotalStats(statOptions)).ContinueWith(t =>
          Log.Error("Problem building damage stats.", t.Exception), TaskContinuationOptions.OnlyOnFaulted);
      }
    }

    private void EditGroupMouseLeftButton(object sender, MouseButtonEventArgs e)
    {
      if (sender is not ImageAwesome ia || ia.DataContext is not PlayerStats stats)
        return;

      if (UiElementUtil.FindGridCell(ia) is not { } cell)
        return;

      _currentEditPlayer = stats;
      groupEditComboBox.SelectedItem = _currentEditPlayer?.AssignedGroup > 0 ? $"{_currentEditPlayer.AssignedGroup}" : "";
      UiElementUtil.OpenCellPopup(groupEditPopup, groupEditComboBox, cell, () =>
      {
        _currentEditPlayer = null;
        groupEditComboBox.SetValue(ComboBox.SelectedValueProperty, null);
      });
    }

    private async void GroupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (sender is not ComboBox combo || combo.SelectedIndex == -1)
        return;

      if (CurrentStats == null || _currentEditPlayer is null || _currentEditPlayer.AssignedGroup == combo.SelectedIndex)
        return;

      var oldGroupId = _currentEditPlayer.AssignedGroup;

      SetManualGroup(_currentEditPlayer, combo.SelectedIndex);

      if (_viewOptions?.GetSelectedOptionName() == Labels.ByGroupOption)
      {
        // Change membership before yielding; a roster update may replace these groups while rendering.
        var groups = _groupEntries;
        UpdateGroupMembership(_currentEditPlayer, oldGroupId, combo.SelectedIndex);
        prog.Icon = EFontAwesomeIcon.Solid_HourglassStart;
        prog.Visibility = Visibility.Visible;
        await Dispatcher.Yield(DispatcherPriority.Render);

        try
        {
          if (CurrentStats != null && ReferenceEquals(groups, _groupEntries))
          {
            var expandedGroups = CaptureExpandedGroups();
            var newList = BuildGroupedPlayers();
            dataGrid.ItemsSource = null;
            dataGrid.ItemsSource = UpdateRankGrouped(newList);
            RestoreExpandedGroups(expandedGroups);
          }
        }
        finally
        {
          prog.Icon = EFontAwesomeIcon.Solid_HourglassEnd;
          prog.Visibility = Visibility.Hidden;
        }
      }

      groupEditPopup.IsOpen = false;
    }

    private void UpdateGroupMembership(PlayerStats player, int oldGroupId, int newGroupId)
    {
      var oldGroup = _groupEntries[oldGroupId];
      var newGroup = _groupEntries[newGroupId];

      // Members are the only state a group keeps; BuildGroupedPlayers recomputes each group's seconds from them.
      oldGroup.Members.Remove(player);
      newGroup.Members.Add(player);
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
      if (AppSettings.IsMapSendToEqEnabled && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.IsKeyDown(Key.C))
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
      _busyState.Cancel();   // a cleared capture has no build left to announce
      if (cleared) _manualGroups.Clear();

      if (cleared && _viewOptions?.GetSelectedOptionName() == Labels.ByGroupOption)
      {
        CleanupGroupTracking();
      }

      ClearData();
    }

    /// <summary>
    /// Captures the GroupIds of currently expanded nodes in the tree grid.
    /// </summary>
    private List<int> CaptureExpandedGroups()
    {
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

      return expandedGroups;
    }

    /// <summary>
    /// Re-expands tree grid nodes for the given group IDs after an ItemsSource rebuild.
    /// </summary>
    private void RestoreExpandedGroups(List<int> expandedGroups)
    {
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

    /*
     * "No board here" is an EMPTY COLLECTION, never null: SfDataGrid answers a null ItemsSource with a spinner claiming work is still
     * happening. Safe because no guard asks the widget whether an answer exists — they read CurrentStats. See SummaryTable's note.
     */
    private void ClearData()
    {
      CurrentStats = null;
      dataGrid.ItemsSource = NoResultsList;
      title.Content = Labels.NoNpcs;
      PopulateRosterSelector();
    }

    private void EventsGenerationStatus(StatsGenerationEvent e)
    {
      DispatchGenerationStatus(e, () =>
      {
        // Any state of a build ends the promise made by the one before it; STARTED makes a new one, on its own delay.
        _busyState.Cancel();

        switch (e.State)
        {
          case "STARTED":
            // The title is the whole busy state; rows stay. An empty grid is a second indicator nobody controls, and the
            // previous board is wrong by milliseconds rather than absent (docs/DesignNotes.md → "Where a board build's time actually goes").
            _busyState.Arm(() => title.Content = "Calculating DPS...", "damage");
            break;
          case "COMPLETED":
            CurrentStats = e.CombinedStats;
            CurrentGroups = e.Groups;
            _currentGroupCount = e.UniqueGroupCount;
            PopulateRosterSelector();
            ApplySelectedRoster();

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
            // A build that found nothing has to answer for the rows already on screen: shifting the last row
            // out of the fight list announces zero fights, this branch sets the title, and without clearing the
            // grid the old selection's rows stayed visible underneath "No NPCs". Empty collection, not null — this
            // is a finished answer with no rows, not data that has not arrived.
            dataGrid.ItemsSource = NoResultsList;
            maxTimeChooser.MaxValue = 0;
            minTimeChooser.MaxValue = 0;
            title.Content = e.State == "NONPC" ? Labels.NoNpcs : Labels.NoData;
            CreatePetOwnerMenu();
            UpdateDataGridMenuItems();
            PopulateRosterSelector();
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

          if (stats is not PlayerStats playerStats) return false;

          var name = playerStats.Name;
          var className = playerStats.ClassName;
          var isPet = IdentityLookup.IsPet(name);

          if (isPet)
          {
            var ownerName = IdentityLookup.OwnerOf(name);
            if (!string.IsNullOrEmpty(ownerName) && !Labels.IsUnassignedOwner(ownerName))
            {
              var owner = CurrentStats?.ExpandedStatsList.FirstOrDefault(s => s.Name == ownerName);
              if (owner != null)
              {
                className = owner.ClassName;
              }
            }
          }

          var classMatches = SelectedClasses.Count == 16 || (className != null && SelectedClasses.Contains(className));

          return _viewOptions?.GetSelectedOptionName() switch
          {
            Labels.PlayerOption => !isPet && classMatches,
            Labels.PetOption => isPet && classMatches,
            Labels.ByGroupOption => true,  // Group View - pre-filtered in BuildGroupedPlayers
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

    /*
     * The dial only has work to do when there is a board to re-slice, and "is there a board?" is a question about the ANSWER
     * (`CurrentStats`), not about what the grid happens to be holding. Reading the grid made an empty placeholder — the one this pane
     * used to install while busy and after clearing — look like data: hourglass over nothing, and a rebuild of a board that is not there.
     */
    private void TimeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
      if (CurrentStats != null)
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
        // OfType, not Cast: GroupEntry derives from PlayerStats and the grid holds both, so a plain
        // player row made Cast<GroupEntry> throw (InvalidCastException) every time the chart loaded
        // outside Group View. Empty here means "no group rows selected", which is what FireChartEvent
        // expects; the players themselves already arrive through GetSelectedStats().
        var groups = dataGrid.SelectedItems.OfType<GroupEntry>().ToList();
        DamageStatsBuilder.Instance.FireChartEvent("UPDATE", selected, groups);
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

    internal override List<PlayerStats> GetSelectedStats()
    {
      var selected = base.GetSelectedStats();
      var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      var result = new List<PlayerStats>();

      foreach (var item in selected)
      {
        if (item is GroupEntry group)
        {
          // Expand group into its member players so charts and other consumers work
          foreach (var member in group.Members)
          {
            if (seen.Add(member.OrigName))
            {
              result.Add(member);
            }
          }
        }
        else if (item is PlayerStats player && seen.Add(player.OrigName))
        {
          result.Add(player);
        }
      }

      return result;
    }

    internal override void FireSelectionChangedEvent(List<PlayerStats> selected)
    {
      Dispatcher.InvokeAsync(() =>
      {
        var selectionChanged = new PlayerStatsSelectionChangedEventArgs();
        selectionChanged.Selected.AddRange(selected);
        selectionChanged.CurrentStats = CurrentStats;

        // Extract GroupEntry objects from the raw grid selection so the chart can aggregate groups
        foreach (var item in dataGrid.SelectedItems)
        {
          if (item is GroupEntry group)
          {
            selectionChanged.SelectedGroups.Add(group);
          }
        }

        MainActions.FireDamageSelectionChanged(selectionChanged);
      });
    }

    private void ContentLoaded(object sender, RoutedEventArgs e)
    {
      if (VisualParent != null && !_ready)
      {
        DamageStatsBuilder.Instance.EventsGenerationStatus += EventsGenerationStatus;
        CombatEvents.ActiveDataCleared += EventsClearedActiveData;
        MainActions.EventsChartOpened += EventsChartOpened;
        MainActions.EventsDamageSummaryOptionsChanged += EventsDamageSummaryOptionsChanged;
        RaidRosterStore.EventsRosterUpdated += OnRosterUpdated;
        // Opening must catch up with rosters captured while hidden, without waiting for a board build.
        PopulateRosterSelector(selectLatest: true);
        RefreshRosterGrouping();

        if (DamageStatsBuilder.Instance.GetLastStats() is { } stats)
        {
          EventsGenerationStatus(stats);
        }
        else
        {
          EventsDamageSummaryOptionsChanged("pane shown, no prior build");
        }

        _ready = true;
      }
    }

    public void HideContent()
    {
      _busyState.Cancel();   // a hidden pane must not wake to "Calculating DPS..." on nobody's screen
      DamageStatsBuilder.Instance.EventsGenerationStatus -= EventsGenerationStatus;
      CombatEvents.ActiveDataCleared -= EventsClearedActiveData;
      MainActions.EventsDamageSummaryOptionsChanged -= EventsDamageSummaryOptionsChanged;
      MainActions.EventsChartOpened -= EventsChartOpened;
      RaidRosterStore.EventsRosterUpdated -= OnRosterUpdated;

      if (_viewOptions?.GetSelectedOptionName() == Labels.ByGroupOption)
      {
        CleanupGroupTracking();
      }

      ClearData();

      if ((long)minTimeChooser.Value != 0 || (long)maxTimeChooser.Value != (long)maxTimeChooser.MaxValue)
      {
        DamageStatsBuilder.Instance.RebuildTotalStats(
          new GenerateStatsOptions { Source = "damage pane hidden (time window was narrowed)" }, true);
      }

      _ready = false;
    }
  }
}
