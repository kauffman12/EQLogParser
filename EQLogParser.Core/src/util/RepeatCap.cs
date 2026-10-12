#nullable enable annotations

using System.Collections.Concurrent;

namespace EQLogParser;

/*
 * A voice-cap for a message whose trigger can fire per log line, per record or per tick.
 *
 * Why this exists: an Error in a parser's `catch` is a true statement about a defect and exactly the wrong shape for a night of it. One
 * unexpected line shape throws on EVERY matching line of a capture, so the "something broke" line becomes a stack trace per occurrence —
 * tens of thousands of copies of one sentence — and the operator's log stops being readable for anything else. Same class of defect as the
 * per-build overlap warning (docs/DesignNotes → "Name every door"), and the same cure applied one step earlier: the FIRST sighting speaks,
 * with whatever context makes the bug fixable (the offending line, the ordinal, the trigger name), and everything behind it is counted rather
 * than printed.
 *
 * Two rules, both deliberate:
 *
 *   1. **The cap never re-opens on a timer.** Overlapping builds have a quiet moment to speak at, so they coalesce per episode; a parse fault
 *      has no such moment during a raid, so a cooldown would still print one line per interval for the whole night — a metric wearing an alarm
 *      with a stopwatch attached. One line, then silence, and the count stays available (`SightingsOf`, `Silenced`).
 *   2. **Silence is accounted for, not invisible.** A caller that has a natural boundary — the end of a load, a clear, a report — can name the
 *      total there; nothing in here raises anything by itself. A capped key must therefore carry everything the reader needs ONCE, which is why
 *      the call sites gained the line text and the ordinal instead of `Log.Error(e)` alone.
 *
 * Two siblings, deliberately not merged: `TriggerOverlayManager.ShouldWarnNow` is the COOLDOWN form (keyed by trigger and channel, 300 s), for
 * "dropped text while no overlay is open" — a steady state an operator can be inside for minutes and may fix mid-session, so it re-speaks. A
 * parser fault is not a state anybody can watch being fixed; it is one bug, said once. And `DamageFactTable.MissedRow` already does the one-shot
 * per TABLE INSTANCE with its own exchanged flag, which is right there (each capture gets its own report) where these parser keys are per process.
 */
internal static class RepeatCap
{
  private static readonly ConcurrentDictionary<string, int> _sightings = new();

  /// <summary>
  /// True only the FIRST time `key` is seen; every later sighting of the same key returns false and only raises the count. Key it on the
  /// failure rather than the occurrence — a parser site plus an exception type, a table name — so one bug costs one line and two different
  /// bugs still get two.
  /// </summary>
  internal static bool SpeaksOnce(string key) => _sightings.AddOrUpdate(key, 1, static (_, seen) => seen + 1) == 1;

  /// <summary>How many times that key has happened, first sighting included (0 before it is ever asked about).</summary>
  internal static int SightingsOf(string key) => _sightings.TryGetValue(key, out var seen) ? seen : 0;

  /// <summary>Sightings hidden behind their own first line: everything counted beyond the speaking one.</summary>
  internal static int Silenced
  {
    get
    {
      var silenced = 0;
      foreach (var seen in _sightings.Values) silenced += seen - 1;
      return silenced;
    }
  }

  /// <summary>Forget every key, so the next sighting of each speaks again (tests; a fresh session may use it too).</summary>
  internal static void Reset() => _sightings.Clear();

  /// <summary>
  /// One printable line out of arbitrary log-file text: newlines folded, length cut. A capped message carries its evidence ONCE, so the
  /// offending text has to fit on the line that survives — a stack trace alone never says which shape threw.
  /// </summary>
  internal static string Clip(string? text, int max = 240)
  {
    if (string.IsNullOrEmpty(text)) return "<empty>";

    var clipped = text.Length <= max ? text : text[..max] + "…";
    return clipped.Contains('\n') || clipped.Contains('\r')
      ? clipped.Replace('\r', ' ').Replace('\n', ' ')
      : clipped;
  }
}
