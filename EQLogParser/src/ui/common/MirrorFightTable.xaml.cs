using Syncfusion.UI.Xaml.Grid;
using Syncfusion.UI.Xaml.ScrollAxis;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using EQLogParser.Mirror;

namespace EQLogParser
{
  /*
   * The derived-fight-list twin of FightTable: same grid, the same three columns in the legacy order (Initial Hit
   * Time | HP | Name, with duration and hits on the row tooltip like legacy's), the same search/HP/Inactivity header -
   * different data source (CombatMirror → ClassificationRules → FightDeriver → Sectionizer). Selection feeds the
   * damage summary from derived facts (see DerivedSelectionChanged), and the right-click menu is where R10 lives:
   * say what a name actually is (Set as Player / Mercenary / Pet / NPC), which saves per server and re-derives.
   * Cross-grid selection sync with the legacy list is still ahead of it.
   */
  public partial class MirrorFightTable
  {
    /*
     * Raised when the selection settles, with the derived fights behind the selected rows (empty when
     * nothing is selected — an empty selection means "show no data", same as the legacy list).
     *
     * One-way on purpose, and damage-only: the point of this step is to be able to select the same fight
     * in both lists and read two answers from two engines. Feeding every legacy viewer would make that
     * comparison impossible to reason about, since whichever list was clicked last would own the boards.
     */
    internal event Action<IReadOnlyList<DerivedFight>> DerivedSelectionChanged;

    // Selection settles on a short pause rather than per click, and much more cheaply than the 750 ms the
    // legacy table waits: nothing is recomputed here, the click only says which fights are wanted. Long
    // enough that dragging a range across a thousand rows fires once, short enough to feel immediate.
    private const int SelectionSettleMs = 350;

    private ObservableCollection<MirrorFightRow> _rows = [];
    private readonly DispatcherTimer _selectionTimer;

    // What was last announced, as fight ids. Two jobs: a stale snapshot's rows cannot be re-announced as
    // if they were new, and a grid that re-raises SelectionChanged with the same selection (or with none,
    // when an ItemsSource swap lands) must not clear stats nobody changed.
    private List<int> _announcedIds = [];

    /*
     * The names the last right-click wrote, kept until the derive they asked for finishes so the panel can say
     * what actually became of them. Setting a name to Pet or Player takes its row OFF the list by design (only
     * hostile-side names key an encounter), which otherwise reads as "my click deleted the fight".
     */
    private List<string> _pendingOverride;

    /*
     * What a row IS, across derives. DerivedFight.Id cannot say that: FightProjection renumbers the list on every
     * pass (Id = i + 1 over the rows of THAT pass), so one override - which removes rows on purpose - shifts
     * every number after it, and restoring by id would highlight whatever fight moved into the old slot while
     * showing its numbers underneath. A section of a name is identified by the name and the moment it began;
     * both come out of the same facts on every pass.
     */
    private readonly record struct FightKey(string Name, double BeginTime);

    private MirrorSession _session;
    private bool _currentShowBreaks;
    private bool _currentShowHp;

    // The search box's placeholder doubles as the empty-filter state: while it shows, nothing is filtered.
    private bool _searchPlaceholder;

    public MirrorFightTable()
    {
      InitializeComponent();

      // Same placeholder idiom as the legacy table: the prompt is text in the box, cleared on focus.
      _searchPlaceholder = true;
      mirrorSearchBox.Text = Resource.NPC_SEARCH_TEXT;
      mirrorSearchBox.FontStyle = FontStyles.Italic;

      mirrorGrid.ItemsSource = _rows;
      mirrorShowBreaks.IsChecked = _currentShowBreaks = ConfigUtil.IfSet("NpcShowInactivityBreaks", true);

      // HP is the legacy table's own knob reading its own saved setting: the two grids sit side by side to be
      // compared, so one checkbox's meaning should not fork between them.
      mirrorShowHp.IsChecked = _currentShowHp = ConfigUtil.IfSet("NpcShowHitPoints");
      mirrorDamageColumn.IsHidden = !_currentShowHp;

      // The time column takes the theme's date-time width like every other table that stamps a line, rather
      // than a hand-picked number that stops fitting when the font scale changes.
      mirrorBeginColumn.Width = ThemeConfig.CurrentDateTimeWidth;
      ApplyFilter();

      // Legacy's search debounce, same interval: a name typed at fight speed arrives in a few hundred ms.
      _searchTextTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
      _searchTextTimer.Tick += (_, _) =>
      {
        _searchTextTimer.Stop();
        if (mirrorSearchBox.Text.Length > 0)
        {
          SearchForNpc();
        }
      };

      _selectionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SelectionSettleMs) };
      _selectionTimer.Tick += (_, _) =>
      {
        _selectionTimer.Stop();
        AnnounceSelection();
      };

      MirrorSession.ActiveChanged += OnActiveChanged;
      Attach(MirrorSession.Active);
    }

    private void OnActiveChanged()
    {
      Dispatcher.InvokeAsync(() =>
      {
        Detach();
        Attach(MirrorSession.Active);
        if (MirrorSession.Active is null)
        {
          _selectionTimer.Stop();
          _announcedIds = [];
          _rows.Clear();
          mirrorStatus.Text = "Mirror: no log";
        }
        else
        {
          mirrorStatus.Text = "Mirror: capturing...";
        }
      });
    }

    // Derivation completes on a background thread; rows swap on the dispatcher. Big logs derive
    // thousands of fights — a whole-collection ItemsSource swap re-lays-out once instead of
    // signalling every row insert, and ItemsSourceChanged reapplies the divider filter.
    private void OnDerived(MirrorSnapshot snapshot)
    {
      Dispatcher.InvokeAsync(() =>
      {
        // The mark is a reference to an OLD row that the swap discards: drop it with the rows, or a cleared
        // highlight would sit on nothing and the next search would skip its own bookkeeping.
        ClearSearchMark();
        /*
         * What the user had selected, remembered by name + start time and put back on the new rows.
         *
         * A derive is not a rare event on this panel - every identity override re-derives on purpose - so a
         * swap that dropped the selection would make the feature feel like it deleted the user's work: they set
         * `Dangle` to Pet, and the row they were looking at plus the board under it both vanish. Name and start
         * time are read off the same facts on every pass, so the fight (with this pass's numbers in it) is found
         * again - see FightKey for why the row number cannot be that key.
         *
         * The one case not restored is a user-sorted grid: the keys survive but their new positions depend on how
         * the sort landed, and guessing at row indices to restore a selection would put the highlight on some
         * other fight. Better to lose the highlight than to lie about it.
         */
        var keep = new HashSet<FightKey>();
        foreach (var fight in GetSelectedFights()) keep.Add(KeyOf(fight));
        var restorable = keep.Count > 0 && mirrorGrid.SortColumnDescriptions.Count == 0;

        // The rows the last announcement pointed at no longer exist: forget them rather than let the swap's
        // selection reset look like a change and clear a summary nobody touched. What is on that board now
        // came from the previous pass, and stays there until the next click - or until the selection below
        // comes back, which re-announces with THIS pass's numbers.
        _selectionTimer.Stop();
        _announcedIds = [];

        _rows = new ObservableCollection<MirrorFightRow>(snapshot.Rows);
        mirrorGrid.ItemsSource = _rows;

        if (restorable)
        {
          RestoreSelection(keep);
          AnnounceSelection();
        }

        // The verdict sentence goes on the end of the derive line, not in front of it: "which of my names moved"
        // is the question, and the counts under it are the context for the answer.
        mirrorStatus.Text = $"Derived {snapshot.DerivedAt:HH:mm:ss} - {snapshot.FightCount} fights, " +
                            $"{snapshot.FactCount:N0} facts, {snapshot.ElapsedMs:F0} ms" + OverrideOutcome();
      });
    }

    private void OnDeriveFailed(string message)
    {
      // Both halves on the dispatcher: this handler runs on the derive thread, and OverrideOutcome reads the
      // pending names from the UI thread - a cross-thread clear would only cost a sentence, but it is not that.
      Dispatcher.InvokeAsync(() =>
      {
        _pendingOverride = null;
        mirrorStatus.Text = $"Derive failed: {message}";
      });
    }

    /*
     * What became of the names the user just ruled on, phrased by what the list actually does now. Nothing here
     * guesses: a name still has a row if a row carrying its name came out of this pass, and "left the list" is
     * the honest wording for a name that is raid-side now (its damage still counts, inside whatever it hit).
     */
    private string OverrideOutcome()
    {
      if (_pendingOverride is not { Count: > 0 } names) return string.Empty;
      _pendingOverride = null;

      var listed = 0;
      foreach (var row in _rows)
      {
        if (row.Fight is not { } fight) continue;
        for (var i = 0; i < names.Count; i++)
          if (string.Equals(fight.Name, names[i], StringComparison.OrdinalIgnoreCase)) listed++;
      }

      var gone = names.Count - listed;
      if (gone == 0)
        return $" - override applied, all {names.Count} name{(names.Count == 1 ? "" : "s")} still listed";

      return $" - override applied: {gone} name{(gone == 1 ? "" : "s")} left the list"
             + (listed > 0 ? $", {listed} still on it" : string.Empty);
    }

    // Capture heartbeat (dispatcher thread already). Only fires while no snapshot covers the
    // newest facts, so it never stomps a fresh "Derived …" line.
    private void OnCapturing(long total)
    {
      mirrorStatus.Text = $"Mirror: capturing... {total:N0} captured";
    }

    private void Attach(MirrorSession session)
    {
      _session = session;
      if (session is not null)
      {
        session.Derived += OnDerived;
        session.DeriveFailed += OnDeriveFailed;
        session.Capturing += OnCapturing;
      }
      mirrorRederiveButton.IsEnabled = session is not null;
    }

    private void Detach()
    {
      if (_session is not null)
      {
        _session.Derived -= OnDerived;
        _session.DeriveFailed -= OnDeriveFailed;
        _session.Capturing -= OnCapturing;
      }
      _session = null;
    }

    private void MirrorGridItemsSourceChanged(object sender, Syncfusion.UI.Xaml.Grid.GridItemsSourceChangedEventArgs e) => ApplyFilter();

    private void MirrorSelectionChanged(object sender, GridSelectionChangedEventArgs e)
    {
      // Restart the pause on every click so a dragged range announces once, at the end.
      _selectionTimer.Stop();
      _selectionTimer.Start();
    }

    internal IReadOnlyList<DerivedFight> GetSelectedFights()
    {
      if (mirrorGrid?.SelectedItems is not { } items) return [];

      var selected = new List<DerivedFight>();
      foreach (var item in items)
      {
        // Divider rows are gaps, not fights — they carry no DerivedFight and select nothing.
        if (item is MirrorFightRow { IsDivider: false } row && row.Fight is { } fight) selected.Add(fight);
      }

      return selected;
    }

    // Whether this window has a live session behind it: the answer to "does the mirror answer for this log at
    // all", which is what MainWindow asks before choosing who owns GetFights while both windows can exist.
    internal bool SessionActive => _session != null;

    /*
     * The fights behind this window in the legacy shape the older consumers read: MainWindow.GetFights feeds
     * these to the spell/taunt/death/export paths. `selected` is the grid's own selection; false is every row
     * the list shows, in list order - the same set a "select all" would pick up.
     */
    internal List<Fight> GetFights(bool selected)
        => _session?.MaterializeFights(selected ? GetSelectedFights() : null) ?? [];

    // The scoped variant the death viewer wants per death click - see MirrorSession.MaterializeFightsOverlapping
    // for why materializing everything would be a full board's cost paid per keystroke.
    internal List<Fight> GetFightsOverlapping(double fromT, double toT)
        => _session?.MaterializeFightsOverlapping(fromT, toT) ?? [];

    private void AnnounceSelection()
    {
      var selected = GetSelectedFights();
      var ids = new List<int>(selected.Count);
      foreach (var fight in selected) ids.Add(fight.Id);

      if (SameIds(ids, _announcedIds)) return;

      _announcedIds = ids;
      DerivedSelectionChanged?.Invoke(selected);

      mirrorStatus.Text = selected.Count == 0
        ? "Selection cleared"
        : $"Damage summary from derived facts: {selected.Count} fight{(selected.Count == 1 ? "" : "s")}";
    }

    private static bool SameIds(List<int> a, List<int> b)
    {
      if (a.Count != b.Count) return false;
      for (var i = 0; i < a.Count; i++)
      {
        if (a[i] != b[i]) return false;
      }

      return true;
    }

    private void RederiveClick(object sender, RoutedEventArgs e) => _session?.RederiveAsync();

    private void ShowBreakChanged(object sender, RoutedEventArgs e)
    {
      // Load-time contract, the same one the legacy table's `dataGrid?.View != null` absorbs: XAML sets
      // IsChecked="True" WHILE InitializeComponent parses, firing this handler before ANY of this control's named
      // fields are wired - mirrorShowBreaks itself is still null then. Acting on that synthetic toggle would also
      // overwrite the stored setting before the constructor reads it, so skipping pre-load firings is required.
      if (mirrorShowBreaks is null) return;
      if (mirrorShowBreaks.IsChecked.HasValue && mirrorShowBreaks.IsChecked != _currentShowBreaks)
      {
        _currentShowBreaks = mirrorShowBreaks.IsChecked == true;
        ConfigUtil.SetSetting("NpcShowInactivityBreaks", _currentShowBreaks);
        ApplyFilter();
      }
    }

    // Show or hide the rounded-total column, like the legacy table's handler - except this one addresses its
    // column by name: legacy reaches for dataGrid.Columns[1], which breaks the moment anyone reorders the XAML.
    private void ShowHpChanged(object sender, RoutedEventArgs e)
    {
      // Load-time contract as in ShowBreakChanged: this fired mid-InitializeComponent with mirrorShowHp itself
      // null - the startup crash MainWindow's XAML construct hit 100% of launches. Guard first, then compare.
      if (mirrorShowHp is null) return;
      if (mirrorShowHp.IsChecked.HasValue && mirrorShowHp.IsChecked != _currentShowHp)
      {
        _currentShowHp = mirrorShowHp.IsChecked == true;
        ConfigUtil.SetSetting("NpcShowHitPoints", _currentShowHp);
        mirrorDamageColumn.IsHidden = !_currentShowHp;
      }
    }

  /*
     * R10 - the operator's verdict on a name, entered here and kept by MirrorOverrideStore.
     *
     * Actions go to every selected row at once (ctrl-click twenty rows, "all pets"), write ONE file, then ask
     * for a re-derive instead of editing the grid. That is the whole design: an override is a new reading of the
     * facts, and a reading changes more than the row's badge - which side its damage sits on, whether it keys a
     * row at all, and which raider its output folds under. Patching the visible row would leave the board, the
     * roll-ups and the next pass disagreeing with the label.
     *
     * The selection outlives the re-derive (see OnDerived), so the sequence reads as "that row changed shape",
     * not "my click cleared the window".
     */
    private void OverridePlayerClick(object sender, RoutedEventArgs e) => ApplyOverride(IdentityKind.Player);

    private void OverrideMercClick(object sender, RoutedEventArgs e) => ApplyOverride(IdentityKind.Merc);

    private void OverridePetClick(object sender, RoutedEventArgs e) => ApplyOverride(IdentityKind.Pet);

    private void OverrideNpcClick(object sender, RoutedEventArgs e) => ApplyOverride(IdentityKind.Npc);

    private void OverrideClearClick(object sender, RoutedEventArgs e) => ApplyOverride(null);

    // Names of the selected fights (dividers select nothing), written as one batch.
    private void ApplyOverride(IdentityKind? kind)
    {
      var names = new List<string>();
      foreach (var fight in GetSelectedFights()) names.Add(fight.Name);
      if (names.Count == 0) return;

      // Remembered so the derive that follows can say what became of these names (see OverrideOutcome).
      _pendingOverride = names;
      MirrorOverrideStore.Instance.Apply(names, kind);

      mirrorStatus.Text = kind is { } k
        ? $"Override: {names.Count} name{(names.Count == 1 ? "" : "s")} set to {k} - re-deriving"
        : $"Override cleared for {names.Count} name{(names.Count == 1 ? "" : "s")} - re-deriving";
      _session?.RederiveAsync();
    }

    // Greyed out unless the grid has a real fight selected; clearing is offered even with nothing selected,
    // because "what did I save?" is asked most often right after a name stops appearing in the list at all
    // (set as Pet and its row is gone by design, so there is nothing left to click).
    private void MirrorContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
      var hasFight = GetSelectedFights().Count > 0;

      /*
       * The position-based selects walk _rows in section order and select by grid POSITION (SelectByShown). A user sort
       * reorders the records under those positions, so on a sorted grid they would highlight arbitrary fights - and
       * announce them to the boards. Search does not have this problem: it walks the materialized view (SearchForNpc).
       * Offer them only while the grid is unsorted; RestoreSelection already guards the same way.
       */
      var unsorted = mirrorGrid.SortColumnDescriptions.Count == 0;

      // Enabled by what the grid can actually do with: all/unselect by current selection, group by a real row
      // under the cursor (a divider carries no section of its own).
      selectAllItem.IsEnabled = false;
      foreach (var row in _rows)
      {
        if (IsShown(row) && row.Fight is not null) { selectAllItem.IsEnabled = unsorted; break; }
      }

      unselectAllItem.IsEnabled = mirrorGrid.SelectedItems.Count > 0;
      var hasCurrent = mirrorGrid.CurrentItem is MirrorFightRow { IsDivider: false };
      selectGroupItem.IsEnabled = hasCurrent && unsorted;
      unselectGroupItem.IsEnabled = hasCurrent && mirrorGrid.SelectedItems.Count > 0;

      overridePlayerItem.IsEnabled = hasFight;
      overrideMercItem.IsEnabled = hasFight;
      overridePetItem.IsEnabled = hasFight;
      overrideNpcItem.IsEnabled = hasFight;

      // How many verdicts are on file for this server, so "Clear" says whether it has anything to do - the
      // question gets asked most often about a name that no longer has a row to click (set as Pet hides it by
      // design), where the menu is the only place left that can answer.
      var saved = MirrorOverrideStore.Instance.Count;
      overrideClearItem.IsEnabled = saved > 0 || hasFight;
      overrideClearItem.Header = saved > 0 ? $"Clear Override ({saved} saved)" : "Clear Override";
    }

    /*
     * The inactivity checkbox is this grid's only filter: search deliberately does NOT hide rows. The point of a
     * name here is to fight that raid event - the row needs to be HIGHLIGHTED and IN VIEW so the user can right-
     * click it and select the whole group, not removed from the list they were reading. That is the legacy table's
     * behavior, ported whole: one current result at a time, found on a debounce while typing, cycled with Enter /
     * Shift+Enter (SearchForNpc).
     */
    private void ApplyFilter()
    {
      if (mirrorGrid?.View == null) return;
      mirrorGrid.View.Filter = item => IsShown((MirrorFightRow)item);
      mirrorGrid.View.RefreshFilter();
    }

    private bool IsShown(MirrorFightRow row) => _currentShowBreaks || !row.IsDivider;

    // The search's own state, walking the VISIBLE view (not _rows) both directions from the last hit - the same
    // fields and arithmetic FightTable.SearchForNpc uses; ported, not re-invented.
    private readonly DispatcherTimer _searchTextTimer;
    private MirrorFightRow _searchEntry;
    private int _searchIndex;
    private int _searchDirection = 1;

    private void SearchBoxGotFocus(object sender, RoutedEventArgs e)
    {
      if (!_searchPlaceholder) return;
      _searchPlaceholder = false;
      mirrorSearchBox.Text = string.Empty;
      mirrorSearchBox.FontStyle = FontStyles.Normal;
    }

    private void RestoreSearchPlaceholder()
    {
      if (mirrorSearchBox.Text.Length == 0)
      {
        _searchPlaceholder = true;
        mirrorSearchBox.Text = Resource.NPC_SEARCH_TEXT;
        mirrorSearchBox.FontStyle = FontStyles.Italic;
      }
    }

    private void SearchBoxLostFocus(object sender, RoutedEventArgs e) => RestoreSearchPlaceholder();

    private void SearchBoxKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
      if (e.Key == System.Windows.Input.Key.Enter)
      {
        // Explicit next / previous, legacy's Shift-Enter for backwards: cycle the same way while typing does not.
        SearchForNpc(e.KeyboardDevice.IsKeyDown(System.Windows.Input.Key.RightShift)
                     || e.KeyboardDevice.IsKeyDown(System.Windows.Input.Key.LeftShift));
      }
      else if (e.Key == System.Windows.Input.Key.Escape)
      {
        // Legacy's escape: clear the box to its placeholder, drop the highlight, hand focus to the grid - the
        // next keystroke is a selection. The box text goes with it; there is nothing to cycle back to.
        _searchPlaceholder = true;
        mirrorSearchBox.Text = Resource.NPC_SEARCH_TEXT;
        mirrorSearchBox.FontStyle = FontStyles.Italic;
        ClearSearchMark();
        mirrorGrid.Focus();
      }
    }

    private void SearchBoxTextChanged(object sender, TextChangedEventArgs e)
    {
      _searchTextTimer?.Stop();

      // Legacy's debounce fires only when something was ADDED: backspacing alone must not re-search, but it does
      // drop the stale highlight so an old mark never sits on a row the text no longer names.
      if (_searchPlaceholder) return;
      if (e.Changes.FirstOrDefault(change => change.AddedLength > 0) != null)
      {
        _searchTextTimer?.Start();
      }
      else
      {
        ClearSearchMark();
      }
    }

    private void ClearSearchMark()
    {
      if (_searchEntry != null)
      {
        _searchEntry.IsSearchResult = false;
        _searchEntry = null;
      }
    }

    /*
     * The port of FightTable.SearchForNpc: walk the visible records from the last hit, in the last direction,
     * mark the one row that matches and scroll it into view. The index arithmetic (the += 2 / -= 2 on a
     * direction change, the two-pass wrap) is the legacy's word for word - it has lived with thousands of rows.
     */
    private void SearchForNpc(bool backwards = false)
    {
      ClearSearchMark();

      // Legacy walks View.Records - the materialized item list, not the view itself.
      var records = mirrorGrid.View.Records;
      if (mirrorSearchBox.Text.Length == 0 || records.Count == 0) return;

      int checksNeeded;
      var direction = 1;
      if (backwards)
      {
        direction = -1;
        if (_searchDirection != direction)
        {
          _searchIndex -= 2;
        }

        if (_searchIndex < 0)
        {
          _searchIndex = records.Count - 1;
        }

        // 1 check/loop from start to finish or add a 2nd to continue from the middle to element - 1
        checksNeeded = _searchIndex == (records.Count - 1) ? 1 : 2;
      }
      else
      {
        direction = 1;
        if (_searchDirection != direction)
        {
          _searchIndex += 2;
        }

        if (_searchIndex >= records.Count)
        {
          _searchIndex = 0;
        }

        // 1 check/loop from start to finish or add a 2nd to continue from the middle to element - 1
        checksNeeded = _searchIndex == 0 ? 1 : 2;
      }

      _searchDirection = direction;

      while (checksNeeded-- > 0)
      {
        for (var i = _searchIndex; i < records.Count && i >= 0; i += 1 * direction)
        {
          // Case-insensitive on purpose: rows are stored CapitalizeFirst and the user types however they type.
          // A divider row's name is the gap label, never a fight, so it simply never matches.
          if (records.GetItemAt(i) is MirrorFightRow { Name: not null } row &&
              row.Name.IndexOf(mirrorSearchBox.Text, StringComparison.OrdinalIgnoreCase) > -1)
          {
            row.IsSearchResult = true;
            _searchEntry = row;
            _searchIndex = i + (1 * direction);
            Dispatcher.InvokeAsync(() => mirrorGrid.ScrollInView(new RowColumnIndex(mirrorGrid.ResolveToRowIndex(i), 0)));
            return;
          }
        }

        if (checksNeeded == 1)
        {
          _searchIndex = (direction == 1) ? 0 : records.Count - 1;
        }
      }
    }

    /*
     * The right-click menu's selection items. Programmatic selects go through SelectRows over the SHOWN rows -
     * the same run walk RestoreSelection uses, because SelectRows wants contiguous grid ranges and a hidden row
     * (a filtered-out divider or name) takes no range. Each handler announces straight away rather than waiting
     * out the settle timer: "Select All" is one deliberate act, not a drag to debounce; the timer's later tick
     * re-announces the same ids and no-ops.
     */
    private void SelectAllClick(object sender, RoutedEventArgs e)
    {
      SelectByShown(row => row.Fight is not null);
      AnnounceSelection();
    }

    private void UnselectAllClick(object sender, RoutedEventArgs e)
    {
      mirrorGrid.SelectedItems.Clear();
      AnnounceSelection();
    }

    private void SelectGroupClick(object sender, RoutedEventArgs e) => SelectGroup(true);

    private void UnselectGroupClick(object sender, RoutedEventArgs e) => SelectGroup(false);

    // The group of a row is its SECTION: every fight between the same pair of inactivity rows - legacy's GroupId
    // meaning, read off THIS display list. Walked rather than taken from fight.GroupId on purpose: that stamp
    // comes from the walk over ALL fights (the "Fight N" number a stats run reads), while the dividers the user
    // sees come from the walk over the VISIBLE ones, and a hidden pet row active across a quiet gap can bridge a
    // divider in one but not the other. What the user sees is the definition, so the group stops at what the
    // user sees. A sorted grid does not matter: _rows keeps section order regardless of how the view is arranged.
    private void SelectGroup(bool add)
    {
      if (mirrorGrid.CurrentItem is not MirrorFightRow { IsDivider: false } target) return;

      var idx = _rows.IndexOf(target);
      var lo = idx;
      while (lo > 0 && !_rows[lo - 1].IsDivider) lo--;
      var hi = idx;
      while (hi + 1 < _rows.Count && !_rows[hi + 1].IsDivider) hi++;

      // Dividers are gaps, not fights: never selected, even though IsShown lets them through with breaks on.
      var section = new HashSet<MirrorFightRow>();
      for (var i = lo; i <= hi; i++)
        if (!_rows[i].IsDivider) section.Add(_rows[i]);
      Predicate<MirrorFightRow> inSection = row => section.Contains(row);

      if (add)
      {
        SelectByShown(inSection);
      }
      else
      {
        var remove = new HashSet<MirrorFightRow>();
        foreach (var item in mirrorGrid.SelectedItems)
        {
          if (item is MirrorFightRow { IsDivider: false } row && inSection(row)) remove.Add(row);
        }

        foreach (var row in remove) mirrorGrid.SelectedItems.Remove(row);
      }

      AnnounceSelection();
    }

    // The run walk RestoreSelection already needs: select every shown row matching the predicate, range by range.
    private void SelectByShown(Predicate<MirrorFightRow> matches)
    {
      var first = FirstRecordRow();
      var runStart = -1;
      for (var i = 0; i <= _rows.Count; i++)
      {
        var hit = i < _rows.Count && IsShown(_rows[i]) && matches(_rows[i]);
        if (hit && runStart < 0) runStart = i;
        else if (!hit && runStart >= 0)
        {
          mirrorGrid.SelectRows(first + runStart, first + i - 1);
          runStart = -1;
        }
      }
    }

    /*
     * Put the highlight back on the rows whose fight ids survived the swap.
     *
     * SelectRows wants CONTIGUOUS grid-row ranges, so the kept set is walked as runs. Positions are counted over
     * the shown rows only (a hidden divider takes no grid row), and offset by wherever this grid's records
     * actually start rather than by an assumed header height.
     */
    private static FightKey KeyOf(DerivedFight fight) => new(fight.Name, fight.BeginTime);

    private void RestoreSelection(HashSet<FightKey> keepKeys)
    {
      var first = FirstRecordRow();
      var runStart = -1;
      for (var i = 0; i <= _rows.Count; i++)
      {
        var hit = i < _rows.Count && IsShown(_rows[i]) && _rows[i].Fight is { } fight && keepKeys.Contains(KeyOf(fight));
        if (hit && runStart < 0) runStart = i;
        else if (!hit && runStart >= 0)
        {
          mirrorGrid.SelectRows(first + runStart, first + i - 1);
          runStart = -1;
        }
      }
    }

    // Where the data rows begin, read off the grid instead of assumed: a row that is not a record (header,
    // filter row, footer) resolves to no record at all, so the first index answering 0 IS the first row.
    private int FirstRecordRow()
    {
      for (var i = 0; i < 8; i++)
      {
        if (mirrorGrid.ResolveToRecordIndex(i) == 0) return i;
      }
      return 1;
    }
  }
}
