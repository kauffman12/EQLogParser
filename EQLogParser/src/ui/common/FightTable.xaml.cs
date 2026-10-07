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

    /*
     * Selection settles on a pause rather than per click: nothing is recomputed here, the click only says which fights are
     * wanted, and what the announcement costs (MainWindow materialises one record per selected fact and runs three builders)
     * is worth waiting a beat for. Long enough that dragging a range across a thousand rows fires once; short enough not to
     * feel like a lag.
     *
     * 600 ms, chosen by the operator after the first field run with the gate in place. This started at 350 as my own guess -
     * nothing measured it - and legacy's was 750. The number is deliberately inert: no other code computes off it, and the two
     * things that actually prevent mid-gesture rebuilds (menu open, button down) are states, not durations, so moving this dial
     * changes only how long a still selection waits before its boards are rebuilt.
     */
    private const int SelectionSettleMs = 600;

    private ObservableCollection<DerivedFightRow> _rows = [];
    private readonly DispatcherTimer _selectionTimer;

    /*
     * Whether a tick may announce, or whether something is still happening under the cursor. Two parked cases (see
     * SelectionSettle for the reasoning): the right-click menu is open - where SfDataGrid's own move of the current
     * cell to the clicked row would otherwise spend a full materialization before the user has chosen anything, and a
     * Select All then spends a second one, which is what "it ends up doing two selections" was - and a mouse button still
     * down mid-drag. The pointer state is QUERIED at tick time, never latched from down/up events, so a lost button-up
     * cannot wedge announcements off. BOTH buttons count: SfDataGrid selects on a right-drag as well as a left one, and
     * "right-drag to pick a range, then open the menu" is precisely the gesture this pane has to survive.
     */
    private readonly SelectionSettle _settle = new(PointerIsDown);

    /// <summary>Any mouse button held over the app: a drag in progress parks a selection announcement.</summary>
    internal static bool PointerIsDown() =>
      System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed ||
      System.Windows.Input.Mouse.RightButton == System.Windows.Input.MouseButtonState.Pressed;

    // What was last announced, as fight ids. Two jobs: a stale snapshot's rows cannot be re-announced as
    // if they were new, and a grid that re-raises SelectionChanged with the same selection (or with none,
    // when an ItemsSource swap lands) must not clear stats nobody changed.
    private List<int> _announcedIds = [];

    /*
     * What the announced ids were last MATERIALIZED against: the snapshot's own content stamp (see SelectionStamp). The
     * announcement is what spends the expensive work - MainWindow materializes one record per selected fact and runs three
     * builders - so "did anything this pass could render move?" is the question that decides it, and instance identity cannot
     * answer it: a reclassifying rebuild replaces every backing DerivedFight, so "the object changed" is true on every full
     * pass whether or not a single figure did (measured cost of that misreading: a whole-capture selection re-materializes in
     * seconds, once per pass, forever - docs/DesignNotes.md → "What makes a board go one pass stale").
     */
    private long _announcedStamp;

    // The stamp of the newest snapshot this pane has been handed (the one _rows came from). Announcing records it, so
    // "what did the board get built from?" stays answerable while a pass is in flight.
    private long _currentStamp;

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

    /*
     * Pane readiness for the dial handlers: false until the constructor's last line. A synthetic IsChecked toggle
     * fires DURING InitializeComponent (the XAML sets IsChecked="True" on every dial), and at that moment this flag
     * is still false; by the time it flips, every XAML-named field is wired. It is deliberately NOT fightGrid.View:
     * SfDataGrid materializes View only on its Loaded pass - its ItemsSource callback (OnItemsSourceChanged) returns
     * early while the grid is not yet loaded, and SetSourceList/CreateCollectionView run from the load path only -
     * so a pane that is constructed but never arranged keeps View null forever and a View guard would swallow every
     * REAL toggle (measured: FightTableStartupTest's post-load uncheck landed in exactly that dead zone). The handler
     * bodies touch columns and saved settings, and ApplyFilter already tolerates an absent view until one exists.
     */
    private bool _paneReady;

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

      // The time column's width is applied at Loaded, not here - see ApplyTimeColumnWidth: ThemeConfig does not
      // exist yet at constructor time, and a zero-width fixed column "exists" without ever showing in the grid.
      // The theme listener attaches with the load and leaves with the pane (ThemeConfig's event is process-static
      // and outlives every pane; this class is constructed by MainWindow AND by the headless test host).
      fightGrid.Loaded += OnGridLoaded;
      Unloaded += (_, _) => ThemeConfig.EventsThemeChanged -= EventsThemeChanged;

      // The identity cascade is filled here rather than declared four times in markup, so this pane and the three summary panes cannot
      // drift about what the words are or what they write (IdentityVerdictMenu). Called before any menu can open; idempotent, because the
      // WPF test host constructs this control too.
      IdentityVerdictMenu.Populate(overrideSetItem, ApplyVerdict);

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
        if (_settle.ShouldAnnounce()) AnnounceSelection();
        else if (_settle.Pending) _selectionTimer.Start();   // still parked (menu open / button held): ask again
      };

      DeriveEngine.ActiveChanged += OnActiveChanged;
      Attach(DeriveEngine.Active);

      // Last line on purpose: the dial handlers gate on this, and a synthetic IsChecked firing from the XAML parse
      // must not outrun it. From here on every XAML field is wired, so a real toggle can act on full state.
      _paneReady = true;
    }

    /*
     * The ONE place that decides the time column's width, so the two hooks below cannot drift apart. internal for
     * FightTableTimeColumnTest: neither hook can be raised from a windowless host (that class says why), so the test
     * measures this method and the fact that a constructor run under a themeless ThemeConfig leaves the column at 0.
     */
    internal void ApplyTimeColumnWidth() => beginColumn.Width = ThemeConfig.CurrentDateTimeWidth;

    /*
     * The time column's width comes from ThemeConfig, and MainWindow initializes that only AFTER this pane's
     * constructor has returned (InitializeComponent → SetMainWindow → ThemeConfig.Init). Reading the static in
     * the ctor hands back 0.0, and a zero-width fixed column "exists" but shows nothing in the grid - which is
     * how the pane shipped without its Initial Hit Time while its HP sibling, Auto-sized by its own sizer,
     * looked fine. Loaded fires strictly later than the theme init.
     */
    private void OnGridLoaded(object sender, RoutedEventArgs e)
    {
      ApplyTimeColumnWidth();

      // Re-armed rather than added once: a re-dock unloads and reloads this pane, and an element that never
      // unloaded can raise Loaded twice - the listener must stay exactly one entry deep.
      ThemeConfig.EventsThemeChanged -= EventsThemeChanged;
      ThemeConfig.EventsThemeChanged += EventsThemeChanged;
    }

    private void EventsThemeChanged(string _) => ApplyTimeColumnWidth();

    private void OnActiveChanged()
    {
      Dispatcher.InvokeAsync(() =>
      {
        Detach();
        Attach(DeriveEngine.Active);
        if (DeriveEngine.Active is null)
        {
          _selectionTimer.Stop();
          _settle.Reset();
          _announcedIds = [];

          // Nothing is announced, so nothing may be remembered as announced: a stamp left over from the log that just closed
          // could otherwise match a new capture's first snapshot by arithmetic (fact totals restart at zero every session) and
          // swallow an announcement that has never happened.
          _announcedStamp = 0;
          _rows.Clear();
          SetLoadBand(null);
        }
        else
        {
          // A new session is a new load: the band may show again, and its counters start from nothing.
          _loadBandSettled = false;
          _capturedFacts = 0;
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
        if (snapshot?.Rows == null) return;

        _currentStamp = SelectionStamp(snapshot);

        // Whatever the band was saying, the list itself is now the answer - including the mid-load case where
        // the reader pump is still under 100 %: real rows beat a progress bar, and for this session they win.
        _loadBandSettled = true;
        SetLoadBand(null);

        /*
         * Patch first, rebuild only when the patch cannot be honest about it (RowPatch: a re-sort, a duplicate key, or more than
         * about half the list churned). On a live raid night that means the ordinary pass updates ~40 rows and leaves ~730 alone
         * (docs/DesignNotes.md → "Would an equality gate have saved anything?"), so the row objects under the reader's selection,
         * the scroll position and the search mark all survive untouched — no re-lookup by name, no blink.
         */
        /*
         * Two states keep the wholesale path, both because a patch cannot make their bookkeeping come true. A SORTED grid would hold
         * updated cells in yesterday's order (this build's Syncfusion has no live re-sort the pane can call cheaply, and guessing row
         * positions is the failure mode the selection code already refuses). And with "show tanking" off the view filters per ROW on
         * Fight.DamageToOwner — a field a patch mutates — and there is no filter refresh to ask for either (checked against the shipped
         * assembly: no RefreshLiveFilter), so a row that starts or stops taking damage would stay hidden or stay shown. Patching the
         * unfiltered, unsorted list — which is what a live raid pane spends its whole night in — is where the win lives.
         */
        var canPatch = _rows.Count > 0 && fightGrid.SortColumnDescriptions.Count == 0 && _currentShowTanking;
        var patch = canPatch
          ? RowPatch.Build<DerivedFightRow>(_rows, snapshot.Rows, static row => row.Key,
                                            static (a, b) => a.SameDisplayAs(b), Math.Max(256, _rows.Count / 2))
          : null;
        if (patch != null)
        {
          // What the reader has selected, by INSTANCE: those objects are about to stay in the list, so no restore is needed — but a
          // selected row that this pass edited means the boards under it now show one pass-old numbers.
          var selected = fightGrid?.SelectedItems is { } items ? items.Cast<object>().ToList() : [];
          var contentMoved = SelectionStamp(snapshot) != _announcedStamp;

          // The refresh runs on EVERY survivor, not just the display-updated ones: a reclassifying rebuild hands out brand-new
          // DerivedFight objects for every row while most cells read identically, and the snapshot this pass published is keyed on
          // the new objects — a survivor still holding last pass's fight would materialize as "no damage, no tanking" the next time
          // its boards are built.
          RowPatch.Apply(_rows, patch, static (target, source) => target.CopyDisplayFrom(source),
                               static (target, source) => target.Fight = source.Fight);

          // A marked row that left the list takes its highlight with it; one that stayed keeps it WITHOUT a repaint, which is the
          // difference between a search mark that holds and one that blinks off twice a second.
          if (_searchEntry != null && !_rows.Contains(_searchEntry)) ClearSearchMark();

          /*
           * Re-announce only when there is something to announce: the same selection over rows this pass did not touch still describes
           * the same board, and re-materializing it on every pass would spend a stats run to redraw identical figures. An empty
           * selection announces nothing either way.
           *
           * Two signals, because they fail differently. The row-level diff catches what THIS list saw move. The stamp catches what the
           * list cannot: a rebuild where every cell reads identically but the ANSWER changed - an ownership or charm verdict moved, so
           * the same facts now split between `X +Pets` and the mob, and damage/tanking routing followed. That is invisible to
           * SameDisplayAs and to object identity alike; the stamp is exactly its two causes (newly captured facts, moved verdicts).
           */
          var touched = selected.Count > 0 &&
                        (contentMoved ||
                         patch.Updates.Any(u => selected.Contains(u.Existing)) ||
                         patch.Removals.Any(selected.Contains));
          if (touched) AnnounceSelection(force: true);
          return;
        }

        /*
         * Wholesale rebuild below: the mark is a reference to an OLD row that the swap discards, so drop it here, or a cleared
         * highlight would sit on nothing and the next search would skip its own bookkeeping.
         */
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
        _settle.Reset();
        _announcedIds = [];
        _announcedStamp = 0;   // same reason as the session-change path: these rows are gone, so no content memory survives

        _rows = new ObservableCollection<DerivedFightRow>(snapshot.Rows);
        fightGrid.ItemsSource = _rows;

        if (restorable)
        {
          RestoreSelection(keep);
          AnnounceSelection();
        }
      });
    }

    /*
     * Whether THIS session's load may put the band up — and it is off unless somebody turns it on, because only one
     * open path wants it. MainWindow sets it before the reader starts: true for the automatic startup open (the
     * auto-monitor restoring the last log, where the fight list is empty and silent and nothing else says work has
     * begun), false for every open the operator chose (File / Recent — the application status line counts percent and
     * seconds off the same pump right then, so a panel over the grid duplicates it while they wait for their own click).
     */
    internal bool AllowsLoadBand { get; set; }

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

      /*
       * Two vetoes, in order: an open nobody asked to be announced (see AllowsLoadBand), and "rows already landed this
       * session". There is no session gate beyond those: while the pump runs this window is either showing the band or
       * docked-hidden (engine off), and a hidden band costs nothing.
       */
      if (!AllowsLoadBand || _loadBandSettled) return;

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
        session.Capturing += OnCapturing;
      }
    }

    private void Detach()
    {
      if (_session is not null)
      {
        _session.Derived -= OnDerived;
        _session.Capturing -= OnCapturing;
      }
      _session = null;
    }

    private void FightGridItemsSourceChanged(object sender, Syncfusion.UI.Xaml.Grid.GridItemsSourceChangedEventArgs e) => ApplyFilter();

    private void GridSelectionChanged(object sender, GridSelectionChangedEventArgs e)
    {
      // Restart the pause on every click so a dragged range announces once, at the end - and ask SelectionSettle
      // whether "the end" has actually arrived (menu open or button down parks it; see that class).
      _settle.Changed();
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

    /*
     * `force` is the pass-driven re-announce: the selection itself did not change, but the rows under it did (a display edit, a row
     * this pass removed, or a rebuilt backing object), so the board must be rebuilt even though the ids are the ones already announced.
     * The dedup stays for everything else — an ItemsSource swap landing and the settle timer firing both announce the same selection,
     * and only the first may materialize (see OnDerived's wholesale path, which clears _announcedIds before the swap on purpose).
     */
    private void AnnounceSelection(bool force = false)
    {
      var selected = GetSelectedFights();
      var ids = new List<int>(selected.Count);
      foreach (var fight in selected) ids.Add(fight.Id);

      if (!force && SameIds(ids, _announcedIds)) return;

      _announcedIds = ids;
      _announcedStamp = _currentStamp;
      DerivedSelectionChanged?.Invoke(selected);
    }

    /*
     * A cheap stamp of what a board WOULD be computed from, in O(1): the two inputs a materialized selection reads.
     *   - `FactCount` - the engine's captured total (damage facts AND heals), so any new event moves it; and
     *   - the timeline's identity digest, which moves when a verdict moves (an override, a charm window, a rank the rules
     *     could not see last pass and can now) even though no fact arrived.
     * A row's own numbers are derived from those two, so "both equal" means every figure on every board under this selection
     * is already correct - and one announcement that costs seconds (measured: a whole-capture select-all) is skipped instead of
     * spent redrawing the same figures. A hand-built snapshot with no timeline answers 0, which simply leaves the stamp at
     * whatever the last real pass recorded.
     */
    internal static long SelectionStamp(DerivedSnapshot snapshot)
    {
      var facts = snapshot.FactCount;
      var verdicts = snapshot.Timeline is { } timeline ? timeline.StateStamp() : 0L;
      return unchecked((facts * 397) ^ verdicts);
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
      // reads it. The sentinel is _paneReady (constructor's last line), NOT the sender's nullness: a real startup
      // firing was measured where this checkbox's field WAS already wired and a later element in the markup (a
      // column) was not - so testing fightShowBreaks here would let that firing through to half-built state.
      // It is also not fightGrid.View: View materializes only on the grid's Loaded pass, so on a pane that never
      // arranges the guard would swallow every real toggle forever (see _paneReady). ApplyFilter below tolerates
      // a missing view until one exists.
      if (!_paneReady) return;
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
      // Load-time contract as in ShowBreakChanged - _paneReady sentinel, same reason: the measured startup
      // crashes came in BOTH shapes (once with this checkbox's own field still null, once wired while
      // damageColumn below was not), so only the flag separates a synthetic toggle from a real one.
      if (!_paneReady) return;
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
      // Load-time contract as in ShowBreakChanged - _paneReady sentinel, same reason.
      if (!_paneReady) return;
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

    /*
     * Legacy's Clear All. It is deliberately NOT a wipe of the rows: there is nothing to wipe under a projection, and rows
     * taken out of the display come straight back on the next pass, which would read as a button that does nothing. What it
     * does is the File / Open Monitor open over the same file (MainWindow.ClearAllFights) - facts, rows, parsed records and
     * boards all go, the saved identity memory stays because it lives in files this path does not re-initialise, and the
     * reader follows from end of file. "Take me back as if I never loaded this log file", which is also the state a fresh
     * monitor open leaves, so the button needs no explanation of its own.
     *
     * Ordering that matters here: the click runs the re-open synchronously, so OnActiveChanged has already dropped the rows
     * and reset the selection gate by the time this menu closes; CloseMenu then finds nothing pending and cannot announce a
     * selection whose fights no longer exist.
     */
    private void ClearAllClick(object sender, RoutedEventArgs e) => MainActions.ClearAllFights();

    /*
     * Where the cascade's four picks land: the ONE selected fight's name (the menu is greyed unless exactly one row is selected, and the
     * inactivity dividers name nothing). SET only: there is no clear here, because taking a verdict back lives behind the Type cell of the
     * Player/NPC Identity pane, the app's ONE unset. This pane still writes them because a fight row is where a misfiled mob is noticed.
     */
    private void ApplyVerdict(IdentityKind kind)
    {
      var selected = GetSelectedFights();
      IdentityVerdictMenu.Write(selected.Count == 1 ? selected[0].Name : null, kind);
    }

    /*
     * Legacy's Refresh, for the case it was added for: the fights still going are selected, the damage summary is open, and the raid keeps
     * hitting things. New facts land INSIDE rows that are already selected, so no id changes - and AnnounceSelection dedupes on ids, so a
     * plain announce correctly concludes "nothing to do". The force flag is the whole of this menu item: it re-runs the materialize-and-build
     * pass over the current selection whatever the dedupe thinks. (A derive pass that moved the content stamp does this by itself; Refresh is
     * for the operator who wants the answer now rather than on the cadence.)
     */
    private void RefreshClick(object sender, RoutedEventArgs e) => AnnounceSelection(force: true);

    // Greyed out unless the grid has a real fight selected - every item in this menu acts on the selection.
    private void GridContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
      // Nothing is announced while the menu is up; whatever the opening itself selected waits for the close.
      _settle.MenuOpen = true;

      var selected = GetSelectedFights();
      var hasFight = selected.Count > 0;
      // The name the cascade's header will show (this project compiles with nullable annotations off, hence the explicit null).
      string firstSelectedName = hasFight ? selected[0].Name : null;

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

      // Clear All needs a session to re-open over. With no engine there is nothing loaded, and the menu item would be
      // offering to unload an empty window (MainWindow.ClearAllFights guards the same thing for the file itself).
      clearAllItem.IsEnabled = _session is not null;
      var hasCurrent = fightGrid.CurrentItem is DerivedFightRow { IsDivider: false };
      selectGroupItem.IsEnabled = hasCurrent && unsorted;
      unselectGroupItem.IsEnabled = hasCurrent && fightGrid.SelectedItems.Count > 0;

      // One fight row, or the cascade greys out (IdentityVerdictMenu.Present): the verdict is one judgement about one name somebody looked
      // at, and a ctrl-click batch would rewrite kinds - and therefore board routing - across rows nobody inspected. Group selection stays
      // useful for Copy and Refresh; it just cannot be an identity edit.
      IdentityVerdictMenu.Present(overrideSetItem, selected.Count == 1 ? firstSelectedName : null);

      // Refresh takes ANY selection - one row or the whole list - because it changes nothing: it re-reads the boards over what is selected,
      // and its whole use case is "the fights that are still going" (plural). Force-announcing an empty selection would blank the boards,
      // which is not what a person pressing "refresh" means.
      refreshItem.IsEnabled = hasFight;
    }

    /*
     * The menu is down. If a selection change parked behind it - the click that opened the menu, or the item that was
     * chosen - announce it now, once (SelectionSettle.CloseMenu consumes the pending mark). Select All and friends
     * also announce straight from their Click; this second call costs nothing when the ids already match, which is
     * exactly what AnnounceSelection's dedupe is for.
     */
    private void GridContextMenuClosing(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
      if (_settle.CloseMenu()) AnnounceSelection();
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
      SelectShownRuns(row => row.Fight is not null);
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
        SelectShownRuns(inSection);
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

    /*
     * Put the highlight back on the rows whose fights survived the swap — the same walk as every other programmatic select.
     */
    private static FightKey KeyOf(DerivedFight fight) => new(fight.Name, fight.BeginTime);

    private void RestoreSelection(HashSet<FightKey> keepKeys)
      => SelectShownRuns(row => row.Fight is { } fight && keepKeys.Contains(KeyOf(fight)));

    // Both programmatic selects share this: select every SHOWN row matching the predicate, range by range.
    // The grid positions come from ShownRunPositions — counted over the shown rows only, because a hidden divider or a
    // filtered-out name takes no grid row and counting it would push every later range down by one per hidden row.
    private void SelectShownRuns(Predicate<DerivedFightRow> matches)
    {
      var first = FirstRecordRow();
      foreach (var (from, to) in ShownRunPositions(_rows, IsShown, matches))
        fightGrid.SelectRows(first + from, first + to);
    }

    /*
     * The walk's decision, apart from the grid so it can be tested without one: the closed runs of shown-and-matching rows,
     * as [from, to] pairs in SHOWN positions (0-based among the rows the view actually renders). A hidden row closes any open
     * run but does not advance the position, and a shown row that does not match closes the run AND advances it — the next run
     * starts after the row that broke the first.
     */
    internal static List<(int From, int To)> ShownRunPositions(IReadOnlyList<DerivedFightRow> rows,
                                                              Predicate<DerivedFightRow> isShown,
                                                              Predicate<DerivedFightRow> matches)
    {
      var runs = new List<(int, int)>();
      var visiblePos = 0;
      var runStart = -1;
      for (var i = 0; i <= rows.Count; i++)
      {
        var hit = i < rows.Count && isShown(rows[i]) && matches(rows[i]);
        if (hit)
        {
          if (runStart < 0) runStart = visiblePos;
        }
        else if (runStart >= 0)
        {
          runs.Add((runStart, visiblePos - 1));
          runStart = -1;
        }

        if (i < rows.Count && isShown(rows[i])) visiblePos++;
      }

      return runs;
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
