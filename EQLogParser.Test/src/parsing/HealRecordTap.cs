using EQLogParser;

namespace EQLogParser;

/*
 * "What did the heal parser produce?" — asked through the event the parser already raises, because the answer is no
 * longer kept in a store.
 *
 * Tests used to read RecordsStore.GetAllHeals() for this, which was fine while the parse really did register every
 * heal as a live object. It does not any more: the capture's heal rows are the record list (Core → HealRecordSource,
 * measured at 766,713 HealRecord objects / 29.25 MB of retained heap on one capture for two readers), and a test that
 * wants the parser's own output should look at what the parser says rather than at a copy somebody else keeps.
 *
 * Attached for the whole run in AssemblyLifecycle rather than per test class: a class that forgot to attach would
 * assert against an empty list and PASS, which is the one failure mode worse than a red run. Isolation is per test by
 * Clear(), called wherever these classes already call RecordsStore.Instance.Clear().
 *
 * What this deliberately does NOT do is stand in for the capture: HealFactCaptureTest compares this tap against
 * HealFactTable rows, and that comparison is the parity law ("every heal the parser accepted is a fact"). A list of
 * events cannot replace rows — it only proves what was said.
 */
internal static class HealTap
{
  private static readonly List<(double, HealRecord)> Seen = [];
  private static bool _attached;

  internal static void Attach()
  {
    if (_attached) return;
    _attached = true;
    HealingLineParser.EventsHealProcessed += e =>
    {
      lock (Seen) Seen.Add((e.BeginTime, e.Record));
    };
  }

  internal static void Clear()
  {
    lock (Seen) Seen.Clear();
  }

  /// <summary>Snapshot of what has been parsed since the last Clear, in parse order — the order the store handed out.</summary>
  internal static List<(double, HealRecord)> All()
  {
    lock (Seen) return [.. Seen];
  }
}
