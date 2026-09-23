# Project Rules & Guidelines

You are an expert AI assistant tasked with maintaining this C#/WPF/.net 10.0 project.

## Core Principles
- **Follow Coding Standards** read and follow the standards under docs/CodingStandards.md
- **File structure**: Prefer small files and atomic commits.
- **Git**: Commit with detailed messages, but **never push to remote**. Pushing is the user's call.
- **Searching**: All files are under the current directoy. 
- **Do not** add heavy dependencies without explicit user approval.

## Testing Guidelines
- **Always** run `dotnet build` after completing work
- **FCT vocabulary is closed**: two region schemes (`Bands` = the panel's *fountain*, `ByType` = *split*) and four motion styles
  (`Freeze`, `Fountain`, `Spray`, `Arc`) plus `Straight`, which is arc's rail with the bend taken out. Halves, pulse, the cell grid and the
  stream were deleted on measurement, not parked: do not resurrect one because a comment mentions it. A new scheme or style arrives with a
  settings word, a panel entry, and tests — the shape names saved in `settings.txt` are exactly the words the dropdown shows (`arc`, `line`,
  `spray`, `freeze`; `Straight` excepted, saved as `line`). One dial rides inside `arc`: `FctArcBend` (`open`, `out`, `left`, `right`; absent =
  `open`, which is the pre-existing curve, not a fifth shape — see docs/DesignNotes.md). An explicit word makes the lane pay: `FctStage.ParkForBend` shifts a split column off the wall it leans at, from
  lane-level facts only (slot, lane rect, `FctLayout.RailDemands`, the bow share) — never from the arriving number, or a column goes out of line with itself.
  `open` parks nothing. **Assert a lean's depth, not its sign**: a bow clamped to `0.0` passes `IsTrue(bow <= 0)` and draws a straight scroll, which is
  how `left` and half of `out` shipped looking like nothing (docs/DesignNotes.md → "The lane has to pay for the lean").
- **A leftward lean turns its rows around**: `FctHitState.HangRight` makes the rail carry a value's LEFT edge, because a right-aligned block was standing in
  the hand its own bow needed. It is stamped at spawn (and in `FctStage.SpineFor`, for a pinned conveyor entry) and read by `BlockFromRail` (extents swap),
  `ArcedX` (rail→centre flips) and `DrawMark` (glyph outside the outer edge) — the canvas places every other glyph from the value's centre, so nothing else
  moves. Only a named word in split turns anything: **`open` and `line`/`Straight` draw the shipped right-alignment in every case**, and a test that sets
  `ArcBend` should keep asserting which hand the digits hang on (`ALeftwardLeanTurnsTheRowAround`). Related: `FitSource` cuts names against the room left
  **after** the lean spends it, and drops a name that would have stood in the curve — per-row clamping of a too-wide label was measured stealing 56 px of a
  153 px bend, differently for every row on the spine (docs/DesignNotes.md → "Turning the row around").
- **FCT engine tests**: the dials are process globals (`FctScale.Text/Crit/Time`, `FctLayout.LabelSide`, `FctLayout.ArcBend`). Test classes in
  `EQLogParser.Test/src/control/fct` reset them through `FctAmbient.Reset()` via `[TestInitialize]`, and both test assemblies declare
  `[assembly: DoNotParallelize]`. Anything new that touches that engine must keep both, or state from one test leaks into another
  (a leaked speed dial was measured moving a row's spine 74 px — a real assertion failing on a machine that ran the same code).
- **UI-thread instrumentation**: "the numbers froze for a second" is answered by `UiBeatMonitor` (app) over `PerfCounters`/`PerfGc`/
  `PerfJournal` (`EQLogParser.Core/src/perf`), and every instrumented span is listed in docs/DesignNotes.md → "Instrumenting the UI
  thread". A span takes its handle from one `Register` call in a field initializer (no name lookups, no locks in frame paths) and closes
  in a `finally`: a pass left marked running by an exception keeps naming itself in every later stall line. **None of it reaches the log unless asked**:
  `PerfJournal.Enabled` is the single gate on everything this writes and is off unless settings.txt says `PerfReport=True` (or `Debug`, which implies it) — and then
  `App` does not start the monitor at all, so normal use puts no perf lines in a player's log. Anything asserting journal behaviour sets that flag and restores it in
  a `finally`. `UiBeatMonitorTest` pumps a
  real dispatcher with short thresholds and relies on `[assembly: DoNotParallelize]` like the notes above, and the priorities it queues at
  are load-bearing — beats go out at `Render` (WPF's 7), so a test's pump check belongs between that and the work under test, never at
  `Normal` (9), or it wins the race against the late beat and the stall goes unmeasured. Numbers and reasoning: docs/DesignNotes.md.
- **`EQLogParser.Wpf.Test` runs MTA**: MSTest hands a test method an MTA thread, and WPF will not construct a `FrameworkElement` on one —
  its constructor asks that thread for its `InputManager` and throws before any of our code runs. So any test that news up a `UIElement`
  (`FctSkiaCanvas`, a window, a control) goes through `Sta.Run(...)` (`EQLogParser.Wpf.Test/src/Sta.cs`), which claims a thread as STA, runs
  the body there and rethrows at the call site so a failure still reads as a failed assertion. `Dispatcher.CurrentDispatcher` does not help;
  it hands out a dispatcher on any thread and fails in the same constructor. The lane guide test found this by failing three for three.
- **Namespaces**: the WPF app compiles every source into the flat `namespace EQLogParser` whatever folder it lives in, while
  `EQLogParser.Core` matches folders (`EQLogParser.perf`). Tests reach app internals through `using EQLogParser;` (`InternalsVisibleTo` is
  already granted to both test assemblies); declaring `EQLogParser.ui.perf` in a new app file breaks that.
- **Timer bars: the lifecycle rules live in Core.** A countdown's length comes from the config box or a `TS:` capture (`DateUtil.SimpleTimeToSeconds` answers in
  *uint* seconds), so it arrives unbounded; durations go through `TimerLifecycle.ClampDuration` and every stamp/delay through that class, because an unclamped value
  saturates `Task.Delay`'s int-ms cast (measured: a 24.8-day sleep for the task whose only job is removing the bar) or wraps `EndTicks` into the past. A row is also
  refused at the door when it has no future — cancelled, lengthless (`FormatTicks` prints `00:00` for zero *and* negatives, which in show-reset mode is a greyed bar
  forever), or already past end + grace — because Start/Stop are fire-and-forget on the same semaphore and nothing keeps Add before Stop. The overlay reaps what its
  owner forgot (`ReapForgottenRows`, 2 s grace) and the render loop's `_isRendering` is cleared in a `finally`, since a latched flag freezes the window permanently.
  `EQLogParser.Test/src/util/TimerLifecycleTest.cs` sweeps the real parser's output range and the 720 service orders of a three-message burst — keep that green
  rather than moving the math back into `TimerOverlayWindow`. Reasoning: docs/DesignNotes.md → "Timer overlays: how a bar gets taken away".
- **Record sharing starts on the third sighting**: `FightManager._damageCache` and `HealingLineParser._healCache` are `RepeatStore<T>`, whose first
  sighting of a value deliberately takes no entry (84% of distinct records are never restated). The heal store and its `RepeatFilter` are **static**
  process state, so a test asserting that two parsed heals share an instance has to call `HealingLineParser.ClearCaches()` in setup — otherwise it
  passes only from whichever position in the run order it happens to occupy (`LineParsersTest` is where that was found). Sharing is never a correctness
  mechanism: records are equal by value, and the filter is allowed to be wrong only toward "seen". Numbers and reasoning:
  docs/DesignNotes.md → "What a loaded raid costs in memory".
- **`EQLogParser.Wpf.Test`** is the Windows-only assembly (WPF and Skia surfaces, `EnableWindowsTargeting`): it builds everywhere but its
  tests need Windows to run, so `dotnet test` on the other assembly says nothing about it. Build it explicitly when touching app UI code.
- **Releases**: when touching `sign.cmd` or `EQLogParserInstall/*.iss`, read `docs/ReleaseChecklist.md`

## Post-Implementation Checklist
After completing work, verify:
- **Method visibility ordering**: public → internal → private (per CodingStandards.md)
- **Pattern matching**: use `is not T` / `is T var` instead of `as T` + null check (per CodingStandards.md)
- **Unused usings**: no leftover or unnecessary `using` statements

## Agent Behavior
- **Ask for Clarification**: If a feature requirement is ambiguous, ask before implementing.
- **Read the Syncfusion PDF files**: If there's a question about Syncfusion APIs look under syncfusion-wpf for info.
- **Never Use Claude/Anthropic Models**: Never use Claude or any other Anthropic model/provider for anything — no Claude Code, no Anthropic LLMs, no Anthropic VLMs.
- **Vision / Image Inspection**: Handle vision requests with `read` (native vision) and local tools only. Never call external or remote VLM services (e.g., `inspect_image`).

## Important: Never Touch
- `BackupUtil`
- `MaterialDarkCustom`
