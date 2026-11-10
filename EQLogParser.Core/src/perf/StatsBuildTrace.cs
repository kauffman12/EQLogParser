#nullable enable annotations

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using log4net;

namespace EQLogParser;

/*
 * 1. Every caller names itself; an unlabelled caller still warns.
 * 2. Same-thread nesting and same-request siblings are not overlaps.
 * 3. Costs and overlap episodes are Debug diagnostics; counters stay active at every level.
 * Independent meter/summary work may legitimately overlap. docs/DesignNotes.md → "Name every door: which build was which".
 */
internal static class StatsBuildTrace
{
  /// <summary>What an unlabelled caller shows as, in the log and in tests.</summary>
  internal const string UnlabelledDoor = "UNLABELLED";

  /*
   * Test-only: a summary builder that throws RETHROWS instead of being swallowed, so a defect inside a board build fails
   * the run with its real stack rather than reading as "the raid did no damage". Off in production on purpose — one bad
   * record must not cost a player their night, and eqlogparser.log keeps the stack either way. Same law and shape as
   * ClassificationRules.FailFastStages; both test assemblies turn this on at assembly init.
   *
   * Why it exists: every builder wraps its body in `catch (Exception) { Log.Error(ex); }`, so a null reference inside a
   * build is indistinguishable from an empty selection. That is how a hoisted line threw over an empty `options.AllRanges`
   * for a whole test cycle with nothing but a missing NONPC event to show for it.
   */
  internal static bool FailFast;

  private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

  private static readonly object _sync = new();
  // What is inside Begin..End right now, with the thread that owns it: HealingStatsBuilder.RebuildTotalStats calls
  // BuildTotalStats on the SAME thread while holding its lock, and that nesting is one door, not two colliding.
  // `Mark` is where the last closed stage ended, so Stage() can report each phase on its own; `Stages` collects them
  // for the finished line. A build is a handful of phases, so this is a list of a few entries per in-flight build.
  /*
   * `MarkAlloc` carries `GC.GetTotalAllocatedBytes` at the last boundary, so each stage reports the bytes it allocated next to
   * the milliseconds it spent: `walk 2436 ms/398.1 MB`. A board that costs two seconds and allocates nothing is a different
   * problem from one that costs two seconds and 400 MB (the first wants fewer operations, the second wants fewer objects, and
   * only the second explains a UI-thread pause that outlives the pass itself). Asked once per stage boundary, not per record.
   */
  private static readonly List<(int Seq, string What, int ThreadId, int Group, long Mark, long MarkAlloc, List<string>? Stages)> _inside = [];

  /* A stage under half a megabyte is not worth a term on the line; over it, where the bytes are is the question being asked. */
  private const long StageAllocTermBytes = 512 * 1024;

  private static int _seq;

  /*
   * The group counter. Ids start at 1 because 0 means "no group was given" at every call site: a build with no group is issued its
   * own fresh one, so it stays foreign to everything and the pre-fan-out warning behaviour survives without a branch.
   */
  private static int _groupSeq;

  private static readonly ConcurrentDictionary<string, string> _lastFinished = new();
  private static long _overlapBuilds;
  private static long _unlabelledBuilds;

  /*
   * Collisions since the boards were last completely quiet, guarded by `_sync` alongside the list they describe. Zero means "the
   * next one opens the Debug episode"; the closing line reports the accumulated count and resets it.
   */
  private static int _overlapsInEpisode;

  /// <summary>One build in progress. Obtain from Begin, close with End (in a finally — an unclosed handle hides the next overlap).</summary>
  internal readonly record struct Handle(int Seq, string Builder, string Door, string Kind, long StartTicks, long StartAlloc, int OverlappedWith, int Group);

  /// <summary>Builds started since process start.</summary>
  internal static int TotalBuilds => Volatile.Read(ref _seq);

  /// <summary>
  /// Issue a group id for ONE request whose builds may run side by side. Pass it to every build of that request (on
  /// `GenerateStatsOptions.BuildGroup`, since the builders are what call Begin) and they stop counting each other as collisions.
  /// A request that does not ask gets one implicitly, so nothing is ever un-grouped by accident.
  /// </summary>
  internal static int NextGroupId() => Interlocked.Increment(ref _groupSeq);

  /// <summary>How many of them started while another build was still running.</summary>
  internal static long OverlapBuilds => Interlocked.Read(ref _overlapBuilds);

  /// <summary>Collisions counted in the current diagnostic episode (0 when the boards are quiet).</summary>
  internal static int OverlapsInCurrentEpisode { get { lock (_sync) return _overlapsInEpisode; } }

  /// <summary>How many came through a door that did not set GenerateStatsOptions.Source.</summary>
  internal static long UnlabelledBuilds => Interlocked.Read(ref _unlabelledBuilds);

  /// <summary>Builds inside Begin..End at this instant (0 when the boards are quiet).</summary>
  internal static int ActiveBuilds { get { lock (_sync) return _inside.Count; } }

  /*
   * Start tracing one build. `builder` is what got rebuilt ("damage", "tanking", "healing"), `kind` says whether it was built
   * from a selection's blocks or re-sliced/rebuilt from stored records, `door` is the caller's own name for why it asked, and
   * `groupId` ties the builds of one request together (0 = "this build stands alone"; it is issued its own fresh group).
   */
  internal static Handle Begin(string builder, string? door, string kind = "full", int groupId = 0)
  {
    if (groupId == 0) groupId = NextGroupId();

    var seq = Interlocked.Increment(ref _seq);
    var labelled = !string.IsNullOrWhiteSpace(door);
    if (!labelled)
    {
      Interlocked.Increment(ref _unlabelledBuilds);
    }

    var threadId = Environment.CurrentManagedThreadId;
    var overlappedWith = 0;
    long startTicks;
    long startAlloc;
    lock (_sync)
    {
      for (var i = 0; i < _inside.Count; i++)
      {
        // Same thread = nesting (one door); same group = siblings of one request (also one door). Anything else is two doors.
        if (_inside[i].ThreadId != threadId && _inside[i].Group != groupId) overlappedWith++;
      }
      startTicks = Stopwatch.GetTimestamp();
      startAlloc = GC.GetTotalAllocatedBytes();
      _inside.Add((seq, $"{builder} {kind}", threadId, groupId, startTicks, startAlloc, null));
    }

    if (!labelled)
    {
      Log.Warn($"stats build #{seq} {builder} {kind} came from a door that did not name itself "
               + "- set GenerateStatsOptions.Source so duplicate work can be attributed");
    }

    if (overlappedWith > 0)
    {
      Interlocked.Increment(ref _overlapBuilds);
      string? others = null;
      bool opensEpisode;
      lock (_sync)
      {
        opensEpisode = _overlapsInEpisode++ == 0;
        // Only the FOREIGN ones are named: blaming a sibling for a collision it did not cause would send the reader to the wrong door.
        if (opensEpisode && Log.IsDebugEnabled)
          others = string.Join(", ", _inside.Where(e => e.Seq != seq && e.Group != groupId).Select(e => $"#{e.Seq} {e.What}"));
      }

      // Coalesce diagnostic starts, but count every overlap even with Debug disabled.
      if (opensEpisode && Log.IsDebugEnabled)
      {
        Log.Debug($"stats build #{seq} {builder} {kind} started while {overlappedWith} other stats build(s) were running: {others}"
                  + " - further overlapping starts are counted until the boards go quiet");
      }
    }

    return new Handle(seq, builder, labelled ? door! : UnlabelledDoor, kind, startTicks, startAlloc, overlappedWith, groupId);
  }

  /// <summary>Close a build and write its line. `detail` is whatever the caller can say cheaply about the inputs.</summary>
  internal static void End(in Handle handle, string? detail = null)
  {
    var ms = ElapsedMs(handle.StartTicks);
    var seq = handle.Seq;

    List<string>? stages;
    int heldBack;
    lock (_sync)
    {
      var i = _inside.FindIndex(e => e.Seq == seq);
      stages = i < 0 ? null : _inside[i].Stages;
      _inside.RemoveAll(e => e.Seq == seq);

      // Quiet at last: the episode closes here, and whatever it swallowed gets said once.
      heldBack = _inside.Count == 0 ? _overlapsInEpisode : 0;
      if (_inside.Count == 0) _overlapsInEpisode = 0;
    }

    if (heldBack > 1 && Log.IsDebugEnabled)
    {
      Log.Debug($"stats builds overlapped {heldBack} times since the boards were last quiet - "
                + "read the \"from <door>\" of each build line to identify the concurrent callers");
    }

    var totalAlloc = GC.GetTotalAllocatedBytes() - handle.StartAlloc;

    var line = $"stats build #{handle.Seq} {handle.Builder,-8} {handle.Kind,-7}: {ms:F0} ms | from {handle.Door}"
             + $" | {Mb(totalAlloc)} allocated"
             + (stages is { Count: > 0 } ? " | " + string.Join(" ", stages) : string.Empty)
             + (string.IsNullOrWhiteSpace(detail) ? string.Empty : $" | {detail}")
             + (handle.OverlappedWith > 0 ? $" | started over {handle.OverlappedWith} other build(s)" : string.Empty);

    // The line itself is the contract, so a surface that wants to report a build's cost (a gated benchmark printing the
    // walk/present split for the record) reads it instead of re-timing the builder from outside. Keyed by builder,
    // because one overlay refresh builds three boards and "the last line" would be whichever finished last.
    _lastFinished[handle.Builder] = line;

    /*
     * Debug only, and that is the law, not an implementation detail: this printed at Info once per board per build, which
     * on a live pull is several lines a second - thousands in a raid night, all of them restating the same door. A first
     * pass thinned it (a line when the door changed, plus a periodic echo with what was held back); the thinning itself
     * still left a metric stream in an ordinary log, and the question it answered - "WHICH door built this, and did it
     * change" - is one for whoever turns Debug on, not for a player. Every build's full line is therefore a Debug line,
     * and nothing of this class reaches Info without being asked; where they go is stated in the header and in
     * docs/DesignNotes ("Name every door"), and StatsBuildTraceTest pins everything else these lines carry.
     */
    Log.Debug(line);
  }


  /// <summary>The most recent finished line for that board ("damage", "healing", …), or null before its first build.</summary>
  internal static string? LastFinishedLineOf(string builder) => _lastFinished.TryGetValue(builder, out var line) ? line : null;

  /*
   * Close one phase of a build and name it on the finished line: `walk 2810 ms present 940 ms`.
   *
   * This exists for the measure-then-present restructure. "The damage board takes four seconds" is not actionable;
   * "the record walk is 2.8 s and everything downstream is 0.9 s" says exactly what a per-name accumulator buys and
   * what it does not, and it is the number that decides whether a minimal refresh (re-present only) can ever be
   * milliseconds. Phases are relative to the previous Stage call, so they add up toward the build's total without
   * anyone having to subtract; a build with no stages printed simply never called this.
   */
  internal static void Stage(in Handle handle, string name)
  {
    var now = Stopwatch.GetTimestamp();
    var seq = handle.Seq;
    lock (_sync)
    {
      var i = _inside.FindIndex(e => e.Seq == seq);
      if (i < 0) return;   // closed (or never opened): a stage after End is a bug in the caller, not a log line

      var entry = _inside[i];
      var allocNow = GC.GetTotalAllocatedBytes();
      var stageAlloc = allocNow - entry.MarkAlloc;
      (entry.Stages ??= []).Add($"{name} {ElapsedMs(entry.Mark):F0} ms"
                                + (stageAlloc >= StageAllocTermBytes ? $"/{Mb(stageAlloc)}" : string.Empty));
      _inside[i] = (entry.Seq, entry.What, entry.ThreadId, entry.Group, now, allocNow, entry.Stages);
    }
  }

  /// <summary>Elapsed milliseconds for a tick stamp taken earlier (MainWindow times its materialization with this too).</summary>
  internal static double ElapsedSince(long startTicks) => ElapsedMs(startTicks);

  /* The only clock here: whatever Stopwatch.GetTimestamp stamped, against the same frequency. */
  private static double ElapsedMs(long startTicks)
    => (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;

  private static string Mb(long bytes) => $"{bytes / 1048576.0:F1} MB";
}
