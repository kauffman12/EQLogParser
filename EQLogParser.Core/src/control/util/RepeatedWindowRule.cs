using System;

namespace EQLogParser;

/*
 * One step of a repeat window: given what the counter holds and where its deadline is anchored, decide what THIS fire makes of it.
 *
 * Two anchor policies exist, and the whole difference between them is one assignment:
 *
 *   fixed   — the anchor stays where the epoch started, so counting restarts every `resetAfterSeconds` no matter how often the
 *             trigger fires. This is how EQLP has always counted, so it stays the default: an existing trigger file means the same
 *             thing after this rule exists as it meant before.
 *   sliding — every fire pushes the deadline out another `resetAfterSeconds`. GINA did exactly this (`TriggerFilter.IsMatch` ends with
 *             `LastMatched = DateTime.Now`, unconditionally), and NAG's own importer documents its counter duration as an "idle-reset
 *             window". Continuous traffic counts upward without bound, and only a gap LONGER than the window starts epoch 2.
 *
 * Both are lazy: nothing zeros a counter in the background; a fire that arrives after the deadline simply reads 1. So the two policies
 * look identical between fires and differ only in WHICH timestamp the window is pinned to — which is why a test has to assert on which
 * fire starts the next epoch, never on "the count incremented".
 *
 * The comparison stays `elapsed > resetAfterSeconds` with elapsed truncated to whole seconds by tick division, exactly as the shipped
 * code computed it. GINA compared fractional seconds; at the windows people actually set (0.75 s defaults, minutes for Exp tracking)
 * the difference is not observable, and moving boundary times nobody complained about is not part of this change.
 */
internal static class RepeatedWindowRule
{
  /// <summary>What a window holds after a fire, and where its deadline now sits.</summary>
  internal readonly record struct Result(long Count, long AnchorTicks);

  internal static Result Next(long previousCount, long previousAnchorTicks, long fireAtTicks, double resetAfterSeconds, bool slides)
  {
    var elapsedSeconds = (fireAtTicks - previousAnchorTicks) / TimeSpan.TicksPerSecond;

    if (elapsedSeconds > resetAfterSeconds)
    {
      return new Result(1, fireAtTicks);
    }

    // Sliding pays for its "resets on idle" answer by moving the anchor on EVERY fire; fixed leaves it alone, which is what pins
    // a long grind to restarting once per window measured from the first match.
    return new Result(previousCount + 1, slides ? fireAtTicks : previousAnchorTicks);
  }
}
