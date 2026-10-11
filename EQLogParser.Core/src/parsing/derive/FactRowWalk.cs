namespace EQLogParser;

/*
 * Slice-walk doors for the stages that reach rows through IFactTable / IHealFactTable.
 *
 * ReadOnlySpan cannot be an interface member — and the interfaces stay implementation-agnostic on purpose, because a spooled or
 * chunked-on-disk store has to answer the same questions honestly (see the storage contract notes beside HealFactTable). So this is the
 * ONE place that names the production in-memory chunked store: a cast that throws loudly the day a second implementation arrives, rather
 * than a silent copy or a per-stage downcast someone forgets. Everything hot walks slices here instead of indexing one row at a time —
 * the ordinal reader (DamageFactReader) costs a bounds check plus the shift/mask split per row, which measured ~50 ms on a whole-capture
 * classification pass and is pure addressing.
 */
internal static class FactRowWalk
{
  /// <summary>Damage rows from an ordinal to the end of the capture, as contiguous chunk slices.</summary>
  internal static DamageFactRange Damage(IFactTable facts, int from) => ((DamageFactTable)facts).FactsIn(from, facts.FactCount);

  /// <summary>Heal rows from an ordinal to the end of the capture, as contiguous chunk slices.</summary>
  internal static HealFactRange Healing(IHealFactTable heals, int from) => ((HealFactTable)heals).HealsIn(from, heals.HealCount);
}
