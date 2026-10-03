using log4net;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

using EQLogParser;

/*
 * Annotations only, no null-flow analysis: the project builds with Nullable=disable, and this API speaks in optional
 * strings/kinds because a name legitimately has no class, no owner and no verdict. Stating that is not the same as
 * switching on warnings across code written before nullable existed.
 */
#nullable enable annotations

namespace EQLogParser
{
  // Owns the combat mirror for one log-open: taps the pipeline statics, receives the chat fan-out,
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

    public static event Action ActiveChanged;

    // Raised on the derivation thread; subscribers marshal to the dispatcher themselves.
    public event Action<DerivedSnapshot> Derived;

    /*
     * "Damage came in", for the one surface that opens itself: the meter. Legacy raised this on **every damage line** of a
     * fight (`FightManager.UpdateIfNewFightMap` fires it whenever `fight.DamageHits > 0`, outside the new-fight branch), which
     * is why closing a meter with the X during a pull brought it back within a line or two. A derived list has no per-line
     * moment — rows appear on a pass — so this says "newer row activity than the last announcement, and something still live"
     * (LiveFights.HasFreshDamage), at derive rate rather than line rate. The reader no-ops while a window exists, so the extra
     * passes cost one null check; what they buy is that the reopen happens on the damage the player can see rather than at the
     * next pull.
     *
     * STATIC, unlike Derived, because its reader outlives a capture: MainWindow keeps one subscription for the whole run
     * and the meter must auto-open for the next log too, not just the one open when it subscribed. Unsubscribed on window
     * close, exactly like ActiveChanged.
     */
    public static event Action LiveDamageObserved;

    // Newest row activity announced so far, so the announcement means "newer than last time" and not "still fighting".
    private double _lastAnnouncedActivityT = double.NegativeInfinity;

    public event Action<string> DeriveFailed;

    // Liveness feedback while capture is dirty (raised on the timer, i.e. the dispatcher):
    // total captured facts since no snapshot covers them yet.
    public event Action<long> Capturing;

    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    private readonly DamageFactTable _facts = new(100_000);

    /*
     * Healing in its own table, sharing the damage table's interned names — see HealFact for why the streams
     * are separate. Captured from here so the mirror's heal population is exactly the population the healing
     * board reads (RecordsStore gets the same records, off the same event).
     */
    private readonly HealFactTable _heals;
    private readonly CombatCapture _capture;

    // The snapshot currently on screen (see DerivedSnapshot.Facts): a selection is materialized against
    // the pass that produced the rows, so it has to be reachable from the UI without re-deriving.
    private DerivedSnapshot _snapshot;

    /*
     * The projection carried from pass to pass, together with the index its facts went into. Whether a pass may continue it
     * is decided inside (FightProjectionCache) on two gates: the watermarks still name the same facts, and the
     * classification stamp matches the one the carried rows were projected under.
     */
    private readonly FightProjection.FightProjectionCache _projection = new();

    private readonly DispatcherTimer _quietTimer;
    private int _deriveInFlight;
    private long _lastTickCount = -1;
    private long _lastDerivedCount = -1;

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
    private EntityTimeline? _carriedTimeline;

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

    // The first pass of a session logs itself once, whatever lane the cadence picked: "the list never filled"
    // is otherwise invisible in a log that carries a million fact lines - either MainWindow's session-started
    // line or this one is missing, and that says which half of the chain died.
    private bool _firstDeriveLogged;
    private bool _disposed;

    public DeriveEngine()
    {
      // A fresh capture gets the full rule book even if this app run already saw a stage retire over other data:
      // retirement protects one session's pass loop, it is not a verdict about every file the process will open.
      ClassificationRules.ResetRuleHealth();

      _heals = new HealFactTable(_facts);
      _capture = new CombatCapture(_facts, _heals);
      ChatSink = new IdentityChatSink(_capture);
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
      ActiveChanged?.Invoke();
      _quietTimer.Start();
    }

    /*
     * The name census for the Names window. Built on demand rather than carried in the snapshot: a derive lands every
     * few seconds while a log loads and a census nobody has open would be thrown away each time. The timeline is
     * assembled exactly as a derive assembles it - roster seed, rules, operator overrides last - so what the window
     * shows is what the boards were classified with, including the roster (docs/combat-mirror-design.md).
     *
     * Facts can arrive while this walks them. That is acceptable here and nowhere else: a census is display data, and
     * a name whose damage shifts by one fact between two passes costs nobody a decision, whereas a lock on the capture
     * path would be paid for by every event. A table that refuses the walk outright leaves the previous list on screen.
     */
    public ClassificationReport? BuildNameCensus()
    {
      if (_disposed) return null;

      try
      {
        var timeline = new EntityTimeline();
        RegistrySeed.Apply(timeline, _facts, _capture.FirstEventTime, _capture.LastEventTime);
        ClassificationRules.Apply(_facts, timeline, _heals, _classification);
        IdentityOverrideStore.Instance.Apply(timeline);

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

    public void RederiveAsync(DeriveKind kind)
    {
      if (_disposed || Interlocked.Exchange(ref _deriveInFlight, 1) == 1) return;

      /*
       * What reaches the player's own log. An expensive pass is an event worth a line; the cheap lane runs twice a second, and four
       * lines of bookkeeping per second would push the raid out of the file this app writes (and out of the tail anyone reading it).
       * Failures still log at their own level whatever the lane.
       */
      void Note(string message)
      {
        if (kind == DeriveKind.Full) Log.Info(message); else Log.Debug(message);
      }

      Note($"Derive starting over {CapturedTotal:N0} captured facts");

      _ = Task.Run(() =>
      {
        try
        {
          var sw = Stopwatch.StartNew();

          // The classification this pass reached, kept out here so the sighting ledger can read it AFTER the derive
          // instead of inside it: Record() writes a file, and nothing that writes belongs between the rules and the
          // snapshot the grid is waiting on.
          EntityTimeline? classified = null;
          var snapshot = _capture.DeriveQuiescent(() =>
          {
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

            var snapshot = DerivedFightRows.Build(fights, timeline, CapturedTotal, _facts, damageIndex);
            snapshot.Heals = _heals;
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
                IdentityPriorStore.Instance.Record(classified, _facts.InternedNames, PlayerRegistry.Instance,
                                                   (long)_capture.LastEventTime);
              }
              catch (Exception ex)
              {
                Log.Error("Could not record identity priors during derive; the derived boards are unaffected", ex);
              }
            }
          }

          /*
           * Did damage arrive since the last announcement, with something still live? Asked on the capture's own clock (its
           * newest event) rather than wall time: during a bulk load of an old log the middle of the file must not count as
           * activity worth opening a meter for, while its tail — which does — is exactly where a reader who reopened yesterday's
           * log wants one. Gated on the setting for the same lazy reason legacy gated its per-line event.
           */
          var freshDamage = ConfigUtil.IfSet("IsDamageOverlayEnabled")
                            && LiveFights.HasFreshDamage(snapshot.AllFights, _lastAnnouncedActivityT, _capture.LastEventTime,
                                                         LiveFights.GapS);

          // Swapped before the event: a selection made from the fresh rows materializes against the pass
          // that made them, never against the previous snapshot's facts.
          _snapshot = snapshot;
          _lastDerivedCount = CapturedTotal;

          // Any completed pass - either lane, even one that logged degraded stages - pays off the failure ladder.
          _deriveFailures = 0;
          // "continued" is the interesting half of the cost story: a continuing pass walked only what arrived since the
          // last one, while a rebuild re-walked the night (a new identity verdict anywhere earns one).
          Note($"Derive done: {snapshot.FightCount} fights, {sw.ElapsedMilliseconds} ms " +
               $"({(_projection.LastPassContinued ? "continued" : "rebuilt")})");

          if (!_firstDeriveLogged)
          {
            _firstDeriveLogged = true;
            Log.Info($"derive: first pass - {snapshot.FactCount:N0} facts, {snapshot.Rows.Count} rows");
          }

          Derived?.Invoke(snapshot);

          /*
           * After Derived, so a meter already on screen has repainted before one that is not decides to open itself; and in
           * its own try because an auto-open handler must never make the pass look like it failed (the catch below would
           * disable auto-derive over somebody else's exception).
           */
          if (freshDamage)
          {
            // Recorded whether or not anyone listened: the announcement's meaning is "newer than the last one we made", and a
            // meter that was open (and ignored this) must not make the NEXT pass look like news all over again.
            _lastAnnouncedActivityT = LiveFights.LatestActivityAt(snapshot.AllFights);
            try { LiveDamageObserved?.Invoke(); }
            catch (Exception ex) { Log.Error("Live-damage subscriber failed", ex); }
          }
        }
        catch (Exception ex) when (!_disposed)
        {
          /*
           * Backoff, not a kill switch. A pass is allowed to fail - a file locked mid-write, a race in somebody's new
           * rule - and the meter must not go silent for the rest of the night over one hiccup, which is what the old
           * "stop the loop" latch did: it left a Re-derive button as the only recovery, and no legitimate flow needed
           * that button (its whole job had become this corner). Every attempt logs - a repeating stack in
           * eqlogparser.log IS the diagnosis - QuietTick spaces the attempts on DeriveCadence.RetryDelayS (1 s
           * doubling to a minute), and any success ends it. Note the finer grain above: a single classification STAGE
           * failing never reaches here at all (ClassificationRules.RunStage retires just that stage).
           */
          var failures = ++_deriveFailures;
          Log.Error($"Derive pass failed (attempt {failures}); " +
                    $"next retry in {DeriveCadence.RetryDelayS(failures):0} s", ex);
          DeriveFailed?.Invoke(ex.Message);
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
        }
      });
    }

    public void Dispose()
    {
      if (_disposed) return;
      _disposed = true;
      _quietTimer.Stop();
      _capture.Stop();

      // The fact table is the biggest thing the mirror holds; a disposed session must not stay the reason
      // a closed log's records are still reachable.
      _snapshot = null;
      _carriedTimeline = null;
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
    internal SummaryInput BuildSummaryInput(IReadOnlyList<DerivedFight> selected)
    {
      var snapshot = _snapshot;
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
     * spell, taunt, death and export paths while the mirror is attached. Materialization is the same pass a board
     * click pays - records rebuilt from the facts - but nothing here is stored twice (the snapshot keeps the
     * derived rows; this only hands out the legacy shape the older consumers read). `selected` is the grid's own
     * selection; null means every visible row in list order, exactly what the display list shows.
     */
    internal List<Fight> MaterializeFights(IReadOnlyList<DerivedFight>? selected)
    {
      if (selected == null)
      {
        var visible = new List<DerivedFight>();
        foreach (var row in _snapshot?.Rows ?? [])
          if (row.Fight is not null) visible.Add(row.Fight);
        selected = visible;
      }

      return FightSummarySource.Build(selected, _projection.Index, _facts).Fights.ToList();
    }

    /*
     * The scoped variant DeathLogViewer wants: on every death click only the rows whose activity windows touch
     * [fromT, toT] are materialized. Materializing a whole night's snapshot per click would rebuild every record
     * of the capture - a full board's cost paid per keystroke. A row with an empty (NaN) window fails both
     * comparisons, which is exactly the row its viewer-side predicate used to drop anyway.
     */
    internal List<Fight> MaterializeFightsOverlapping(double fromT, double toT)
    {
      var overlapping = new List<DerivedFight>();
      foreach (var row in _snapshot?.Rows ?? [])
      {
        if (row.Fight is not { } fight) continue;
        if ((fight.BeginDamageTime <= toT && fight.LastDamageTime >= fromT) ||
            (fight.BeginTankingTime <= toT && fight.LastTankingTime >= fromT))
          overlapping.Add(fight);
      }

      return FightSummarySource.Build(overlapping, _projection.Index, _facts).Fights.ToList();
    }

    /*
     * The damage meter's own question: every row this capture holds that was still alive when the window opened,
     * measured through the window. No hidden-pet re-adding is needed because the scope is already every row (a pet's
     * row is in it), which is also why a mirror-fed meter and a mirror-list select-all can be compared at all.
     *
     * Null means the mirror has nothing to say yet (no derive landed, or nothing fought since the window opened);
     * what to paint for that is the overlay's call, exactly as it is today.
     */
    /*
     * "Is there a fight going on?" — the question FightManager's overlay-fight set answered for the meter, asked here of
     * derived rows (LiveFights) on the capture's own newest event. `gapS` comes from the caller because it is the meter's
     * dial: the same number that zeroes its board, so a window cannot be kept open by one rule and blanked by another.
     *
     * True for as long as the capture keeps moving, which on an old log loaded and left alone means its last moments stay
     * "live" — the clock is not advancing, and the legacy overlay behaved the same way for a different reason (its fights
     * only expired while a board was being built). Nothing here decides what a hidden window should do about that.
     */
    public bool HasLiveFight(double gapS)
    {
      var snapshot = _snapshot;
      return snapshot is not null && LiveFights.AnyLive(snapshot.AllFights, _capture.LastEventTime, gapS);
    }

    internal DamageOverlayStats BuildOverlayStats(double fromT, double toT, out double lastFactT)
    {
      lastFactT = double.NaN;
      var snapshot = _snapshot;
      if (snapshot is null)
      {
        return null;
      }

      var rows = new List<DerivedFight>();
      foreach (var row in snapshot.AllFights)
      {
        // LastTime is the last second this name did anything, so a row that finished before the reset drops out and a
        // running one stays (its seconds before the reset are cut by the window, not by the row).
        if (row.LastTime >= fromT)
        {
          rows.Add(row);
          if (double.IsNaN(lastFactT) || row.LastTime > lastFactT) lastFactT = row.LastTime;
        }
      }

      return DerivedTotals.ForOverlay(rows, snapshot.DamageIndex, snapshot.Facts, snapshot.Heals, fromT, toT);
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
     * Derivation itself parks ingest at the gate, so a completed pass always leaves count == lastDerivedCount.
     */
    private void QuietTick(object sender, EventArgs e)
    {
      if (_disposed) return;

      /*
       * A pass that threw comes back on the ladder - 1 s, doubling to a minute - instead of never: the old latch froze
       * every derived surface for the rest of the night over one hiccup. The gate sits ahead of the clock reads below
       * so a failing derive costs one attempt per rung rather than a pump-rate storm on the derive gate.
       */
      if (_deriveFailures > 0 && _sinceFailedPass.Elapsed.TotalSeconds < DeriveCadence.RetryDelayS(_deriveFailures))
        return;

      var count = CapturedTotal;

      // Growth over the window since the last ask, as a rate: the same ingest reads the same however often it is polled.
      var windowS = _sinceTick.Elapsed.TotalSeconds;
      var grown = count - Math.Max(0, _lastTickCount);
      _lastTickCount = count;
      var factsPerSecond = windowS > 0 ? grown / windowS : 0d;
      _sinceTick.Restart();

      if (grown > 0) _sinceFactChange.Restart();

      // Dirty-while-idle feedback doubles as field diagnostics: "capturing… 0 captured" separates
      // an empty log from a stalled pipeline without needing a debugger.
      if (count != _lastDerivedCount) Capturing?.Invoke(count);

      var kind = DeriveCadence.Decide(count, _lastDerivedCount, _sinceFactChange.Elapsed.TotalSeconds,
                                            factsPerSecond, _sinceAnyPass.Elapsed.TotalSeconds,
                                            _sinceFullPass.Elapsed.TotalSeconds, _lastFullPassSeconds);
      if (kind != DeriveKind.None) RederiveAsync(kind);
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

      return timeline;
    }

    private sealed class IdentityChatSink : IChatSink
    {
      private readonly CombatCapture _capture;

      public IdentityChatSink(CombatCapture mirror) => _capture = mirror;

      public void Init()
      {
      }

      public void Add(ChatType chat) => _capture.HandleChat(chat);
    }
  }

  // Forwards to several chat sinks in order — the seam that lets ChatDB archiving and the mirror
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
