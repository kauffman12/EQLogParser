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
  // so a snapshot never races mid-append fact mutation and no tail line is lost. The first derive
  // fires automatically once the initial bulk load goes quiet (fact count stable across two ticks);
  // the mirror window's Re-derive button repeats it on demand.
  internal sealed class MirrorSession : IDisposable
  {
    public static MirrorSession Active { get; private set; }

    public static event Action ActiveChanged;

    // Raised on the derivation thread; subscribers marshal to the dispatcher themselves.
    public event Action<MirrorSnapshot> Derived;

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
    private readonly DispatcherTimer _quietTimer;
    private int _deriveInFlight;
    private long _lastTickCount = -1;
    private long _lastDerivedCount = -1;
    private volatile bool _autoDeriveDisabled;
    private bool _disposed;

    public MirrorSession()
    {
      _heals = new HealFactTable(_facts);
      _mirror = new CombatMirror(_facts, _heals);
      ChatSink = new MirrorChatSink(_mirror);
      _quietTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
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

            // The index is filled DURING the projection: it needs the same direction decision the rows split
            // DamageToOwner by, and that decision exists only inside FightProjection (FactOwnershipHandler). It
            // also gets the timeline, so a charmed mob's damage is credited to its charmer in the stats board
            // (MirrorDamageIndex.OwnerOf) instead of sitting under the mob's own name.
            var damageIndex = new MirrorDamageIndex(timeline);

            // Display list = facts projected over the classification above. Rows self-correct:
            // a name that gains player-side evidence between passes loses its row to the NPC it
            // was actually fighting (FightDeriver's legacy-keyed list is retired from display and
            // lives on only in the parity tests/bench).
            var fights = FightProjection.Build(_facts, timeline, damageIndex.OnFact);

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

          // Swapped before the event: a selection made from the fresh rows materializes against the pass
          // that made them, never against the previous snapshot's facts.
          _snapshot = snapshot;
          _lastDerivedCount = CapturedTotal;
          Log.Info($"Combat mirror derive done: {snapshot.FightCount} fights, {sw.ElapsedMilliseconds} ms");
          Derived?.Invoke(snapshot);
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

    // Quiescence proxy without touching LogReader internals: derive once the captured-fact count
    // has been stable for two ticks AND differs from the last derived count. Not a one-shot EOF
    // latch — a damage-free stretch during bulk load (raid gap in a recorded log, GC pause) may
    // fire an early pass, but any later growth re-arms the trigger, so the visible snapshot
    // converges to end-of-file and tailing refreshes whenever the log goes quiet. Derivation
    // itself parks ingest at the gate, so a completed pass always leaves count == lastDerivedCount.
    private void QuietTick(object sender, EventArgs e)
    {
      if (_disposed || _autoDeriveDisabled) return;

      var count = CapturedTotal;

      // Dirty-while-idle feedback doubles as field diagnostics: "capturing… 0 captured" separates
      // an empty log from a stalled pipeline without needing a debugger.
      if (count != _lastDerivedCount) Capturing?.Invoke(count);

      if (count == 0) return;

      if (count == _lastTickCount && count != _lastDerivedCount) RederiveAsync();
      else _lastTickCount = count;
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
