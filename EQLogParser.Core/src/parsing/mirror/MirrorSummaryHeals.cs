using System.Collections.Generic;

namespace EQLogParser.Mirror
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
   *     capture's shared sequence (ingest order), and filtering preserves that order — which is why nothing sorts
   *     here. A sort would be the fix if a chunked spill ever handed back runs out of order (D2), not this.
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
   * What this deliberately does NOT do is attribute a heal to a fight. Heals ride the same sequence as damage but
   * carry no fight id (they open no encounter), and the healing board does not slice by fight — it slices by time,
   * which is why `range` here is the selection's own AllRanges window: one click, one clock on every board.
   */
  internal static class MirrorSummaryHeals
  {
    /// <summary>
    /// Every heal whose timestamp falls inside <paramref name="range"/>. A null range means the whole capture; a
    /// range with no segments means nothing, which is what an empty selection's window is and keeps "empty means
    /// empty" true at the seam rather than quietly falling back to the record store.
    /// </summary>
    internal static List<(double, HealRecord)> Materialize(HealFactTable heals, TimeRange range)
    {
      List<(double, HealRecord)> records = [];
      if (heals is null) return records;

      var segments = range?.TimeSegments;
      var facts = heals.Heals;

      for (var i = 0; i < facts.Length; i++)
      {
        ref readonly var heal = ref facts[i];
        var time = (double)heal.TimeS;

        if (range is not null && !Inside(segments, time)) continue;

        records.Add((time, RecordFrom(heal, heals)));
      }

      return records;
    }

    // Inclusive on both ends, which is the same test the builder applies to its own window.
    private static bool Inside(List<TimeSegment> segments, double time)
    {
      if (segments is null) return false;

      for (var i = 0; i < segments.Count; i++)
      {
        if (time >= segments[i].BeginTime && time <= segments[i].EndTime) return true;
      }

      return false;
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
