using log4net;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

/*
 * Annotations only, no null-flow analysis: the project builds with Nullable=disable, and this API speaks in optional
 * strings/kinds because a name legitimately has no class, no owner and no verdict. Stating that is not the same as
 * switching on warnings across code written before nullable existed.
 */
#nullable enable annotations

namespace EQLogParser
{
  // Owns the the derivation for one log-open: taps the pipeline statics, receives the chat fan-out,
  // and produces derived-list snapshots. Subscriptions are process-global parser events, so exactly
  // one session may run at a time — MainWindow creates it before LogReader starts and disposes it
  // when the log closes, mirroring the harness lifecycle (RunCore) that proved this wiring.
  //
  // Derivation is quiescent: CombatCapture.DeriveQuiescent parks ingest at its gate for the pass,
  // so a snapshot never races mid-append fact mutation and no tail line is lost. A load that stops moving gets its
  // pass on the next tick, and a live tail that never stops moving gets one on DeriveCadence's clock — waiting
  // for silence alone meant a raid-length freeze on every surface reading the snapshot. There is no Re-derive button: every trigger refreshes itself,
  // overrides re-derive themselves, and a failed pass comes back on DeriveCadence.RetryDelayS's ladder - no flow
  // ever needed a human to force one (the button existed only because the old latch stopped asking forever; see
  // RederiveAsync's catch).
  internal sealed class DeriveEngine : IDisposable
  {
    public static DeriveEngine Active { get; private set; }

    /*
     * A process-unique number for this capture, so a surface can tell "the session I already looked at" from "a session that
     * replaced it" WITHOUT holding the engine. That is not a nicety: Dispose promises that closing a log stops being the reason
     * its records are still reachable, and a long-lived window that tags "which capture wrote my state" with the instance itself
     * breaks exactly that promise (a hidden meter window would keep a dead engine - every fact table in it - alive for the rest of
     * the run). Ids come from a static counter starting at 1, so 0 means "no session seen" and two captures never share one.
     */
    internal int SessionId { get; } = Interlocked.Increment(ref _sessionCounter);

    // All identity-write UI doors ask for a Full pass. Reading its ledger lets a census reject writes from OTHER panes too.
    internal int ExplicitRequestGeneration => _requests.StartBound();

    private static int _sessionCounter;

    public static event Action ActiveChanged;

    // Raised on the derivation thread; subscribers marshal to the dispatcher themselves.
    public event Action<DerivedSnapshot> Derived;

    /*
     * "Damage came in", for the one surface that opens itself: the meter. Legacy raised this on **every damage line** of a
     * fight (`FightManager.UpdateIfNewFightMap` fires it whenever `fight.DamageHits > 0`, outside the new-fight branch), which
     * is why closing a meter with the X during a pull brought it back within a line or two. A derived list has no per-line
     * moment — rows appear on a pass — so this announces new fact ordinals with recent overlay activity, including a completed
     * one-shot. Death-only republishes cannot spend the same damage twice. The reader no-ops while a window exists, so the extra
     * passes cost one null check; what they buy is that the reopen happens on the damage the player can see rather than at the
     * next pull.
     *
     * STATIC, unlike Derived, because its reader outlives a capture: MainWindow keeps one subscription for the whole run
     * and the meter must auto-open for the next log too, not just the one open when it subscribed. Unsubscribed on window
     * close, exactly like ActiveChanged.
     */
    public static event Action LiveDamageObserved;

    // Newest row activity announced so far, so the announcement means "newer than last time" and not "still fighting".
    private int _lastAnnouncedActivityOrdinal;

    // Liveness feedback while capture is dirty (raised on the timer, i.e. the dispatcher):
    // total captured facts since no snapshot covers them yet.
    public event Action<long> Capturing;

    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    // What this class started its damage array with before file-size sizing existed; used when no hint arrives.
    private const int UnhintedDamageSlots = 100_000;

    // Sized per session from the file about to be read (see FactCapacity); unhinted, the fixed start above.
    // Doubling still carries a live tail, whose length nobody knows.
    private readonly DamageFactTable _facts;

    /*
     * Healing in its own table, sharing the damage table's interned names — see HealFact for why the streams
     * are separate. Captured from here so the engine's heal population is exactly the population the healing
     * board reads (RecordsStore gets the same records, off the same event).
     */
    private readonly HealFactTable _heals;
    private readonly CombatCapture _capture;
    private readonly MeterArrivals _meterArrivals = new();

    internal int MeterCapturedCount => _facts.FactCount;

    // The snapshot currently on screen (see DerivedSnapshot.Facts): a selection is materialized against
    // the pass that produced the rows, so it has to be reachable from the UI without re-deriving.
    private DerivedSnapshot _snapshot;
    // Published under the capture gate so meter headers cannot be read between a projection and its publication.
    private DerivedSnapshot _meterSnapshot;

    // Reused row objects for THIS capture only (DerivedRowCarry); nulled on Dispose so a closed log's fights are not kept alive by them.
    private DerivedRowCarry _rowCarry = new();

    /*
     * The projection carried from pass to pass, together with the index its facts went into. Whether a pass may continue it
     * is decided inside (FightProjectionCache) on two gates: the watermarks still name the same facts, and the
     * classification stamp matches the one the carried rows were projected under.
     */
    private readonly FightProjection.FightProjectionCache _projection = new();

    // Bound in the constructor so Dispose can tell "my seam is still wired" from "another session already replaced it"
    // (a method group allocates a fresh delegate each time, so ReferenceEquals needs one stored instance).
    private readonly Func<string, double, IdentityKind> _liveKindAt;

    /// <summary>The owner half of the same seam, held as a field so Dispose can tell whether the seam is still ours.</summary>
    private readonly Func<string, string?> _liveOwner;

    private readonly DispatcherTimer _quietTimer;
    private int _deriveInFlight;
    private long _lastTickCount = -1;

    /*
     * TWO watermarks, where this class used to keep one, and the bug two counters is the fix for: a cheap lane fold puts facts
     * on screen without putting them through the rule book, so "what the surfaces show" and "what the rules have read" are two
     * different numbers. With one counter, a cheap pass retired the count that classification still owed, and the cadence's
     * first test ("captured == derived") then answered None for as long as nothing further arrived - which, when the last lines
     * of a raid night are what the fold covered, is forever. DeriveCadence.Decide takes both.
     *
     * Both are written from what a pass COVERED (snapshot.FactCount, measured inside the ingest gate), never from a re-read of
     * CapturedTotal outside it: acknowledging facts nobody processed is the same mistake wearing a different hat.
     */
    private long _lastProjectedCount = -1;
    private long _lastClassifiedCount = -1;

    /*
     * Explicit requests, so one arriving while a pass runs waits to be served instead of being dropped on the busy flag -
     * see DeriveRequests for why a dropped override on an otherwise idle log is never retried by anything.
     */
    private readonly DeriveRequests _requests = new();

    /*
     * How often the cadence is asked. It is a POLLS rate, not the refresh rate — DeriveCadence takes seconds and
     * facts-per-second precisely so this number can be changed without moving any threshold — and it is fine-grained so
     * a pass lands on its moment instead of on the next whole second: the end-of-load pass up to a full interval
     * earlier, and a live tail's floor read within a quarter of it. A dispatcher timer at Background priority, so this
     * costs a wakeup behind layout when nothing else is queued and never competes with rendering.
     *
     * A tenth of a second, down from a quarter: the cheap lane's interval is half a second, and a poll that coarse would add its
     * own quarter-second of jitter on top of it. No threshold moves — the cadence reads durations, which
     * `TheRuleDoesNotDependOnHowOftenItIsAsked` holds — so this buys only promptness.
     */
    private const int TimerIntervalMs = 100;

    /*
     * The measurements the cadence needs, all monotonic intervals rather than DateTime moments (a clock adjustment must
     * not buy a free re-derive or hold one off): how long this count has held still, how wide the observation window behind the
     * growth RATE was, and TWO clocks for the two lanes (DeriveCadence.DeriveKind) — `_sinceFullPass` against the cost of
     * the last pass that classified, `_sinceAnyPass` against the cheap floor. One stopwatch could not express "cheap now,
     * expensive in two more seconds", which is the whole shape of a live raid refresh; see _lastFullPassSeconds for why only the
     * expensive lane's clock may be restarted by an expensive pass.
     */
    private readonly Stopwatch _sinceAnyPass = Stopwatch.StartNew();
    private readonly Stopwatch _sinceFullPass = Stopwatch.StartNew();
    private readonly Stopwatch _sinceFactChange = Stopwatch.StartNew();
    private readonly Stopwatch _sinceTick = Stopwatch.StartNew();
    private double _lastFullPassSeconds;

    /*
     * The verdicts the last full pass produced, kept for the cheap lane to project over. Carrying the INSTANCE rather than
     * rebuilding it is what makes the cheap lane cheap twice over: no rule book runs, and `EntityTimeline.StateStamp()` comes back
     * unchanged, which is exactly the answer FightProjectionCache needs to keep folding from its watermark instead of re-walking
     * the night. Nothing mutates a timeline outside ClassificationRules/RegistrySeed/overrides, all of which build a fresh one,
     * so this reference cannot be revised behind the pass that holds it.
     *
     * The staleness this accepts is stated in DeriveCadence: a verdict readable only from facts that arrived after the last
     * full pass (a pet folding onto its raiders, a charm flipping a name) lands one full cadence later.
     */
    /*
     * Volatile because IdentityLookup reads it from the UI thread while a pass publishes it from the derive task. A full
     * pass BUILDS a new timeline and swaps the reference (ClassificationRules/RegistrySeed/overrides never revise one in
     * place), so a reader sees either the old verdicts or the new ones, never a half-written table.
     */
    private volatile EntityTimeline? _carriedTimeline;

    /*
     * Rule aggregates and stream cursors carried across classifying passes so the costly walks resume where they
     * stopped (ClassificationState is the full contract). The design keeps two properties whole: every pass still
     * builds its timeline FRESH, so a rule's walk sees what the stages before it produced this pass - never its
     * own earlier passes' claims; and each gated rule rebuilds from zero whenever its boundary digest moved,
     * which is "something upstream changed", so a carried aggregate can lag knowledge, never contradict it.
     * Overrides enter no rule's input and need no invalidation: they apply last, exactly as before. One instance
     * per capture - a new session makes a new one, and the first pass of a log is a full walk whatever was
     * classified before it.
     */
    private readonly ClassificationState _classification = new();

    // Consecutive derive passes that THREW. Drives DeriveCadence.RetryDelayS's backoff in QuietTick and is
    // zeroed by any completed pass; nothing disables anything - the surfaces keep trying at the ladder's pace for as
    // long as this session owns them.
    private volatile int _deriveFailures;

    // Time since the last THREW pass; only consulted while _deriveFailures > 0.
    private readonly Stopwatch _sinceFailedPass = Stopwatch.StartNew();

    /*
     * Quiet retries spent on the CURRENT failure: attempts made while the capture was not growing. The allowance
     * (DeriveCadence.MaxQuietFailureRetries) exists because a deterministic throw on a closed log would otherwise run a full pass
     * and write an error-with-stack once a minute for the life of the window - the same file that carries the raid. Any new fact
     * clears it: a capture that starts moving again is exactly the reason to try, and while facts arrive every pass happens for its
     * own sake (and logs its own failure) without this counter counting anything.
     */
    private volatile int _quietFailureRetries;

    // One park line per parking, not one per tick - QuietTick asks ten times a second.
    private volatile bool _failureParkLogged;

    // The first pass of a session logs itself once, whatever lane the cadence picked: "the list never filled"
    // is otherwise invisible in a log that carries a million fact lines - either MainWindow's session-started
    // line or this one is missing, and that says which half of the chain died.
    private bool _firstDeriveLogged;
    private bool _disposed;

    /*
     * captureBytes: how many bytes of log this session expects to append facts from, used ONLY to size the row
     * arrays (see FactCapacity). 0 means "no hint" and gives the tables their own defaults. A wrong hint cannot
     * corrupt anything — growth still doubles — it can only be too big or too small, which is why a caller may
     * estimate from a file length instead of waiting for certainty.
     */
    public DeriveEngine(long captureBytes = 0)
    {
      // A fresh capture gets the full rule book even if this app run already saw a stage retire over other data:
      // retirement protects one session's pass loop, it is not a verdict about every file the process will open.
      ClassificationRules.ResetRuleHealth();

      _facts = new DamageFactTable(captureBytes > 0 ? FactCapacity.DamageForBytes(captureBytes) : UnhintedDamageSlots);
      _heals = new HealFactTable(_facts, FactCapacity.HealForBytes(captureBytes));
      _capture = new CombatCapture(_facts, _heals, damageArrived: MeterDamageArrived);
      ChatSink = new IdentityChatSink(_capture);
      _liveKindAt = LiveKindAt;
      _liveOwner = LiveOwnerOf;
      _quietTimer = new DispatcherTimer(DispatcherPriority.Background)
      {
        Interval = TimeSpan.FromMilliseconds(TimerIntervalMs)
      };
      _quietTimer.Tick += QuietTick;
    }

    // Chat fan-out target for LogProcessor (composed with the app's own sinks — see CompositeChatSink).
    public IChatSink ChatSink { get; }

    public void Start()
    {
      _capture.Start();
      Active = this;

      /*
       * The seam IdentityLookup asks when it wants to know what THIS capture decided about a name (docs: the roster stops
       * answering a question about evidence). Wiring it here rather than in Core is the dependency direction: Core owns
       * the question, the engine owns the verdicts, and closing the session takes the seam away so the app's memory
       * answers again instead of reading a dead capture's timeline.
       */
      IdentityLookup.LiveVerdict = _liveKindAt;
      IdentityLookup.LiveOwner = _liveOwner;

      /*
       * The other seam a closed session must not leave standing: the healing board's fallback and the death log's 20-second
       * window read heal RECORDS, and records are no longer kept as objects anywhere (the capture's rows are the store —
       * HealRecordSource). Wiring the table here is what makes "this capture" answer those two; Dispose takes it down with
       * the same still-mine guard as the identity seams, because a new log can be open before an old engine is disposed.
       */
      HealRecordSource.Current = _heals;
      ActiveChanged?.Invoke();
      _quietTimer.Start();
    }

    /*
     * IdentityLookup's view of the carried verdicts. Unknown means "this log never said", which is the seam's cue to ask
     * memory rather than an answer about the name; PositiveInfinity means "as far as we know".
     *
     * The lock is kept as cheap insurance, not as the load-bearing part of the design (docs/DesignNotes.md → "A published timeline is
     * frozen"): the carried instance was published by replacement and frozen by Classify, so this UI-thread read has no writer to race,
     * locked or not. What still matters here is the SHAPE — hop inside one lock and hand back a plain IdentityKind, so no live
     * collection escapes to a menu handler that might walk it after the pass moves on.
     */
    private IdentityKind LiveKindAt(string? name, double t)
    {
      if (name is null || _carriedTimeline is not { } timeline) return IdentityKind.Unknown;

      lock (timeline.SyncRoot)
        return double.IsPositiveInfinity(t) ? timeline.Identity(name) : timeline.IdentityAt(name, t);
    }

    /*
     * "Whose pet, according to this capture?" — the charm window's owner (docs/DesignNotes.md -> "A charm takes a mob off
     * the enemy list"), which lives nowhere else: no file records that tonight's `a bone walker` belonged to Kylo. Same
     * locking law as LiveKindAt above: this answers UI-thread questions, and nothing may escape the lock but a string. The frozen
     * carried timeline means there is no writer to race; the lock stays so the two hops have one shape and one rule.
     */
    private string? LiveOwnerOf(string? name)
    {
      if (name is null || _carriedTimeline is not { } timeline) return null;

      lock (timeline.SyncRoot)
        return timeline.OwnerOf(name, double.PositiveInfinity);
    }

    /// <summary>Freezes selection, prefixes and identity before the export worker starts.</summary>
    internal CaptureExport.Options PrepareExport(DerivedSnapshot snapshot, IReadOnlyList<DerivedFight> selected,
      string title, string sourceFile, string localPlayer)
    {
      return _capture.DeriveQuiescent(() =>
      {
        if (_disposed || snapshot.SessionId != SessionId) return null;
        var scope = CharmPetRows.WithHiddenPets(selected, snapshot.AllFights);
        return new CaptureExport.Options
        {
          EventName = title,
          SourceFile = sourceFile,
          LocalPlayer = localPlayer,
          SessionId = SessionId,
          AnchorS = (long)selected.Min(fight => fight.BeginTime),
          DamageOrdinals = snapshot.DamageIndex.CaptureOrdinals(scope),
          Windows = selected.Select(fight => new CaptureExport.Span((long)fight.BeginTime, (long)fight.LastTime)).ToArray(),
          ToOrdinal = snapshot.Facts.FactCount,
          HealLimit = snapshot.Heals?.HealCount ?? 0,
          EvidenceRows = snapshot.Facts.Evidence.ToArray(),
          Lens = CaptureExportLens.Create(snapshot.Timeline, snapshot.Facts.InternedNames)
        };
      });
    }

    /*
     * The name census for the Identity Table window. Built on demand rather than carried in the snapshot: a derive lands every
     * few seconds while a log loads and a census nobody has open would be thrown away each time.
     *
     * It reads THE TIMELINE THE LAST EXPENSIVE PASS PUBLISHED (`_carriedTimeline`) instead of assembling a second one. It used to run
     * `RegistrySeed` + `ClassificationRules` again here, on a throwaway state, which is the same rule book the derive had just finished:
     * measured 186 ms on Kizant and 261 ms on Incogitable per census (docs/DesignNotes.md → "The Names census reads the derive's own
     * timeline"), paid again on every 2 s auto-refresh with the tab open, and landing on a thread of the app's choosing. Reusing the
     * published instance buys two further things beyond the time: the window cannot disagree with the grids any more (it read a rule book
     * that ran a few seconds LATER than the one behind the fight list, so a name could say Player in this pane and sit under another
     * verdict on the boards), and nothing here touches the carried classification AGGREGATES - cursors and candidate tables stay the
     * derive thread's alone, which is what made the old copy look necessary.
     *
     * Reading that instance without a lock is sound because publication is by reassignment: `Classify` builds a NEW timeline and only
     * assigns `_carriedTimeline` when it is finished (and `FightProjection` reads a timeline, never writes one), so what this walks is
     * already frozen - a later pass replaces the reference, it does not edit this object.
     *
     * Facts can arrive while this walks them. That is acceptable here and nowhere else: a census is display data, and a name whose
     * damage shifts by one fact between two passes costs nobody a decision, whereas a lock on the capture path would be paid for by
     * every event. A table that refuses the walk outright leaves the previous list on screen.
     */
    public ClassificationReport? BuildNameCensus()
    {
      if (_disposed) return null;
      using var damageRead = _facts.RetainDecoded();
      using var healRead = _heals.RetainDecoded();

      try
      {
        /*
         * An operator's verdict written since that pass is NOT replayed into the carried instance (mutating a published timeline would
         * move its state stamp and buy a full rebuild, with readers in the middle). It does not need to be: ClassificationReport asks the
         * override store itself, and an override outranks a rule verdict there - so the window is current on the operator's own writes
         * and stale only on what no one but the rules could know, which is exactly one expensive cadence (docs/DesignNotes.md →
         * "A derive pass continues where the last one stopped").
         */
        var timeline = _carriedTimeline;
        if (timeline is null)
        {
          /*
           * No expensive pass has finished yet - the window was opened in the moment between the log opening and its first full
           * derive. Run the rule book here rather than show a list of Unknowns; this is the one case where that work is not a duplicate.
           */
          timeline = new EntityTimeline();
          RegistrySeed.Apply(timeline, _facts, _capture.FirstEventTime, _capture.LastEventTime);
          ClassificationRules.Apply(_facts, timeline, _heals);
          IdentityOverrideStore.Instance.Apply(timeline);
        }

        return ClassificationReport.Build(timeline, _facts, _heals, IdentityOverrideStore.Instance,
                                         PlayerRegistry.Instance, IdentityPriorStore.Instance);
      }
      catch (Exception ex)
      {
        // A stale census beats no census, but not a silent one.
        Log.Error("Name census skipped; keeping the previous list", ex);
        return null;
      }
    }

    // A pass that classifies: what the UI asks for directly - opening the derived meter over a fresh board, and an
    // identity override (which has to re-run classification before the ruled name can move anywhere).
    public void RederiveAsync() => RederiveAsync(DeriveKind.Full);

    /*
     * THE GESTURE DOOR - somebody's click. Only a gesture records a debt in `_requests`, and this is the ONLY place that does.
     *
     * The pump used to come through here too, and that is where every pass bought a free twin. A field run over
     * `eqlog_Kizant_xegony-10-06-26.txt` shows it on every single line: `Derive done: 511 fights, 940 ms (rebuilt)` immediately
     * followed - same millisecond, same worker, same captured count - by `Derive starting over 6,917,650` and
     * `Derive done: 511 fights, 70 ms (continued)`. Not one of those pairs had a click in front of it; several had no operator
     * action at all. The mechanism: a full pass takes 600-940 ms, the pump ticks every 100 ms, and a tick that found the rule book
     * still owed simply asked again - met the busy flag, but had already stamped `_requests.Arrive()` on its way to meeting it. The
     * finished pass settled only what existed at ITS start, so the pump's own mid-pass ask looked exactly like an operator's, and the
     * cascade in the finally paid it with a second expensive lane over facts that had not moved (the 46-72 ms "continued" line IS the
     * re-run of `DerivedFightRows.Build` over every row, twice a pass, forever).
     *
     * The pump does not need to ask for leave in words it cannot tell apart from a click: its debt is ALREADY stated, as
     * `count != lastClassifiedCount`, and `DeriveCadence.Decide` re-reads that every tick until it is paid. A dropped pump ask costs
     * one tick (100 ms) and no correctness; a gesture ask must never be dropped, because on an idle log nothing else ever asks again.
     * That asymmetry is the whole boundary between this method and `StartPass`.
     */
    public void RederiveAsync(DeriveKind kind)
    {
      if (_disposed) return;

      /*
       * Stamped BEFORE the busy test, which is the whole point: a request that arrives while a pass is running used to fall
       * straight through that `return` and vanish. A cheap fold asks for nothing (it inherits verdicts, so stamping would make
       * every half-second refresh look like an operator's correction). The owed request is paid off in this task's finally.
       */
      if (kind == DeriveKind.Full) _requests.Arrive();
      StartPass(kind);
    }

    /*
     * Run a pass if none is running; asks nothing of the debt ledger. Callers are the pump (`QuietTick`), the failure ladder, and a
     * gesture that has already recorded itself above. Never call this for something a person did - the recording is what keeps their
     * click alive across a busy session.
     */
    private void StartPass(DeriveKind kind)
    {
      if (_disposed) return;
      if (Interlocked.Exchange(ref _deriveInFlight, 1) == 1) return;

      // What existed when THIS pass started is what it answers; anything later is still owed when it finishes.
      var requestBound = _requests.StartBound();

      /*
       * Debug only, for both lanes. These lines were Info and a normal session filled with them: every pass - including the ones
       * the cadence runs by itself over a capture that stopped growing - wrote a start line and a done line, so the player's log
       * said more about derive bookkeeping than about the raid. Debug=True still gets the whole story (cost, continued vs rebuilt),
       * which is what a cost question needs; an ordinary session pays two interpolated strings and no log write. Failures keep
       * their own level whatever the lane.
       */
      void Note(string message)
      {
        Log.Debug(message);
      }

      Note($"Derive starting over {CapturedTotal:N0} captured facts");

      _ = Task.Run(() =>
      {
        // Only a pass that FINISHED may settle a request: an owed request plus a deterministic throw is the one
        // combination that must not spin, so retries after a failure belong to DeriveCadence.RetryDelayS alone.
        var succeeded = false;

        try
        {
          var sw = Stopwatch.StartNew();

          // The classification this pass reached, kept out here so the sighting ledger can read it AFTER the derive
          // instead of inside it: Record() writes a file, and nothing that writes belongs between the rules and the
          // snapshot the grid is waiting on.
          EntityTimeline? classified = null;
          var snapshot = _capture.DeriveQuiescent(() =>
          {
            using var damageRead = _facts.RetainDecoded();
            using var healRead = _heals.RetainDecoded();
            /*
             * The expensive lane rebuilds the verdicts; the cheap lane takes the instance the last expensive pass left behind, so
             * no rule book runs between the two folds. If no expensive pass has ever finished there is nothing to carry, and this
             * pass becomes the expensive one whatever the cadence asked for — a first board cannot be built out of no verdicts.
             */
            if (kind == DeriveKind.Full || _carriedTimeline is null) classified = Classify();
            var timeline = classified ?? _carriedTimeline!;

            /*
             * Rows and damage index come from ONE walk: the index needs the same direction decision the rows split
             * DamageToOwner by, and that decision exists only inside FightProjection (FactOwnershipHandler) — which is why
             * the cache below owns both and hands them back together rather than an index built afterwards.
             *
             * The pass continues where the previous one stopped whenever it may (FightProjectionCache: the watermarks still
             * name the same facts, and the classification stamp matches the one the carried rows were projected under). A
             * charmed mob's damage is credited to its charmer on the stats board through the same timeline (FightFactIndex).
             */
            var fights = _projection.Project(_facts, timeline);
            var damageIndex = _projection.Index;

            // "Fight N" numbers for a stats run reading GroupId, from the same walk that draws the
            // dividers in this grid.
            Sectionizer.StampGroupIds(fights);

            /*
             * Rows are built with this session's carry (DerivedRowCarry): a row whose fight and every printed value are unchanged since
             * the last pass comes back as the SAME object instead of a fresh one plus six formatted strings. A raid runs two passes a
             * second over every row the capture has ever made, so that reuse is the difference between an idle-hour allocation rate and
             * a live one; it lives here, on the session, because a carried row holds its fight and a closed log's fights must not stay
             * reachable through anything static.
             */
            var snapshot = DerivedFightRows.Build(fights, timeline, CapturedTotal, _facts, damageIndex, _rowCarry);
            snapshot.Heals = _heals;
            // Every announcement says which capture it came from; see DerivedSnapshot.SessionId.
            snapshot.SessionId = SessionId;
            if (!_disposed) Volatile.Write(ref _meterSnapshot, snapshot);
            return snapshot;
          });
          sw.Stop();

          snapshot.ElapsedMs = sw.Elapsed.TotalMilliseconds;

          /*
           * Cross-log memory (identity-priors.txt): fold what THIS capture's rules read off lines into the server's
           * ledger, so a later log that says nothing about a name can still show what was concluded before. Display
           * input only — ClassificationReport borrows from it where this log reached no verdict, and nothing in the
           * derive above reads it, which is deliberate: R7 decides sides from what the timeline already knows, so
           * yesterday's conclusion arriving as evidence would let the rules argue with their own memory.
           */
          if (classified is not null)
          {
            // Only an expensive pass leaves verdicts behind, and only then does the full-pass clock restart: the cheap lane must
            // not push the expensive one further away, or a busy tail would never re-classify.
            _carriedTimeline = classified;
            _lastFullPassSeconds = sw.Elapsed.TotalSeconds;
            _sinceFullPass.Restart();

            /*
             * A pass that outlives its session does not get to write the ledger: closing this log and opening another runs
             * IdentityPriorStore.Init(serverB) while this task is still in flight, and Record would then file THIS capture's
             * conclusions under the NEXT server's name - where they sit for 90 days. Re-opening this file re-records them
             * (the ledger is idempotent by design), so skipping costs nothing.
             */
            if (!_disposed)
            {
              /*
               * The ledger is memory for the NEXT log. A locked or half-written file here must not cost this session
               * the snapshot that was already computed - a failed write is a logged write failure, never "derive
               * failed", and it does not touch the boards on screen.
               */
              try
              {
                IdentityPriorStore.Instance.Record(classified, _facts.InternedNames, (long)_capture.LastEventTime);
              }
              catch (Exception ex)
              {
                Log.Error("Could not record identity priors during derive; the derived boards are unaffected", ex);
              }
            }
          }

          /*
           * The row arrays' doubling slack is handed back here, and on this lane only: a pass that classified is what runs when
           * the capture stopped growing in bulk, which is exactly when the load's last Array.Resize leaves up to half its capacity
           * written to nothing (measured 47 MB of a loaded session's 397 MB). CombatCapture.CompactRows owns the gate and the
           * once-per-doubling rule that keeps a live tail from copying the table per fact. A resize that cannot allocate leaves
           * the arrays as they were, so this can cost a session its memory and never its boards - hence its own catch instead of
           * the pass's retry ladder.
           */
          if (classified is not null)
          {
            try { _capture.CompactRows(); }
            catch (Exception ex) { Log.Debug("Could not trim row-array slack; the derived boards are unaffected", ex); }

            /*
             * The index's per-row ordinal lists carry the same doubling slack the fact arrays did (measured 4.7 MiB on a 2.3M-fact
             * capture), and they die with the snapshot, so their trim belongs to this session too. Dead rows only, reference-swapped
             * under the index gate - see CompactOrdinalSlack for why that shape is the safe one.
             */
            try { _projection.Index.CompactOrdinalSlack(); }
            catch (Exception ex) { Log.Debug("Could not trim ordinal-list slack; the derived boards are unaffected", ex); }
          }

          // Verify receipts only while the meter is enabled. Disabled captures retain unchecked metadata for later,
          // but do not decode facts to answer an opening question nobody is asking. Deriving never restamps receipt.
          var freshDamage = false;
          var activityOrdinal = 0;
          if (ConfigUtil.IfSet("IsDamageOverlayEnabled"))
          {
            // Asked with the displayed panels, because this announcement IS the auto-open path: an unwatched lane must not
            // open a window. Receipts still record both lanes, so widening the choice later needs no rescan.
            var panels = DamageMeterConfigState.LoadPanels();
            var mode = ConfigUtil.GetSettingAsInteger("OverlayDamageMode");
            var arrival = _meterArrivals.Latest(_facts, snapshot.Timeline, mode, panels);
            activityOrdinal = arrival.Through;
            freshDamage = activityOrdinal > _lastAnnouncedActivityOrdinal
                          && DamageOverlayLifecycle.HasRecentActivity(arrival, MeterArrivals.Now, mode);
          }

          // Swapped before the event: a selection made from the fresh rows materializes against the pass
          // that made them, never against the previous snapshot's facts.
          _snapshot = snapshot;

          /*
           * Both watermarks read what this pass COVERED, taken inside the ingest gate when the snapshot was assembled - not
           * a fresh CapturedTotal from out here, where lines may have landed while the task waited its turn. Acknowledging
           * facts nobody projected or classified is how a capture ends permanently under-derived with nothing ever asking again.
           */
          _lastProjectedCount = snapshot.FactCount;
          if (classified is not null) _lastClassifiedCount = snapshot.FactCount;

          // Any completed pass - either lane, even one that logged degraded stages - pays off the failure ladder.
          _deriveFailures = 0;
          _quietFailureRetries = 0;
          _failureParkLogged = false;
          // "continued" is the interesting half of the cost story: a continuing pass walked only what arrived since the
          // last one, while a rebuild re-walked the night (a new identity verdict anywhere earns one).
          Note($"Derive done: {snapshot.FightCount} fights, {sw.ElapsedMilliseconds} ms " +
               $"({(_projection.LastPassContinued ? "continued" : "rebuilt")})");

          if (!_firstDeriveLogged)
          {
            _firstDeriveLogged = true;
            /*
             * The counters of the first pass go to Debug ("did the derive run at all, and over how much"); they are the same
             * bookkeeping as the per-pass lines above and a session that is working needs neither in its own file. What stays at
             * Warn level by severity is the packing-guard count below.
             */
            var guards = CombatCapture.FactGuardCount;
            Log.Debug($"derive: first pass - {snapshot.FactCount:N0} facts, {snapshot.Rows.Count} rows");
            /*
             * The non-zero case is its own Warn rather than a clause on the Debug line above: a clamped clock or a heal mask that
             * did not fit means the boards are built from clipped values, and that must reach a normal log - it reads as a layout
             * bug or a wrong number long before anyone thinks to run with Debug.
             */
            if (guards != 0)
              Log.Warn($"derive: {guards} fact value(s) did not fit their field (clock clamp or heal mask/label) - see FactTime/HealFact");
          }

          /*
           * A disposed session announces nothing. Dispose stops the timer but does not join a pass already in flight, and a
           * dying capture's rows arriving after the next log opened repaints the panel with the raid that was just cleared (or
           * with nothing, when the new open is a follow-from-end that has no rows yet). The stamp on the snapshot is the second
           * line of the same defence for a listener that would rather ask than trust the ordering.
           */
          if (!_disposed) Derived?.Invoke(snapshot);

          /*
           * After Derived, so a meter already on screen has repainted before one that is not decides to open itself; and in
           * its own try because an auto-open handler must never make the pass look like it failed (the catch below would
           * disable auto-derive over somebody else's exception).
           */
          if (freshDamage && !_disposed)
          {
            // Recorded whether or not anyone listened: the announcement's meaning is "newer than the last one we made", and a
            // meter that was open (and ignored this) must not make the NEXT pass look like news all over again.
            _lastAnnouncedActivityOrdinal = activityOrdinal;
            try { LiveDamageObserved?.Invoke(); }
            catch (Exception ex) { Log.Error("Live-damage subscriber failed", ex); }
          }

          succeeded = true;
        }
        catch (Exception ex) when (!_disposed)
        {
          /*
           * Backoff, not a kill switch. A pass is allowed to fail - a file locked mid-write, a race in somebody's new
           * rule - and the meter must not go silent for the rest of the night over one hiccup, which is what the old
           * "stop the loop" latch did: it left a Re-derive button as the only recovery, and no legitimate flow needed
           * that button (its whole job had become this corner). Every attempt logs - a repeating stack in
           * eqlogparser.log IS the diagnosis (there is deliberately no failure event on this class: nothing
           * in the app listened to the one that was here, and the fight pane carries no failure state at all) - QuietTick
           * spaces the attempts on DeriveCadence.RetryDelayS (1 s doubling to a minute), and any success ends it. Note the
           * finer grain above: a single classification STAGE failing never reaches here at all (ClassificationRules.RunStage
           * retires just that stage).
           */
          var failures = ++_deriveFailures;
          Log.Error($"Derive pass failed (attempt {failures}); " +
                    $"next retry in {DeriveCadence.RetryDelayS(failures):0} s", ex);
          _sinceFailedPass.Restart();
        }
        catch (Exception)
        {
          // Log closed mid-derive — the snapshot has no reader anymore.
        }
        finally
        {
          // Counted as an interval even when the pass threw: the wait belongs to the cost of a pass, and a failing
          // derive that restarted its own stopwatch would be re-attempted at the floor cadence forever.
          _sinceAnyPass.Restart();
          Interlocked.Exchange(ref _deriveInFlight, 0);

          /*
           * Release first, then look at the debt: the flag must be down before a follow-up request can take it, or the cascade
           * below would be the pass that never starts. An expensive pass settles exactly the requests that existed when it
           * began (DeriveRequests), so an override applied while it was working is what keeps one owed - and the operator's
           * correction is served one pass later instead of never.
           *
           * Nothing happens on a failed pass. The debt stays visible to the next pass that finishes, and the retry ladder owns
           * the pacing after a throw - an immediate cascade over poison would be a loop with a log in it.
           */
          /*
           * A real debt - a gesture that landed while this pass was working - still gets its one hop, and `StartPass` rather than
           * `RederiveAsync` because paying a debt must not create one: arriving here would re-stamp the ledger every cascade and
           * leave the next pass looking owed for the same reason this one ran.
           */
          if (succeeded)
          {
            if (kind == DeriveKind.Full) _requests.ServedThrough(requestBound);
            if (_requests.IsOwed) StartPass(DeriveKind.Full);
          }
        }
      });
    }

    public void Dispose()
    {
      if (_disposed) return;
      _disposed = true;
      _quietTimer.Stop();
      _capture.Stop();

      // Carried rows hold their fights; dropping the carry is part of letting a closed capture go.
      _rowCarry = null;

      // The fact table is the biggest thing the engine holds; a disposed session must not stay the reason
      // a closed log's records are still reachable.
      _snapshot = null;
      Volatile.Write(ref _meterSnapshot, null);
      _carriedTimeline = null;

      /*
       * The names whose claim was taken back belong to THIS capture (2026-11): the list exists so a click keeps its row on the Identity
       * Table, and last night's click has no business pinning a name onto tonight's log. Deliberately NOT inside the still-mine guard
       * below — a stale entry would sit on the new capture until some later session happened to be disposed, while the race this does
       * accept (a take-back clicked in the microseconds between an open and its first census) has no rows on screen to click yet.
       */
      TakenBackNames.Clear();

      // Only take the seam down if it is still OURS: a new session can already be wired by the time an old engine is
      // disposed, and blanking that one would send every identity question back to memory behind its owner's back.
      if (ReferenceEquals(IdentityLookup.LiveVerdict, _liveKindAt)) IdentityLookup.LiveVerdict = null;
      if (ReferenceEquals(IdentityLookup.LiveOwner, _liveOwner)) IdentityLookup.LiveOwner = null;
      if (ReferenceEquals(HealRecordSource.Current, _heals)) HealRecordSource.Current = null;
      if (ReferenceEquals(Active, this))
      {
        Active = null;
        ActiveChanged?.Invoke();
      }
    }

    /*
     * Turn a derived selection into stats input (damage side). Called from a worker task by whoever is
     * feeding the summary, which is where the allocation belongs: one DamageRecord per selected fact,
     * the same cost the legacy pipeline already pays for its own selection — only here it is paid per
     * click rather than once at parse time, and it is paid again on no row that was not asked for.
     *
     * Returns an empty input when no snapshot has landed: an empty selection is the caller's business,
     * a missing one means "the list you clicked is gone".
     */
    internal static SummaryInput BuildSummaryInput(DerivedSnapshot snapshot, IReadOnlyList<DerivedFight> selected)
    {
      using var damageRead = snapshot?.Facts?.RetainDecoded();
      using var healRead = snapshot?.Heals?.RetainDecoded();
      if (snapshot is null || selected is not { Count: > 0 })
      {
        // An empty selection clears every board, so healing arrives as an empty list rather than null: null
        // would mean "say nothing about healing" and the board would keep last click's numbers.
        return new SummaryInput([], new TimeRange()) { Heals = [] };
      }

      // The grid hides a charmed mob's own rows (CharmPetRows: that is a pet, and pets have no fight row), so a
      // selection can never contain them. Put back the ones whose span overlaps what was clicked — otherwise the
      // hiding would take the charmer's +Pets damage, and the raid's own stray swings on their pet, out of every
      // board built from a selection.
      var input = FightSummarySource.Build(CharmPetRows.WithHiddenPets(selected, snapshot.AllFights),
        snapshot.DamageIndex, snapshot.Facts);

      /*
       * The healing half of the same click. A heal belongs to no fight (it opens no encounter), so this is not
       * "the selected rows' heals" but "every heal inside the selection's own time window" — which is precisely
       * how the legacy board works, since HealingStatsBuilder windows the whole heal store by AllRanges and never
       * asks which fight anything was in. Same window object, so both boards answer to one clock.
       *
       * Cost is parity, not overhead: a legacy summary request allocates the same list out of RecordsStore on
       * every click (GetAllHeals().ToList()), and this replaces it rather than adding to it.
       */
      return input with { Heals = HealSummarySource.Materialize(snapshot.Heals, input.AllRanges) };
    }

    /*
     * Numbers for a SCOPE: the rows a surface is showing right now, which for the damage overlay means "the fights
     * this session has touched". Same hidden-pet rule as a click (a charmed mob's own row carries damage the charmer
     * gets credit for, and the grid hides that row), same one calculation — so the overlay's running total and
     * "select these fights, press summary" are the same call on the same rows and cannot disagree.
     *
     * Null means there is no scope: no snapshot yet (the derive has not landed, so the rows a caller holds came from a
     * list that no longer exists), or the session has touched nothing. What to display for either is the surface's call.
     */
    public DerivedSnapshot Snapshot => _snapshot;

    /*
     * The fights this window answers for, as legacy-shaped rows: the door MainWindow.GetFights feeds to the
     * spell, taunt, death and export paths while the engine is attached. Materialization is the same pass a board
     * click pays - records rebuilt from the facts - but nothing here is stored twice (the snapshot keeps the
     * derived rows; this only hands out the legacy shape the older consumers read). `selected` is the grid's own
     * selection; null means every visible row in list order, exactly what the display list shows.
     */
    internal static List<Fight> MaterializeFights(DerivedSnapshot snapshot, IReadOnlyList<DerivedFight>? selected)
    {
      if (snapshot?.Facts is null) return [];
      using var damageRead = snapshot.Facts.RetainDecoded();
      if (selected == null)
      {
        var visible = new List<DerivedFight>();
        foreach (var row in snapshot.Rows)
          if (row.Fight is not null) visible.Add(row.Fight);
        selected = visible;
      }

      return FightSummarySource.Build(selected, snapshot.DamageIndex, snapshot.Facts).Fights.ToList();
    }

    /*
     * The scoped variant DeathLogViewer wants: on every death click only the rows whose activity windows touch
     * [fromT, toT] are materialized. Materializing a whole night's snapshot per click would rebuild every record
     * of the capture - a full board's cost paid per keystroke. A row with an empty (NaN) window fails both
     * comparisons, which is exactly the row its viewer-side predicate used to drop anyway.
     */
    internal List<Fight> MaterializeFightsOverlapping(double fromT, double toT)
    {
      var snapshot = _snapshot;
      if (snapshot?.Facts is null) return [];
      using var damageRead = snapshot.Facts.RetainDecoded();
      var overlapping = new List<DerivedFight>();
      foreach (var row in snapshot.Rows)
      {
        if (row.Fight is not { } fight) continue;
        if ((fight.BeginDamageTime <= toT && fight.LastDamageTime >= fromT) ||
            (fight.BeginTankingTime <= toT && fight.LastTankingTime >= fromT))
          overlapping.Add(fight);
      }

      return FightSummarySource.Build(overlapping, snapshot.DamageIndex, snapshot.Facts).Fights.ToList();
    }

    // The panels this window displays: activity on a lane it does not show answers None, which is what closes or keeps shut a
    // meter whose only busy lane is the hidden one.
    internal bool HasOverlayActivity(int mode, MeterPanels panels)
    {
      var snapshot = _snapshot;
      return !_disposed && snapshot is not null && DamageOverlayLifecycle.HasRecentActivity(
        _meterArrivals.Observed(panels, mode), MeterArrivals.Now, mode);
    }

    // Select and materialize from ONE snapshot. The lifecycle retains only scalar reset state, never this capture's rows.
    internal MeterBuildResult BuildOverlayStats(DamageOverlayLifecycle lifecycle, DamageMeterCalculation calculation,
      int mode, MeterPanels panels, double now, CancellationToken cancellation, Action<double> publishExpiry = null)
    {
      return MeterBuildRetry.Run(() =>
      {
        if (_disposed) return MeterBuildResult.Completed();
        var snapshot = Volatile.Read(ref _meterSnapshot);
        if (snapshot is null) return MeterBuildResult.Completed();
        // The potentially large suffix scan is outside the ingest gate. Retry if a pass replaced its snapshot meanwhile.
        snapshot.PendingActivity.Update(snapshot.Facts, snapshot.Timeline, cancellation);
        var candidates = _capture.DeriveQuiescent(() =>
        {
          cancellation.ThrowIfCancellationRequested();
          if (_disposed || !ReferenceEquals(snapshot, Volatile.Read(ref _meterSnapshot))) return null;
          var scope = lifecycle.SelectCandidates(SessionId, snapshot.AllFights, mode);
          return snapshot.DamageIndex.CaptureMeterInput(scope.Rows, snapshot.Facts, scope.FromTime,
            double.PositiveInfinity, scope.FirstOrdinal);
        });
        if (candidates is null) return MeterBuildResult.Deferred;
        calculation.Activity.Update(candidates, cancellation);
        var receipts = _meterArrivals.BeginRead(snapshot.Timeline);
        // The arrival a build selects on is the displayed panels' answer, so an unwatched lane can neither extend this window's
        // quiet deadline nor rescue it from expiring.
        var arrival = receipts.Complete(snapshot.Facts, mode, panels, cancellation);
        var input = _capture.DeriveQuiescent(() =>
        {
          cancellation.ThrowIfCancellationRequested();
          if (_disposed || !ReferenceEquals(snapshot, Volatile.Read(ref _meterSnapshot))) return null;
          // A newer eligible receipt must not renew old amounts before pending membership has seen its combat.
          if (!receipts.InputsCurrent || !calculation.Activity.InputsCurrent
            || !snapshot.PendingActivity.Covers(arrival.Through, snapshot.Timeline)) return null;
          // An expiry may cut only the receipt prefix we checked; ingest may have appended fresh damage during the scan.
          if (!lifecycle.TrySelect(SessionId, snapshot.AllFights, now, mode, arrival, receipts.Through, out var scope,
            snapshot.PendingActivity, calculation.Activity.For, panels)) return null;
          // Publish before counting: a blocked build cannot hold its prior presentation past this deadline.
          publishExpiry?.Invoke(scope.ExpiresAt);
          return snapshot.DamageIndex.CaptureMeterInput(scope.Rows, snapshot.Facts, scope.FromTime,
            double.PositiveInfinity, scope.FirstOrdinal, scope.WindowSecond);
        });
        if (input is null) return MeterBuildResult.Deferred;
        var stats = calculation.Update(input, cancellation);
        return receipts.InputsCurrent ? MeterBuildResult.Completed(stats) : MeterBuildResult.Deferred;
      }, cancellation);
    }

    internal StatsGenerationEvent? BuildScopeStats(IReadOnlyList<DerivedFight> rows)
      => BuildScopeStats(rows, double.NegativeInfinity, double.PositiveInfinity);

    /*
     * The same scope through a time window: what the damage meter asks when it was zeroed partway through the fights
     * it is still showing. The overlay owns its zero point and its expiry rule (legacy's `OverlayDamageMode`: 0 = on
     * kill, otherwise N seconds of quiet before the board zeroes) and hands the resulting seconds here, so the numbers
     * are the same arithmetic the fight list uses — sliced, not re-tallied. Healing follows automatically, since the
     * heal window is this scope's AllRanges.
     */
    internal StatsGenerationEvent? BuildScopeStats(IReadOnlyList<DerivedFight> rows, double fromT, double toT)
    {
      var snapshot = _snapshot;
      if (snapshot is null || rows is not { Count: > 0 })
      {
        return null;
      }

      return DerivedTotals.For(CharmPetRows.WithHiddenPets(rows, snapshot.AllFights), snapshot.DamageIndex,
        snapshot.Facts, snapshot.Heals, fromT, toT);
    }

    /*
     * Every fact stream counts, heals included. Two reasons the heals belong in the quiescence signal rather
     * than beside it: a healing-only stretch (a raid regrouping while everyone recovers) would otherwise read
     * as quiet and fire a derive over facts that are still arriving; and "capturing… N", which is the one
     * number a user can compare against the log, would be quietly short by a third of the raid's output.
     *
     * A log without combat damage (login/city logs) still derives — an empty fight list is an honest answer,
     * "capturing forever" is not.
     */
    private void MeterDamageArrived(int through, double sourceTime) =>
      _meterArrivals.Record(through, MeterArrivals.Now, sourceTime, DateUtil.ToDotNetSeconds(DateTime.Now));

    private long CapturedTotal =>
      _facts.FactCount + _heals.HealCount + _facts.DeathCount + _facts.TauntCount
      + _facts.IdentityEventCount + _facts.EvidenceCount;

    /*
     * The trigger, decided by DeriveCadence (where the rule and its measurements live). This method only feeds it the counters
     * and the two clocks, then dispatches whichever lane came back — a cheap fold or an expensive pass. Quiescence is still the fast path — a load that stops moving gets its pass on
     * the next tick — but it is no longer the ONLY path: waiting for two silent ticks meant that a live raid tail,
     * which never offers two silent ticks, held one snapshot for the whole encounter, and every surface reading it (the
     * fight list, a click's summary, the damage meter) showed the same frozen numbers until the file stopped growing.
     *
     * Derivation itself parks ingest at the gate, so a completed pass always leaves count equal to the watermark of whichever
     * lane ran - and only that lane's, which is why there are two of them.
     */
    private void QuietTick(object sender, EventArgs e)
    {
      if (_disposed) return;

      var count = CapturedTotal;

      // Growth over the window since the last ask, as a rate: the same ingest reads the same however often it is polled.
      var windowS = _sinceTick.Elapsed.TotalSeconds;
      var grown = count - Math.Max(0, _lastTickCount);
      _lastTickCount = count;
      var factsPerSecond = windowS > 0 ? grown / windowS : 0d;
      _sinceTick.Restart();

      if (grown > 0)
      {
        _sinceFactChange.Restart();

        // New facts are a reason to try again, so a parked retry is not parked for a live capture - and a failure that has
        // been quiet long enough to park comes back the moment the file does.
        if (_quietFailureRetries != 0) _quietFailureRetries = 0;
      }

      // Dirty-while-idle feedback doubles as field diagnostics: "capturing… 0 captured" separates
      // an empty log from a stalled pipeline without needing a debugger.
      // "capturing" is about what the player can see move, which lags on the CHEAP lane's number: the rules being behind
      // that same count is normal between full passes and must not make a live raid look stalled.
      if (count != _lastProjectedCount) Capturing?.Invoke(count);

      /*
       * A pass that threw comes back on the ladder - 1 s, doubling to a minute - instead of never: the old latch froze
       * every derived surface for the rest of the night over one hiccup. The decision lives in DeriveCadence (one place
       * answers backoff AND "is this tick allowed to try"), and it carries the two things that make a retry wrong on its
       * own: inside the current rung nothing is due, and while a file is still being read a retry would park ingest at the
       * gate on top of the load - the load's own quiet tick settles the debt when the file stops.
       *
       * The reason it fires here rather than through Decide is the idle capture: an override arrives while a pass is
       * running, that pass throws, and a closed log never grows again - quiescence has already happened and no live tail
       * ever will, so Decide answers None forever and the operator's click degrades to "logged, not acted on". A failure
       * is always present when this debt sits (the pass that could have settled it was the one that threw).
       */
      if (_deriveFailures > 0)
      {
        var decision = DeriveCadence.DecideFailureRetry(_deriveFailures, _sinceFailedPass.Elapsed.TotalSeconds,
                                                       factsPerSecond, _quietFailureRetries);
        if (decision == FailureRetry.Park)
        {
          if (!_failureParkLogged)
          {
            _failureParkLogged = true;
            Log.Info($"Derive retries parked after {_quietFailureRetries} quiet attempts " +
                     "(the failure is repeating and nothing is arriving); the next new fact, a re-derive or a new log tries again");
          }
          return;
        }

        if (decision == FailureRetry.Retry)
        {
          if (factsPerSecond <= 0d) _quietFailureRetries++;
          // Not a gesture: the debt this retry pays is already on the books (a failed pass settles nothing), so it asks for
          // a pass rather than another entry in the ledger.
          StartPass(DeriveKind.Full);
          return;
        }
      }

      var kind = DeriveCadence.Decide(count, _lastProjectedCount, _lastClassifiedCount,
                                            _sinceFactChange.Elapsed.TotalSeconds,
                                            factsPerSecond, _sinceAnyPass.Elapsed.TotalSeconds,
                                            _sinceFullPass.Elapsed.TotalSeconds, _lastFullPassSeconds);
      // The pump asks for a PASS, never for a debt (see RederiveAsync for what that distinction was worth in ms per pass).
      if (kind != DeriveKind.None) StartPass(kind);
    }

    /*
     * The expensive half of a pass: rebuild the verdicts from nothing. Fresh timeline each time, because the rules replay over the
     * facts from scratch — that is what lets manual overrides and mid-log registry changes re-apply cleanly (idempotent by design).
     *
     * Runs inside the ingest gate, and its measured cost (186 ms on Kizant, 261 ms on Incogitable) is what paces it: rules that
     * re-run without learning anything are the entire price of a refresh, which is why the cheap lane exists at all.
     */
    private EntityTimeline Classify()
    {
      var timeline = new EntityTimeline();
      RegistrySeed.Apply(timeline, _facts, _capture.FirstEventTime, _capture.LastEventTime);

      // The heal stream goes in too: R15 (our side keeps healing this name) is the only rule that can
      // see a mercenary or custom-named pet that never speaks, never joins and owns nothing; R18 reads the
      // same stream for an NPC-verdict name the whole raid keeps topping up.
      // The carried classification serves this fallback too: same capture, same aggregates (see _classification).
      var ruleOutcome = ClassificationRules.Apply(_facts, timeline, _heals, _classification);

      /*
       * A stage that threw or retired is a bug in ONE check, and the pass continues on every verdict the other stages
       * reached (ClassificationRules.RunStage) - which is exactly why it must not be silent: degraded-but-live reads
       * as healthy otherwise. Full exception text reaches the player's log once per attempt (five times, then one
       * final retirement line).
       */
      foreach (var failure in ruleOutcome.FailedRules)
        Log.Error($"Classification stage failed; its verdicts are missing from this pass: {failure}");
      foreach (var retired in ruleOutcome.RetiredRules)
        Log.Error($"Classification stage retired after repeated failures: {retired}");

      /*
       * R10 last, and it has to be last: this timeline is built from nothing on every pass (see above), so
       * what the operator saved has to be replayed into each one or an override would vanish at the very
       * re-derive it asked for. Manual strength means nothing above can outvote it, order included.
       */
      IdentityOverrideStore.Instance.Apply(timeline);

      /*
       * Publish by replacement, and publish FROZEN: from the next line this instance is reachable by the UI thread (menu enables),
       * the census, the boards and the meter's suffix scans, none of which take a read lock. Cheap passes run no rule book at all, so
       * nothing would ever write it — freezing makes that an executable check instead of a habit, so a future in-place fold fails here
       * with a stack instead of racing a reader (docs/DesignNotes.md → "A published timeline is frozen").
       */
      timeline.Freeze();
      return timeline;
    }

  }

  // Chat → capture, as one half of the composite fan-out. Top-level (was nested) because it is a collaborator of
  // the pipeline rather than a detail of this class: LogProcessor composes it beside the app's own chat sinks.
  internal sealed class IdentityChatSink(CombatCapture capture) : IChatSink
  {
    public void Init()
    {
    }

    public void Add(ChatType chat) => capture.HandleChat(chat);
  }

  // Forwards to several chat sinks in order — the seam that lets ChatDB archiving and the engine
  // share the single IChatSink slot LogProcessor takes, without Core knowing about either.
  internal sealed class CompositeChatSink(params IChatSink[] sinks) : IChatSink
  {
    public void Init()
    {
      foreach (var sink in sinks) sink.Init();
    }

    public void Add(ChatType chat)
    {
      foreach (var sink in sinks) sink.Add(chat);
    }
  }
}
