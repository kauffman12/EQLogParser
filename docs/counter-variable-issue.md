# `{counter}` variable: GINA compatibility issue

Status: open — semantics decision needed before changing anything.

## User report

A long-time GINA user ported a countdown trigger for tracking Exp messages:

| Field | Value |
|---|---|
| Match pattern | `^(You gain experience\|You gain party experience)` |
| Repeated Reset Time (s) | 3600 |
| Alternate Timer Name | `-- Exp Timer [{counter}]` |
| Duration | `4:25` (Befallen respawn, EQLegends) |

Expected (how GINA behaved): each Exp message should extend the reset window —
the counter keeps incrementing for as long as grinding continues, and only
restarts at 1 when a match arrives **an hour after the previous match**.

Observed in EQLP: the counter increments happily, then — once per hour,
**measured from the first fire** — jumps back to 1 even though matches have
been arriving continuously. The user's words: *"it mainly just [is] different
from how Gina worked and is therefore unexpected."*

## Verdict

The user is correct on both counts. GINA used a **sliding window anchored at
the last match**; EQLP uses a **fixed window anchored at the first match of
the current epoch**. The difference is one missing assignment in EQLP.

Both engines do the reset *lazily* — nothing auto-zeros in the background;
the counter just reads 1 on the first match that falls after the deadline.
So both look identical in steady state and differ only in which timestamp
the window is pinned to.

## GINA behavior (verified against decompiled code)

Reference copy: `local/GINA/decompiled/` (decompiled with ilspycmd from the
ClickOnce cache under `local/GINA/PGNW4D82.X0Q/…`).

`GimaSoft.Business.GINA/GimaSoft.Business.GINA/TriggerFilter.cs`:

```csharp
public int Matches { get; set; }   // protected by _MatchesLockObject

public bool IsMatch(string str)
{
    bool flag = Matcher.IsMatch(str);
    if (flag)
        lock (_MatchesLockObject)
        {
            if (LastMatched.HasValue && Trigger.UseCounterResetTimer &&
                (DateTime.Now - LastMatched.Value).TotalSeconds > Trigger.CounterResetDuration)
                Matches = 1;      // gap since LAST match exceeded window → restart at 1
            else
                Matches++;
            LastMatched = DateTime.Now;   // ← runs on EVERY fire, unconditionally
        }
    return flag;
}
```

Because `LastMatched` is refreshed on every match, each Exp message pushes the
reset deadline back another hour. With continuous grinding the counter grows
without bound; it only restarts at 1 when a match arrives more than
`CounterResetDuration` seconds after the *previous* match. The substitution
itself was a final `Regex.Replace(text, "\\{COUNTER\\}", CounterInstance.ToString())`
in `TriggerMatchedEventArgs.ResolveText`, with `CounterInstance` snapshotted
from `filter.Matches` at match time.

Notes on GINA's exact semantics:

- Count starts at **1**, first fire after any reset shows 1.
- Comparison is strictly greater (`> duration`).
- Counter scope: one counter per (character, trigger) pair — in-memory only,
  no persistence across restarts. Manual resets existed too (in-game chat
  command `{GINA:resetcounts}`, UI "resetcounters"), irrelevant to this issue.

## EQLP behavior today

`EQLogParser/src/control/processors/TriggerProcessor.cs`, `UpdateRepeatedTimes`
(~line 1753):

```csharp
var diff = (beginTicks - repeatedData.CountTicks) / TimeSpan.TicksPerSecond;
if (diff > wrapper.TriggerData.RepeatedResetTime)
{
    repeatedData.Count = 1;
    repeatedData.CountTicks = beginTicks;   // anchor moves ONLY here
}
else
{
    repeatedData.Count++;                   // ← anchor NOT refreshed
}
```

`CountTicks` (the window anchor) is written in exactly two places: entry
creation and the reset branch. The increment branch never touches it, so the
window is pinned to the **first fire of the current epoch**.

Trace of the reported scenario (continuous grinding, 3600 s window):

| Time | Event |
|---|---|
| T₀ | first Exp line → Count = 1, anchor = T₀ |
| T₀ + 5 min … T₀ + ~59 min | each Exp line: `diff < 3600` → Count++ |
| T₀ + 60 min (first match after deadline) | `diff > 3600` → Count = 1, anchor = T₀ + 60 min |
| … | increments again until T₀ + 120 min, then resets again — once per hour forever |

That matches the user's observation exactly.

Minor non-issue: GINA compared in fractional seconds (`TotalSeconds`), EQLP
truncates to whole seconds via integer tick division. Irrelevant at a
3600 s window; noted only so it isn't re-discovered later.

## Candidate fix and scope caveat

The one-line GINA-compatible change is refreshing the anchor in the
increment branch:

```csharp
else
{
    repeatedData.Count++;
    repeatedData.CountTicks = beginTicks;   // sliding window, like GINA
}
```

**Caveat:** `UpdateRepeatedTimes` is shared infrastructure, not
counter-specific. The same reset window currently drives all of these:

- `{counter}` — `_counterTimes`, key `"trigger-count"` (~line 731)
- repeated timer names — `_repeatedTimerTimes` (~line 750)
- text `{repeated}` counts — `_repeatedTextTimes` (~line 798)
- TTS repeated-speak counts — `_repeatedSpeakTimes` (~line 1244)
- end-early stop condition — `EndEarlyRepeatedCount` reads
  `GetRepeatedCount(_counterTimes, …, "trigger-count")` (~line 570), i.e. it
  inherits whatever anchor semantics the counter has

So sliding the anchor to GINA style changes **all** of these at once. If
"reset N seconds after the *first* fire" was an intentional choice for the
repeated-timer/text features, `{counter}` needs its own path instead of
changing the shared method.

No existing test pins the current anchor semantics — the only tests touching
`RepeatedResetTime` are import/copy tests (`NagUtilTriggerImportTest`,
`TriggerUtilCopyTest`) — so nothing mechanically breaks either way; this is a
user-visible behavior decision:

1. **Slide everything** (full GINA compatibility, simplest), or
2. **Split** `{counter}` onto sliding semantics and keep the rest fixed, or
3. **Keep as-is** and document the difference.

A test for whichever choice wins should assert on *which fire starts epoch N+1*
(feed a stream of matches with known gaps: continuous grind must not reset;
a gap longer than the window must), since both variants pass any test that
only checks "count incremented per match."
