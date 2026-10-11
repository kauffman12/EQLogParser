using System.Collections.Concurrent;

namespace EQLogParser;

/*
 * A detached copy of a board row — and the boundary a carried cell has to be taken at.
 *
 * WHY THIS EXISTS. Layer 2 of the incremental refresh (carry each name's counted row across passes, fold it into the next
 * build, and walk only the records that arrived since) was measured over four captures and cleared its bar by two orders of
 * magnitude: a row's 1-146 us publish against a 46 ms recount, 99 %+ saved everywhere (docs/DesignNotes.md → "Fold-at-publish
 * measured: the cost cleared the bar, and the row on screen did not"). What blocked it was not cost. Two facts:
 *
 *   - **A published row is not a cacheable cell.** Every published row still holds its running second — `BestSecTemp` with
 *     83/83, 59/59 and 413/414 rows measured — because publishing must NOT drain it (a report going out may not end the second
 *     the next batch is still writing into: that law is `UpdateCalculations` and its test). `MergeStatsRecursive` therefore
 *     refuses such a row on purpose, by name. A cell has to be taken at a drained boundary rather than lifted out of the display
 *     model afterwards.
 *   - **The display model had no copy path.** `SubStats`, `SubStats2` and `SubSubStats` are `{ get; } = []`, so there was no way
 *     to hand the grid a row and keep an accumulating cell for oneself. A cache that stored what the builder published would be a
 *     cache of objects the next build mutates — the fold would write into what the reader is looking at, in both directions.
 *
 * So this file answers two questions, and only these: **detached** (no mutable object on both sides — every list, dictionary and
 * `TimeRange` on the copy is a new one, and the child rows are copies rather than the same instances) and **drained** (the copy's
 * running second is closed into its best second at EVERY level, which is what makes it legal fold input; the row it came from keeps
 * its scratch and can keep growing this second).
 *
 * Deliberately NOT here: the cache itself (cells keyed by row × ordinal window, under `FightMaterializationCache`'s
 * identity-proof discipline — that is step 2), what a snapshot costs (measured already, in the section above), and anything about
 * when to prefer a full rebuild.
 */
internal static class StatsSnapshot
{
  /*
   * The row shapes this copier knows. A shallow replica carries SCALARS and SHARES references, so a new subclass of PlayerStats or
   * PlayerSubStats with reference fields of its own would hand both sides the same object and read as a correct copy until somebody
   * appended to it. Refusing on an unknown shape turns that into a named failure at first use; adding the shape here — and re-pointing
   * its fields in Copy — is the fix the message asks for.
   */
  private static readonly Type[] RowShapes = [typeof(Attempt), typeof(PlayerSubStats), typeof(SubStatsBreakdown), typeof(PlayerStats), typeof(GroupEntry)];

  /// <summary>A detached, drained copy of a published row: same numbers, same child rows, nothing shared. The source keeps its own
  /// running second (publishing stays non-destructive) and its own subscribers.</summary>
  internal static PlayerStats Take(PlayerStats row)
    => (PlayerStats)Copy(row, new Dictionary<PlayerStats, PlayerStats>(ReferenceEqualityComparer.Instance), drainRunningSecond: true);

  /// <summary>The same for one cell — a sub-stat node at any depth. Use this to carry a cell; use Take for a whole report.</summary>
  internal static PlayerSubStats TakeCell(PlayerSubStats cell)
    => Copy(cell, new Dictionary<PlayerStats, PlayerStats>(ReferenceEqualityComparer.Instance), drainRunningSecond: true);

  /*
   * A detached copy that KEEPS the running second open — the boundary a carried board cell is taken at.
   *
   * `Take` drains because it was written as fold input: `MergeStatsRecursive` refuses a row whose `BestSecTemp` still holds
   * the second being written (that refusal is `UpdateCalculations`' law). A carried cell is not folded, it is CONTINUED:
   * the next build hands its own accumulating rows back and keeps counting records into them, so closing the open second at
   * the carry would split one busy second across two partial maxima — `BestSec` (a column the grid prints) would read the
   * larger of two halves of a second rather than the whole of it, for every name active at the moment the pass landed.
   * Keeping it open costs nothing and is what makes an incremental build's numbers the same numbers a single build over the
   * same records produces. (docs/DesignNotes.md → "A carried cell keeps the running second open".)
   *
   * Everything else about detachment holds: new lists, new dictionaries, new `TimeRange`s, copied child rows.
   */
  internal static PlayerStats TakeCarry(PlayerStats row)
    => (PlayerStats)Copy(row, new Dictionary<PlayerStats, PlayerStats>(ReferenceEqualityComparer.Instance), drainRunningSecond: false);

  /*
   * `rows` exists for one edge: `MoreStats` (the healing row hung on a damage row, and anything that ever points back). Registering
   * each row's copy BEFORE descending means a cycle meets its own clone instead of recursing until the stack is gone.
   */
  private static PlayerSubStats Copy(PlayerSubStats source, Dictionary<PlayerStats, PlayerStats> rows, bool drainRunningSecond)
  {
    if (source is null) return null!;

    var shape = source.GetType();
    if (Array.IndexOf(RowShapes, shape) < 0)
    {
      throw new InvalidOperationException($"StatsSnapshot does not know how to copy {shape.Name}: a shallow replica would share its"
        + " reference fields with the original. Add the shape to StatsSnapshot.RowShapes and re-point them there.");
    }

    // The cast is safe by the parameter's own type: this method only ever copies a PlayerSubStats (or below), and
    // ShallowReplica preserves the runtime type.
    var copy = (PlayerSubStats)source.ShallowReplica();
    if (source is PlayerStats publishedRow)
    {
      var detached = (PlayerStats)copy;
      detached.DetachViewSubscribers();
      rows[publishedRow] = detached;
    }

    /*
     * The running second closes on the COPY, at every level: `BestSecTemp` folds into `BestSec` and clears, which is what lets
     * MergeStatsRecursive take this node as a source without its guard firing. Nothing here touches the source's scratch — that is
     * the half publishing was not allowed to do.
     */
    if (drainRunningSecond) StatsUtil.CloseBestSecond(copy);

    // The chart's amounts ride as fresh objects: an unasked-for row carries raw amounts, an asked-for one carries its dictionaries,
    // and either way the two sides append and read separately. `MergeStatsRecursive` has no term for these, so a cache that skipped
    // them would empty HitFreqChart on every cached row.
    source.CopyDistributionTo(copy);

    copy.Ranges = CopyRange(source.Ranges);
    copy.AllRanges = CopyRange(source.AllRanges);
    copy.SubSubStats = CopyCells(source.SubSubStats, rows, drainRunningSecond);
    if (source.SubSubStatsIndex is not null) copy.SubSubStatsIndex = IndexOver(copy.SubSubStats);

    if (copy is SubStatsBreakdown breakdown)
    {
      breakdown.Children = CopyCells(((SubStatsBreakdown)source).Children, rows, drainRunningSecond);
    }

    /*
     * A group row owns two more lists of rows. They are the same problem in a different place — a shallow replica hands both sides the
     * same `List<PlayerStats>`, so a later Group View rebuild would sort members out of a carried cell — and they are easy to miss
     * because nothing in PlayerStats hints at them.
     */
    if (copy is GroupEntry groupCopy)
    {
      var sourceGroup = (GroupEntry)source;
      groupCopy.Members = CopyRows(sourceGroup.Members, rows, drainRunningSecond);
      groupCopy.Children = CopyRows(sourceGroup.Children, rows, drainRunningSecond);
    }

    if (source is not PlayerStats row) return copy;
    var target = (PlayerStats)copy;

    target.SubStats = CopyCells(row.SubStats, rows, drainRunningSecond);
    if (row.SubStatsIndex is not null) target.SubStatsIndex = IndexOver(target.SubStats);
    target.SubStats2 = CopyCells(row.SubStats2, rows, drainRunningSecond);
    if (row.SubStats2Index is not null) target.SubStats2Index = IndexOver(target.SubStats2);

    /*
     * The special-effect strings and the resist counts are per-name facts a walk writes once, so their ENTRIES copy as strings and
     * ints while the containers are new. `Deaths` is the one list whose payload is deliberately shared: a `DeathRecord` is a parsed
     * line — written at parse and read thereafter, never edited by a board build — so copying it would cost an object per death and
     * buy nothing. If a build ever mutates a record in place, that is the day this line becomes a bug; the copy boundary is where
     * such a decision has to be re-asked, which is what the comment is for.
     */
    target.Specials = new ConcurrentDictionary<string, string>(row.Specials);
    target.ResistCounts = new ConcurrentDictionary<string, ConcurrentDictionary<string, int>>(
      row.ResistCounts.ToDictionary(static e => e.Key, static e => new ConcurrentDictionary<string, int>(e.Value)));
    target.Deaths = [.. row.Deaths.Select(static d => new DeathEvent { Record = d.Record, BeginTime = d.BeginTime })];

    target.MoreStats = row.MoreStats is null ? null : CopyMoreStats(row.MoreStats, rows, drainRunningSecond);
    return target;
  }

  private static PlayerStats CopyMoreStats(PlayerStats source, Dictionary<PlayerStats, PlayerStats> rows, bool drainRunningSecond)
    => rows.TryGetValue(source, out var already) ? already : (PlayerStats)Copy(source, rows, drainRunningSecond);

  private static List<PlayerSubStats> CopyCells(List<PlayerSubStats> cells, Dictionary<PlayerStats, PlayerStats> rows, bool drainRunningSecond)
  {
    var copy = new List<PlayerSubStats>(cells.Count);
    foreach (var cell in cells) copy.Add(Copy(cell, rows, drainRunningSecond));

    return copy;
  }

  private static List<PlayerStats> CopyRows(List<PlayerStats> members, Dictionary<PlayerStats, PlayerStats> rows, bool drainRunningSecond)
  {
    var copy = new List<PlayerStats>(members.Count);
    foreach (var member in members) copy.Add((PlayerStats)Copy(member, rows, drainRunningSecond));

    return copy;
  }

  /*
   * A NEW range, filled through the overload that copies each segment. The `List<TimeSegment>` overload ADOPTS — those segment objects
   * become part of the receiving list and get welded when something overlaps them — which is how summing one row's seconds rewrites
   * spans another reader still holds (docs/DesignNotes.md → "Three boards, three workers": a build may never add to a TimeRange it
   * adopted). Copying is the only correct door here, and this is the line that keeps it that way.
   */
  private static TimeRange CopyRange(TimeRange source)
  {
    var copy = new TimeRange();
    copy.Add(source);
    return copy;
  }

  /*
   * The accelerator beside a copied child list, rebuilt rather than shared: an index maps keys to ROW INSTANCES, and the rows on the
   * other side of this boundary are different objects. Leaving it empty is not an option either — `SubStatLookup` never scans, so an
   * un-indexed copy would mint a SECOND row for a key the board already has. It is rebuilt only where the source HAD one: through
   * `SubStatOf`/`SubStat2Of`/`SubSubStatOf` an index exists exactly when its list has entries, so mirroring presence keeps the copy the
   * same shape as the row instead of inventing empty dictionaries for leaf cells.
   *
   * Keyed by each child's own `Key`, which is what `CreateRecordKey` stamped when the builder made it: the folded-pair rule
   * (("Spell","Foo") and ("Melee","Foo") are one row) therefore cannot drift, because no new keying rule is applied here at all. The
   * `ByPair` fast lane is left empty on purpose — it is an accelerator over pairs a caller holds, refilled on demand, and this copier
   * holds no pairs (registering (Type, Name) as a pair would be a SECOND opinion about which pair folds to which row).
   */
  private static SubStatIndex IndexOver(List<PlayerSubStats> cells)
  {
    var index = new SubStatIndex();
    foreach (var cell in cells)
    {
      if (cell.Key is null) index.NoName ??= cell; else index.ByKey[cell.Key] = cell;
    }

    return index;
  }
}
