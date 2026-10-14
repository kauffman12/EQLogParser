using EQLogParser;
using log4net;
using Syncfusion.UI.Xaml.Grid;
using Syncfusion.UI.Xaml.ScrollAxis;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

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
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    internal event Action<BoardRequest, DerivedSnapshot> DerivedSelectionChanged;

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

    /*
     * The capture's rows in list order: the truth the patch keeps current, one instance per fight with a `Key` that survives every derive.
     * What the grid is BOUND to is `_rows`, the projection of this list through the two row dials. A row object is SHARED between the two, so
     * hiding a fight moves a reference rather than copying or rebuilding anything — which is how a selected row survives a live night with either
     * dial off, and why SfDataGrid's index-based selection has no filter left to disagree with it. The whole reason, including what the grid-side
     * filter used to cost: docs/DesignNotes.md → "The fight list dials filter our rows, not the grid".
     */
    private List<DerivedFightRow> _allRows = [];

    // What the grid shows: `_allRows` through the Inactivity and Tanking predicates, same order, same instances. Never a copy of a row.
    private ObservableCollection<DerivedFightRow> _rows = [];
    private readonly DispatcherTimer _selectionTimer;

    /*
     * The row the POINTER opened the right-click menu over, resolved once per opening. Every position-based item in that
     * menu (Select Group, Unselect Group) acts on this row's section, and the grid's own current cell is moved to it at
     * the same moment — the one line the legacy table carried in its ContextMenuOpening and this pane did not, which is
     * why "the select cursor/item stays where it was": without it a right-click leaves the cursor wherever the person
     * last LEFT-clicked, so un-selecting a group removed the previously selected one instead of the one under the mouse.
     * Cleared with the rows it belonged to (a snapshot swap replaces every instance).
     */
    private DerivedFightRow _menuRow;

    /// <summary>
    /// What was selected when the RIGHT button went down, or null. SfDataGrid treats a right-press as selection input and collapses an
    /// extended selection before any menu exists; this pane remembers it on the way down and puts it back at ContextMenuOpening.
    /// A Ctrl/Shift press IS the operator editing the selection, so nothing is remembered then.
    /// </summary>
    private List<DerivedFightRow> _rightPressSelection;

    /*
     * Whether a tick may announce, or whether something is still happening under the cursor. Two parked cases (see
     * SelectionSettle for the reasoning): the right-click menu is open - where SfDataGrid's own move of the current
     * cell to the clicked row would otherwise spend a full materialization before the user has chosen anything, and a
     * Select All then spends a second one, which is what "it ends up doing two selections" was - and a mouse button still
     * down mid-drag. The pointer state is QUERIED at tick time, never latched from down/up events, so a lost button-up
     * cannot wedge announcements off. BOTH buttons count: SfDataGrid selects on a right-drag as well as a left one, and
     * "right-drag to pick a range, then open the menu" is precisely the gesture this pane has to survive.
     */
    private readonly SelectionSettle _settle;

    /// <summary>Any mouse button held over the app: a drag in progress parks a selection announcement.</summary>
    internal static bool PointerIsDown() =>
      System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed ||
      System.Windows.Input.Mouse.RightButton == System.Windows.Input.MouseButtonState.Pressed;

    /*
     * The grid's context menu, open RIGHT NOW. WPF clears IsOpen itself when the menu goes away, so this is the menu equivalent of
     * querying the button state: the open/close handlers still drive the latch (their order is what announces a change exactly once at the
     * close), and this backstops the case where the closing event never arrives — latched alone, one such menu would park every later
     * gesture in the session, which is the shape of "I deselected the row and the summary never updated".
     */
    private bool MenuIsOpen() => fightGrid?.ContextMenu?.IsOpen == true;

    // What was last announced, as fight ids - which is to say, WHAT THE REPORT UNDERNEATH THIS GRID adds up. Two jobs: a stale snapshot's rows
    // cannot be re-announced as if they were new, and a grid that re-raises SelectionChanged with the same selection must not rebuild boards
    // nobody changed. Because it is a claim about the boards, only an announce may write it: clearing it to "nothing" without announcing left
    // every later deselection deduping against an answer nobody gave (see OnDerived's wholesale path).
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

    /*
     * Whether THIS capture has ever announced - the dedupe needs it, because an empty selection and a fresh pane look identical
     * to arithmetic (`_announcedIds` starts empty, `_announcedStamp` starts 0) and swallowing the very first announcement is how
     * "a wholesale pass says what is on screen" became "nothing arrives until you click something". The first answer of a capture
     * always goes out; only a REPEAT of an answer dedupes.
     */
    private bool _announcedOnce;

    /*
     * What the boards on screen are watching: the identity answer for every name they display, plus the selected rows' own names. A derive
     * pass re-announces only when one of those changed WHAT IT IS (Unknown→Pet, Player→Npc under a charm, an operator taking a claim back) —
     * never because damage arrived. The operator's rule (2026-10-08): "id rather it be like a snapshot of what was selected at the time
     * except for the pet changes or player turning npc". IdentityWatch holds the measurement behind the scope.
     */
    private readonly IdentityWatch _watch = new();

    // The timeline behind `_rows`, kept so the watch can be read on a later pass without waiting for a request. Null until the session paints.
    private EntityTimeline _currentTimeline;
    private DerivedSnapshot _currentSnapshot;

    // The stamp of the newest snapshot this pane has been handed (the one _rows came from). Announcing records it, so
    // "what did the board get built from?" stays answerable while a pass is in flight.
    private long _currentStamp;



    /*
     * One line per declined-unasked-rebuild episode, not one per derive pass. A live raid hands out passes about twice a
     * second and every one of them re-asks the same question this pane already answered "no" to, so the repeat is silent;
     * the first refusal of an episode prints (see UnaskedRefresh for why a big selection's report stops following the pass),
     * and any announced build ends the episode. Without the flag the operator's question — "why did the numbers stop moving?"
     * — would be answered by nothing at all except a Debug line they never see; with it, eqlogparser.log says which stamp was
     * refused, at what size, and that a gesture or Refresh is the door.
     */
    private bool _unaskedDeclined;

    /*
     * Loading-band state (see loadOverlay in the XAML). `_loadBandSettled` is this session's "a snapshot has
     * landed" flag: the band goes down for good at the first Derived - a quiet stretch mid-file can legitimately
     * complete a derive under 100 %, and real rows beat a bar - and it comes back only with the next session.
     * `_awaitingFirstRows` is the other half: it turns on once this open has actually handed lines over, because an
     * open that read NOTHING (follow-from-end-of-file: the startup auto-monitor, and Clear All) has no first snapshot
     * owed to it and an empty list there is the correct answer rather than a wait. Without it the band would sit over a
     * monitored log saying "building" for the rest of the evening.
     * `_capturedFacts` rides in from the session's capture event to put a number on what the build is chewing
     * through; the file's own percent belongs to the application status line, not here (see ReportCaptureProgress).
     */
    private bool _loadBandSettled;
    private bool _awaitingFirstRows;

    /*
     * Set when this open's reader reports done. A 0-row snapshot MID-read proves nothing (an early pass can find no finished fight while
     * rows are still landing); after the read, zeros are all this capture has to say and the band may settle on them.
     */
    private bool _readerDone;
    private long _linesRead;

    private DispatcherTimer _bandGuard;

    /// <summary>Wakes the band after the post-load collection it is waiting on (see SettleLoadBand).</summary>
    private DispatcherTimer _cleanupHold;
    private long _cleanupHoldDeadline;

    /* Escape hatch, not a wait: longer than the worst collection ever measured (docs/DesignNotes.md → "Garbage collection after loading a log")
     * and short enough that a bar nobody pays for still comes down. */
    private const long CleanupHoldMs = 8_000;

    /*
     * The one scroll this pane is allowed to do on its own: owed by each capture and paid when that capture's file finishes reading, which is the
     * moment an operator watches rather than the moment some derive happened to finish. FightListReveal holds the sequencing (and why both orders of
     * rows-vs-read happen) and this class only feeds it events and does the scrolling. Dispatcher-thread only.
     */
    private readonly FightListReveal _reveal = new();

    /*
     * One Info line per capture, written the first time this session's rows reach the grid. The open and the first derive
     * pass already announce themselves (`capture: started`, `derive: first pass`); what was missing on the other side is the
     * moment the READER could have clicked something, which is the timestamp every "the stats built three times when I
     * clicked once" report needs in order to be read at all.
     */
    private bool _sessionPainted;
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
     * bodies touch columns and saved settings, and RebuildVisible below does nothing at all until a grid exists.
     */
    private bool _paneReady;

    public FightTable()
    {
      // Before anything can park or release a gesture: the probe reads `fightGrid`, which InitializeComponent wires.
      _settle = new SelectionSettle(PointerIsDown, MenuIsOpen);

      InitializeComponent();

      // The search box's watermark doubles as the empty-filter state (UiElementUtil.SetWatermark): while the cue
      // word shows, nothing is filtered - TextChanged reads that off the Text itself, not a side flag.
      UiElementUtil.SetWatermark(fightSearchBox, Resource.NPC_SEARCH_TEXT);

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

      // No visibility rebuild here: `_allRows` is empty until the first snapshot lands, and every path that fills it projects as it goes.

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
        if (_settle.ShouldAnnounce()) AnnounceSelection(BoardReason.SettleTick);
        else if (_settle.Pending) _selectionTimer.Start();   // still parked (menu open / button held): ask again
      };

      // Same single-entry idiom as ActiveChanged: the event is process-wide and a pane can be rebuilt.
      MainActions.EventsLogLoadingComplete -= OnLogLoadingComplete;
      MainActions.EventsLogLoadingComplete += OnLogLoadingComplete;

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
      Dispatcher.InvokeAsync(ClearForNewCapture);
    }

    /*
     * The file finished reading (`open == false` is the CLOSE of a log, which owes nothing to a viewport). This is "the progress went away": if rows
     * are already on screen the jump happens now, and if the derive is still chewing through a big capture the apply paths pay it when they land.
     * MainWindow raises this from its own UI-thread pump; the dispatch is not for correctness but so the flag and the rows are touched by one thread.
     */
    private void OnLogLoadingComplete(string file, bool open)
    {
      if (!open) return;

      Dispatcher.InvokeAsync(() =>
      {
        ReportReadFinished();
        _reveal.ReadFinished();
        RevealNewestRowIfOwed();
      });
    }

    /*
     * The reader finished with this open; only the dispatcher touches band state. Arms one guard if rows still have not landed:
     * a capture with no fights and no further derive pass would otherwise hold the band forever, nothing being left to derive.
     * Internal for the WPF band test (a real engine needs a file).
     */
    internal void ReportReadFinished()
    {
      _readerDone = true;
      if (_loadBandSettled || _sessionPainted || !_awaitingFirstRows) return;

      // The merge lands the facts a beat before the reader's last percent; restate the detail line now so the big
      // number appears the moment it is allowed to speak, not at the next event.
      if (loadOverlay.Visibility == Visibility.Visible) UpdateLoadDetail();

      _bandGuard ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
      _bandGuard.Stop();                       // re-armed, never added twice: this fires on every 100 % pump tick
      _bandGuard.Tick -= BandGuardTick;
      _bandGuard.Tick += BandGuardTick;
      _bandGuard.Start();
    }

    private void BandGuardTick(object sender, EventArgs e)
    {
      ((DispatcherTimer)sender).Stop();
      if (_sessionPainted || _loadBandSettled) return;   // rows arrived, or a post-read snapshot already settled it
      SettleLoadBand("read finished with no fights");
    }

    /*
     * The bar comes down when this session has an answer AND no load cleanup is owed. Only the HIDING is deferred here — the rows are
     * already painted. A stop-the-world collection landing on a grid whose bar just dropped reads as a hang, so the pause stays behind
     * the bar that asked for it; PostLoadCleanup is asked, never waited on, and CleanupHoldMs bounds the wait (MainWindow.ArmingLoadTidy).
     */
    private void SettleLoadBand(string reason)
    {
      if (PostLoadCleanup.Owes && !CleanupHoldExpired())
      {
        ArmCleanupHold();
        return;
      }

      StopCleanupHold();
      _loadBandSettled = true;
      SetLoadBand(null);
      Log.Debug($"load band: hidden [{reason}]{(PostLoadCleanup.Owes ? " - cleanup still unpaid at this moment" : string.Empty)}");
    }

    /*
     * The collection's start/stop is the only thing that changes what the band SAYS (PostLoadCleanup.Notice is shared with the status line).
     * A band this session already put down is not brought back for a cleanup.
     */
    internal void ReportCleanupState(bool cleaning)
    {
      if (!Dispatcher.CheckAccess())
      {
        Dispatcher.InvokeAsync(() => ReportCleanupState(cleaning));
        return;
      }

      if (_loadBandSettled || loadOverlay.Visibility != Visibility.Visible) return;

      if (!cleaning)
      {
        // The pass is over and the mark went with it, so this is the settle the hold was waiting for - no 200 ms poll needed.
        if (!PostLoadCleanup.Owes && (_sessionPainted || _readerDone)) SettleLoadBand("post-load cleanup ended");
        else SetLoadBand("Building the Fight List\u2026");
        return;
      }

      SetLoadBand(PostLoadCleanup.Notice);
    }

    private bool CleanupHoldExpired() => _cleanupHoldDeadline != 0 && Environment.TickCount64 >= _cleanupHoldDeadline;

    private void ArmCleanupHold()
    {
      _cleanupHold ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
      _cleanupHold.Stop();
      _cleanupHold.Tick -= OnCleanupHoldTick;
      _cleanupHold.Tick += OnCleanupHoldTick;

      if (_cleanupHoldDeadline == 0)
      {
        _cleanupHoldDeadline = Environment.TickCount64 + CleanupHoldMs;
        Log.Debug("load band: rows landed, holding the bar while the post-load collection finishes");
      }

      _cleanupHold.Start();
    }

    private void OnCleanupHoldTick(object sender, EventArgs e) => SettleLoadBand("cleanup hold ended");

    private void StopCleanupHold()
    {
      _cleanupHoldDeadline = 0;
      if (_cleanupHold == null) return;
      _cleanupHold.Stop();
      _cleanupHold.Tick -= OnCleanupHoldTick;
    }

    /*
     * The session changed — either one ended or a new one started — so everything displayed here belongs to a capture
     * that no longer answers. Blank the list on BOTH sides of that seam, not only when the engine went null:
     *
     *  - an open of a file with nothing derivable in it, and Clear All (which is a follow-from-end re-open of the same
     *    file), never produce a snapshot with rows to overwrite what is showing, so "clear" used to leave last night's
     *    raid in the grid indefinitely; and
     *  - during a load of any file, the previous capture's rows were the thing on screen until the new one's first pass.
     *
     * The rows go immediately; what replaces them is announced by the band (see ReportCaptureProgress) once this open has
     * proven it has history to build. internal for the WPF test — ActiveChanged itself cannot be raised from outside the
     * engine, and a real engine needs a file.
     */
    internal void ClearForNewCapture()
    {
      Detach();
      Attach(DeriveEngine.Active);

      _selectionTimer.Stop();
      _settle.Reset();
      _announcedIds = [];

      // A write made against the log that just closed has nothing left to land in: its rows are gone, and a debt carried across
      // would spend this capture's first announce on the previous night's click.
      UnaskedRefresh.ClearGesture();

      // Nothing is announced, so nothing may be remembered as announced: a stamp left over from the log that just closed
      // could otherwise match a new capture's first snapshot by arithmetic (fact totals restart at zero every session) and
      // swallow an announcement that has never happened.
      _announcedOnce = false;
      _announcedStamp = 0;
      _watch.Clear();
      _currentTimeline = null;
      _currentSnapshot = null;
      _allRows.Clear();
      _rows.Clear();

      // Last night's pointer cannot name this capture's rows: the instance is gone, and a menu opened without a pointer
      // (keyboard) must not inherit a row from the log that just closed.
      _menuRow = null;

      // A new session is a new load: the band may show again, its counters start from nothing, nothing has been read yet - and the jump to this
      // capture's newest row is owed again (RevealNewestRowIfOwed).
      SetLoadBand(null);
      _loadBandSettled = false;
      _awaitingFirstRows = false;
      _readerDone = false;
      _linesRead = 0;
      _bandGuard?.Stop();   // a guard armed for the session that just closed must not settle this one's band
      StopCleanupHold();    // ...and neither may a hold waiting on the dead capture's cleanup

      // A press whose menu never opened leaves its snapshot standing; restoring it into the next capture's grid would hand out
      // row instances from the dead one (docs/DesignNotes.md → "The fight list announces when the gesture ends").
      _rightPressSelection = null;
      _reveal.Arm();
      _sessionPainted = false;
      _capturedFacts = 0;
    }

    /*
     * Derivation completes on a background thread; rows change on the dispatcher. Two paths: patch the rows that moved (the common case) and then
     * reconcile the visible projection so a fight crossing a dial's line joins or leaves without touching anyone else, or swap the whole collection
     * where a patch cannot be trusted — a sorted grid, a list with nothing in it yet, too much churn. A swap is one re-layout instead of thousands of
     * row notifications, which is also why a dial toggle swaps rather than reconciles (RebuildVisible).
     */
    // Internal for the band's WPF test (a live session is not needed to hand this panel a snapshot); every
    // production call arrives through the session's Derived event.
    internal void OnDerived(DerivedSnapshot snapshot)
    {
      Dispatcher.InvokeAsync(() =>
      {
        if (snapshot?.Rows == null) return;

        // A pass belonging to a capture that is no longer open must not repaint this grid. Dispose does not join a pass
        // already running, so the log just closed can announce after the new one started — and when the new open has no
        // rows of its own yet (Clear All, an empty file) that late pass IS the content on screen forever.
        if (!snapshot.FromLiveSession) return;

        _currentSnapshot = snapshot;
        _currentTimeline = snapshot.Timeline;
        _currentStamp = SelectionStamp(snapshot);

        // Real rows beat a bar even mid-read; zeros only answer once the reader has finished (see ReportReadFinished).
        if (!_loadBandSettled && (snapshot.Rows.Count > 0 || _readerDone)) SettleLoadBand("first rows of the session");

        if (!_sessionPainted && snapshot.Rows.Count > 0)
        {
          _sessionPainted = true;
          Log.Debug($"fight list: {snapshot.Rows.Count} row(s) painted - this capture is on screen");
        }

        /*
         * Patch first, rebuild only when the patch cannot be honest about it (RowPatch: a re-sort, a duplicate key, or more than
         * about half the list churned). On a live raid night that means the ordinary pass updates ~40 rows and leaves ~730 alone
         * (docs/DesignNotes.md → "Would an equality gate have saved anything?"), so the row objects under the reader's selection,
         * the scroll position and the search mark all survive untouched — no re-lookup by name, no blink.
         */
        /*
         * One state keeps the wholesale path: a SORTED grid would hold updated cells in yesterday's order (this build's Syncfusion has no live
         * re-sort the pane can call cheaply, and guessing row positions is the failure mode the selection code already refuses).
         *
         * It used to refuse a SECOND state, "Tanking unchecked", because the grid filtered on Fight.DamageToOwner — a field a patch mutates — and
         * the shipped assembly offers no live-filter refresh (View.RefreshFilter is all there is). That put the unchecked mode on a whole-list
         * rebuild every pass while the checked mode patched some rows of many, which is one reason the dial misbehaved exactly when it was used.
         * Visibility is the pane's own bookkeeping now (ShownRows), so the patch runs either way and only a sort still forces a swap.
         */
        var canPatch = _allRows.Count > 0 && fightGrid.SortColumnDescriptions.Count == 0;
        var patch = canPatch
          ? RowPatch.Build<DerivedFightRow>(_allRows, snapshot.Rows, static row => row.Key,
                                            static (a, b) => a.SameDisplayAs(b), Math.Max(256, _allRows.Count / 2))
          : null;
        if (patch != null)
        {
          // What the reader has selected, by INSTANCE: those objects are about to stay in the list, so no restore is needed — but a
          // selected row that this pass edited means the boards under it now show one pass-old numbers.
          var selected = fightGrid?.SelectedItems is { } items ? items.OfType<DerivedFightRow>().ToList() : [];
          var contentMoved = SelectionStamp(snapshot) != _announcedStamp;

          // The refresh runs on EVERY survivor, not just the display-updated ones: a reclassifying rebuild hands out brand-new
          // DerivedFight objects for every row while most cells read identically, and the snapshot this pass published is keyed on
          // the new objects — a survivor still holding last pass's fight would materialize as "no damage, no tanking" the next time
          // its boards are built.
          RowPatch.Apply(_allRows, patch, static (target, source) => target.CopyDisplayFrom(source),
                               static (target, source) => target.Fight = source.Fight);

          /*
           * The patch moved the truth; bring the visible list along. Reference bookkeeping only: a fight that crossed the Tanking line joins or
           * leaves, every other row keeps its object, so the selection, the scroll position and the search mark hold through a pass however the
           * dials happen to be set.
           */
          ShownRows.Reconcile(_rows, VisibleRows());

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
          /*
           * A row whose NUMBERS this pass updated is not a board that owes a rebuild (see AnnounceSelection: a selection is a snapshot
           * unless a name changed what it is). What still announces on its own is a selected row being TAKEN AWAY - the row the boards were
           * built from no longer exists, which is a structural fact no watch can see - and anything at all moving under the selection, which
           * AnnounceSelection filters by identity before it spends a build.
           */
          /*
           * Gone means gone from the list the reader can see: a row this pass deleted, or one the dials now hide, takes its boards with it. The set
           * is built once per pass rather than probing the visible list per selected row — a raid-total selection against a night's list is
           * hundreds of rows, and this runs on every patched pass.
           */
          var visibleNow = new HashSet<DerivedFightRow>(_rows, ReferenceEqualityComparer.Instance);
          var removedSelected = false;
          foreach (var row in selected)
          {
            if (visibleNow.Contains(row)) continue;
            fightGrid.SelectedItems.Remove(row);
            removedSelected = true;
          }
          var touched = selected.Count > 0 && (removedSelected || contentMoved || UnaskedRefresh.HasGesture);
          if (touched) AnnounceSelection(removedSelected ? BoardReason.RowEdited : BoardReason.ContentMoved, force: true);

          RevealNewestRowIfOwed();
          return;
        }

        /*
         * Wholesale rebuild below: the mark is a reference to an OLD row that the swap discards, so drop it here, or a cleared
         * highlight would sit on nothing and the next search would skip its own bookkeeping.
         */
        ClearSearchMark();

        // Which door and why. This is the one pass shape that can move the viewport under the reader - the grid re-asks its whole source - so with
        // the pane no longer scrolling on its own, triage needs to know how often it runs (Debug only; a player's log carries none of this).
        Log.Debug($"fight list: wholesale swap ({(_allRows.Count == 0 ? "first rows" : fightGrid.SortColumnDescriptions.Count > 0 ? "sorted grid" : "patch refused")}) - {_allRows.Count} row(s) shown");

        /*
         * What the user had selected, remembered by name + start time and put back on the new rows.
         *
         * A derive is not a rare event on this panel - every identity override re-derives on purpose - so a
         * swap that dropped the selection would make the feature feel like it deleted the user's work: they set
         * `Dangle` to Pet, and the row they were looking at plus the board under it both vanish. Name and start
         * time are read off the same facts on every pass, so the fight (with this pass's numbers in it) is found
         * again - see FightKey for why the row number cannot be that key.
         *
         * Restoration uses row objects, not positions, so sorting does not change which fights come back selected.
         */
        var keep = new HashSet<FightKey>();
        foreach (var fight in GetSelectedFights()) keep.Add(KeyOf(fight));
        var restorable = keep.Count > 0;

        /*
         * Re-arm rather than erase. `_settle.Reset()` used to run here, which threw away a gesture that had not been announced yet: select a
         * row, then deselect it, and if this pass lands inside the 600 ms settle window (or while the right-click menu is up) the pending mark
         * is gone - and with nothing pending the menu's close announces nothing either. The reader keeps a report showing a row the grid no
         * longer highlights, Refresh is greyed out for want of a selection, and the only door left is clicking some other fight.
         * Restarting the timer costs one tick that does nothing when nothing is pending, and evaluates the selection AS THE SWAP LEFT IT when
         * something is.
         */
        _selectionTimer.Stop();

        _allRows = new List<DerivedFightRow>(snapshot.Rows);
        _rows = new ObservableCollection<DerivedFightRow>(VisibleRows());
        fightGrid.ItemsSource = _rows;

        // Every instance was just replaced. GroupSectionSelect's chain reads the cursor and the selection instead, both of
        // which RestoreSelection has put back by key; holding a discarded row would only name no section.
        _menuRow = null;

        if (restorable) RestoreSelection(keep);

        /*
         * Announce whatever the swap left selected, always - INCLUDING "nothing". Those two fields describe what the boards show, and a swap can
         * change that question with no click involved: it restores rows by key, or refuses to on a sorted grid, or finds nothing selected at all.
         * Clearing the memory to an empty selection instead (what this used to do) recorded a question as ANSWERED that nobody answered, after
         * which every later deselect deduped against it ("announce skipped: already announced 0 row(s)") while the report went on showing the fight
         * that stopped being selected.
         */
        AnnounceSelection(BoardReason.SnapshotSwap);

        RevealNewestRowIfOwed();
      });
    }

    /*
     * The loading band's tick (dispatcher thread already). The count used to replace the status line every tick, which
     * read as a terminal ticking past; it belongs on the loading band, next to the file progress that says when
     * the number will stop moving. With no band showing there is nothing to update - a settled list refreshes
     * through OnDerived, which is the message.
     */
    // Internal for the band's WPF test; production arrival is the session's Capturing event.
    internal void OnCapturing(long total)
    {
      _capturedFacts = total;
      if (loadOverlay.Visibility == Visibility.Visible) UpdateLoadDetail();
    }

    /*
     * The reader pump MainWindow drives every ~500 ms while a file is open. Called on the dispatcher with what the reader
     * has handed to the parser so far — which is the whole question this panel asks, and deliberately NOT the byte percent:
     * the application-wide status line counts that off the same pump, and a second copy in the dock duplicates it. Lines
     * landed is what distinguishes "a first build is owed" from "this open read no history, so an empty list is the answer"
     * (follow-from-end-of-file: the startup auto-monitor and Clear All).
     *
     * So the band says one thing, for as long as this session has shown no rows and owes a first build: the list is empty
     * because it is being made — with the captured-fact count under the bar as the moving part, which only this panel has.
     */
    internal void ReportCaptureProgress(long linesRead)
    {
      if (Dispatcher.CheckAccess() == false)
      {
        Dispatcher.InvokeAsync(() => ReportCaptureProgress(linesRead));
        return;
      }

      _linesRead = Math.Max(_linesRead, linesRead);   // the detail line's other moving part; monotonic for the session
      if (_loadBandSettled) return;   // rows landed this session: the list itself is the message
      if (linesRead > 0) _awaitingFirstRows = true;   // monotonic for the session; a follow-from-end open never sets it
      if (!_awaitingFirstRows) return;

      SetLoadBand("Building the Fight List\u2026");
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

    /*
     * The detail line speaks in captured facts, not lines: thirteen million "lines" out loud reads as telemetry from the wrong feature.
     * Lines are the opening phrase only, for the seconds before the first fact batch lands.
     */
    private void UpdateLoadDetail()
    {
      var facts = _capturedFacts;
      if (facts > 0)
      {
        loadDetail.Text = $"{facts:N0} facts captured";
        return;
      }

      loadDetail.Text = _linesRead > 0 ? $"{_linesRead:N0} lines read" : "waiting for first facts";
    }

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

      /*
       * Order the grid's way is not an order at all: `SelectedItems` enumerates in whatever sequence the selection operation
       * touched the rows, and a Ctrl+A over 708 rows produced a different first element on every gesture (measured in the field:
       * same rows selected, different name in the boards' title line each time). The builders count a selection in TIME order -
       * they re-sort by Id themselves for exactly that reason, since ids are handed out in the fight list's own display order
       * (`published[i].Id = i + 1`) - so hand them that order here too: everything downstream (legacy's sort, the title line, and
       * SummaryKeyFor, where a re-selection of the same set in another order should not read as a new question) gets one answer.
       */
      selected.Sort(static (a, b) => a.Id.CompareTo(b.Id));
      return selected;
    }

    // Whether this window has a live session behind it: the answer to "does the engine answer for this log at
    // all", which is what MainWindow asks before choosing who owns GetFights while both windows can exist.
    internal bool SessionActive => _session != null;

    /*
     * What the LAST derive pass produced, in one long (see SelectionStamp: captured facts folded with the identity digest).
     * The board path needs it because "the same rows" is not "the same answer" — during a pull, re-asking over an unchanged
     * selection IS a new question. internal for MainWindow, which keys its single-flight build gate on it.
     */
    internal long ContentStamp => _currentStamp;

    /*
     * Called by whoever BUILT the boards (MainWindow, at the end of a summary build) with the names that build put on screen. Two owners by
     * design - the builder knows the displayed names, this pane knows the selection - and IdentityWatch keeps their slots apart so neither
     * writes a table the other is reading.
     */
    internal void WatchBoardNames(EntityTimeline builtUnder, IReadOnlyList<string> names, int sessionId)
    {
      Dispatcher.InvokeAsync(() =>
      {
        if (DeriveEngine.Active?.SessionId != sessionId) return;
        _watch.CaptureBoard(builtUnder, names);
        // A pass may have landed while the board was building. Ask now: its stamp may not move again on a finished log.
        if (_watch.AnyChanged(_currentTimeline)) AnnounceSelection(BoardReason.ContentMoved, force: true);
      });
    }

    /*
     * What this pane is holding right now. internal for the session-switch tests: ActiveChanged cannot be raised from
     * outside DeriveEngine and a real engine needs a file, so the seam those laws live behind is ClearForNewCapture.
     *
     * The pair states the dial law in one line: `RowCount` counts what the grid renders (the visible projection), `RowCountAll` counts what the
     * capture holds. A dial moves the first and must never move the second — a hidden fight is still a fight, and it comes back when the box is
     * ticked again.
     */
    internal int RowCount => _rows.Count;
    internal int RowCountAll => _allRows.Count;

    // Hidden dividers still separate encounters; only the visible fights in that encounter can be selected.
    internal List<DerivedFightRow> SectionRowsOf(DerivedFightRow row) =>
      GroupSectionSelect.VisibleMembers(_allRows, _allRows.IndexOf(row), static item => !item.IsDivider, IsShown);

    /*
     * The fights behind this window in the legacy shape the older consumers read: MainWindow.GetFights feeds
     * these to the spell/taunt/death/export paths. `selected` is the grid's own selection; false is every row
     * the list shows, in list order - the same set a "select all" would pick up.
     */
    internal List<Fight> GetFights(bool selected)
        => DeriveEngine.MaterializeFights(_currentSnapshot, selected ? GetSelectedFights() : null);

    // The scoped variant the death viewer wants per death click - see DeriveEngine.MaterializeFightsOverlapping
    // for why materializing everything would be a full board's cost paid per keystroke.
    internal List<Fight> GetFightsOverlapping(double fromT, double toT)
        => _session?.MaterializeFightsOverlapping(fromT, toT) ?? [];

    /*
     * `force` is the pass-driven re-announce: the selection itself did not change, but the rows under it did (a display edit, a row
     * this pass removed, or a rebuilt backing object), so the board must be rebuilt even though the ids are the ones already announced.
     * The dedup stays for everything else - an ItemsSource swap landing and the settle timer firing both announce the same selection, and only
     * the first may materialize. A swap announces the selection it actually left behind, empty included, so "the ids are the ones already
     * announced" means the boards already answer for what is highlighted.
     */
    private void AnnounceSelection(BoardReason reason, string detail = null, bool force = false)
    {
      var selected = GetSelectedFights();
      var ids = new List<int>(selected.Count);
      foreach (var fight in selected) ids.Add(fight.Id);

      if (!force && _announcedOnce && SameIds(ids, _announcedIds))
      {
        /*
         * The dedupe is where a duplicated door disappears, and "disappears" is exactly why it needs a line: the pane announces from
         * five places, and when a board fills three times the reader has to know whether three asks arrived or one did and something
         * else built twice. Debug rather than Info — this can fire on every pass of a live raid — but it names the reason, so a run
         * with Debug on shows the whole announce pattern without a single rebuild being guessed at.
         */
        Log.Debug($"announce skipped [{reason}]{(detail is null ? string.Empty : $" {detail}")}: already announced "
                  + $"{ids.Count} row(s) at stamp {_currentStamp}");
        return;
      }

      /*
       * An operator write whose result only exists after a pass (a pet claimed, a verdict written) owns the announce that pass
       * produces, so it is re-labelled as what it was: a gesture. Without this, claiming a summon under a whole-capture selection
       * would be declined by the budget below — the write asked for the pass, the patch announced ContentMoved, and the operator's
       * deliberate act would be swallowed by a policy written for traffic. Consumed here, on the first announce after the write: an
       * operator who selects something else in the meantime still gets their claim on screen, just riding that ask.
       */
      if (UnaskedRefresh.TryConsumeGesture(out var gesture))
      {
        reason = BoardReason.Manual;
        force = true;
        detail = gesture;
      }

      /*
       * The cost gate on rebuilds nobody asked for. A derive pass drives three of the reason words (ContentMoved, RowEdited,
       * SnapshotSwap), and on a large selection one of them is a second-plus rebuild landing under the reader's eyes, twice a
       * second while a raid runs; below the limit the same pass keeps a pull's boards live exactly as it always has. The number
       * and the reasoning are Core's (UnaskedRefresh), so the boundary is testable without a dispatcher.
       *
       * A refusal deliberately does NOT record _announcedIds/_announcedStamp: those two say "this question was ANSWERED", and
       * recording a decline would make the next identical gesture dedupe against a board it never got. The pass keeps asking
       * (its announce is forced, so nothing suppresses it) and keeps being told no, cheaply: counting hits over the selection
       * is O(rows), and rows are what a selection holds.
       */
      if (!UnaskedRefresh.Allows(reason, selected, out var outcomes))
      {
        if (!_unaskedDeclined)
        {
          _unaskedDeclined = true;
          Log.Debug($"announce declined [{reason}]{(detail is null ? string.Empty : $" {detail}")}: {ids.Count} row(s), "
                    + $"~{UnaskedRefresh.EstimatedCostMs(outcomes):N0} ms estimated, needs {UnaskedRefresh.RequiredGapMs(outcomes):N0} ms "
                    + "since the last unasked rebuild — the report stays as it was until that gap opens or a selection, a dial or "
                    + "Refresh asks for it");
        }

        return;
      }

      /*
       * "Content moved" means the CAPTURE moved, and on a running log it moves every pass. Whether that can change the boards under THIS
       * selection is a different question, answered in O(selected): the rows' own figures plus the identity term,
       * which no row can see. Facts landing anywhere else — every other mob in the raid, every heal, every miss — describe the same
       * numbers here, and re-presenting them is what the operator saw as the table "doing full refreshes for no reason". Like a declined
       * announce, this records nothing: the answer on screen is still current, and Refresh stays able to force one.
       */
      /*
       * A derive pass is not news to a board somebody is reading. New damage lands on rows nobody selected, in fights nobody is looking at, and
       * rebuilding to redraw identical figures is the "the whole table cleared and reloaded for no reason" reported twice from the field — and
       * asked, explicitly, to stop: a selection is a SNAPSHOT unless a name changed what it IS. So the only thing a pass may announce on is a
       * changed identity answer for a displayed or selected name (IdentityWatch) — exactly "the pet changes or player turning npc" — and that
       * costs one dictionary lookup per name already on screen.
       *
       * A hand-built snapshot (what a test hands the pane: only DerivedFightRows.Build stamps) carries no timeline, and there the older behaviour
       * stands, because nothing can tell. An empty watch answers "nothing changed" on purpose: that is what makes a quiet tail cheap.
       *
       * An EMPTY selection is exempt from all of it, because the watch has no subject there: it asks "did the answer under this selection change",
       * and with nothing selected the answer is always the same one — the boards go blank (MainWindow's BuildSummaryInput says so: an empty
       * selection clears every board, healing included). Gating that made a deselection read as traffic — "the capture moved but no displayed name
       * changed what it is", said the Debug line, while the report under a now-empty grid went on showing one fight.
       */
      if (selected.Count > 0 && reason is BoardReason.ContentMoved or BoardReason.SnapshotSwap && _currentTimeline is { } watched)
      {
        var moved = _watch.FirstChanged(watched);
        if (moved is null)
        {
          Log.Debug($"announce skipped [{reason}]{(detail is null ? string.Empty : $" {detail}")}: the capture moved to stamp "
                    + $"{_currentStamp} but no displayed name changed what it is ({ids.Count} row(s), {_watch.Count} name(s) watched)");
          return;
        }

        Log.Debug($"announce [{reason}]{(detail is null ? string.Empty : $" {detail}")}: identity moved - {moved}");
      }

      _unaskedDeclined = false;
      _announcedOnce = true;
      _announcedIds = ids;
      _announcedStamp = _currentStamp;

      /*
       * Work is on its way, so re-shoot the watch against what is ABOUT to be displayed. Deliberately AFTER the check above: capturing first
       * would compare the selection against itself and no charm could ever reach a board. Selected names join here (UI thread, O(rows))
       * because a row can change routing on its own account even when every name listed under it keeps its answer.
       */
      if (_currentTimeline is { } fresh)
      {
        var names = new List<string>(selected.Count);
        foreach (var fight in selected) names.Add(fight.Name);
        _watch.CaptureSelection(fresh, names);
      }
      Log.Debug($"announce [{reason}]{(detail is null ? string.Empty : $" {detail}")}: {ids.Count} row(s), stamp {_currentStamp}"
                + (force ? " (forced)" : string.Empty));
      DerivedSelectionChanged?.Invoke(new BoardRequest(selected, _currentStamp, reason, detail), _currentSnapshot);
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
    internal static long SelectionStamp(DerivedSnapshot snapshot) => unchecked((snapshot.FactCount * 397) ^ VerdictStamp(snapshot));

    /*
     * The identity half of SelectionStamp on its own: it moves when a verdict moves (an override, a charm window, a rank the rules could
     * not see last pass and can now) even though no fact arrived — the ONE thing a selected row's own figures cannot show. FightTable keeps
     * it apart from the fact count because a live tail moves the count every pass and nothing moves this until the rule book changes its
     * mind. (A fingerprint over the selected rows' figures was built, measured and deleted: replaying a capture as growing prefixes
     * moved it constantly while flipping no verdict — see docs/DesignNotes.md → "Snapshot unless a name changed what it is".)
     */
    internal static long VerdictStamp(DerivedSnapshot snapshot)
    {
      /*
       * The identity term is what a board would SEE (`AnswerStamp`), not what evidence the rule book holds (`StateStamp`). A Full
       * pass reseeds its timeline from the memory the previous pass wrote, so the pass after a load legitimately carries evidence
       * tuples nobody had yet - and with the evidence stamp in this term that reads as "content moved", forcing a whole-capture
       * rebuild over identical rows (field report: boards built, cleared, and rebuilt about 9 s after a select-all). An unstamped
       * snapshot - which is what a test hands the pane, since only DerivedFightRows.Build stamps - falls back to the evidence stamp
       * rather than comparing two zeros, which would make every pass look identical to every other.
       */
      var verdicts = snapshot.AnswerStamp != 0 ? snapshot.AnswerStamp
                   : snapshot.Timeline is { } timeline ? timeline.StateStamp()
                   : 0L;
      return verdicts;
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
      // arranges the guard would swallow every real toggle forever (see _paneReady). RebuildVisible below does nothing at all until there is a
      // grid to point at, so absorbing this firing costs nothing.
      if (!_paneReady) return;
      if (fightShowBreaks.IsChecked.HasValue && fightShowBreaks.IsChecked != _currentShowBreaks)
      {
        _currentShowBreaks = fightShowBreaks.IsChecked == true;
        ConfigUtil.SetSetting("NpcShowInactivityBreaks", _currentShowBreaks);
        RebuildVisible();
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
        RebuildVisible();
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
     * Identity Table pane, the app's ONE unset. This pane still writes them because a fight row is where a misfiled mob is noticed.
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
    private void RefreshClick(object sender, RoutedEventArgs e) => AnnounceSelection(BoardReason.Manual, "refresh button", force: true);

    // Greyed out unless the grid has a real fight selected - every item in this menu acts on the selection.
    private void GridContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
      // Nothing is announced while the menu is up; whatever the opening itself selected waits for the close.
      _settle.MenuOpen = true;

      // The press has already spent whatever selection it walked over (see GridPreviewRightButtonDown). It comes
      // back now, before the enable/disable arithmetic below, the group gestures, or a settle-driven board build
      // can answer for what the button did instead of what the operator chose.
      var remembered = _rightPressSelection;
      _rightPressSelection = null;
      if (remembered is { Count: > 0 }
          && !fightGrid.SelectedItems.OfType<DerivedFightRow>().ToHashSet().SetEquals(remembered)) RestoreSelectedRows(remembered);

      /*
       * Point the cursor at the row under the pointer, exactly as the legacy table did (`if (source.DataContext is Fight
       * fight) dataGrid.CurrentItem = fight;`). Two things follow: every position-based item below reads the section the
       * person actually aimed at, and the highlight on screen says which row the menu is about to act on — so "Set X as"
       * and "Select Group" answer for the line the pointer is touching rather than for whatever was clicked minutes ago.
       */
      _menuRow = RowUnderPointer(e.OriginalSource);
      if (_menuRow is { IsDivider: false }) fightGrid.CurrentItem = _menuRow;

      var selected = GetSelectedFights();
      var hasFight = selected.Count > 0;
      // The name the cascade's header will show (this project compiles with nullable annotations off, hence the explicit null).
      string firstSelectedName = hasFight ? selected[0].Name : null;

      /*
       * The range-based selects walk _rows in section order and select by grid POSITION (SelectVisibleRuns). A user sort
       * reorders the records under those positions, so on a sorted grid they would highlight arbitrary fights - and
       * announce them to the boards. Search does not have this problem: it walks the materialized view (SearchForNpc).
       * Offer range-based selects only while unsorted. Restoration uses row instances and needs no such guard.
       */
      var unsorted = fightGrid.SortColumnDescriptions.Count == 0;

      // Enabled by what the grid can actually do with: all/unselect by current selection, group by a real row
      // under the cursor (a divider carries no section of its own).
      selectAllItem.IsEnabled = false;
      foreach (var row in _rows)
      {
        // `_rows` IS the visible list, so this only asks whether a Select All would have anything to take; dividers carry no fight.
        if (row.Fight is not null) { selectAllItem.IsEnabled = unsorted; break; }
      }

      var groupRow = GroupTargetRow();
      var hasSection = SectionRowsOf(groupRow).Count > 0;

      unselectAllItem.IsEnabled = fightGrid.SelectedItems.Count > 0;

      // Clear All needs a session to re-open over. With no engine there is nothing loaded, and the menu item would be
      // offering to unload an empty window (MainWindow.ClearAllFights guards the same thing for the file itself).
      clearAllItem.IsEnabled = _session is not null;
      // Group items follow the pointer (GroupSectionSelect): a section must be nameable, and un-selecting additionally
      // needs something selected to take away. Selecting does NOT — with an empty selection, right-clicking a fight row
      // and taking its whole pull is the point of the item (the legacy menu enabled these whenever it had rows at all).
      selectGroupItem.IsEnabled = GroupSectionSelect.CanSelect(hasSection) && unsorted;
      unselectGroupItem.IsEnabled = GroupSectionSelect.CanUnselect(hasSection, fightGrid.SelectedItems.Count) && unsorted;

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
      if (_settle.CloseMenu()) AnnounceSelection(BoardReason.MenuClose);
    }

    /// <summary>
    /// Tunnels ahead of the control's press handling (which runs during the bubbling MouseDown below it) to
    /// remember what was selected before it can collapse. See _rightPressSelection.
    /// </summary>
    private void GridPreviewRightButtonDown(object sender, MouseButtonEventArgs e)
    {
      // Ctrl/Shift means the operator IS editing the selection with this click; the control keeps it then.
      _rightPressSelection = Keyboard.Modifiers != ModifierKeys.None || fightGrid?.SelectedItems is not { } items
        ? null
        : items.OfType<DerivedFightRow>().ToList();
    }

    /// <summary>
    /// A left gesture ends any unconsumed right-press memory.
    /// </summary>
    private void GridPreviewLeftButtonDown(object sender, MouseButtonEventArgs e) => _rightPressSelection = null;

    // Keyboard selection and keyboard-opened menus must never resurrect an abandoned mouse gesture.
    private void GridPreviewKeyDown(object sender, KeyEventArgs e) => _rightPressSelection = null;

    /*
     * Two row dials, no more - Inactivity (dividers) and Tanking (rows whose whole story is something hitting
     * us). Search deliberately does NOT hide rows. The point of a
     * name here is to fight that raid event - the row needs to be HIGHLIGHTED and IN VIEW so the user can right-
     * click it and select the whole group, not removed from the list they were reading. That is the legacy table's
     * behavior, ported whole: one current result at a time, found on a debounce while typing, cycled with Enter /
     * Shift+Enter (SearchForNpc).
     */
    private bool IsShown(DerivedFightRow row) => row.IsDivider
      ? _currentShowBreaks
      : _currentShowTanking || ShownWhenTankingHidden(row.Fight);

    // The projection the dials currently call for, in list order and by reference (ShownRows).
    private List<DerivedFightRow> VisibleRows() => ShownRows.Project(_allRows, IsShown);

    /*
     * A dial moved: re-point the grid at the new projection. Wholesale rather than reconciled because a toggle moves most of the list at once,
     * and one ItemsSource assignment repaints once where hundreds of insert/remove notifications would not.
     *
     * The selection follows what the reader can still see, BY INSTANCE — legal because `_allRows` and this projection share the objects. Legacy
     * had to drop the selection on every swap (its two lists held different Fight objects) and this pane used to restore it by recomputing shown
     * positions; neither is needed now, so a row that survives the toggle keeps its highlight and only what the new view legitimately hides loses
     * one — with the report told either way, so it never keeps showing a fight that stopped being on the list.
     */
    private void RebuildVisible()
    {
      if (fightGrid is not { } grid) return;

      var held = new List<DerivedFightRow>();
      foreach (var item in grid.SelectedItems)
      {
        if (item is DerivedFightRow row) held.Add(row);
      }

      var shown = VisibleRows();
      _rows = new ObservableCollection<DerivedFightRow>(shown);
      grid.ItemsSource = _rows;
      grid.SelectedItems.Clear();

      var keeps = new HashSet<DerivedFightRow>(shown, ReferenceEqualityComparer.Instance);
      if (_searchEntry != null && !keeps.Contains(_searchEntry)) ClearSearchMark();
      _searchIndex = 0;
      foreach (var row in held)
      {
        if (keeps.Contains(row)) grid.SelectedItems.Add(row);
      }

      AnnounceSelection(BoardReason.DialChange, "row dial", force: true);
    }

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
        UiElementUtil.RestoreWatermark(fightSearchBox, Resource.NPC_SEARCH_TEXT);
        ClearSearchMark();
        fightGrid.Focus();
      }
    }

    private void SearchBoxTextChanged(object sender, TextChangedEventArgs e)
    {
      _searchTextTimer?.Stop();

      // Legacy's debounce fires only when something was ADDED: backspacing alone must not re-search, but it does
      // drop the stale highlight so an old mark never sits on a row the text no longer names.
      if (fightSearchBox.Text == Resource.NPC_SEARCH_TEXT) return;   // the watermark itself is not a query
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

      // Search follows the displayed order, which differs from _rows when a column is sorted.
      var records = fightGrid.View?.Records;
      if (fightSearchBox.Text.Length == 0 || records == null || records.Count == 0) return;

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
          if (records.GetItemAt(i) is DerivedFightRow { IsDivider: false, Name: not null } row &&
              row.Name.IndexOf(fightSearchBox.Text, StringComparison.OrdinalIgnoreCase) > -1)
          {
            row.IsSearchResult = true;
            _searchEntry = row;
            _searchIndex = i + (1 * direction);
            Dispatcher.InvokeAsync(() =>
            {
              // Resolve the object when scrolling: a sort, filter toggle or newer search may have landed while queued.
              if (!ReferenceEquals(_searchEntry, row)) return;
              var rowIndex = fightGrid.ResolveToRowIndex(row);
              if (rowIndex >= 0) fightGrid.ScrollInView(new RowColumnIndex(rowIndex, 0));
            });
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
     * The right-click menu's selection items. Programmatic selects go through SelectRows over the VISIBLE rows as runs (`SelectVisibleRuns`),
     * because SelectRows wants contiguous grid ranges and a divider row takes no range. Restoring a selection does not need the walk — it adds row
     * instances (RestoreSelection). Each handler announces straight away rather than waiting
     * out the settle timer: "Select All" is one deliberate act, not a drag to debounce; the timer's later tick
     * re-announces the same ids and no-ops.
     */
    private void SelectAllClick(object sender, RoutedEventArgs e)
    {
      SelectVisibleRuns(row => row.Fight is not null);
      AnnounceSelection(BoardReason.SelectCommand, "select all");
    }

    private void UnselectAllClick(object sender, RoutedEventArgs e)
    {
      fightGrid.SelectedItems.Clear();
      AnnounceSelection(BoardReason.SelectCommand, "unselect all");
    }

    private void SelectGroupClick(object sender, RoutedEventArgs e) => SelectGroup(true);

    private void UnselectGroupClick(object sender, RoutedEventArgs e) => SelectGroup(false);

    // Find the encounter in the complete display list, then intersect with visibility. A hidden divider still bounds a group.
    private void SelectGroup(bool add)
    {
      var section = new HashSet<DerivedFightRow>(SectionRowsOf(GroupTargetRow()), ReferenceEqualityComparer.Instance);
      if (section.Count == 0) return;

      if (add)
      {
        /*
         * ADD, never replace — the legacy item was `if (!SelectedItems.Contains(fight)) Add`, and that asymmetry is what
         * made "Select All, then Select Group" harmless there. So the gesture's answer is the union: keep every row the
         * person already had (whatever they selected by hand, by drag or by Select All) and add this section to it. The
         * kept rows go through the SAME run walk as the new ones rather than being left alone, because a single
         * SelectRows range pass is what makes the result independent of whether the control replaces or extends here.
         */
        foreach (var item in fightGrid.SelectedItems)
          if (item is DerivedFightRow row) section.Add(row);

        SelectVisibleRuns(row => section.Contains(row));
      }
      else
      {
        Predicate<DerivedFightRow> inSection = row => section.Contains(row);
        var remove = new HashSet<DerivedFightRow>();
        foreach (var item in fightGrid.SelectedItems)
        {
          if (item is DerivedFightRow { IsDivider: false } row && inSection(row)) remove.Add(row);
        }

        foreach (var row in remove) fightGrid.SelectedItems.Remove(row);
      }

      AnnounceSelection(BoardReason.SelectCommand, add ? "select group" : "unselect group");
    }

    /// <summary>
    /// Replaces the selection with exactly these rows, through the control's own selection-changed door. Rows that left the
    /// projection between press and open are not chased — the operator's dials removed them.
    /// </summary>
    private void RestoreSelectedRows(List<DerivedFightRow> rows)
    {
      var keeps = new HashSet<DerivedFightRow>(rows, ReferenceEqualityComparer.Instance);

      // Clear first: the controller's Reset re-lists whatever SelectedItems still holds, so emptying it is the
      // only ordering where Reset means cleared.
      fightGrid.SelectedItems.Clear();
      foreach (var row in _rows)
      {
        if (keeps.Contains(row)) fightGrid.SelectedItems.Add(row);
      }
    }

    /*
     * The row the pointer is standing on. Walks up from whatever raised the event until an element carries a row as its
     * data context — in this application's grids a cell's DataContext IS the row item, the same read the identity pane's
     * pencil makes — and stops at the first non-visual, because a `Run` inside text has no visual parent to ask (asking one
     * throws, and a throw in ContextMenuOpening costs the menu).
     *
     * Null is a real answer: a menu opened over a divider's gap, over the header row, or from the keyboard names no row,
     * and each of those falls through to the next link of the chain below.
     */
    private static DerivedFightRow RowUnderPointer(object originalSource)
    {
      if (originalSource is not System.Windows.Media.Visual visual) return null;

      for (DependencyObject v = visual; v is not null; v = System.Windows.Media.VisualTreeHelper.GetParent(v))
        if (v is FrameworkElement { DataContext: DerivedFightRow row }) return row;

      return null;
    }

    /*
     * Which row this menu's group items are about, in the order GroupSectionSelect states it: the row the pointer opened
     * the menu on, then the grid's current row, then the first selected fight row. A row that names no section (a divider)
     * is SKIPPED rather than inherited, so clicking the gap between two pulls still un-selects the group that is on screen
     * instead of doing nothing.
     */
    private DerivedFightRow GroupTargetRow()
    {
      // A stale instance from a snapshot that has since been replaced is no row at all: IndexOf answers -1 and the chain moves on.
      var clicked = _menuRow is { } menuRow && _rows.Contains(menuRow) ? menuRow : null;
      var current = fightGrid.CurrentItem is DerivedFightRow currentRow ? currentRow : null;

      DerivedFightRow firstSelected = null;
      foreach (var item in fightGrid.SelectedItems)
      {
        if (item is DerivedFightRow { IsDivider: false } row) { firstSelected = row; break; }
      }

      var index = GroupSectionSelect.TargetFor(
        _rows.IndexOf(clicked), clicked is { IsDivider: false },
        _rows.IndexOf(current), current is { IsDivider: false },
        _rows.IndexOf(firstSelected));

      return index >= 0 && index < _rows.Count ? _rows[index] : null;
    }

    // Name and start time identify one life even when a derive renumbers fight ids.
    private static FightKey KeyOf(DerivedFight fight) => new(fight.Name, fight.BeginTime);

    /*
     * Re-put a selection onto brand-new row objects. The key (name + the second it began) is what identifies a life across passes, because
     * FightProjection renumbers ids every pass — but the SELECT is by instance: `_rows` holds exactly what is on screen, so an object found by
     * key is added directly and no position has to be computed for it. A kept fight the dials hide is simply not in `_rows` to be found, which is
     * the right answer rather than a loss: the report cannot keep describing a row this list does not have.
     */
    private void RestoreSelection(HashSet<FightKey> keepKeys)
    {
      if (fightGrid is not { } grid) return;

      foreach (var row in _rows)
      {
        if (row.Fight is { } fight && keepKeys.Contains(KeyOf(fight)) && !grid.SelectedItems.Contains(row))
          grid.SelectedItems.Add(row);
      }
    }

    /*
     * Pay the capture's one reveal: when its file finishes reading, put the list at its newest row - ONCE, and for no other reason ever. Nothing here
     * asks where the pointer is or whether anybody is looking at this tab: legacy's `!IsMouseOver` excuse was never protection, since the list slid
     * down again the moment the pointer left, and a derive pass lands about twice a second while a raid fights. FightListReveal holds the sequencing -
     * including why two doors call this method, the apply paths (rows arriving after the read) and the loading-complete event (the read finishing
     * after the rows): either order has to land, and TryPay answers exactly once whichever came first.
     *
     * One row is no fold to cross so nothing scrolls, and the debt is spent anyway: a reveal left owed until some later pass is the exact surprise this
     * retires. A grid on a tab nobody has slid out gets scrolled regardless (one layout pass) - that is what makes the tab OPEN at the bottom. The
     * scroll waits that pass because rows exist before the scrollable extent does; scrolling to a row the presenter has not measured yet is a no-op.
     */
    private void RevealNewestRowIfOwed()
    {
      if (!_reveal.TryPay(_rows.Count > 0)) return;

      if (fightGrid is not { } grid || _rows.Count < 2) return;

      grid.Dispatcher.BeginInvoke(new System.Action(() =>
      {
        // Re-read the count: another pass may have landed in the meantime, and the point is the LAST row, whatever number that is now. The view is
        // re-asked too - Clear All or a new log between this ask and that layout pass leaves a grid with no records to scroll, and an exception here
        // has nobody above it to catch it.
        var last = _rows.Count;
        if (last > 1 && grid.View != null) grid.ScrollInView(new RowColumnIndex(last, 0));
      }), DispatcherPriority.Background);
    }

    /*
     * Both programmatic selects share this: every visible row matching the predicate, range by range. `_rows` is what the grid renders, so a run
     * of it IS a run of grid rows and the only offset left is where the grid's data begin. The shown-position walk this replaces existed purely to
     * translate between index-based grid calls and a grid-side filter; with that filter gone, the translation went too.
     */
    private void SelectVisibleRuns(Predicate<DerivedFightRow> matches)
    {
      var first = FirstRecordRow();
      foreach (var (from, to) in ShownRows.Runs(_rows, matches))
        fightGrid.SelectRows(first + from, first + to);
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
