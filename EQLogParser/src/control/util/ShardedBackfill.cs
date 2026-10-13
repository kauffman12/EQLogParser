using System;
using System.Collections.Generic;

/*
 * Annotations only (see DeriveEngine): the contract speaks of captures and lanes it does not own.
 */
#nullable enable annotations

namespace EQLogParser
{
  /*
   * One lane's ingest machinery for a byte range of the file being opened: the tables its capture fills,
   * the lane itself, and the capture (subscribed, running) until the merge stops it. Created only through
   * IShardedBackfill so the tables get the same capacity math as the session's own.
   */
  internal sealed class BackfillShard(FactShard rows, ParseLane lane, CombatCapture capture)
  {
    internal FactShard Rows { get; } = rows ?? throw new ArgumentNullException(nameof(rows));
    internal ParseLane Lane { get; } = lane ?? throw new ArgumentNullException(nameof(lane));
    internal CombatCapture Capture { get; } = capture ?? throw new ArgumentNullException(nameof(capture));
  }

  /*
   * The collaborator a LogReader asks for a sharded backfill: who can make per-range ingest lanes and
   * absorb their output. It is the engine that owns the session's capture, tables and capacity math, so
   * it is what implements this — but the READER decides when to shard (whole-file mode, big enough, not a
   * gzip) and owns the ordering the merge requires: every shard drained and its capture stopped before
   * Merge runs, and no tail line handed over before Merge returns. Passing null — every small log, every
   * seek-back, every gz, and every headless caller that predates this — reads exactly as it always did.
   */
  internal interface IShardedBackfill
  {
    // Tables sized for one range; a lane; a capture already started on that lane (its events must exist
    // before the range's first line, same contract as the session's own capture).
    BackfillShard NewShard(long windowBytes);

    // A processor bound to the shard's lane, with the shard's world wired into it: chat evidence lands on
    // the SHARD's capture (identity events merge from there), while app side channels (archive, quick-share)
    // behave as they do for the main processor — each byte belongs to exactly one range, so each line still
    // reaches each channel exactly once.
    // The concrete processor type: the reader awaits its Completion before Merge stops any capture.
    LogProcessor NewShardProcessor(string fileName, BackfillShard shard);

    // All shards drained, captures stopped: rebuild pools and the shared sequence into the session's real
    // tables under the ingest gate (CombatCapture.MergeShards). After this returns the reader may tail.
    void Merge(IReadOnlyList<BackfillShard> shards);

    // The open was replaced or closed mid-backfill: stop the shard captures, let GC take the tables.
    void Abort(IReadOnlyList<BackfillShard> shards);
  }
}
