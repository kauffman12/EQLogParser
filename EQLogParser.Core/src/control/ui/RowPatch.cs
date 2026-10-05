#nullable enable annotations
namespace EQLogParser;

/*
 * Turning one displayed row list into the next WITHOUT throwing the surviving rows away.
 *
 * Why this exists, with numbers. Every completed derive pass raises `Derived`, and the derived fight list answered by
 * replacing its whole collection: ~4,835 row objects rebuilt on a farm night, ~770 on a raid night, plus a selection that had to
 * be re-found by name + start time because nothing survived. Measured over real captures replayed as growing prefixes
 * (docs/DesignNotes.md → "Would an equality gate have saved anything?"), a pass is never a visual no-op — 0 of 11 on Incogitable,
 * 0 of 23 on Kizant at finer grain — but the rows it actually touch are few: median 42 of ~770 on a raid night, median 188 of
 * ~4,835 on a farm night, i.e. **roughly five percent**. So the win is not a "did anything change at all" gate (which almost never
 * fires and charges a comparison every pass); it is updating only the rows that moved. An empty update set then IS the
 * only-when-needed behaviour the gate was after, obtained for free — and the pass where something did move costs 5 % instead of 100 %.
 *
 * Three laws the shape of this code exists to keep:
 *   - **Instances survive.** A row that is still on screen stays the same object, so a grid keeps its selection, its scroll
 *     position and the user's place without any re-lookup. `Apply` never replaces a survivor; it copies CONTENT into it.
 *   - **A patch never lies about order.** If matched rows arrive in a different relative order than they sit (a re-sort, a
 *     section re-grouping), merging in place would move cells under the reader while their instances stayed put. That is not a
 *     patch: `Build` returns null and the caller rebuilds wholesale — the one case where making all-new rows is the honest answer.
 *   - **Bounded churn.** When half the list is new (first snapshot after a bulk load, a re-classification that renames rows), a
 *     patch is more work than a build, so `maxChurn` turns the caller back to the wholesale path before anything is mutated.
 */
internal static class RowPatch
{
  internal sealed class Plan<T> where T : class
  {
    /// <summary>Surviving rows whose displayed content differs, paired with the row to copy from.</summary>
    internal List<(T Existing, T Source)> Updates = [];

    /// <summary>Rows present now and not in the new list. Removed before newcomers are placed.</summary>
    internal List<T> Removals = [];

    /// <summary>How many rows needed nothing at all — the number that makes this "only when changed".</summary>
    internal int Unchanged;

    /// <summary>Newcomers, counted for the churn cap (placement walks `Layout` at apply time).</summary>
    internal int InsertedCount;

    /*
     * The sequence the list must end up holding, with EVERY position resolved to a concrete instance: a matched row resolves to
     * the SURVIVING old object (that is the whole point — the grid keeps its object), a newcomer to the incoming object. Apply walks
     * this and inserts only where the live list does not already hold that reference. A plan that instead carried the new list would
     * "find" no survivor at any position, insert every newcomer and quietly hand the grid brand-new rows — which is exactly what the
     * first version of this did until `OneRowChanged_OneUpdateAndEveryInstanceKept` refused it.
     */
    internal List<T> Layout { get; } = [];

    internal Plan() { }

    internal int Churn => Updates.Count + Removals.Count + InsertedCount;
  }

  /*
   * Compare the two lists and describe the change, or refuse it. `keyOf` must be stable across passes for the same displayed
   * row (the fight list uses name + begin time — the pair its selection restore already trusts); duplicate keys are refused
   * rather than guessed at, because "which of these two rows is this one?" has no answer that a merge may invent.
   */
  internal static Plan<T>? Build<T>(IReadOnlyList<T> current, IReadOnlyList<T> next, Func<T, string> keyOf,
                                    Func<T, T, bool> sameContent, int maxChurn) where T : class
  {
    var plan = new Plan<T>();

    // Keys must be unique on BOTH sides: a duplicate means the key is not identifying a row, and every decision below would be
    // a coin flip between two rows. Refuse; the caller's wholesale rebuild is correct for any list, keyed or not.
    var nextIndex = new Dictionary<string, int>(StringComparer.Ordinal);
    for (var i = 0; i < next.Count; i++)
    {
      if (!nextIndex.TryAdd(keyOf(next[i]), i)) return null;
    }

    var seenOnCurrentSide = new HashSet<string>(StringComparer.Ordinal);
    foreach (var row in current)
    {
      if (!seenOnCurrentSide.Add(keyOf(row))) return null;
    }

    // Where each surviving row goes: next-position keyed by key, so the layout below can resolve a position to the OLD object.
    var survivorByNext = new Dictionary<string, T>(StringComparer.Ordinal);

    var lastMatchedNext = -1;
    foreach (var row in current)
    {
      if (!nextIndex.TryGetValue(keyOf(row), out var at))
      {
        plan.Removals.Add(row);
        continue;
      }

      // Matched rows must appear in the same relative order on both sides, or this is a re-sort seen as a patch.
      if (at < lastMatchedNext) return null;
      lastMatchedNext = at;

      var source = next[at];
      if (sameContent(row, source)) plan.Unchanged++;
      else plan.Updates.Add((row, source));
      survivorByNext[keyOf(row)] = row;
    }

    foreach (var row in next)
    {
      var key = keyOf(row);
      if (survivorByNext.TryGetValue(key, out var survivor))
      {
        plan.Layout.Add(survivor);
        continue;
      }
      plan.Layout.Add(row);
      plan.InsertedCount++;
    }

    if (plan.Churn > maxChurn) return null;
    return plan;
  }

  /*
   * Carry the plan out on the live list. Order matters: content first (survivors are still where they were and a grid repaints
   * them through property change), then removals, then newcomers walked into position by reading the target sequence — because
   * survivors are already in each other's order, that walk lands every newcomer exactly once with no index arithmetic to get wrong.
   */
  internal static void Apply<T>(IList<T> target, Plan<T> plan, Action<T, T> copyContent) where T : class
  {
    foreach (var (existing, source) in plan.Updates) copyContent(existing, source);
    foreach (var row in plan.Removals) target.Remove(row);

    var at = 0;
    foreach (var expected in plan.Layout)
    {
      if (at < target.Count && ReferenceEquals(target[at], expected))
      {
        at++;
        continue;
      }
      target.Insert(at, expected);
      at++;
    }

    // By construction there is nothing left over once the walk ends; drop it rather than trust that.
    while (target.Count > plan.Layout.Count) target.RemoveAt(target.Count - 1);
  }
}
