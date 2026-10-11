using log4net;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace EQLogParser
{
  internal class DamageStatsBuilder
  {
    internal static DamageStatsBuilder Instance = new();
    internal event Action<DataPointEvent> EventsUpdateDataPoint;
    internal event Action<StatsGenerationEvent> EventsGenerationStatus;

    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private readonly object _lock = new();
    private readonly Dictionary<int, byte> _damageGroupIds = [];
    private readonly ConcurrentDictionary<string, int> _playerGroupAssignments = new();
    private readonly ConcurrentDictionary<string, TimeRange> _playerTimeRanges = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TimeRange>> _playerSubTimeRanges = new();

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _playerPets = new();
    private readonly ConcurrentDictionary<string, string> _petToPlayer = new();

    /*
     * "X +Pets" built once per NAME per build instead of once per RECORD. The walk folded every pet's output onto its owner by
     * concatenating the row name for each swing — a fresh string millions of times a night (measured: 74 bytes allocated per
     * record in the walk stage, most of it this suffix and the DD/DoT sub-stat key). The value is exactly what the expression
     * produced, so no board number can move; only the number of strings built does. Cleared with the build like the maps beside it.
     */
    private readonly Dictionary<string, string> _petRowNames = [];

    private List<List<ActionGroup>> _allDamageGroups;
    private List<List<ActionGroup>> _damageGroups = [];

    /*
     * The blocks the counting walk reads, in build order. A full build sets this to the sorted blocks materialization handed
     * over BEFORE the grouping phase welded same-second runs together (see the walk in ComputeDamageStats for why the pre-join
     * objects are the ones that matter); anything else - a pane's own dial re-slicing groups it already holds - flattens what is
     * in hand instead, which costs the walk its resume points and is exactly why carried cells refuse a re-slice.
     */
    private List<ActionGroup> _walkBlocks = [];
    private List<ActionGroup> _walkBlocksSource = [];
    private bool _walkBlocksAreSource;

    // This build's block -> actions counted, only when a carried cell set was opened. Null on every cold build.
    private Dictionary<ActionGroup, int> _cellCursor;
    private PlayerStats _raidTotals;
    private List<Fight> _selected;
    private StatsGenerationEvent _lastStatsEvent;
    private string _title;

    /*
     * Only the process-wide board listens for "the log closed", and it signs up exactly once - here, not in the
     * instance constructor.
     *
     * The instance constructor used to subscribe, which was fine while this class had exactly one instance. It no
     * longer has: DerivedTotals builds a throwaway scope per calculation (the damage meter asks about once a second),
     * and a constructor subscription on a short-lived object is a permanent one - the static event holds the delegate,
     * the delegate holds the builder, and the builder holds its groups, per-player dictionaries and generated stats.
     * Measured: ten overlay calculations left twenty subscribers behind through a full GC, about 118 KB of retained
     * heap each, growing for as long as the meter stayed open (and making every later clear walk thousands of
     * lock-taking handlers). See DerivedTotalsTest.ACalculationLeavesNothingWatchingTheClearSignal.
     *
     * A scoped builder has nothing to be cleared: nobody keeps its result, and the surface painting it blanks itself
     * when the session ends. The singleton's handler is kept verbatim - reset the boards, and drop group assignments
     * only when the server of the opened log changed.
     */
    static DamageStatsBuilder()
    {
      Instance.ClearedByActiveData();
    }

    private void ClearedByActiveData()
    {
      CombatEvents.ActiveDataCleared += (bool serverChanged) =>
      {
        lock (_lock)
        {
          Reset();

          if (serverChanged)
          {
            _playerGroupAssignments.Clear();
          }
        }
      };
    }

    internal StatsGenerationEvent GetLastStats()
    {
      lock (_lock)
      {
        return _lastStatsEvent;
      }
    }

    internal void SetPlayerAssignedGroup(string playerName, int assignedGroup)
    {
      if (string.IsNullOrEmpty(playerName))
        return;

      if (assignedGroup >= 0 && assignedGroup <= 12)
      {
        _playerGroupAssignments[playerName] = assignedGroup;
      }
      else
      {
        _playerGroupAssignments.TryRemove(playerName, out _);
      }
    }

    /*
     * Traced wrapper: every build of this board gets a line naming its door and its cost (see StatsBuildTrace). The body below is
     * unchanged apart from the name — re-slicing the groups already in hand, which is what the panes' own dials ask for.
     */
    internal void RebuildTotalStats(GenerateStatsOptions options, bool reset = false)
    {
      var trace = StatsBuildTrace.Begin("damage", options.Source, "re-slice", options.BuildGroup);
      try
      {
        RebuildTotalStatsCore(options, reset, trace);
      }
      finally
      {
        StatsBuildTrace.End(trace, $"window {options.MinSeconds}..{options.MaxSeconds} reset={reset}");
      }
    }

    private void RebuildTotalStatsCore(GenerateStatsOptions options, bool reset, in StatsBuildTrace.Handle trace)
    {
      ChartDataGeneration = trace.Seq;
      var built = false;

      if (EventsGenerationStatus?.GetInvocationList().Length > 0)
      {
        lock (_lock)
        {
          if (reset)
          {
            _damageGroups = _allDamageGroups ?? [];
          }

          if (_damageGroups.Count > 0)
          {
            FireNewStatsEvent();
            // A re-slice reads groups that are already in hand, so it neither opens nor stores cells (see ComputeDamageStats).
            ComputeDamageStats(options, trace, new CellTrace());
            built = true;
          }
        }
      }

      // Outside the lock: the tidy waits before collecting, and it must not be holding the builder while it does.
      if (built)
      {
        GcTidyUp.Request("stats built");
      }
    }

    // Traced wrapper (see StatsBuildTrace): the door label comes in on the options, the cost and the record count go out on the line.
    internal void BuildTotalStats(GenerateStatsOptions options)
    {
      var trace = StatsBuildTrace.Begin("damage", options.Source, "full", options.BuildGroup);
      var cellTrace = new CellTrace();
      try
      {
        BuildTotalStatsCore(options, trace, cellTrace);
      }
      finally
      {
        StatsBuildTrace.End(trace, $"npcs={options.Npcs.Count} window {options.MinSeconds}..{options.MaxSeconds} {cellTrace.Text}");
      }
    }

    private void BuildTotalStatsCore(GenerateStatsOptions options, in StatsBuildTrace.Handle trace, CellTrace cellTrace)
    {
      ChartDataGeneration = trace.Seq;
      lock (_lock)
      {
        try
        {
          FireNewStatsEvent();
          Reset();

          _lastStatsEvent = null;
          _selected = [.. options.Npcs];
          _selected.Sort(static (a, b) => a.Id.CompareTo(b.Id));

          /*
           * The title comes from the SORTED selection, never from `options.Npcs[0]`. The sort above is the legacy law - a
           * selection's fights are counted in time order, and row ids are handed out in the fight list's own display order
           * (`published[i].Id = i + 1`), so the lowest id IS the first row of the list. Reading element zero of the caller's
           * raw input instead made the title a property of however the grid happened to enumerate its selection: a whole-capture
           * select-all then printed a different NPC every time (field report, `EQLogParser.log`: three select-alls over identical
           * rows, three titles), while legacy printed the top row 100% of the time because its selection arrived in list order.
           */
          _title = _selected.Count > 0 ? _selected[0].Name : null;

          var damageBlocks = new List<ActionGroup>();

          foreach (var fight in CollectionsMarshal.AsSpan(_selected))
          {
            damageBlocks.AddRange(fight.DamageBlocks);

            if (fight.GroupId > -1)
            {
              _damageGroupIds[fight.GroupId] = 1;
            }

            _raidTotals.Ranges.Add(new TimeSegment(fight.BeginDamageTime, fight.LastDamageTime));
            StatsUtil.UpdateRaidTimeRanges(fight.DamageSegments, fight.DamageSubSegments, _playerTimeRanges, _playerSubTimeRanges);
          }

          // Once for the whole selection rather than once per fight: merging is idempotent, so adding the same spans
          // 4,452 times proved nothing and each proof cost a search. This is the raid row's "In-Raid %" denominator.
          //
          //
          // The null check is load-bearing and a test found it: an empty selection carries no AllRanges at all, and this line
          // used to sit inside the per-fight loop, where it never ran for nobody. Nothing would throw louder than a board that
          // simply reports nothing — the catch below swallows what happens here.
          if (options.AllRanges is not null)
          {
            _raidTotals.AllRanges.Add(options.AllRanges.TimeSegments);
          }

          damageBlocks.Sort((a, b) => a.BeginTime.CompareTo(b.BeginTime));

          // The walk's list is the materialization objects themselves, taken before the joins below mint replacements —
          // and kept in two places: `_walkBlocksSource` is never filtered, because a slice must be cut from the whole
          // pool every time rather than from last slice's remainder.
          _walkBlocks = damageBlocks;
          _walkBlocksSource = damageBlocks;
          _walkBlocksAreSource = true;

          if (damageBlocks.Count != 0)
          {
            _raidTotals.TotalSeconds = _raidTotals.MaxTime = _raidTotals.Ranges.GetTotal();

            var rangeIndex = 0;
            var newBlock = new List<ActionGroup>();

            /*
             * Grouping by REFERENCE. The blocks handed here are `Fight.DamageBlocks` — already ActionGroups, already one per
             * second of that fight — and copying every one of them (a fresh list of up to a few hundred record references per
             * second of the raid) measured MORE expensive than counting them all: groups 1196 ms against walk 822 ms on the
             * Incogitable capture. Nothing in the app mutates a finished group's Actions (the healing builder builds its own;
             * the timeline chart and the validators only read), so a block that stands alone in its second joins the group as
             * the object it already is, and a block is still built only where two fights really did share a second.
             *
             * That also closes a drop the copying hid: after a group boundary flushed `newBlock`, a block whose second equalled
             * the previous one went to `newBlock.LastOrDefault()` on an EMPTY list — null, and its records vanished from the
             * board while still updating pet mapping. Runs are peeled here instead, so every block lands somewhere.
             */
            for (var i = 0; i < damageBlocks.Count;)
            {
              var blockTime = damageBlocks[i].BeginTime;

              if (_raidTotals.Ranges.TimeSegments.Count > rangeIndex && blockTime > _raidTotals.Ranges.TimeSegments[rangeIndex].EndTime)
              {
                rangeIndex++;
                if (newBlock.Count > 0)
                {
                  _damageGroups.Add(newBlock);
                }

                newBlock = [];
              }

              if (i + 1 < damageBlocks.Count && damageBlocks[i + 1].BeginTime == blockTime)
              {
                // Parallel fights: one entry per second is what the chart's buckets and the sub-stat keys assume, so this run
                // becomes a single joined block. The only allocation left in this phase.
                var joined = new ActionGroup { BeginTime = blockTime };
                while (i < damageBlocks.Count && damageBlocks[i].BeginTime == blockTime)
                {
                  var run = damageBlocks[i++];
                  joined.Actions.AddRange(run.Actions);

                  foreach (var action in run.Actions)
                  {
                    if (action is DamageRecord record)
                    {
                      UpdatePetMapping(record);
                    }
                  }
                }

                newBlock.Add(joined);
              }
              else
              {
                var single = damageBlocks[i++];
                newBlock.Add(single);

                foreach (var action in single.Actions)
                {
                  if (action is DamageRecord record)
                  {
                    UpdatePetMapping(record);
                  }
                }
              }
            }

            _damageGroups.Add(newBlock);

            // The regrouping above copied every block of the selection; the walk below counts them. Named apart because
            // a measure/present restructure spends these two very differently (docs/DesignNotes.md -> "The damage board's golden").
            StatsBuildTrace.Stage(trace, "groups");
            ComputeDamageStats(options, trace, cellTrace);
          }
          else if (_selected == null || _selected.Count == 0)
          {
            FireNoDataEvent(options, "NONPC");
          }
          else
          {
            FireNoDataEvent(options, "NODATA");
          }
        }
        catch (Exception ex)
        {
          Log.Error(ex);
          if (StatsBuildTrace.FailFast)
            throw;   // tests: a swallowed builder throw is an empty board, not a failed run
        }
      }
    }

    /*
     * B13: the content generation this builder's groups currently hold -- the trace sequence of the build that wrote them (-1 until a
     * build has run). Stamped on every chart event so a chart can tell "new data to aggregate" from "the same question again"; see
     * DataPointEvent.DataGeneration. A build ALWAYS restamps (that is what makes reuse safe), and this builder's groups are written only
     * inside builds -- the one exception clears them and resets this to -1 with them.
     */
    internal long ChartDataGeneration { get; private set; } = -1;

    internal void FireChartEvent(string action, List<PlayerStats> selected = null, List<GroupEntry> selectedGroups = null)
    {
      lock (_lock)
      {
        // send update
        var de = new DataPointEvent { Action = action, DataGeneration = ChartDataGeneration, Iterator = new DamageGroupCollection(_damageGroups) };

        if (selected is not null)
        {
          de.Selected.AddRange(selected);
        }

        if (selectedGroups is not null)
        {
          de.SelectedGroups.AddRange(selectedGroups);
        }

        EventsUpdateDataPoint?.Invoke(de);
      }
    }

    /*
     * What this build decided about carried cells, printed on the trace line next to the phase costs. A board that got fast
     * and a board that quietly never tried look identical from the grid, which is the whole reason the words exist.
     */
    private sealed class CellTrace
    {
      internal string Text = "no proof";
    }

    /*
     * Open the carried cell set for this build, or say why not. Every refusal is a full recount - the safe answer, one the
     * board was already paying before this file existed - and each one names itself so `grep "stats build"` says which.
     *
     * The proof beyond `DamageCellStore.TryOpen`'s four: every block the set counted must still be in THIS selection.
     * That is checked by building this build's cursor from its own block list and comparing entry counts - the blocks in a
     * selection are unique objects, so more counted blocks than present blocks can only mean a row left the board. Cheap
     * (one probe per block, no hashing) and it is what makes "the user deselected a fight" impossible to answer with a
     * stale night's damage instead of a smaller one.
     */
    private DamageCellSet TryOpenCells(GenerateStatsOptions options, bool sliced, CellTrace cellTrace, out Dictionary<ActionGroup, int> cursor)
    {
      cursor = null;

      if (options.CellProof is not { } proof)
      {
        return Refuse(cellTrace, "no proof");
      }

      if (sliced)
      {
        return Refuse(cellTrace, "windowed build");
      }

      if (!_walkBlocksAreSource)
      {
        return Refuse(cellTrace, "re-sliced blocks");
      }

      var held = DamageCellStore.TryOpen(proof, ValidatorFingerprint(), _petToPlayer.Count, out var refuse);
      if (held is null)
      {
        return Refuse(cellTrace, refuse);
      }

      /*
       * Every block the set counted must still be in this selection, in the same order and with the same records in front.
       * Extra blocks are welcome (they are new and get counted whole), which is why this counts MATCHES rather than comparing
       * sizes: selecting a wider set of rows keeps working; dropping one does not, and a set that dropped a row is precisely
       * the case where carrying the old cells would report damage nobody selected.
       *
       * One counted block per row is allowed to have changed identity, because materialization says it may: a grown row's
       * last block is CLONED before its second is extended (`FightSummarySource.ReopenTail`, so the Fight the grid already
       * holds keeps its own object), which means every live row hands back a fresh object for its newest second on every
       * pass. That clone carries the SAME record objects in front of the ones appended, so the resume is proven by reference
       * on the counted prefix rather than by the block surviving: same records, same order, more after them. Anything that
       * fails that test — a rebuilt row, a dropped row, a block that came back shorter — refuses and recounts.
       */
      var opened = new Dictionary<ActionGroup, int>(ReferenceEqualityComparer.Instance);
      foreach (var block in _walkBlocks) opened[block] = held.Cursor.TryGetValue(block, out var seen) ? seen : 0;

      // Blocks this selection owns that the set never saw, per row the tail of its list: the only place a clone can appear.
      List<ActionGroup> tails = null;
      foreach (var fight in options.Npcs)
      {
        var blocks = fight.DamageBlocks;
        if (blocks.Count == 0 || opened.ContainsKey(blocks[^1])) continue;

        tails ??= [];
        tails.Add(blocks[^1]);
      }

      var carried = 0;
      foreach (var pair in held.Cursor)
      {
        if (opened.ContainsKey(pair.Key)) continue;

        var match = FindReopenedTail(tails, pair.Key, pair.Value);
        if (match is null)
        {
          return Refuse(cellTrace, "selection lost counted blocks");
        }

        opened[match] = pair.Value;
        tails.Remove(match);
        carried++;
      }

      cursor = opened;
      DamageCellStore.RecordDecision(null);
      cellTrace.Text = $"reusing {held.Rows.Count:N0} rows, {carried} reopened tails";
      return held;
    }

    /// <summary>Record and return a refusal, so every "no" in TryOpenCells is readable after the build (DamageCellStore.LastDecision).</summary>
    private static DamageCellSet Refuse(CellTrace cellTrace, string reason)
    {
      DamageCellStore.RecordDecision(reason);
      cellTrace.Text = reason;
      return null;
    }

    /*
     * The one legitimate way a counted block can come back as a different object: materialization reopened that row's second,
     * cloning the block and appending the new records behind the ones already there. Proven by asking for the same BeginTime,
     * at least as many actions, and - for every action this set already counted - THE SAME RECORD OBJECTS. Cheap because a
     * block is one second of one row's damage, so this compares tens of pointers rather than a night of records; and a block
     * that came back with different records in front is not this block, which is the whole point of asking.
     */
    private static ActionGroup FindReopenedTail(List<ActionGroup> tails, ActionGroup counted, int actions)
    {
      if (tails is null || actions > 0 && counted.Actions.Count != actions) return null;

      ActionGroup found = null;
      foreach (var candidate in tails)
      {
        if (candidate.BeginTime != counted.BeginTime || candidate.Actions.Count < actions) continue;

        var same = true;
        for (var i = 0; i < actions; i++)
        {
          if (!ReferenceEquals(candidate.Actions[i], counted.Actions[i])) { same = false; break; }
        }

        if (!same) continue;

        // Two rows ending in the same second would be two plausible clones of one counted block: ambiguous, so refuse.
        if (found is not null) return null;
        found = candidate;
      }

      return found;
    }

    /// <summary>The six `DamageValidator` boxes, folded into one int. They filter INSIDE the walk, so a set built with
    /// assassinate damage on is simply wrong the moment that box is unchecked - and the only honest answer then is a recount.</summary>
    private static int ValidatorFingerprint()
    {
      var mask = 0;
      if (AppSettings.IsAssassinateDamageEnabled) mask |= 1;
      if (AppSettings.IsBaneDamageEnabled) mask |= 2;
      if (AppSettings.IsDamageShieldDamageEnabled) mask |= 4;
      if (AppSettings.IsFinishingBlowDamageEnabled) mask |= 8;
      if (AppSettings.IsHeadshotDamageEnabled) mask |= 16;
      if (AppSettings.IsSlayUndeadDamageEnabled) mask |= 32;
      return mask;
    }

    /*
     * Detach this build's counted rows as the next build's starting point. Taken between the walk and the present phase, so
     * what is stored is pure accumulation: totals, sub-stats, activity ranges, the open running second (StatsSnapshot.TakeCarry
     * keeps it open on purpose), plus the walk's alias maps and the raid line's running total.
     */
    private void StoreCells(in DamageBoardProof proof, Dictionary<string, PlayerStats> rows, Dictionary<string, PlayerStats> topLevel,
      Dictionary<string, Dictionary<string, PlayerStats>> children, Dictionary<string, double> frameTime, long recordsCounted)
    {
      var set = new DamageCellSet
      {
        SessionId = proof.SessionId,
        ClassificationStamp = proof.ClassificationStamp,
        ValidatorFingerprint = ValidatorFingerprint(),
        PetMapCount = _petToPlayer.Count,
        RaidTotal = _raidTotals.Total,
        RecordsCounted = recordsCounted,
      };

      foreach (var pair in frameTime) set.FrameTime[pair.Key] = pair.Value;
      foreach (var name in topLevel.Keys) set.TopLevel.Add(name);
      foreach (var pair in children) set.Children[pair.Key] = [.. pair.Value.Keys];

      if (_cellCursor is not null)
      {
        set.Cursor = _cellCursor;
      }
      else
      {
        var fresh = new Dictionary<ActionGroup, int>(ReferenceEqualityComparer.Instance);
        foreach (var block in _walkBlocks) fresh[block] = block.Actions.Count;
        set.Cursor = fresh;
      }

      foreach (var pair in rows) set.Rows[pair.Key] = StatsSnapshot.TakeCarry(pair.Value);

      DamageCellStore.Store(set);
    }

    private void ComputeDamageStats(GenerateStatsOptions options, in StatsBuildTrace.Handle trace, CellTrace cellTrace)
    {
      lock (_lock)
      {
        _lastStatsEvent = null;
        if (_raidTotals is not null)
        {
          var childrenStats = new Dictionary<string, Dictionary<string, PlayerStats>>();
          var topLevelStats = new Dictionary<string, PlayerStats>();
          var damageValidator = new DamageValidator(
            AppSettings.IsAssassinateDamageEnabled, AppSettings.IsBaneDamageEnabled, AppSettings.IsDamageShieldDamageEnabled,
            AppSettings.IsFinishingBlowDamageEnabled, AppSettings.IsHeadshotDamageEnabled, AppSettings.IsSlayUndeadDamageEnabled);
          var individualStats = new Dictionary<string, PlayerStats>();

          // always start over
          _raidTotals.Total = 0;
          _raidTotals.MaxBeginTime = double.NaN;
          _raidTotals.MinBeginTime = double.NaN;
          var startTime = double.NaN;
          var stopTime = double.NaN;
          var sliced = false;

          try
          {
            if ((options.MaxSeconds > -1 && options.MaxSeconds < _raidTotals.MaxTime && !options.MaxSeconds.Equals((long)_raidTotals.TotalSeconds)) ||
              (options.MinSeconds > 0 && options.MinSeconds < _raidTotals.MaxTime))
            {
              StatsUtil.UpdateMinMaxTimes(_raidTotals, options, out startTime, out stopTime);

              var filteredGroups = new List<List<ActionGroup>>();
              _allDamageGroups.ForEach(group =>
              {
                var filteredBlocks = new List<ActionGroup>();
                group.ForEach(block =>
                {
                  if ((double.IsNaN(startTime) || block.BeginTime >= startTime) && (double.IsNaN(stopTime) || block.BeginTime <= stopTime))
                  {
                    filteredBlocks.Add(block);
                  }
                });

                if (filteredBlocks.Count > 0)
                {
                  filteredGroups.Add(filteredBlocks);
                }
              });

              _damageGroups = filteredGroups;
              sliced = true;
              _raidTotals.TotalSeconds = options.MaxSeconds - options.MinSeconds;
              _raidTotals.MinTime = options.MinSeconds;
            }
            else
            {
              _damageGroups = _allDamageGroups;
              _raidTotals.MinTime = 0;
              _raidTotals.TotalSeconds = _raidTotals.MaxTime;
            }

            StatsBuildTrace.Stage(trace, "window");

            /*
             * The walk's list, cut AFTER the window decision so both read one answer:
             *   - a build that just grouped has the materialization objects (`_walkBlocksSource`) and slices from THEM,
             *     never from a previous slice's remainder — a retained pool is re-sliced whole each time, and a walk list
             *     that narrowed in place would quietly report a window inside a window (a real golden diff, found the day
             *     this was written: 9,430 became 7,730 on the second slice);
             *   - anything else (the pane's own dials re-slicing groups already in hand) flattens those groups, which are
             *     partly joined objects and therefore carry no resume points — see TryOpenCells refusing that build.
             */
            if (_walkBlocksAreSource)
            {
              if (sliced)
              {
                var kept = new List<ActionGroup>();
                foreach (var block in _walkBlocksSource)
                {
                  if ((double.IsNaN(startTime) || block.BeginTime >= startTime) && (double.IsNaN(stopTime) || block.BeginTime <= stopTime))
                  {
                    kept.Add(block);
                  }
                }

                _walkBlocks = kept;
              }
              else
              {
                _walkBlocks = _walkBlocksSource;
              }
            }
            else
            {
              _walkBlocks = [];
              foreach (var group in _damageGroups) _walkBlocks.AddRange(group);
            }

            var lastTime = double.NaN;
            var prevPlayerTimes = new Dictionary<string, double>();

            /*
             * Carried cells: what the LAST build of this capture already counted (see DamageCellStore for the whole argument
             * and the four things that invalidate a set). Opening one seeds this build's dictionaries with detached copies of
             * the counted rows, so the loop below runs unchanged over only the records that arrived since - identical
             * accumulation logic, started from where the last pass stopped, which is the same shape layer 1 gave materializing.
             *
             * A cold build that carries a proof stores its own cells at the end, so the second build of a capture is fast even
             * when the first one could not be.
             */
            var cells = TryOpenCells(options, sliced, cellTrace, out var cursor);
            if (cells is not null)
            {
              foreach (var pair in cells.Rows) individualStats[pair.Key] = StatsSnapshot.TakeCarry(pair.Value);

              // The walk's alias maps, by name: a top-level row IS the individual row, a child is the pet row under its owner.
              foreach (var name in cells.TopLevel)
              {
                if (individualStats.TryGetValue(name, out var top)) topLevelStats[name] = top;
              }

              foreach (var pair in cells.Children)
              {
                var kids = new Dictionary<string, PlayerStats>(pair.Value.Count);
                foreach (var kid in pair.Value)
                {
                  if (individualStats.TryGetValue(kid, out var row)) kids[kid] = row;
                }

                if (kids.Count > 0) childrenStats[pair.Key] = kids;
              }

              foreach (var pair in cells.FrameTime) prevPlayerTimes[pair.Key] = pair.Value;
              _raidTotals.Total = cells.RaidTotal;
              lastTime = cells.LastBlockTime;
              _cellCursor = cursor;
            }

            /*
             * Identity is a question about a NAME; this walk was asking it per RECORD. A night holds a few hundred names and
             * millions of records, and every answer walks override -> timeline -> ledger with its own counted span on top.
             *
             * One answer per name per build is also MORE self-consistent than the per-record answer was. Nothing this walk
             * writes can change an answer (pet learnings go to PlayerRegistry, which KindAt deliberately does not consult),
             * while a derive session swapped in under a running build used to be able to split one player's night across two
             * different verdicts halfway through the board.
             */
            var petAnswers = new Dictionary<string, bool>();

            /*
             * The walk iterates the blocks materialization handed over (`_walkBlocks`), NOT the joined copy of them that
             * `_damageGroups` holds for the chart. Same records in the same order - grouping only welds runs that share a
             * second, and welding preserves their relative order - but the pre-join objects are the ones layer 1 keeps
             * reference-stable across passes, which is what makes "how many of this block's actions have I counted" a
             * usable resume point. A joined block would be a new object every build and look unseen forever.
             */
            var skippedActions = 0L;
            var walkedActions = 0L;
            foreach (var block in CollectionsMarshal.AsSpan(_walkBlocks))
            {
              var actions = block.Actions;
              var from = _cellCursor is not null && _cellCursor.TryGetValue(block, out var seen) ? seen : 0;
              if (from > actions.Count) from = 0;   // a block that came back SHORTER is not the block this cursor describes
              skippedActions += from;

              for (var i = from; i < actions.Count; i++)
              {
                if (actions[i] is not DamageRecord record) continue;
                var isValid = damageValidator.IsValid(record);
                var stats = StatsUtil.CreatePlayerStats(individualStats, record.Attacker);

                if (record.Type == Labels.Bane && !isValid)
                {
                  stats.BaneHits++;

                  if (individualStats.TryGetValue(PetRowName(stats.OrigName), out var temp))
                  {
                    temp.BaneHits++;
                  }
                }
                else if (isValid)
                {
                  // Per name, not per record (see petAnswers above). The empty-string stand-in keeps a record whose line
                  // never named an attacker out of dictionary-key business; IsPet answers Unknown/false either way.
                  var attackerKey = record.Attacker ?? string.Empty;
                  if (!petAnswers.TryGetValue(attackerKey, out var isAttackerPet))
                  {
                    petAnswers[attackerKey] = isAttackerPet = IdentityLookup.IsPet(record.Attacker);
                  }

                  var isNewFrame = StatsUtil.CheckNewFrame(prevPlayerTimes, stats.Name, block.BeginTime);

                  _raidTotals.Total += record.Total;
                  StatsUtil.UpdateDamageStats(stats, record, isNewFrame, isAttackerPet);

                  /*
                   * Whose pet is this? The record's own answer first. A derived record carries the owner this LINE said,
                   * or the owner of the charm window this fact fell inside (FightSummarySource.OwnerOf), and a name can be
                   * possessed twice in one selection — a mob charmed by one raider, unfriended, recharmed by another — where
                   * an untimed lookup answers with the LAST owner for every record of that name. Measured before this line
                   * existed: 100 damage for Firstowner and 200 for Secondowner came out as a single "Secondowner +Pets = 300",
                   * a number nobody did.
                   *
                   * The per-name map stays for records the capture never attributed (stored records from a previous session,
                   * whose AttackerOwner is empty) — it is the fallback, not the authority.
                   */
                  var player = !string.IsNullOrEmpty(record.AttackerOwner) ? record.AttackerOwner
                    : _petToPlayer.TryGetValue(record.Attacker, out var mapped) ? mapped : null;

                  if ((player is null && !_playerPets.ContainsKey(record.Attacker)) || Labels.IsUnassignedOwner(player))
                  {
                    topLevelStats[record.Attacker] = stats;
                    stats.IsTopLevel = true;
                  }
                  else
                  {
                    var origName = player ?? record.Attacker;
                    var aggregateName = PetRowName(origName);
                    isNewFrame = StatsUtil.CheckNewFrame(prevPlayerTimes, aggregateName, block.BeginTime);

                    var aggregatePlayerStats = StatsUtil.CreatePlayerStats(individualStats, aggregateName, origName);
                    StatsUtil.UpdateDamageStats(aggregatePlayerStats, record, isNewFrame, isAttackerPet);
                    topLevelStats[aggregateName] = aggregatePlayerStats;

                    if (childrenStats.TryGetValue(aggregateName, out var children))
                    {
                      children.TryAdd(stats.Name, stats);
                    }
                    else
                    {
                      childrenStats[aggregateName] = new Dictionary<string, PlayerStats> { { stats.Name, stats } };
                    }

                    stats.IsTopLevel = false;
                  }

                  var subStats = stats.SubStatOf(record.SubType, record.Type);
                  var critHits = subStats.CritHits;
                  StatsUtil.UpdateDamageStats(subStats, record, false, isAttackerPet);

                  // don't count misses/dodges or where no damage was done
                  if (record.Total > 0)
                  {
                    // The histogram is not built here: the walk keeps the amount and Attempt turns the list into counts when HitFreqChart asks.
                    subStats.RecordHitTotal(record.Total, isCrit: subStats.CritHits > critHits);
                  }
                }
                walkedActions++;
              }

              if (_cellCursor is not null) _cellCursor[block] = actions.Count;
              lastTime = block.BeginTime;
            }

            StatsBuildTrace.Stage(trace, "walk");
            DamageCellStore.RecordWalk(skippedActions, walkedActions);

            /*
             * Store what this build counted, between the walk and the present phase: at this moment no view field has been
             * written (Rank/Percent/Ranges/ClassName/Special are re-derived for every row below), so a carried row needs none
             * of them reset - the copy simply cannot inherit last build's ranking into tonight's board.
             *
             * A sliced or unproven build stores nothing: a slice is a different question, and cells that answered one would
             * silently answer the next with fewer seconds in them.
             */
            if (options.CellProof is { } storeProof && !sliced && _walkBlocksAreSource)
            {
              StoreCells(storeProof, individualStats, topLevelStats, childrenStats, prevPlayerTimes, skippedActions + walkedActions);
              var heldBytes = DamageCellStore.Held is { } h ? h.RoughBytes : 0L;
              cellTrace.Text = $"cells {(cells is not null ? "hit" : "stored")} counted {skippedActions + walkedActions:N0}"
                + $" (new {walkedActions:N0}, reused {skippedActions:N0}) rows {individualStats.Count:N0} held ~{heldBytes / (1024 * 1024)} MB";
            }
            else
            {
              cellTrace.Text = $"cells cold ({cellTrace.Text})";
            }

            _cellCursor = null;
            StatsBuildTrace.Stage(trace, "cells");

            _raidTotals.Dps = (long)Math.Round(_raidTotals.Total / _raidTotals.TotalSeconds, 2);
            StatsUtil.PopulateSpecials(_raidTotals, true);

            var expandedStats = new List<PlayerStats>();
            var uniqueClasses = new HashSet<string>();
            var playerClasses = new Dictionary<string, string>();
            foreach (var stats in individualStats.Values)
            {
              if (_playerGroupAssignments.TryGetValue(stats.OrigName, out var cachedGroup))
              {
                stats.AssignedGroup = cachedGroup;
              }

              if (topLevelStats.ContainsKey(stats.Name))
              {
                if (childrenStats.TryGetValue(stats.Name, out var children))
                {
                  var timeRange = new TimeRange();
                  foreach (var child in children.Values)
                  {
                    // update before using _playerTimeRanges
                    StatsUtil.UpdateAllStatsTimeRanges(child, _playerTimeRanges, _playerSubTimeRanges, startTime, stopTime);

                    if (_playerTimeRanges.TryGetValue(child.Name, out var range))
                    {
                      timeRange.Add(range.TimeSegments);
                      // Store time segments on player for Group View and other view modes
                      child.Ranges = new TimeRange(range.TimeSegments);
                    }

                    expandedStats.Add(child);
                    _raidTotals.ResistCounts.TryGetValue(child.Name, out var childResists);

                    StatsUtil.UpdateCalculations(child, _raidTotals, childResists);

                    if (stats.Total > 0)
                    {
                      child.Percent = (float)Math.Round(Convert.ToDouble(child.Total) / stats.Total * 100, 2);
                    }

                    if (_raidTotals.Specials.TryGetValue(child.Name, out var special1))
                    {
                      child.Special = special1;
                    }
                  }

                  var filteredTimeRange = StatsUtil.FilterTimeRange(timeRange, startTime, stopTime);
                  stats.TotalSeconds = filteredTimeRange.GetTotal();
                }
                else
                {
                  expandedStats.Add(stats);
                  StatsUtil.UpdateAllStatsTimeRanges(stats, _playerTimeRanges, _playerSubTimeRanges, startTime, stopTime);
                  // Store time segments on player for Group View and other view modes
                  if (_playerTimeRanges.TryGetValue(stats.Name, out var range))
                  {
                    stats.Ranges = new TimeRange(range.TimeSegments);
                  }
                }

                _raidTotals.ResistCounts.TryGetValue(stats.Name, out var resists);
                StatsUtil.UpdateCalculations(stats, _raidTotals, resists);

                if (_raidTotals.Specials.TryGetValue(stats.OrigName, out var special2))
                {
                  stats.Special = special2;
                }
              }

              var playerClass = PlayerRegistry.Instance.GetPlayerClass(stats.OrigName, lastTime);
              stats.ClassName = playerClass;
              playerClasses.TryAdd(stats.OrigName, playerClass);

              if (!string.IsNullOrEmpty(playerClass))
              {
                uniqueClasses.Add(playerClass);
              }
            }

            /*
             * A diagnostic split inside what used to be one "present" number: the loop above walks EVERY name on the board -
             * mobs included, 12k of them on a group night - copying each name's time ranges and asking the registry for a
             * class, while everything below is O(players) summary arithmetic. They scale differently, so they get separate
             * words ("totals" = per-name assembly, "present" = combined stats and events).
             */
            StatsBuildTrace.Stage(trace, "totals");

            var combined = new CombinedStats
            {
              RaidStats = _raidTotals,
              TargetTitle = (_selected.Count > 1 ? "Combined (" + _selected.Count + "): " : "") + _title,
              TimeTitle = string.Format(CultureInfo.CurrentCulture, StatsUtil.TimeFormat, _raidTotals.TotalSeconds),
              TotalTitle = string.Format(CultureInfo.CurrentCulture, StatsUtil.TotalFormat, StatsUtil.FormatTotals(_raidTotals.Total),
                " Damage ", StatsUtil.FormatTotals(_raidTotals.Dps)),
              PlayerClasses = playerClasses
            };

            combined.StatsList.AddRange(topLevelStats.Values);
            combined.StatsList.Sort(static (a, b) => b.Total.CompareTo(a.Total));
            combined.FullTitle = StatsUtil.FormatTitle(combined.TargetTitle, combined.TimeTitle, combined.TotalTitle);
            combined.ShortTitle = StatsUtil.FormatTitle(combined.TargetTitle, combined.TimeTitle);
            combined.ExpandedStatsList.AddRange(expandedStats);
            combined.ExpandedStatsList.Sort(static (a, b) => b.Total.CompareTo(a.Total));
            combined.UniqueClasses.AddRange(uniqueClasses);
            combined.UniqueClasses.Sort(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < combined.ExpandedStatsList.Count; i++)
            {
              combined.ExpandedStatsList[i].Rank = Convert.ToUInt16(i + 1);
              if (combined.StatsList.Count > i)
              {
                var stats = combined.StatsList[i];
                stats.Rank = Convert.ToUInt16(i + 1);

                if (childrenStats.TryGetValue(stats.Name, out var children))
                {
                  var sortedChildren = new List<PlayerStats>(children.Values);
                  sortedChildren.Sort(static (a, b) => b.Total.CompareTo(a.Total));
                  combined.Children.Add(stats.Name, sortedChildren);
                }
              }
            }

            // Everything from here down is arithmetic and lists over what survived the walk: O(names on the board), not
            // O(records). It is the half a "one row changed" refresh would run on its own.
            StatsBuildTrace.Stage(trace, "present");

            // generating new stats
            var genEvent = new StatsGenerationEvent
            {
              Type = Labels.DamageParse,
              State = "COMPLETED",
              CombinedStats = combined,
              Limited = damageValidator.IsDamageLimited()
            };

            genEvent.Groups.AddRange(_damageGroups);
            genEvent.UniqueGroupCount = _damageGroupIds.Count;
            EventsGenerationStatus?.Invoke(genEvent);
            _lastStatsEvent = genEvent;
            FireChartEvent("UPDATE");
          }
          catch (Exception ex)
          {
            Log.Error(ex);
            if (StatsBuildTrace.FailFast)
              throw;   // tests: a swallowed builder throw is an empty board, not a failed run
          }
        }
      }

      return;

    }

    private void FireNewStatsEvent()
    {
      // generating new stats
      EventsGenerationStatus?.Invoke(new StatsGenerationEvent { Type = Labels.DamageParse, State = "STARTED" });
    }

    private void FireNoDataEvent(GenerateStatsOptions options, string state)
    {
      // nothing to do
      EventsGenerationStatus?.Invoke(new StatsGenerationEvent { Type = Labels.DamageParse, State = state });
      FireChartEvent("CLEAR");
    }

    /* "X +Pets" for this build, from the memo beside the field. Same string the concatenation would have produced. */
    private string PetRowName(string owner)
    {
      if (!_petRowNames.TryGetValue(owner, out var row)) _petRowNames[owner] = row = owner + " +Pets";
      return row;
    }

    private void Reset()
    {
      _allDamageGroups = _damageGroups;
      _damageGroups.Clear();
      _walkBlocks = [];
      _walkBlocksSource = [];
      _walkBlocksAreSource = false;
      _cellCursor = null;
      _damageGroupIds.Clear();
      _petRowNames.Clear();
      _raidTotals = StatsUtil.CreatePlayerStats(Labels.RaidTotals);
      _playerPets.Clear();
      _petToPlayer.Clear();
      _playerTimeRanges.Clear();
      _playerSubTimeRanges.Clear();
      _selected = null;
      _title = "";
    }

    private void UpdatePetMapping(DamageRecord damage)
    {
      // The owner the record itself carries wins; the untimed lookup is the fallback for records that carry none (see the
      // fold in ComputeDamageStats, which does the same and is where an untimed answer would move damage to the wrong person).
      var petName = !string.IsNullOrEmpty(damage.AttackerOwner) ? damage.AttackerOwner : IdentityLookup.OwnerOf(damage.Attacker);
      if (!string.IsNullOrEmpty(petName) && !Labels.IsUnassignedOwner(petName))
      {
        if (!_playerPets.TryGetValue(petName, out var mapping))
        {
          mapping = new ConcurrentDictionary<string, byte>();
          _playerPets[petName] = mapping;
        }

        mapping[damage.Attacker] = 1;
        _petToPlayer[damage.Attacker] = petName;
      }
    }
  }
}

