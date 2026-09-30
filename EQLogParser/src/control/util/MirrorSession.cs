using log4net;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

using EQLogParser.Mirror;

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
  // Derivation is quiescent: CombatMirror.DeriveQuiescent parks ingest at its gate for the pass,
  // so a snapshot never races mid-append fact mutation and no tail line is lost. A load that stops moving gets its
  // pass on the next tick, and a live tail that never stops moving gets one on MirrorDeriveCadence's clock — waiting
  // for silence alone meant a raid-length freeze on every surface reading the snapshot. The Re-derive button stays for
  // the moment somebody wants an answer right now.
  internal sealed class MirrorSession : IDisposable
  {
    public static MirrorSession Active { get; private set; }

    public static event Action ActiveChanged;

    // Raised on the derivation thread; subscribers marshal to the dispatcher themselves.
    public event Action<MirrorSnapshot> Derived;

    /*
     * "A fight just started", for the one surface that opens itself: the damage meter used to learn this from
     * FightManager, which created a fight object at the moment a first hit landed. A derived list has no such instant — a
     * row appears on a pass — so this says which row the last pass opened that is still going (LiveFights), on the derive
     * thread.
     *
     * STATIC, unlike Derived, because its reader outlives a capture: MainWindow keeps one subscription for the whole run
     * and the meter must auto-open for the next log too, not just the one open when it subscribed. Unsubscribed on window
     * close, exactly like ActiveChanged.
     */
    public static event Action<DerivedFight> NewFightObserved;

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
    private readonly CombatMirror _mirror;

    // The snapshot currently on screen (see MirrorSnapshot.Facts): a selection is materialized against
    // the pass that produced the rows, so it has to be reachable from the UI without re-deriving.
    private MirrorSnapshot _snapshot;

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
     * How often the cadence is asked. It is a POLLS rate, not the refresh rate — MirrorDeriveCadence takes seconds and
     * facts-per-second precisely so this number can be changed without moving any threshold — and it is fine-grained so
     * a pass lands on its moment instead of on the next whole second: the end-of-load pass up to a full interval
     * earlier, and a live tail's floor read within a quarter of it. A dispatcher timer at Background priority, so this
     * costs a wakeup behind layout when nothing else is queued and never competes with rendering.
     */
    private const int TimerIntervalMs = 250;

    /*
     * The measurements the cadence needs, all monotonic intervals rather than DateTime moments (a clock adjustment must
     * not buy a free re-derive or hold one off): how long the last pass took — which is what the capture costs, so it
     * sets how often another one is affordable — how long ago it finished, how long this count has held still, and how
     * wide the observation window behind the growth RATE was.
     */
    private readonly Stopwatch _sinceLastPass = Stopwatch.StartNew();
    private readonly Stopwatch _sinceFactChange = Stopwatch.StartNew();
    private readonly Stopwatch _sinceTick = Stopwatch.StartNew();
    private double _lastPassSeconds;
    private volatile bool _autoDeriveDisabled;
    private bool _disposed;

    public MirrorSession()
    {
      _heals = new HealFactTable(_facts);
      _mirror = new CombatMirror(_facts, _heals);
      ChatSink = new MirrorChatSink(_mirror);
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
      _mirror.Start();
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
        RegistrySeed.Apply(timeline, _facts, _mirror.FirstEventTime, _mirror.LastEventTime);
        ClassificationRules.Apply(_facts, timeline, _heals);
        MirrorOverrideStore.Instance.Apply(timeline);

        return ClassificationReport.Build(timeline, _facts, _heals, MirrorOverrideStore.Instance,
                                         PlayerRegistry.Instance, IdentityPriorStore.Instance);
      }
      catch (Exception ex)
      {
        // A stale census beats no census, but not a silent one.
        Log.Error("Name census skipped; keeping the previous list", ex);
        return null;
      }
    }

    public void RederiveAsync()
    {
      if (_disposed || Interlocked.Exchange(ref _deriveInFlight, 1) == 1) return;

      Log.Info($"Combat mirror derive starting over {CapturedTotal:N0} captured facts");

      _ = Task.Run(() =>
      {
        try
        {
          var sw = Stopwatch.StartNew();

          // The classification this pass reached, kept out here so the sighting ledger can read it AFTER the derive
          // instead of inside it: Record() writes a file, and nothing that writes belongs between the rules and the
          // snapshot the grid is waiting on.
          EntityTimeline? classified = null;
          var snapshot = _mirror.DeriveQuiescent(() =>
          {
            // Fresh timeline each pass: rules replay over the facts from scratch, so manual
            // overrides and mid-log registry changes re-apply cleanly (idempotent by design).
            var timeline = new EntityTimeline();
            RegistrySeed.Apply(timeline, _facts, _mirror.FirstEventTime, _mirror.LastEventTime);
            // The heal stream goes in too: R15 (our side keeps healing this name) is the only rule that can
            // see a mercenary or custom-named pet that never speaks, never joins and owns nothing; R18 reads the
            // same stream for an NPC-verdict name the whole raid keeps topping up.
            ClassificationRules.Apply(_facts, timeline, _heals);

            /*
             * R10 last, and it has to be last: this timeline is built from nothing on every pass (see above), so
             * what the operator saved has to be replayed into each one or an override would vanish at the very
             * re-derive it asked for. Manual strength means nothing above can outvote it, order included.
             */
            MirrorOverrideStore.Instance.Apply(timeline);
            classified = timeline;

            /*
             * Rows and damage index come from ONE walk: the index needs the same direction decision the rows split
             * DamageToOwner by, and that decision exists only inside FightProjection (FactOwnershipHandler) — which is why
             * the cache below owns both and hands them back together rather than an index built afterwards.
             *
             * The pass continues where the previous one stopped whenever it may (FightProjectionCache: the watermarks still
             * name the same facts, and the classification stamp matches the one the carried rows were projected under). A
             * charmed mob's damage is credited to its charmer on the stats board through the same timeline (MirrorDamageIndex).
             */
            var fights = _projection.Project(_facts, timeline);
            var damageIndex = _projection.Index;

            // "Fight N" numbers for a stats run reading GroupId, from the same walk that draws the
            // dividers in this grid.
            Sectionizer.StampGroupIds(fights);

            var snapshot = MirrorFightRows.Build(fights, timeline, CapturedTotal, _facts, damageIndex);
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
          IdentityPriorStore.Instance.Record(classified, _facts.InternedNames, PlayerRegistry.Instance,
                                             (long)_mirror.LastEventTime);

          /*
           * Whether this pass opened a fight that was not live on the last one. Asked BEFORE the swap because the previous
           * snapshot is what "new" means, and answered on the capture's own clock (its newest event) rather than wall time:
           * during a bulk load of an old log the middle of the file must not count as a fight starting, and the tail of it —
           * which does — is exactly where a reader who reopened yesterday's log wants the meter to appear.
           */
          var openedFight = ConfigUtil.IfSet("IsDamageOverlayEnabled")
                              ? LiveFights.FindNewLive(_snapshot?.AllFights, snapshot.AllFights, _mirror.LastEventTime,
                                                       LiveFights.GapS)
                              : null;

          // Swapped before the event: a selection made from the fresh rows materializes against the pass
          // that made them, never against the previous snapshot's facts.
          _snapshot = snapshot;
          _lastPassSeconds = sw.Elapsed.TotalSeconds;
          _lastDerivedCount = CapturedTotal;
          // "continued" is the interesting half of the cost story: a continuing pass walked only what arrived since the
          // last one, while a rebuild re-walked the night (a new identity verdict anywhere earns one).
          Log.Info($"Combat mirror derive done: {snapshot.FightCount} fights, {sw.ElapsedMilliseconds} ms " +
                   $"({(_projection.LastPassContinued ? "continued" : "rebuilt")})");
          Derived?.Invoke(snapshot);

          /*
           * After Derived, so a meter already on screen has repainted before one that is not decides to open itself; and in
           * its own try because an auto-open handler must never make the pass look like it failed (the catch below would
           * disable auto-derive over somebody else's exception).
           */
          if (openedFight is not null)
          {
            try { NewFightObserved?.Invoke(openedFight); }
            catch (Exception ex) { Log.Error("Mirror new-fight subscriber failed", ex); }
          }
        }
        catch (Exception ex) when (!_disposed)
        {
          // A stale list must never fail silently: it is the one surface where a broken
          // derivation would otherwise be indistinguishable from an idle mirror. Auto-retry at
          // timer rate would spam the log — stop the loop, leave Re-derive available.
          _autoDeriveDisabled = true;
          Log.Error("Combat mirror derivation failed; auto-derive disabled", ex);
          DeriveFailed?.Invoke(ex.Message);
        }
        catch (Exception)
        {
          // Log closed mid-derive — the snapshot has no reader anymore.
        }
        finally
        {
          // Counted as an interval even when the pass threw: the wait belongs to the cost of a pass, and a failing
          // derive that restarted its own stopwatch would be re-attempted at the floor cadence forever.
          _sinceLastPass.Restart();
          Interlocked.Exchange(ref _deriveInFlight, 0);
        }
      });
    }

    public void Dispose()
    {
      if (_disposed) return;
      _disposed = true;
      _quietTimer.Stop();
      _mirror.Stop();

      // The fact table is the biggest thing the mirror holds; a disposed session must not stay the reason
      // a closed log's records are still reachable.
      _snapshot = null;
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
    internal MirrorSummaryInput BuildSummaryInput(IReadOnlyList<DerivedFight> selected)
    {
      var snapshot = _snapshot;
      if (snapshot is null || selected is not { Count: > 0 })
      {
        // An empty selection clears every board, so healing arrives as an empty list rather than null: null
        // would mean "say nothing about healing" and the board would keep last click's numbers.
        return new MirrorSummaryInput([], new TimeRange()) { Heals = [] };
      }

      // The grid hides a charmed mob's own rows (CharmPetRows: that is a pet, and pets have no fight row), so a
      // selection can never contain them. Put back the ones whose span overlaps what was clicked — otherwise the
      // hiding would take the charmer's +Pets damage, and the raid's own stray swings on their pet, out of every
      // board built from a selection.
      var input = MirrorSummaryFights.Build(CharmPetRows.WithHiddenPets(selected, snapshot.AllFights),
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
      return input with { Heals = MirrorSummaryHeals.Materialize(snapshot.Heals, input.AllRanges) };
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
    public MirrorSnapshot Snapshot => _snapshot;

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
      return snapshot is not null && LiveFights.AnyLive(snapshot.AllFights, _mirror.LastEventTime, gapS);
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

      return MirrorStats.ForOverlay(rows, snapshot.DamageIndex, snapshot.Facts, snapshot.Heals, fromT, toT);
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

      return MirrorStats.For(CharmPetRows.WithHiddenPets(rows, snapshot.AllFights), snapshot.DamageIndex,
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
     * The trigger, decided by MirrorDeriveCadence (where the rule and its measurements live). This method only feeds it
     * the three counters and the clock. Quiescence is still the fast path — a load that stops moving gets its pass on
     * the next tick — but it is no longer the ONLY path: waiting for two silent ticks meant that a live raid tail,
     * which never offers two silent ticks, held one snapshot for the whole encounter, and every surface reading it (the
     * fight list, a click's summary, the damage meter) showed the same frozen numbers until somebody pressed Re-derive.
     *
     * Derivation itself parks ingest at the gate, so a completed pass always leaves count == lastDerivedCount.
     */
    private void QuietTick(object sender, EventArgs e)
    {
      if (_disposed || _autoDeriveDisabled) return;

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

      if (MirrorDeriveCadence.ShouldDerive(count, _lastDerivedCount, _sinceFactChange.Elapsed.TotalSeconds,
            factsPerSecond, _sinceLastPass.Elapsed.TotalSeconds, _lastPassSeconds))
      {
        RederiveAsync();
      }
    }

    private sealed class MirrorChatSink : IChatSink
    {
      private readonly CombatMirror _mirror;

      public MirrorChatSink(CombatMirror mirror) => _mirror = mirror;

      public void Init()
      {
      }

      public void Add(ChatType chat) => _mirror.HandleChat(chat);
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
