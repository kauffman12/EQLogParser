using Syncfusion.UI.Xaml.Grid;
using Syncfusion.UI.Xaml.ScrollAxis;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using EQLogParser;

namespace EQLogParser
{
  /*
   * THE fight list. Every grid convention is the old table's, kept on purpose: the same columns in the legacy order
   * (Initial Hit Time | HP | Name, duration and hits on the row tooltip), the same search/HP/Inactivity/Tanking
   * header - but the rows are DERIVED, not accumulated (CombatCapture → ClassificationRules → FightProjection →
   * Sectionizer), so they re-derive when an override changes what a name is. When the legacy list was deleted this
   * pane took over its window name and menu entry too. Selection feeds the summary boards from the captured facts
   * (see DerivedSelectionChanged), and the right-click menu is where R10 lives: say what a name actually is
   * (Set as Player / Mercenary / Pet / NPC), which saves per server and re-derives.
   */
  public partial class FightTable
  {
    /*
     * Raised when the selection settles, with the derived fights behind the selected rows (empty when
     * nothing is selected — an empty selection means "show no data", same as the old list's rule).
     *
     * One-way on purpose: the grid says WHICH fights, and MainWindow decides what a board is. Nothing can push
     * a selection back into the grid, so no viewer can ever make the list disagree with itself.
     */
    internal event Action<IReadOnlyList<DerivedFight>> DerivedSelectionChanged;

    // Selection settles on a short pause rather than per click, and much more cheaply than the 750 ms the
    // legacy table waits: nothing is recomputed here, the click only says which fights are wanted. Long
    // enough that dragging a range across a thousand rows fires once, short enough to feel immediate.
    private const int SelectionSettleMs = 350;

    private ObservableCollection<DerivedFightRow> _rows = [];
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
     * Loading-band state (see loadOverlay in the XAML). `_loadBandSettled` is this session's "a snapshot has
     * landed" flag: the band goes down for good at the first Derived - a quiet stretch mid-file can legitimately
     * complete a derive under 100 %, and real rows beat a bar - and it comes back only with the next session.
     * `_capturedFacts` rides in from the session's capture event to put a number on what the build is chewing
     * through; reading itself is announced by the application-wide status line, not here (see ReportCaptureProgress).
     */
    private bool _loadBandSettled;
    private long _capturedFacts;

    /*
     * What a row IS, across derives. DerivedFight.Id cannot say that: FightProjection renumbers the list on every
     * pass (Id = i + 1 over the rows of THAT pass), so one override - which removes rows on purpose - shifts
     * every number after it, and restoring by id would highlight whatever fight moved into the old slot while
     * showing its numbers underneath. A section of a name is identified by the name and the moment it began;
     * both come out of the same facts on every pass.
     */
    private readonly record struct FightKey(string Name, double BeginTime);

    private DeriveEngine _session;
    private bool _currentShowBreaks;
    private bool _currentShowHp;
    private bool _currentShowTanking;

    // The search box's placeholder doubles as the empty-filter state: while it shows, nothing is filtered.
    private bool _searchPlaceholder;

    public FightTable()
    {
      InitializeComponent();

      // Same placeholder idiom as the legacy table: the prompt is text in the box, cleared on focus.
      _searchPlaceholder = true;
      fightSearchBox.Text = Resource.NPC_SEARCH_TEXT;
      fightSearchBox.FontStyle = FontStyles.Italic;

      fightGrid.ItemsSource = _rows;
      fightShowBreaks.IsChecked = _currentShowBreaks = ConfigUtil.IfSet("NpcShowInactivityBreaks", true);

      // HP is the legacy table's own knob reading its own saved setting: the two grids sit side by side to be
      // compared, so one checkbox's meaning should not fork between them.
      fightShowHp.IsChecked = _currentShowHp = ConfigUtil.IfSet("NpcShowHitPoints");
      damageColumn.IsHidden = !_currentShowHp;

      // Same inheritance for the tanking dial, same saved word ("NpcShowTanking", default on): an operator who
      // hides mob-only rows in the legacy table keeps that filter on arrival.
      fightShowTanking.IsChecked = _currentShowTanking = ConfigUtil.IfSet("NpcShowTanking", true);

      // The time column takes the theme's date-time width like every other table that stamps a line, rather
      // than a hand-picked number that stops fitting when the font scale changes.
      beginColumn.Width = ThemeConfig.CurrentDateTimeWidth;
      ApplyFilter();

      // Legacy's search debounce, same interval: a name typed at fight speed arrives in a few hundred ms.
      _searchTextTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
      _searchTextTimer.Tick += (_, _) =>
      {
        _searchTextTimer.Stop();
        if (fightSearchBox.Text.Length > 0)
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

      DeriveEngine.ActiveChanged += OnActiveChanged;
      Attach(DeriveEngine.Active);
    }

    private void OnActiveChanged()
    {
      Dispatcher.InvokeAsync(() =>
      {
        Detach();
        Attach(DeriveEngine.Active);
        if (DeriveEngine.Active is null)
        {
          _selectionTimer.Stop();
          _announcedIds = [];
          _rows.Clear();
          fightStatus.Text = "No log open";
          SetLoadBand(null);
        }
        else
        {
          // A new session is a new load: the band may show again, and its counters start from nothing.
          _loadBandSettled = false;
          _capturedFacts = 0;
          fightStatus.Text = "Capturing...";
        }
      });
    }

    // Derivation completes on a background thread; rows swap on the dispatcher. Big logs derive
    // thousands of fights — a whole-collection ItemsSource swap re-lays-out once instead of
    // signalling every row insert, and ItemsSourceChanged reapplies the divider filter.
    // Internal for the band's WPF test (a live session is not needed to hand this panel a snapshot); every
    // production call arrives through the session's Derived event.
    internal void OnDerived(DerivedSnapshot snapshot)
    {
      Dispatcher.InvokeAsync(() =>
      {
        // Whatever the band was saying, the list itself is now the answer - including the mid-load case where
        // the reader pump is still under 100 %: real rows beat a progress bar, and for this session they win.
        _loadBandSettled = true;
        SetLoadBand(null);

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
        var restorable = keep.Count > 0 && fightGrid.SortColumnDescriptions.Count == 0;

        // The rows the last announcement pointed at no longer exist: forget them rather than let the swap's
        // selection reset look like a change and clear a summary nobody touched. What is on that board now
        // came from the previous pass, and stays there until the next click - or until the selection below
        // comes back, which re-announces with THIS pass's numbers.
        _selectionTimer.Stop();
        _announcedIds = [];

        _rows = new ObservableCollection<DerivedFightRow>(snapshot.Rows);
        fightGrid.ItemsSource = _rows;

        if (restorable)
        {
          RestoreSelection(keep);
          AnnounceSelection();
        }

        // The header says nothing more about this pass. "Derived HH:mm:ss - N fights, M facts, X ms" never fit
        // the dock beside three columns, and the rows themselves are the message; what still earns the space is the
        // override verdict (the user asked a question seconds before) and clearing a placeholder or stale failure
        // line that has served its turn. Selection messages come from AnnounceSelection and must survive.
        var outcome = OverrideOutcome().TrimStart(' ', '-');
        if (outcome.Length > 0)
          fightStatus.Text = outcome;
        else if (fightStatus.Text == "No log open"
              || fightStatus.Text.StartsWith("Capturing", StringComparison.Ordinal)
              || fightStatus.Text.StartsWith("Derive failed", StringComparison.Ordinal))
          fightStatus.Text = string.Empty;
      });
    }

    private void OnDeriveFailed(string message)
    {
      // Both halves on the dispatcher: this handler runs on the derive thread, and OverrideOutcome reads the
      // pending names from the UI thread - a cross-thread clear would only cost a sentence, but it is not that.
      Dispatcher.InvokeAsync(() =>
      {
        _pendingOverride = null;
        // The session retries a failed pass itself (backoff ladder, DeriveEngine); the line says so rather
        // than leaving the reader looking for a button that no longer exists.
        fightStatus.Text = $"Derive failed (retrying automatically): {message}";
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

    /*
     * Capture heartbeat (dispatcher thread already). The count used to replace the status line every tick, which
     * read as a terminal ticking past; it belongs on the loading band, next to the file progress that says when
     * the number will stop moving. With no band showing there is nothing to update - a settled list refreshes
     * through OnDerived, which is the message.
     */
    private void OnCapturing(long total)
    {
      _capturedFacts = total;
      if (loadOverlay.Visibility == Visibility.Visible) UpdateLoadDetail();
    }

    /*
     * The reader pump MainWindow drives every ~500 ms while a file is open (byte progress through the log). Called
     * on the dispatcher; `percent` counts against the size the file had when reading began, so a growing live tail
     * runs past 100 - >= 100 means "reading is done", which is the truth from the reader's side, and the panel then
     * waits for the first snapshot on its own. The percent itself is NOT shown here - the application-wide status
     * line counts it already, and a second copy in the dock duplicates it without adding anything.
     */
    internal void ReportCaptureProgress(double percent)
    {
      if (Dispatcher.CheckAccess() == false)
      {
        Dispatcher.InvokeAsync(() => ReportCaptureProgress(percent));
        return;
      }

      // No session gate: while the reader pump runs this window is either showing the band or docked-hidden
      // (engine off), and a hidden band costs nothing. The only veto is "rows already landed this session".
      if (_loadBandSettled) return;

      // The reading phase says nothing HERE: the status line at the top of the application counts the same pump's
      // percent (and seconds) already, and a second copy in the dock duplicates it. The one gap only this panel can
      // speak of is EOF-to-first-snapshot - file done, rows still being built - so that is all the band shows.
      if (percent >= 100.0)
        SetLoadBand("Building derived fight list\u2026");
    }

    // null takes the band down; UI thread. The bar is the XAML's own indeterminate one - the only phase this band
    // speaks of has no known length, and the phase that DOES (reading) reports through the application status line.
    private void SetLoadBand(string headline)
    {
      if (headline is null)
      {
        loadOverlay.Visibility = Visibility.Collapsed;
        return;
      }

      loadText.Text = headline;
      UpdateLoadDetail();
      loadOverlay.Visibility = Visibility.Visible;
    }

    private void UpdateLoadDetail()
      => loadDetail.Text = _capturedFacts > 0 ? $"{_capturedFacts:N0} facts captured" : "waiting for first facts";

    private void Attach(DeriveEngine session)
    {
      _session = session;
      if (session is not null)
      {
        session.Derived += OnDerived;
        session.DeriveFailed += OnDeriveFailed;
        session.Capturing += OnCapturing;
      }
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

    private void FightGridItemsSourceChanged(object sender, Syncfusion.UI.Xaml.Grid.GridItemsSourceChangedEventArgs e) => ApplyFilter();

    private void MirrorSelectionChanged(object sender, GridSelectionChangedEventArgs e)
    {
      // Restart the pause on every click so a dragged range announces once, at the end.
      _selectionTimer.Stop();
      _selectionTimer.Start();
    }

    internal IReadOnlyList<DerivedFight> GetSelectedFights()
    {
      if (fightGrid?.SelectedItems is not { } items) return [];

      var selected = new List<DerivedFight>();
      foreach (var item in items)
      {
        // Divider rows are gaps, not fights — they carry no DerivedFight and select nothing.
        if (item is DerivedFightRow { IsDivider: false } row && row.Fight is { } fight) selected.Add(fight);
      }

      return selected;
    }

    // Whether this window has a live session behind it: the answer to "does the engine answer for this log at
    // all", which is what MainWindow asks before choosing who owns GetFights while both windows can exist.
    internal bool SessionActive => _session != null;

    /*
     * The fights behind this window in the legacy shape the older consumers read: MainWindow.GetFights feeds
     * these to the spell/taunt/death/export paths. `selected` is the grid's own selection; false is every row
     * the list shows, in list order - the same set a "select all" would pick up.
     */
    internal List<Fight> GetFights(bool selected)
        => _session?.MaterializeFights(selected ? GetSelectedFights() : null) ?? [];

    // The scoped variant the death viewer wants per death click - see DeriveEngine.MaterializeFightsOverlapping
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

      fightStatus.Text = selected.Count == 0
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

    private void ShowBreakChanged(object sender, RoutedEventArgs e)
    {
      // Load-time contract: XAML sets IsChecked="True" WHILE InitializeComponent parses, and this handler must
      // absorb that synthetic toggle - acting on it would overwrite the stored setting before the constructor
      // reads it. The sentinel is the pane's readiness (the grid's View, materialized only when the constructor
      // assigns ItemsSource), NOT the sender's nullness: a real startup firing was measured where this
      // checkbox's field WAS already wired and a later element in the markup (a column) was not - so testing
      // fightShowBreaks here would let that firing through to apply work on half-built state. Legacy tests
      // dataGrid?.View != null everywhere for exactly this reason; a pane's own readiness is the only thing a
      // mid-parse firing can be shown not to have.
      if (fightGrid?.View is null) return;
      if (fightShowBreaks.IsChecked.HasValue && fightShowBreaks.IsChecked != _currentShowBreaks)
      {
        _currentShowBreaks = fightShowBreaks.IsChecked == true;
        ConfigUtil.SetSetting("NpcShowInactivityBreaks", _currentShowBreaks);
        ApplyFilter();
      }
    }

    // Show or hide the rounded-total column, like the legacy table's handler - except this one addresses its
    // column by name: legacy reaches for dataGrid.Columns[1], which breaks the moment anyone reorders the XAML.
    private void ShowHpChanged(object sender, RoutedEventArgs e)
    {
      // Load-time contract as in ShowBreakChanged - same pane-readiness sentinel, same reason: the measured
      // startup crashes came in BOTH shapes (once with this checkbox's own field still null, once wired while
      // damageColumn below was not), so only `View` separates a synthetic toggle from the constructor's
      // real sync of the saved setting.
      if (fightGrid?.View is null) return;
      if (fightShowHp.IsChecked.HasValue && fightShowHp.IsChecked != _currentShowHp)
      {
        _currentShowHp = fightShowHp.IsChecked == true;
        ConfigUtil.SetSetting("NpcShowHitPoints", _currentShowHp);
        damageColumn.IsHidden = !_currentShowHp;
      }
    }

    // "Include Fights with only Tanking data", the legacy table's third dial, ported. Same pane-readiness
    // sentinel as ShowBreakChanged/ShowHpChanged (docs/DesignNotes.md -> "Handlers that XAML fires early"):
    // the synthetic IsChecked="True" toggle lands mid-InitializeComponent and must not write the setting
    // before the constructor reads it.
    private void ShowTankingChanged(object sender, RoutedEventArgs e)
    {
      if (fightGrid?.View is null) return;
      if (fightShowTanking.IsChecked.HasValue && fightShowTanking.IsChecked != _currentShowTanking)
      {
        _currentShowTanking = fightShowTanking.IsChecked == true;
        ConfigUtil.SetSetting("NpcShowTanking", _currentShowTanking);
        ApplyFilter();
      }
    }

  /*
     * R10 - the operator's verdict on a name, entered here and kept by IdentityOverrideStore.
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
      IdentityOverrideStore.Instance.Apply(names, kind);

      fightStatus.Text = kind is { } k
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
      var unsorted = fightGrid.SortColumnDescriptions.Count == 0;

      // Enabled by what the grid can actually do with: all/unselect by current selection, group by a real row
      // under the cursor (a divider carries no section of its own).
      selectAllItem.IsEnabled = false;
      foreach (var row in _rows)
      {
        if (IsShown(row) && row.Fight is not null) { selectAllItem.IsEnabled = unsorted; break; }
      }

      unselectAllItem.IsEnabled = fightGrid.SelectedItems.Count > 0;
      var hasCurrent = fightGrid.CurrentItem is DerivedFightRow { IsDivider: false };
      selectGroupItem.IsEnabled = hasCurrent && unsorted;
      unselectGroupItem.IsEnabled = hasCurrent && fightGrid.SelectedItems.Count > 0;

      overridePlayerItem.IsEnabled = hasFight;
      overrideMercItem.IsEnabled = hasFight;
      overridePetItem.IsEnabled = hasFight;
      overrideNpcItem.IsEnabled = hasFight;

      // How many verdicts are on file for this server, so "Clear" says whether it has anything to do - the
      // question gets asked most often about a name that no longer has a row to click (set as Pet hides it by
      // design), where the menu is the only place left that can answer.
      var saved = IdentityOverrideStore.Instance.Count;
      overrideClearItem.IsEnabled = saved > 0 || hasFight;
      overrideClearItem.Header = saved > 0 ? $"Clear Override ({saved} saved)" : "Clear Override";
    }

    /*
     * Two row dials, no more - Inactivity (dividers) and Tanking (rows whose whole story is something hitting
     * us). Search deliberately does NOT hide rows. The point of a
     * name here is to fight that raid event - the row needs to be HIGHLIGHTED and IN VIEW so the user can right-
     * click it and select the whole group, not removed from the list they were reading. That is the legacy table's
     * behavior, ported whole: one current result at a time, found on a debounce while typing, cycled with Enter /
     * Shift+Enter (SearchForNpc).
     */
    private void ApplyFilter()
    {
      if (fightGrid?.View == null) return;
      fightGrid.View.Filter = item => IsShown((DerivedFightRow)item);
      fightGrid.View.RefreshFilter();
    }

    private bool IsShown(DerivedFightRow row) => row.IsDivider
      ? _currentShowBreaks
      : _currentShowTanking || ShownWhenTankingHidden(row.Fight);

    // Legacy swapped whole lists for this dial (_fights vs _nonTankingFights); one predicate over the derived
    // rows says the same thing. DamageToOwner is the raid's output ON this row, so zero means the line exists
    // only because the anchor was hitting us. A person-row reads above zero whenever her person was struck,
    // so nobody vanishes under the dial. Split out because it is the whole decision - and testable.
    internal static bool ShownWhenTankingHidden(DerivedFight fight) => fight is null || fight.DamageToOwner > 0;

    // The search's own state, walking the VISIBLE view (not _rows) both directions from the last hit - the same
    // fields and arithmetic FightTable.SearchForNpc uses; ported, not re-invented.
    private readonly DispatcherTimer _searchTextTimer;
    private DerivedFightRow _searchEntry;
    private int _searchIndex;
    private int _searchDirection = 1;

    private void SearchBoxGotFocus(object sender, RoutedEventArgs e)
    {
      if (!_searchPlaceholder) return;
      _searchPlaceholder = false;
      fightSearchBox.Text = string.Empty;
      fightSearchBox.FontStyle = FontStyles.Normal;
    }

    private void RestoreSearchPlaceholder()
    {
      if (fightSearchBox.Text.Length == 0)
      {
        _searchPlaceholder = true;
        fightSearchBox.Text = Resource.NPC_SEARCH_TEXT;
        fightSearchBox.FontStyle = FontStyles.Italic;
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
        fightSearchBox.Text = Resource.NPC_SEARCH_TEXT;
        fightSearchBox.FontStyle = FontStyles.Italic;
        ClearSearchMark();
        fightGrid.Focus();
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
      var records = fightGrid.View.Records;
      if (fightSearchBox.Text.Length == 0 || records.Count == 0) return;

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
          if (records.GetItemAt(i) is DerivedFightRow { Name: not null } row &&
              row.Name.IndexOf(fightSearchBox.Text, StringComparison.OrdinalIgnoreCase) > -1)
          {
            row.IsSearchResult = true;
            _searchEntry = row;
            _searchIndex = i + (1 * direction);
            Dispatcher.InvokeAsync(() => fightGrid.ScrollInView(new RowColumnIndex(fightGrid.ResolveToRowIndex(i), 0)));
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
      fightGrid.SelectedItems.Clear();
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
      if (fightGrid.CurrentItem is not DerivedFightRow { IsDivider: false } target) return;

      var idx = _rows.IndexOf(target);
      var lo = idx;
      while (lo > 0 && !_rows[lo - 1].IsDivider) lo--;
      var hi = idx;
      while (hi + 1 < _rows.Count && !_rows[hi + 1].IsDivider) hi++;

      // Dividers are gaps, not fights: never selected, even though IsShown lets them through with breaks on.
      var section = new HashSet<DerivedFightRow>();
      for (var i = lo; i <= hi; i++)
        if (!_rows[i].IsDivider) section.Add(_rows[i]);
      Predicate<DerivedFightRow> inSection = row => section.Contains(row);

      if (add)
      {
        SelectByShown(inSection);
      }
      else
      {
        var remove = new HashSet<DerivedFightRow>();
        foreach (var item in fightGrid.SelectedItems)
        {
          if (item is DerivedFightRow { IsDivider: false } row && inSection(row)) remove.Add(row);
        }

        foreach (var row in remove) fightGrid.SelectedItems.Remove(row);
      }

      AnnounceSelection();
    }

    // The run walk RestoreSelection already needs: select every shown row matching the predicate, range by range.
    private void SelectByShown(Predicate<DerivedFightRow> matches)
    {
      var first = FirstRecordRow();
      var runStart = -1;
      for (var i = 0; i <= _rows.Count; i++)
      {
        var hit = i < _rows.Count && IsShown(_rows[i]) && matches(_rows[i]);
        if (hit && runStart < 0) runStart = i;
        else if (!hit && runStart >= 0)
        {
          fightGrid.SelectRows(first + runStart, first + i - 1);
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
          fightGrid.SelectRows(first + runStart, first + i - 1);
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
        if (fightGrid.ResolveToRecordIndex(i) == 0) return i;
      }
      return 1;
    }
  }
}
