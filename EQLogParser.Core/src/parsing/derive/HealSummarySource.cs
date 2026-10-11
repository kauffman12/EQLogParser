using System.Collections.Generic;

namespace EQLogParser
{
  /*
   * The healing board's derived source.
   *
   * HealingStatsBuilder is the one board that never looks at a Fight object: it pulls
   * RecordsStore.GetAllHeals() — a (time, record) pair per heal — and windows that list itself against
   * GenerateStatsOptions.AllRanges. So feeding it derived data takes a source seam (options.Heals) rather than
   * another materialized fight, and this is what fills the seam: the capture's heal facts, back into the shape
   * the builder already consumes.
   *
   * Three rules, each one because the tidy version breaks something quiet.
   *
   *   - **Time ascending.** The builder locates its window with FindIndex(first >= begin) and walks forward from
   *     there, so an unordered list loses part of a segment without complaint. Heal facts are appended in the
   *     capture's shared sequence (ingest order), which may go backwards on a concatenated log. Such a capture is
   *     stably sorted after filtering; an ascending capture keeps the allocation-free ordered path.
   *
   *   - **OverTotal verbatim**, never HealFact.AskedFor. A line with no "(amount)" left the parser's overHeal at
   *     0, meaning "the line never said", and StatsUtil.UpdateHealStats does
   *     MaxPotentialHit = Total + OverTotal — so substituting Total would double that column on every plain heal.
   *     AskedFor is for ratios, not for records.
   *
   *   - **SubType falls back to Labels.SelfHeal**, because that is what HealingLineParser stores when the line
   *     carries no spell text ("fix subtype" at the end of its record build). A derived record with a null subtype
   *     would be a board whose spell rows differ from legacy's by having one row missing, not by being wrong.
   *
   * Two more laws came from measuring this call instead of guessing at it (2026-11, docs/DesignNotes → "A window is searched,
   * never walked"), and both are about not asking the same question N times per row:
   *
   *   - **The span test halves.** `TimeRange` keeps its list sorted and disjoint by construction, so containment is a halving
   *     (`range.Contains`), not a scan from span zero. On a capture with thousands of sections the scan was ~1,300 comparisons per
   *     heal row and cost more than everything else in this file put together.
   *
   *   - **A window is entered, not approached.** Rows ascend (the rule above), so the walk starts at the first row that can be in
   *     range and ends at the last span — which is why `IHealFactTable.AscendingTime` exists: a store that cannot promise the order
   *     says so and pays the full walk, because a halving over unordered rows returns a window missing rows while looking fine.
   *
   * What this deliberately does NOT do is attribute a heal to a fight. Heals ride the same sequence as damage but
   * carry no fight id (they open no encounter), and the healing board does not slice by fight — it slices by time,
   * which is why `range` here is the selection's own AllRanges window: one click, one clock on every board.
   */
  internal static class HealSummarySource
  {
    /// <summary>
    /// Every heal whose timestamp falls inside <paramref name="range"/>. A null range means the whole capture; a
    /// range with no segments means nothing, which is what an empty selection's window is and keeps "empty means
    /// empty" true at the seam rather than quietly falling back to the record store.
    /// </summary>
    internal static List<(double, HealRecord)> Materialize(HealFactTable heals, TimeRange range)
    {
      var records = MaterializeInArrivalOrder(heals, range);
      // The builder carries a time cursor. Only an unordered capture needs a stable sort; ties keep arrival order.
      return heals is { AscendingTime: false } ? records.OrderBy(static row => row.Item1).ToList() : records;
    }

    private static List<(double, HealRecord)> MaterializeInArrivalOrder(HealFactTable heals, TimeRange range)
    {
      List<(double, HealRecord)> records = [];
      if (heals is null) return records;

      var facts = heals.Heals;
      if (facts.Length == 0) return records;

      /*
       * Two different no's, both load-bearing (see the null/empty law above): no window object means "this capture", a window
       * with no spans means "the selection had no seconds". Neither may fall through to the other.
       */
      if (range is null)
      {
        for (var i = 0; i < facts.Length; i++)
        {
          ref readonly var heal = ref facts[i];
          records.Add(((double)heal.TimeS, RecordFrom(heal, heals)));
        }

        return records;
      }

      var segments = range.TimeSegments;
      if (segments is null || segments.Count == 0) return records;

      /*
       * Both sides of this join ascend — rows by ingest sequence, spans by TimeRange's own sorted-and-disjoint law — so the
       * window is WALKED rather than tested: start at the first row that can be inside (a halving on the clock), stop at the
       * last span's end, and carry a cursor forward through the spans. O(rows in range + spans) with no per-row search, and
       * the answer is the same list in the same order because nothing here reorders anything.
       *
       * Measured on Incogitable (420k heal rows against a 2,653-span whole-capture window, four cores): this call went
       * **2,159 ms -> 27 ms**, and on the 2.67M-row farm night 528 -> 253 ms. The old shape asked "is this second in one of N
       * spans?" by scanning from span zero, which costs about N/2 comparisons per row — on a capture with thousands of
       * sections, that scan was the entire cost of opening the healing board, and it repeated on every announce of a held
       * selection. The floor beside it is 34 ms (build every record, filter nothing), which is what says the walk itself was
       * never the problem.
       */
      if (heals.AscendingTime)
      {
        var lastEnd = segments[segments.Count - 1].EndTime;
        var span = 0;

        for (var i = heals.FirstIndexAtOrAfter(segments[0].BeginTime); i < facts.Length; i++)
        {
          ref readonly var heal = ref facts[i];
          var time = (double)heal.TimeS;
          if (time > lastEnd) break;

          // Monotone: a row newer than this span is newer than every span before it, and `time <= lastEnd` guarantees a span left that reaches.
          while (segments[span].EndTime < time) span++;

          if (time >= segments[span].BeginTime) records.Add((time, RecordFrom(heal, heals)));
        }

        return records;
      }

      /*
       * A store that is not ordered says so, and the fast path is gone: full walk, halved span test. Slower by the rows before
       * the window, correct by construction — which is the trade the `CastHistory.AscendingTime` law already makes for casts.
       */
      for (var i = 0; i < facts.Length; i++)
      {
        ref readonly var heal = ref facts[i];
        var time = (double)heal.TimeS;
        if (range.Contains(time)) records.Add((time, RecordFrom(heal, heals)));
      }

      return records;
    }

    private static HealRecord RecordFrom(HealFact heal, HealFactTable table) => new()
    {
      Healer = table.NameOf(heal.HealerIdx),
      Healed = table.NameOf(heal.HealedIdx),
      Total = heal.Total,
      OverTotal = heal.OverTotal,

      // One of two words (Direct Heal / HoT Tick), read back as the same interned literal the parser used, so the
      // builder's Labels comparisons keep working on a derived record.
      Type = LabelTypes.LabelOf(heal.TypeId),
      SubType = table.SpellOf(heal.SubIdx) ?? Labels.SelfHeal,

      // -1 when the line carried no modifier text, and that is what makes HealingValidator's filters (AoE,
      // swarm pets) exclude the same heals here that they exclude on the legacy board.
      ModifiersMask = heal.ModMask,
    };
  }
}
