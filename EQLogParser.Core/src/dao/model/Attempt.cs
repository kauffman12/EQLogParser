/*
 * Annotations only (the project builds with Nullable=disable): the hit-frequency fields are deliberately ABSENT until something needs them,
 * and that has to be sayable without a warning per line.
 */
#nullable enable annotations

namespace EQLogParser;

internal class Attempt
{
  public uint Absorbs { get; set; }
  public uint Blocks { get; set; }
  public uint Dodges { get; set; }
  public uint Misses { get; set; }
  public uint Parries { get; set; }
  public uint Invulnerable { get; set; }
  /* Amounts, not counts: a hit's biggest, smallest and widest-possible number, on the same 48-bit domain the record
     carries. Sum lanes here were already long; these three are one hit each and follow the record they came from. */
  public long Max { get; set; }
  public long MaxPotentialHit { get; set; }
  public long Min { get; set; }
  public uint BaneHits { get; set; }
  public uint Hits { get; set; }
  public uint AssHits { get; set; }
  public uint CritHits { get; set; }
  public uint DoubleBowHits { get; set; }
  public uint FlurryHits { get; set; }
  public uint BowHits { get; set; }
  public uint HeadHits { get; set; }
  public uint FinishingHits { get; set; }
  public uint LuckyHits { get; set; }
  public uint MeleeAttempts { get; set; }
  public uint MeleeHits { get; set; }
  public uint NonTwincastCritHits { get; set; }
  public uint NonTwincastLuckyHits { get; set; }
  public uint SpellHits { get; set; }
  public uint StrikethroughHits { get; set; }
  public uint RampageHits { get; set; }
  public uint RegularMeleeHits { get; set; }
  public uint RiposteHits { get; set; }
  public uint SlayHits { get; set; }
  public uint TwincastHits { get; set; }
  public long Total { get; set; }
  public long TotalAss { get; set; }
  public long TotalCrit { get; set; }
  public long TotalFinishing { get; set; }
  public long TotalHead { get; set; }
  public long TotalLucky { get; set; }
  public long TotalNonTwincast { get; set; }
  public long TotalNonTwincastCrit { get; set; }
  public long TotalNonTwincastLucky { get; set; }
  public long TotalRiposte { get; set; }
  public long TotalSlay { get; set; }
  /*
   * The damage-value distribution behind the Hit Frequency chart: how often this sub-stat landed each amount, split crit from non-crit.
   *
   * Built LAZILY: the walk keeps only the raw amounts until somebody asks, because of what collecting them eagerly measured on the operator's own
   * capture (docs/DesignNotes.md → "What a chart pass is actually made of"). Filling one dictionary entry per damaging record cost **327 ms
   * (13 %) of a whole-capture damage build**, **144 MB of the 202 MB that build allocated**, and left **1,879,130 entries (~72 MB) retained** —
   * read by exactly one window (`HitFreqChart`) that most sessions never open. A dictionary keyed by raw damage amount is not a histogram either:
   * hits are near-unique numbers, so 2,478 rows averaged ~758 keys and every record paid a hash insert plus its share of the rehashes.
   *
   * What the hot loop pays now is a list append (~2 ns rather than ~70). The raw amounts cost about half what the dictionaries did, and they are
   * dropped as soon as a row's histogram exists — which happens for the one row a chart is open on, not for every row of the raid.
   *
   * Two laws, both because the reader iterates and then indexes:
   *   - **assign once.** A materialized dictionary is never replaced, so `Keys` on one line and `[key]` on the next cannot disagree;
   *   - **the same distribution, exactly.** Bucketing or sampling would have been cheaper again and was refused: this chart's point is individual
   *     hit amounts. Laziness buys the time and the memory; the answer must not move by one count.
   */
  /* The amounts are kept in TWO lanes so the wide domain costs nothing on ordinary data (docs/DesignNotes.md →
     "Amount widths measured across 44 million hits"): everything a real log printed fits 32 bits, so it rides a List<uint>
     exactly as before — 4 bytes per retained hit, which is what made the lazy histogram cheaper than the dictionary it
     replaced. Only an amount above 32 bits pays a dictionary, and those are rare enough that the lane is usually null.
     Both lanes merge into the same counts at materialization, so the chart answers exactly as it did. */
  private List<uint>? _critRaw;
  private List<uint>? _nonCritRaw;
  private Dictionary<long, int>? _critWide;
  private Dictionary<long, int>? _nonCritWide;
  private Dictionary<long, int>? _critFreq;
  private Dictionary<long, int>? _nonCritFreq;

  public Dictionary<long, int> CritFreqValues => FreqOf(ref _critFreq, ref _critRaw, ref _critWide);

  public Dictionary<long, int> NonCritFreqValues => FreqOf(ref _nonCritFreq, ref _nonCritRaw, ref _nonCritWide);

  /* One damaging record's amount, kept for a histogram nobody has asked for yet. Zero-damage outcomes never reach this (callers check). */
  internal void RecordHitTotal(long total, bool isCrit)
  {
    if (isCrit)
    {
      AddAmount(total, ref _critRaw, ref _critWide);
    }
    else
    {
      AddAmount(total, ref _nonCritRaw, ref _nonCritWide);
    }
  }

  // The split itself: the narrow list is the one that keeps the retained cost at 4 bytes for everything a real log prints.
  private static void AddAmount(long total, ref List<uint>? narrow, ref Dictionary<long, int>? wide)
  {
    if (total <= uint.MaxValue)
    {
      (narrow ??= []).Add((uint)total);
      return;
    }

    wide ??= [];
    wide.TryGetValue(total, out var had);
    wide[total] = had + 1;
  }

  /* How many damaging amounts are waiting to be counted (0 once a histogram exists). The cost probe reads this instead of the
   * dictionaries, because touching a dictionary is what materializes it: a probe that asks the question eagerly measures nothing. */
  internal int RawHitTotals => (_critRaw?.Count ?? 0) + (_nonCritRaw?.Count ?? 0) + WideCount(_critWide) + WideCount(_nonCritWide);

  private static int WideCount(Dictionary<long, int>? wide)
  {
    if (wide is null) return 0;
    var n = 0;
    foreach (var kv in wide) n += kv.Value;
    return n;
  }

  /* True while a row still owes its histogram to the raw amounts — the state a build leaves every row in. */
  internal bool HoldsRawHitTotals => _critRaw is not null || _nonCritRaw is not null || _critWide is not null || _nonCritWide is not null;

  /* True once a histogram exists: lets a test see that reading built it and that building released the raw list. */
  internal bool HitFreqMaterialized => _critFreq is not null || _nonCritFreq is not null;


  private static Dictionary<long, int> FreqOf(ref Dictionary<long, int>? cache, ref List<uint>? raw, ref Dictionary<long, int>? wide)
  {
    if (cache is not null)
      return cache;

    var built = new Dictionary<long, int>();
    if (raw is not null)
    {
      foreach (var amount in raw)
      {
        built.TryGetValue(amount, out var had);
        built[(long)amount] = had + 1;
      }

      // The dictionary holds every count the list did; keeping both would cost more than the eager version this replaced.
      raw = null;
    }

    if (wide is not null)
    {
      foreach (var kv in wide)
      {
        built.TryGetValue(kv.Key, out var had);
        built[kv.Key] = had + kv.Value;
      }

      wide = null;
    }

    // Assign once, then answer from the field: two racing readers may each build, but nobody ever sees a dictionary swapped out from under them.
    cache ??= built;
    return cache;
  }
}
