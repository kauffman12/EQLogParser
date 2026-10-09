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
  public uint Max { get; set; }
  public uint MaxPotentialHit { get; set; }
  public uint Min { get; set; }
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
  private List<long>? _critRaw;
  private List<long>? _nonCritRaw;
  private Dictionary<long, int>? _critFreq;
  private Dictionary<long, int>? _nonCritFreq;

  public Dictionary<long, int> CritFreqValues => FreqOf(ref _critFreq, ref _critRaw);

  public Dictionary<long, int> NonCritFreqValues => FreqOf(ref _nonCritFreq, ref _nonCritRaw);

  /* One damaging record's amount, kept for a histogram nobody has asked for yet. Zero-damage outcomes never reach this (callers check). */
  internal void RecordHitTotal(long total, bool isCrit)
  {
    if (isCrit)
    {
      (_critRaw ??= []).Add(total);
    }
    else
    {
      (_nonCritRaw ??= []).Add(total);
    }
  }

  /* How many damaging amounts are waiting to be counted (0 once a histogram exists). The cost probe reads this instead of the
   * dictionaries, because touching a dictionary is what materializes it: a probe that asks the question eagerly measures nothing. */
  internal int RawHitTotals => (_critRaw?.Count ?? 0) + (_nonCritRaw?.Count ?? 0);

  /* True while a row still owes its histogram to the raw amounts — the state a build leaves every row in. */
  internal bool HoldsRawHitTotals => _critRaw is not null || _nonCritRaw is not null;

  /* True once a histogram exists: lets a test see that reading built it and that building released the raw list. */
  internal bool HitFreqMaterialized => _critFreq is not null || _nonCritFreq is not null;

  private static Dictionary<long, int> FreqOf(ref Dictionary<long, int>? cache, ref List<long>? raw)
  {
    if (cache is not null)
      return cache;

    var built = new Dictionary<long, int>();
    if (raw is not null)
    {
      foreach (var amount in raw)
      {
        built.TryGetValue(amount, out var had);
        built[amount] = had + 1;
      }

      // The dictionary holds every count the list did; keeping both would cost more than the eager version this replaced.
      raw = null;
    }

    // Assign once, then answer from the field: two racing readers may each build, but nobody ever sees a dictionary swapped out from under them.
    cache ??= built;
    return cache;
  }
}
