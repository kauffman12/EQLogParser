using System.Collections.Generic;

namespace EQLogParser
{
  /*
   * Where a healing surface gets its records: the open capture's heal rows, not a second copy of them.
   *
   * The parse used to hand every HealRecord it created to RecordsStore as well as to the fact table. The heap
   * snapshot said what that cost: 766,713 HealRecord objects at 29.25 MB retained (40 bytes apiece plus the two name
   * strings each row also owns), on a capture whose heal facts — the same information in 32-byte rows over a shared
   * name pool — were already sitting in the process. It was not a cache with a purpose either: only two surfaces ever
   * read that list, and one of them (HealingStatsBuilder's fallback) is a whole-capture read the derived door had
   * already replaced with a materialized span. The ablation run put this at exactly the 29 MB it removed, which is
   * why deleting a store beats trimming one (docs/DesignNotes.md → "Where a large capture's bytes actually are").
   *
   * So the heal fact table IS the record list, and `HealSummarySource` is the one place that turns rows back into
   * records — the same code the derived healing board uses, which is what keeps the two doors from drifting. The
   * records are built when a board asks (a window's worth for a death click, one list per healing rebuild), instead
   * of being alive all night for whoever might ask.
   *
   * Same seam shape as IdentityLookup.LiveVerdict: Core owns the question, the session owner wires the answer in its
   * Start() and takes it down again in Dispose() — only if it is still its own, because a new capture may already be
   * wired by the time an old engine is torn down. Null means "no session", and every reader here answers empty,
   * which matches what the closed-log state was when LifecycleManager cleared the store beside the session.
   */
  internal static class HealRecordSource
  {
    /// <summary>The open capture's heal rows. Wired by its owner, null with no session.</summary>
    internal static HealFactTable Current;

    /// <summary>Every heal of the open capture, time ascending — the order HealingStatsBuilder windows itself against.</summary>
    internal static List<(double, HealRecord)> All() => HealSummarySource.Materialize(Current, null);

    /// <summary>The heals inside one window, inclusive on both ends, ascending like the whole list.</summary>
    internal static List<(double, HealRecord)> During(double beginTime, double endTime)
    {
      if (Current is null) return [];

      var range = new TimeRange();
      range.Add(new TimeSegment(beginTime, endTime));
      return HealSummarySource.Materialize(Current, range);
    }
  }
}
