# Project Rules & Guidelines

You are an expert AI assistant tasked with maintaining this C#/WPF/.net 10.0 project.

## Core Principles
- **Follow Coding Standards** read and follow the standards under docs/CodingStandards.md
- **File structure**: Prefer small files and atomic commits.
- **Git**: Commit with detailed messages, but **never push to remote**. Pushing is the user's call.
- **Docs: exactly three are maintained in git** — `docs/DesignNotes.md`, `docs/CodingStandards.md`, `docs/ReleaseChecklist.md` (the `.gitignore` whitelist). Everything else under `docs/` is a local working document for discussions (`combat-mirror-design.md`, `legacy-replacement-map.md`, `TtsPacks.md`, `NagFctReference.md`, `counter-variable-issue.md`, …): never `git add` one, and never assume such a reference resolves in a fresh clone — durable decisions belong in `DesignNotes.md`, which now carries the orientation chapter ("The parsing direction") and the deletion queue the working map holds.
- **Searching**: All files are under the current directoy. 
- **Do not** add heavy dependencies without explicit user approval.

## Build Environment (Linux, this machine)
- .NET 10 SDK (10.0.4xx) lives in `~/.dotnet`; prepend it on every shell: `export PATH="$HOME/.dotnet:$PATH"`. The system's dotnet 8 will not satisfy `global.json` (`"10.0"`, `rollForward: latestPatch`).
- Full solution on Linux: `dotnet build EQLogParser.sln -p:EnableWindowsTargeting=true` (flag lets the WPF projects cross-compile; no source changes needed).
- **Zero warnings is the bar, and it is counted, not hoped for**: `dotnet build EQLogParser.sln -p:EnableWindowsTargeting=true --no-incremental --nologo 2>&1 | grep -cE ": (warning|error) [A-Z]+[0-9]+"` prints **0** before a commit
  (`--no-incremental` is load-bearing: MSBuild reports diagnostics only for projects it recompiles, so counting after `dotnet test` prints 0 about
  assemblies it never rebuilt — that is how an MSTEST0017 in a new test file shipped past a "clean" local count; docs/CodingStandards.md → "Build Warnings") (match diagnostics, not words — MSBuild's summary always contains `0 Warning(s)`). Nine CS8632s shipped once because a file in Core (project `Nullable=disable`) wrote `string?` without opening `#nullable enable annotations`; the whole fleet is one pragma, and it is why each new warning dies in its own commit (docs/CodingStandards.md → "Build Warnings", "Nullable Reference Types"). Nothing in this repo suppresses a warning (`#pragma warning disable`, `<NoWarn>`) — fixing the cause is the rule.
- Tests: `dotnet test EQLogParser.Test/EQLogParser.Test.csproj` is the non-WPF suite (**1,729 passed / 10 env-gated skips**, plain `net10.0`, 2026-11 — the trim-moves-no-ordinal law, the once-per-doubling watermark and the ungrowable-empty-array floor are in that count). `EQLogParser.Wpf.Test` targets `net10.0-windows` and only
  runs on Windows; its total belongs to a Windows run rather than being quoted here (a `[TestMethod]` grep counts data-driven variants and is not an executed count) — add it to the headless number instead of trusting arithmetic. A Windows run that *loses* `Sta.Run` bodies rather than failing them is the failure mode to watch.
- **Real-log corpus layout (local/, gitignored)**: `local/logs/live/` holds live-format captures; `local/logs/emu/` holds EMU-server captures (THJ/TSS/Heroes Forge shapes) that need the app's `EnableEmuParsing` behaviour. The env-gated real-log tests run them via **`EQLP_EMU=1`**, which sets `AppSettings.IsEmuParsingEnabled` for the duration of a `PipelineHarness` run (restored after — the flag is process-global and live-format logs misparse with it on). Without it an EMU capture parses with DamageLineParser's live grammar and silently loses the `(Owner: X)` / `scores a critical hit! (N)` shapes, so a parity run over `emu/` without the flag measures nothing. Timestamps are the same `[DDD MMM dd HH:mm:ss yyyy]` shape in both directories.

## Testing Guidelines
- **Always** run `dotnet build` after completing work
- **FCT vocabulary is closed**: two region schemes (`Bands` = the panel's *fountain*, `ByType` = *split*) and four motion styles
  (`Freeze`, `Fountain`, `Spray`, `Arc`) plus `Straight`, which is arc's rail with the bend taken out. Halves, pulse, the cell grid and the
  stream were deleted on measurement, not parked: do not resurrect one because a comment mentions it. A new scheme or style arrives with a
  settings word, a panel entry, and tests — the shape names saved in `settings.txt` are exactly the words the dropdown shows (`arc`, `line`,
  `spray`, `freeze`; `Straight` excepted, saved as `line`). One dial rides inside `arc`: `FctArcBend` has exactly **three** words — `out` (the default, both
  halves bow away from the overlay's middle), `left`, `right`; absent or junk = `out`, and so does the retired word `open`, which was deleted on measurement
  rather than parked: it read the lane, so the same column changed direction with window width (+99.5 at 900, -238.0 from 1440). A fourth word would arrive
  with a settings entry, a panel item and tests — `EveryLeanWordIsThreeWordsNoMore` refuses a smuggled one. An explicit word makes the lane pay:
  `FctStage.ParkForBend` shifts a split column off the wall it leans at, from lane-level facts only (slot, lane rect, `FctLayout.RailDemands` +
  `LabelDemands`, the bow share) — **never** from the arriving number's `ValueWidth`/`IconAllowance`, or a crit parks further off the wall than the parry
  below it (that is what `ACritAndItsNeighbourShareOneSpineAndOneBend` holds; it fired the day `out` became default and the park stopped being dead code).
  **Assert a lean's depth, not its sign**: a bow clamped to `0.0` passes `IsTrue(bow <= 0)` and draws a straight scroll, which is how `left` and half of
  `out` shipped looking like nothing (docs/DesignNotes.md → "The lane has to pay for the lean").
- **A leftward lean turns its rows around**: `FctHitState.HangRight` makes the rail carry a value's LEFT edge, because a right-aligned block was standing in
  the hand its own bow needed. It is stamped at spawn (and in `FctStage.SpineFor`, for a pinned conveyor entry) and read by `BlockFromRail` (extents swap),
  `ArcedX` (rail→centre flips) and `DrawMark` (glyph outside the outer edge) — the canvas places every other glyph from the value's centre, so nothing else
  moves. Only an arc in split turns anything: **`line`/`Straight` draws the shipped right-alignment under every word**, and a test that sets `ArcBend` should
  keep asserting which hand the digits hang on (`ALeftwardLeanTurnsTheRowAround`). Two related laws, both because per-row clamping was measured stealing 56 px
  of a 153 px bend differently for every row on one spine: `FctLayout.LabelDemands` reserves a seated label's **floor** on its hand *before any name exists*
  (so a lean never eats a name whole — dropping names instead measured 0 kept of 234 rows at 1280 with `out` + right labels, now 234/234 with the full curve),
  and `FitSource` cuts names against the room left **after** the lean spends it (docs/DesignNotes.md → "Turning the row around", "A name and a lean").
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
- **A board-row read does not allocate, and what the registry costs is a readable number** (2026-11): three names ride the
  heartbeat — `reg.class` (one `PlayerRegistry.GetPlayerClass`, the lookup every stats builder pays per player row for its
  Class column), `reg.identity` (one walk of the override → this capture → memory chain) and `reg.write` (the claims, pet
  pairs and class sightings the parse makes) — all registered `uiThread: false`, because this store is filled by the parsing
  thread and drained by the builders, so naming either in a stall's "in progress" sends a reader to the wrong window.
  **`GetPlayerClass` reads inside the per-name lock**: it used to copy the class-boundary list out and binary-search it,
  measured at **72 bytes per read** (a two-entry list, a per-name lock, and a sibling method that had always read in place),
  on a call made once per row per rebuild — hundreds a second on a live raid, thousands on a select-all.
  `FrenzyClassTest.AReclassIsReadAtItsBoundaryWithoutAnArrayPerRead` asserts under 8 bytes a read **with a control loop that
  proves the probe can see an allocation**, so restoring the copy fails by name rather than quietly printing 72. Why the
  counters exist: they are the evidence for or against flat name→id indexing — `reg.class` at 20k reads/s and microseconds
  says string keys are not the wall and that rewrite does not start; a worst case in milliseconds says it does. Cheap spans
  sort themselves off the printed table, which is also an answer. docs/DesignNotes.md → "Instrumenting the UI thread".
- **`EQLogParser.Wpf.Test` runs MTA**: MSTest hands a test method an MTA thread, and WPF will not construct a `FrameworkElement` on one —
  its constructor asks that thread for its `InputManager` and throws before any of our code runs. So any test that news up a `UIElement`
  (`FctSkiaCanvas`, a window, a control) goes through `Sta.Run(...)` (`EQLogParser.Wpf.Test/src/Sta.cs`), which claims a thread as STA, runs
  the body there and rethrows at the call site so a failure still reads as a failed assertion. `Dispatcher.CurrentDispatcher` does not help;
  it hands out a dispatcher on any thread and fails in the same constructor. The lane guide test found this by failing three for three.
  Two companion laws from the Fight List panes, both measured on Windows: **every DependencyObject read belongs
  INSIDE the `Sta.Run` body** — an assertion that touches `IsChecked`/`IsHidden` after the body returned fails with
  "a different thread owns it" *after* the code under test already passed (capture plain bools in the body, assert
  those outside; `ConfigUtil` is static state and answers anywhere); and **flush at the priority the handlers queued
  at (Normal), never lower** — pumping at SystemIdle also runs the pane's first arrange, whose pass starts an
  indeterminate ProgressBar's infinite animation on a windowless thread and hung both band-showing tests for the
  full 60 s budget while the band-collapsed twin sailed through. An `Sta.Run` timeout names **live** threads only: the interrupt probe takes
  bodies down (an interrupt is delivered *in* a managed wait; a thread that never yields stays armed and dies at its next wait, after
  the failure is reported), so an earlier entry whose thread is gone would point the reader at a corpse — and "first reported ~N s ago"
  counts from detection, because nothing can notice a wedge before its own budget expired. The host carries a third, process-shaped law
  (measured 2026-11; mechanism in docs/DesignNotes.md → "A test host is unlicensed"): neither app nor tests touch
  `SyncfusionLicenseProvider.RegisterLicense` directly — both call `SyncFusionUtil.LoadLicense()`, one key constant that is
  empty in the repo (a vendor no-op; whoever holds a real key manages it there) — and on a machine whose validation still
  produces a message, the
  **first** `SfDataGrid` construction in a process enters Syncfusion's unlicensed notice — `LicenseMessage.DisplayMessage`,
  which does a SYNCHRONOUS `Application.Current.Dispatcher.Invoke` that a non-pumping test thread can never complete, so the
  body wedges inside `SfDataGrid.ctor` for the full 60 s budget (at the sixth construction a process counter past five
  raises the over-limit `shouldQuit`, whose branch re-enters the same display without asking the flag — exactly two bodies
  hang per run). `Wpf.Test/src/AssemblyLifecycle.cs`
  sets `SyncfusionLicenseProvider.IsLicenseExceptionShown = true` in `[AssemblyInitialize]` — before ANY construction —
  because with it set from process start every `GetLicenseType` reads null instead of a message; setting it later would not
  silence the `shouldQuit` re-entry. If an STA body ever wedges in a Syncfusion constructor again, that line is the first
  suspect.
- **XAML-fired handlers guard the PANE, not the sender**: a handler attached to a property XAML itself sets (`IsChecked="True"`) runs
  DURING `InitializeComponent`, and not all named fields are wired at once — measured startup crashes came in both shapes: once with the
  checkbox field itself null, once with the checkbox wired while a column declared later in the markup was not (NRE one line past a
  sender-null guard). So every such handler opens with the pane-readiness sentinel — whatever the body actually needs, never
  `senderField is null`: handlers that READ the grid's view gate on it (`dataGrid?.View != null` in TankingSummary,
  DamageBreakdown, HitLogViewer); `FightTable`'s dial handlers gate on `_paneReady`, a flag set on the constructor's last line —
  because `SfDataGrid.View` materializes only on the grid's **Loaded** pass (its ItemsSource callback bails while `!isGridLoaded`),
  so a View guard there swallows every real toggle on a pane that never arranges (`ARealToggleAfterLoadStillReachesTheColumn`
  failed exactly this way). A test where a post-load toggle must still reach its column belongs with any handler like this
  (`FightTableStartupTest`). Reasoning: docs/DesignNotes.md → "Handlers that XAML fires early".
- **A pane that MainWindow builds during its own `InitializeComponent` cannot read a theme width**: `ThemeConfig.Current*Width` are
  plain fields assigned by `SetThemeFontSizes`, which runs only after `SetMainWindow` → `ThemeConfig.Init` — i.e. after every control
  declared in `MainWindow.xaml` has been constructed — so a constructor read gets `0.0`, and a zero-width **fixed** column exists without
  ever rendering. That is how the derived fight list shipped with no "Initial Hit Time" while its Auto-sized HP sibling looked fine
  (`DataGridUtil.RefreshTableColumns` skips sizer columns, and the sizer writes real pixels). So: apply at `Loaded`, follow
  `ThemeConfig.EventsThemeChanged` (attach there, detach on `Unloaded` — that event is process-static and outlives any pane), and leave
  the read in a constructor only for windows opened lazily (`HitLogViewer`, which the summary panes build long after theme init).
  `FightTableTimeColumnTest` pins it by measuring `FightTable.ApplyTimeColumnWidth` — neither hook is fired there because raising `Loaded`
  on an SfDataGrid wakes the grid's own load path in a windowless host and every ThemeConfig raiser needs a live MainWindow.
- **Namespaces**: the WPF app AND `EQLogParser.Core` each compile every source into the flat `namespace EQLogParser`, whatever folder it
  lives in — folders state what something is (parsing, util, perf), not its namespace. The one historical exception,
  `EQLogParser.Mirror`, was dissolved with the engine's renaming; declaring a per-folder namespace in either project breaks tests'
  `using EQLogParser;` reach into app internals (`InternalsVisibleTo` is already granted to both test assemblies).
- **Timer bars: the lifecycle rules live in Core.** A countdown's length comes from the config box or a `TS:` capture (`DateUtil.SimpleTimeToSeconds` answers in
  *uint* seconds), so it arrives unbounded; durations go through `TimerLifecycle.ClampDuration` and every stamp/delay through that class, because an unclamped value
  saturates `Task.Delay`'s int-ms cast (measured: a 24.8-day sleep for the task whose only job is removing the bar) or wraps `EndTicks` into the past. A row is also
  refused at the door when it has no future — cancelled, lengthless (`FormatTicks` prints `00:00` for zero *and* negatives, which in show-reset mode is a greyed bar
  forever), or already past end + grace — because Start/Stop are fire-and-forget on the same semaphore and nothing keeps Add before Stop. The overlay reaps what its
  owner forgot (`ReapForgottenRows`, 2 s grace) and the render loop's `_isRendering` is cleared in a `finally`, since a latched flag freezes the window permanently.
  `EQLogParser.Test/src/util/TimerLifecycleTest.cs` sweeps the real parser's output range and the 720 service orders of a three-message burst — keep that green
  rather than moving the math back into `TimerOverlayWindow`. Reasoning: docs/DesignNotes.md → "Timer overlays: how a bar gets taken away".
- **Corpses need no rule; do not add one.** A raised raider-corpse (`<name>'s corpse rises to serve <master>.`, *Wake the Dead*) never
  fights under its own name — it attacks as ``<master>`s pet``, exactly like any swarm pet (server-verified on a necro called Kazcro; in six
  captures a master's possessive-pet share jumps ×3.6–×8.7 right after a raise burst while controls move ≤ ×2.3), so the existing R5 owner
  cut already credits the master. Two bans. **Never mint a pet label** for it: an `X`s pets` row would sit beside the real `X`s pet` row and
  split one player's output. **Never decide side or identity from the `'s corpse` shape**: that shape also carries 488 M HP of lingering boss
  DoT across 29 never-raised names, every `falls in battle.` line belongs to a respawning husk mob, and `ParserUtil.UpdateAttacker` strips
  `'s corpse` off **attackers** before a record exists — so a servant's hit is indistinguishable from a player who is alive again. A named
  corpse belongs in the fight list only because facts put it there (raid members hitting it keeps it; defender names keep the suffix), and a
  corpse DoT that hits us is an ordinary hostile under its stripped name — `EQLogParser.Test/src/parsing/derive/RaisedCorpseTest.cs` pins all
  four cases. Reasoning: docs/DesignNotes.md → "A raised corpse needs no rule".
- **Compare boards, not just rows, before replacing a grid** (method preserved; the census was retired with the
  legacy engine in 2026-10 - its final numbers are in docs/DesignNotes.md → "The legacy engine is deleted").
  `RealLogBoardsTest` was gated on `EQLP_DERIVE_BOARDS=<log>` (with `EQLP_DERIVE_DIAG=name,name` for "folded under
  another key or lost?") and ran the real builders twice over one capture, censusing every column per raider, because a grid is per person — two tables can
  agree on a fight's total while disagreeing about who did it, and a fight-row check cannot see that. Measured on
  Incogitable (344 MB): **healing is exact** (403,742 heals, 373 healers, zero mismatched person-column pairs) so that
  board is asserted strictly; **damage is renamed more than lost** (+0.54 % raid, 422 → 437 rows, 48 legacy-only names
  against 63 derived-only `X +Pets`, 22 of 374 shared people moving on Total) because `DamageStatsBuilder.UpdatePetMapping`
  folds by `record.AttackerOwner`, which a derived record carries from the line's own possessive word while legacy needs
  its registry to have learned the pair; **tanking is the report of what people received** — legacy groups by
  `record.Defender` unfiltered, so its 7.11 B contains 4.82 B of NPC names and 428 M of pets; cut to people it is
  1.863 B over 211 rows against derived **1.865 B over 212**, sharing 210 with one person differing on Total. Compare
  **populations as people, never as row names**: legacy puts NPCs from npcs.txt on the *damage* board (`Elmara Emberclaw`,
  `Dhakka Nogg`, both `Npc:R6-npcdb`, ~31 M dealt TO the raid) and lists a pet owner as `X +Pets` — which the derived side
  does for far more owners, because it reads the line's own possessive word. Name counts therefore reported "30 people
  lost" on eqlog_Kizant_xegony.txt where all 8 of legacy's person rows had simply arrived as `X +Pets`; `PersonOf` strips
  that suffix and the bar is "nobody legacy lists is absent under EITHER name". Run the bar over **several captures** — one
  log per finding is how a rule gets written from an accident (2022→2026, eight local logs: tanking people rows equal on
  seven of eight and damage-taken-by-people within ±0.6 % on all eight; that sweep is what surfaced the mob-on-unplaced drop
  in the next bullet). Don't average these into one "parity %": the findings need different actions.
- **Classify before asking an identity question in a test**: `PipelineHarness.RunFileDerived` hands back a timeline
  with `RegistrySeed` on it and **no rule table** — R6 (npcs.txt), R14 (article shape), R15 (healed by our side) and the
  graph run only inside the app's derive (`DeriveEngine`), or in a test that calls them. Measured on Incogitable:
  "damage facts whose defender no rule placed" reads **1,525,786 / 583 B** on the seeded timeline against **4,323 /
  1.69 B (0.25 % of hit facts)** after `ClassificationRules.Apply` — a 350× difference, because that is how many mobs the
  rules place out of the way. Two documents were written on the wrong side of this before it was caught (a gated census
  test since deleted proposed a rule from it; `IsRaidVictimAt` quoted its numbers from it), and the proposal died once the rules ran: with a
  classified timeline, "a mob keeps hitting it" identifies **zero** roster players and its top names are mobs the NPC
  database does not know (`Herald of the Outer Brood`, `War Trainer Prime`), so no such rule exists. Build the timeline
  the way the retired board census did it — fresh `EntityTimeline`, `RegistrySeed.Apply`,
  `ClassificationRules.Apply(facts, timeline, heals)` — for anything that reads identity, pets, charm or ownership.
- **The tank board is damage our people received, decided by three fact targets, not two directions**:
  `FightProjection.FactTarget` is `AtOwner` (aimed at the row's anchor — the raid's output), `RaidSide` (landed on one
  of us — the tanking half) or `Neither` (mob on mob, mob on somebody's pet: captured, counted in
  `FightFactIndex.UnroutedFactCount`, filed nowhere). Three rules, each learned the
  expensive way. (1) **Ask `AtOwner` before the Unknown-victim test, but `IsConfirmedRaidPersonAt` before both** — an
  unidentified mob satisfies "not known to be a pet or a mob", so testing that first files the raid's own opening
  swings as someone's damage taken; and when a row is keyed on a raider herself (legacy's defender-key tiebreak for an
  unclassified attacker) her incoming hits must still be `RaidSide`, not the outgoing side of her own number. (2)
  **`IsRaidVictimAt` is exclusion, not proof**: `IdentityAt is not Npc and not Pet`, and the price of that choice is
  measured small — on Incogitable against a classified timeline the board is 91,036 facts / 1.6389 B Player plus 9,480 /
  196 M Merc, and proof would keep every one of those; exclusion adds **86 facts / 15.6 M (0.85 %)**. That is what buys a
  raider who never casts a database spell, never speaks and joined before the log opened: she stays Unknown all evening
  and her damage taken is the reason the board exists. (The claim this bullet used to carry — "proof loses 90 % of the
  board" — was measured on an unclassified timeline; see the rule above.) (3) **A pet's incoming damage is not a
  player's**, by decision: with the routing in place the derived board holds **zero** Pet facts where legacy carries
  428 M of them (plus 4.82 B on NPC names); if pet damage taken is ever wanted it arrives as its own choice —
  owner-folded like `X +Pets`, or as pet rows — never as a widened predicate that silently moves every tank column. A charmed raider reads Npc while the window holds, so
  her incoming hits are not raid damage taken either; `IsRaidVictimAt` deliberately does not undo the flip.
  (4) **`Unknown` means "no rule placed this name", never "not one of ours"** — and a branch that drops on `Unknown`
  cannot be corrected by a predicate living in another branch. Mob → unplaced used to fall into the mob-on-mob drop, while
  the very next expression (`IsRaidVictimAt`) calls the same name raid-side; the drop branch simply never asked. So raid
  members taking a mob's hits left the tank board: on Incogitable `Worthless` 159 facts / 1.48 M (while spending 322 swings
  of her own on `A cunning scrykin`), `Boner` 186 / 1.39 M, and **80 of Ddread's 85** incoming facts — she is in the roster,
  but verdicts replay from mid-log evidence, so her identity begins at the first heal that names her and everything before it
  was noise. Mob → unplaced is now `RaidSide` on the mob's row; only a defender that reads `Npc` is noise. The test asserts
  **which side** of the row the fact lands on (`AMobBeatingAnUnplacedName_CountsAsDamageTaken_OnTheMobsRow`) because "a row
  exists" and "the total is non-zero" both pass when it goes to nobody.
- **Record sharing starts on the third sighting**: `HealingLineParser._healCache` is a `RepeatStore<T>` whose first
  sighting of a value deliberately takes no entry (84% of distinct records are never restated). The damage line had the
  same store until the legacy engine was deleted; it must NOT move back onto the parser seam while
  `HandleDamageProcessed` rewrites `record.Attacker` after its own cache lookup - a shared damage instance would move
  every earlier sharer's live key (that rewrite is why heals tolerate sharing and damage did not). The heal store and its `RepeatFilter` are **static**
  process state, so a test asserting that two parsed heals share an instance has to call `HealingLineParser.ClearCaches()` in setup — otherwise it
  passes only from whichever position in the run order it happens to occupy (`LineParsersTest` is where that was found). Sharing is never a correctness
  mechanism: records are equal by value, and the filter is allowed to be wrong only toward "seen". Numbers and reasoning:
  docs/DesignNotes.md → "What a loaded raid costs in memory".
- **A hit record carries only what its own lines can write**: `OverTotal` belongs to `HealRecord`, not to `HitRecord` — over-heal is the only
  "asked for more than landed" number EQ writes, and damage has no counterpart, so keeping it in the base cost 4 B in every one of the millions of
  damage records. Counting is split the same way: `StatsUtil.UpdateDamageStats(PlayerSubStats, DamageRecord, …)` and
  `UpdateHealStats(PlayerSubStats, HealRecord)`, and `LineModifiersParser.UpdateStats` takes `(short mask, uint total, …)` — **nothing in the codebase
  takes a `HitRecord`**, which is what keeps the base small. So a new field goes on the type whose log lines can fill it, never on the base "for
  convenience", and the damage switch must not gain `Hot`/`Heal` cases: `CreateDamageRecord`'s callers cannot produce a heal label. Two traps.
  `DamageRecord` has no defender owner and that is deliberate (nothing read it) — but `CheckOwner(defender, out _)` **stays** in
  `DamageLineParser.CreateDamageRecord`, because registering the pet with its player is why that call runs; deleting the line stops pet mapping learning
  from damage taken. And whoever counts a miss must not spend its modifier mask (`(Riposte)` rides on a line the parser called a miss): that guard lives
  in `UpdateDamageStats`, not in `LineModifiersParser`. `HitStatSplitTest` pins both, plus the heal arithmetic (`Extra` = ask − landed).
  Numbers and reasoning: docs/DesignNotes.md → "The eight bytes that were in every damage record for nothing".
- **A record's kind is one byte, not a string and not a `StringCache` id**: `HitLabel : byte` holds the sixteen words a record can be (fourteen damage,
  two heal) and `HitLabels` pairs each with its `Labels` constant, so `record.Type` hands back the same interned literal as ever and every
  `record.Type == Labels.Melee` comparison still works. Four rules. (1) **Write the backing type**: a default enum is an `int`, and one extra byte puts a
  damage record from 40 B back to 48 — measured, and pinned by `Unsafe.SizeOf<HitLabel>()`. A *narrowed* id would be the wrong idea entirely (a long session
  interns far more than 256 strings); this is its own table, not a truncated one. (2) **`None` reads back as null**, because records with no label have always
  read null (`StringCache.GetName(0)` is null) and answering "Unknown" instead would rename events. (3) **A word outside the table maps to `None` and is
  counted in `HitLabels.Unmapped`, never guessed at** — matching is ordinal, so `"direct damage"` does not become `Direct Damage` and quietly move those
  events into another column of every view. (4) **The vocabulary is asserted at its size** by `HitLabelTest`; a new label arrives with an enum member, a table
  pair, the parser branch that produces it and a test, exactly like a new FCT word. The vocabulary is closed by construction (every `CreateDamageRecord` call
  passes a `Labels` constant or a variable assigned from one; `GetTypeFromSpell` returns only its input, `Bane` or `Proc`; the miss branch maps onto six words;
  heals pick two) — and note `Reverse DS` is **not** in it: the parser uses that word for an *attacker*, never as a type. Both records are **40 B** now; below
  that wants two-byte name ids, which is the flat-array conversation, not trimming. Numbers: docs/DesignNotes.md → "Sixteen words in one byte".
- **The derived damage summary decides direction, and never "fixes" the pet gap**: `FightFactIndex` is filled inside
  `FightProjection` through `FactOwnershipHandler`, whose flag is `towardOwner` — the row's own name was the defender,
  which is the same comparison that splits `DamageToOwner` from `DamageByOwner`. It is **not** "the attacker was
  player-side": an unclassified name hitting a known NPC belongs in those blocks too (legacy agrees), and one expression
  for both is what keeps an index sum from drifting from the row's number (`FightSummarySourceTest`). Two silent-nothing
  traps live in materializing: seed-and-compare on `double.NaN` leaves a fight with no end (`time > NaN` is false, so the
  summary divides by an empty window and nothing throws), and a record's `SubType` may never be null —
  `StatsUtil.UpdateDamageStats` keys a `ConcurrentDictionary` on it, inside `DamageStatsBuilder`'s catch that logs and
  carries on, so a null is an empty board. A materialization test therefore asserts real time bounds **and** pushes the
  result through the real builder. On `mini-fight.txt` the derived board reads **+19.5 %** over legacy with every raider
  legacy placed reading identically: two pets whose owner the registry never learned contribute nothing to the legacy
  board at all, while the line's own ownership word (`ClassificationRules.OwnerInName` → `AttackerOwner`) folds them under
  their raiders here. Do not force the two sides into agreement — the gap is the experiment; the test pins its shape.
- **Chat channels are a closed claim list, and one announcement shape is R22**: R3-chat claims a speaker only on
  guild/raid/group/fellowship — tell shout/ooc/**zone chatter never does**, because `] Bane tells General:1, 'WTS Full NoS collect
  sets 3kr each'` is a bazaar hailer and `Bane` sits in npcs.txt as a mob; the same reason `begins singing` was refused
  (`Shalowain begins singing her Rhapsody of Pain.` is an NPC bard). What *was* built is the one shape whose grammar
  belongs to the speaker rather than the thing it names: **`Your guildmate X has completed … achievement.`** —
  `PreLineParser.TryGetGuildmate` (fire-only, like `Targeted (NPC)`: reports evidence, consumes nothing; letters-only
  name so a server-qualified one is refused) → `EvidenceFact.EvGuildmate` → **Player at Strong** `R22-guildmate`, which
  sits under the frames the client produces by looking at the entity (like R17's drink, R19's eye) and over npcs.txt,
  where person-shaped names do sit. `RememberedRules` gained `R22-`, so a silent regular is claimed once and remembered;
  **no `players.txt` write is involved.** **Neither display word says "guildmate"** — the cell reads *Achievement* and the hover
  *Achievement Message* (asked twice, the second time about the hover clause itself); the CODE stays `R22-guildmate` because it is a
  machine word in identity-priors.txt and eqlogparser.log, and `TheAchievementClaimNeverSaysGuildmateOnScreen` sweeps the whole
  vocabulary so the word cannot return through another rule's clause. Measured yield: the winning claim on **22** roster names on Incogitable (168
  named there) and **1** on `-09-20-25` (`Blazem`, four achievement lines in a 3-hour capture), while pool-wide placement
  stays at its 82.5 % — the rule moves the people the aggregate was never about. Numbers, refusals and why tells are out:
  docs/DesignNotes.md → "What was built out of it: R22-guildmate"; pinned by `GuildmateRuleTest`.
- **Entity names are looked up without case**: the parser capitalizes every name it hands out (`ParserUtil.UpdateAttacker`/
  `UpdateDefender`/`UpdateSlain` all finish with `TextUtils.CapitalizeFirst`), so a fact says `A bone walker`; evidence lines keep
  what EQ wrote — `a bone walker has been charmed.`, a wear-off, a tell. So **every entity lookup is case-insensitive**:
  `EntityTimeline`'s identity and affiliation keys and `PlayerRegistry` (which always was `OrdinalIgnoreCase`). An ordinal key at
  this seam does not throw — the reader gets Unknown/hostile and quietly uses its fallback, which is how R9's charm flipped nothing
  in the fight list while its own case-insensitive window tables went on reporting credit. The same rule runs through **storage**:
  `DamageFactTable.InternName` keys `OrdinalIgnoreCase` and stores `CapitalizeFirst(name)`, because an ordinal pool gave one mob two
  ids — per-id evidence, rollups and fight rows each held half of it, and the id's string *is* the row's displayed name, so "a bone
  walker" vs "A bone walker" was decided by which line arrived first (merging is exactly the measured split: pool **2,721 → 2,436** and
  **558 → 518**, i.e. −285 / −40 twins, with `classify`/`derive` unmoved and `[rows]`, `[charm]` and legacy fight counts identical).
  Only the first letter is touched — inner capitals arrive as written — and that is enough because the pipeline's two spellings are
  `raw` and `CapitalizeFirst(raw)`, which differ only at index 0. Subtype/label tables stay ordinal (they are vocabulary words that
  settings and `Labels` constants compare against). `EntityNameKeyTest` pins identity, affiliation, ownership and "one entity per name"
  across spellings; `NamePoolTest` pins one-id-per-entity, order-independent display and that the heal stream interns into the same pool.
- **A name comes from a subject span, never from "the token after some punctuation"**: `HealingLineParser` finds the actor of a
  glued-sentence line (`…as it breaks! You healed Niktaza for …`) by taking what follows a `.`/`!` before `" healed "`, which worked
  while every EQ person name was one token — and minted entities called **"II"** and **"III"** from
  `Bastion of Divinity Rk. II healed Xxuro over time for 6670 hit points by Bastion of Divinity Effect II.`, because the period of a
  spell's **rank formulation** is not a sentence end (`IsSentenceEnd` recognises `Rk.`; 8 such lines in
  `eqlog_Kizant_xegony-2.txt`, heal facts 1,247,992 → 1,247,984, no roman name left in the pool). The damage path never saw them — 0 of
  2,270,292 damage facts — only the heal stream interns those names, and the **identity list is the one surface that shows a parser bug
  in a heal line**, so a row with no reason printed is parser evidence, not a classification gap. Such a line is now refused whole (like its
  rank-free sibling, which has never been stored); crediting caster-less heals onto the spell's own name would move the healing board and is
  its own decision. Two related measured standings: **the Type cell reading `NPC` for a spell is known-wrong and open** (all 34 rank-shaped
  attacker names in that capture read `Spell` as reason with every one of their 590 hits on an NPC — adding `IdentityKind.Spell` touches
  25 `IdentityKind.Npc` sites, several of them board-routing predicates, so it arrives with a real-capture board diff, not as an enum add);
  and **a ledger seed never changes an answer** (17 / 18 / 77 names answered from the seed on three captures, **0** disagreeing with the
  capture's own evidence; a deliberately wrong roster entry still loses to `R6-npcdb`, `R1-target` and `R14-shape`) — what memory costs is
  the *reason word*, so "weak seeds block R7/R15" closed as provenance-only and re-opening those gates needs new measurement, not the old
  argument. Numbers: docs/DesignNotes.md → "A rank formulation is not a sentence end".
- **A charm window is the NPC's death; its pet's death is not one**: `DerivedFight.EndReason` says *why* a row ended while
  `Dead` stays the flag every consumer reads (overlay, grid styling, `FightSummarySource`). A `has been charmed.` sighting closes that name's row as `Charmed`
  (status word `dead, charmed`) — the raid finished that encounter by taking the mob off the enemy list — and a death **inside** a window is skipped by dead-marking
  (`FightProjection.DiedWhileCharmed`, with 1 s of slack because that death *is* the window's exclusive `T1`), leaving the fact on `CharmEndReason.Death`: one corpse
  must not hand out two kills, and the skip is what stops a pet dying in custody from dead-marking whichever same-named row happened to be open. Inside the window the
  mob's damage reaches the board as `AttackerOwner = OwnerOf(name, t)` (so `+Pets` folds an evening of charming one mob type under its charmer), an **ownerless**
  window leaves that field null rather than picking a raider, and a flipped *defender* is exempt from the friendly-fire drop — deleting the raid's own stray swings
  onto their pet would shrink their meter. Two measured laws: name spelling is settled by the case-insensitive entity keys (bullet
  above) rather than by registering `A bone walker` beside `a bone walker` — with ordinal keys nothing flipped *silently* while
  `CharmWindowPolicy`'s own case-insensitive tables went on reporting credit, which is how the feature looked finished while doing
  nothing; and a charm closes only a row whose last fact sits inside `EventTailWindowS`, so another pull of the same name cannot be
  ended retroactively — **that** window, never the 30 s that splits rows (next bullet): the raid's last swing and somebody else's
  charm spell are two acts on two clocks, and tightening the tail with the split leaves rows reading "still going" after their mob
  became the raid's pet. Numbers (rows closed `Charmed`: **3 / 9 / 12** on three captures, taken out of the
  `Gap` counts that used to hide them; charmed-owned rows **0→6** and **0→5** once the exemption landed — **those three predate the late-close
  cap fix**, which on Incogitable cut a 182-day window to 360 s and removed 520 facts / 8.19 M of pet credit, so re-take them before quoting);
  reasoning:
  docs/DesignNotes.md → "A charm takes a mob off the enemy list"; pinned by `CharmRowProjectionTest`.
- **A row is one life, and the split gap is legacy's expiry (30 s)**: `FightProjection.EngagementGapS` is **30 seconds**,
  kept as one number for both lists — not legacy's pair (60 s until boss-directed damage lands), because a row that has not hurt anybody for
  half a minute is over either way, and a threshold keyed on which side the first hit went makes boundaries a function of damage
  direction. It was **300** until one pull was read off `eqlog_Kizant_xegony.txt`: `Waxwork Abolishion` fades at 18:34:53 after 46 s and
  respawns 135 s later, and the wide gap made both lives ONE row ("5 minutes", one entry short of the encounter) while also welding the
  fade into the DPS clock — legacy's `TimeRange.Add` drops silences ≥6 s, so its three rows summed to 324 s where the merged span charged
  343 s. Two consequences that must not be "simplified" back: a row's **direction windows** (`Begin/LastDamageTime`,
  `Begin/LastTankingTime`) are written by the same `aimedAtAnchor` comparison as `DamageToOwner`/`DamageByOwner` and `FightFactIndex`,
  with **NaN meaning "never happened"** (a row nobody hit must not report a zero damage time; `Sectionizer` skips an empty window, since
  `Math.Max(x, NaN)` is NaN and one such row would silence every later "Fight N" divider), and the grid's **duration column prints seconds**
  (`FormatTicks`, `HMSCompact`) because `FormatGeneralTime` returns an *empty string* under a minute and collapses 112 s and 162 s into
  "1 minute"/"2 minutes" — only the inactivity divider keeps the words, where matching legacy is the point. Cost on that capture: rows
  **237→289** (visible **215→267**) vs legacy **262**; reasoning and fixtures: docs/DesignNotes.md → "One row per life"; pinned by
  `ABossThatFadesWhileItsAddsDie_GetsOneRowPerLife`, `AQuarterMinuteOfQuietIsStillTheSameFight`,
  `ARowRemembersItsDamageTimeApartFromItsTankingTime`, `ARowNobodyHit_HasNoDamageTimeRatherThanAFakeOne` and (Windows) `DerivedFightRowsTest`.
- **Quiescence is a completion detector, and the cadence answers WHICH pass, not merely whether**: derivation used to run only when the captured
  count held still for two ticks, which answers "is the file done loading" and nothing else — a live raid tail never offers two silent ticks, so every
  surface reading the snapshot (fight list, click summaries, the damage meter) froze for the whole encounter and moved only when somebody forced a pass.
  `DeriveCadence.Decide(...) -> DeriveKind { None, ProjectionOnly, Full }` is the rule now (Core, so it is testable without a dispatcher; there is
  no `ShouldDerive` anymore — assert *which* pass is due, or a legitimate half-second refresh reads as "no pass"). Check order is hazard by hazard: nothing
  new since the last pass of **either** lane => `None`; count held still for `QuietSeconds` => **`Full`** (never answer that moment cheaply — the end of a
  load is when the rules finally see the whole capture, which is when they learn what a refresh cannot); growth **rate** >= `BulkFactsPerSecond` (25,000/s;
  a file reads at ~170k facts/s against tens/s while tailing) => `None`, and that parks **both** lanes because the cheap one still holds the
  `CombatCapture._gate` the loader needs; full clock due => `Full`; else cheap clock due => `ProjectionOnly`. The ceiling stays load-bearing:
  `BuildMeterUpdate` zeroes the board after the meter's quiet window (`LiveFights.TimeoutFor`, 30 s at the default dial) of quiet measured against *the
  snapshot's* last fact, so a slower cadence makes the refresh rule and the expiry rule argue and the meter blanks on a live raid. Opening a derived meter
  also calls `RederiveAsync()` directly (the expensive lane), since an empty window reads as broken. Every threshold is a duration or a rate rather than a
  tick count — the pump (now **100 ms**) only decides how close to its moment a pass lands, and a per-check allowance would shorten the quiet window and the
  bulk guard purely by asking more often (`TheRuleDoesNotDependOnHowOftenItIsAsked`). Note bulk **parks**, it does not fire: a load ends in quiet.
- **A failed derive degrades one stage at a time and never closes the loop**: `ClassificationRules.Apply` runs every stage inside its own guard (`RunStage`) — a stage that THREW keeps whatever it added before the throw and costs nothing else (rules ADD evidence, so a missing stage reads closer to Unknown, never to the wrong side), reports full exception text on `ClassificationOutcome.FailedRules` for the session to log, and after **five consecutive** failures retires *itself* (one clean run clears a streak; a new `DeriveEngine` calls `ResetRuleHealth` so the next capture gets the full rule book). A session-level throw (projection, snapshot build) retries on `DeriveCadence.RetryDelayS` — 1 s doubling to 60 s, cleared by any completed pass — through **one Core decision** (`DeriveCadence.DecideFailureRetry` → `Wait`/`Retry`/`Park`) so the ladder, the bulk guard and the allowance cannot drift between callers: a retry **never runs into a load** (it holds the ingest gate on top of the read; the load's own quiet tick settles the debt) and **never runs forever on a capture that stopped growing** — `MaxQuietFailureRetries` (8) attempts, then ONE parked line and silence until a new fact, a re-derive or a new log, because a closed log never stops owing the retry and a deterministic throw would otherwise write an error-with-stack every minute for the rest of the day in the file that carries the raid. A live capture cannot park by construction (its passes would run anyway). `_quietFailureRetries` counts only quiet attempts and any growth resets it. `IdentityPriorStore.Record` cannot fail a pass at all (the ledger is next-log memory; the boards on screen were already computed). What this replaced: one exception latched auto-derive off for the life of the session with a Re-derive button as the only recovery — a button no legitimate flow ever pressed (overrides re-derive themselves, meter openings force a full pass), so **the button is gone too**. The fight pane shows no failure state at all - its top-right status section was removed on request (no status messages there, ever) - so a repeating stack in eqlogparser.log with the retry interval *is* the only failure surface, which is why every attempt logs. Tests: `ClassificationRuleHealthTest` (guard laws), `ARetryLadderStartsAtOneSecondAndCapsAtAMinute`, `AFailedPassIsOwedItsRetryOnAnIdleCapture`, `IdleRetriesComeFromAnAllowanceRatherThanForever`.
- **A selected fight row re-derives its boards on CONTENT, never on object identity**: `FightTable.SelectionStamp` (captured fact total — heals included — folded with the timeline's identity digest, both O(1)) is what says "this pass could change what the boards show". Announce when the selected ids changed, when the patch edited/removed a selected row, or when that stamp moved; otherwise the figures on screen are already the answer (docs/DesignNotes.md → "What makes a board go one pass stale"). Do **not** replace it with `!ReferenceEquals(row.Fight, before)`: `RowPatch`'s refreshBacking re-points every survivor by design, so identity is true on every full pass and a whole-capture selection (~3.8 s to materialize, plus its own `Derived damage summary:` log line) would rebuild continuously during a pull. The two stamp terms are exactly the inputs materialization reads — facts and verdicts — which is why skipping cannot leave a board stale; the row diff alone cannot see routing moves (`X +Pets` folding, charm), so it stays in the condition as well.
- **No surface may hold a `DeriveEngine` past its session**: tag "which capture wrote my state" with `DeriveEngine.SessionId` (static counter, 0 = never seen). A `Dispose`d engine keeps every fact table of a closed log, and the meter window is *hidden* by its X rather than closed, so an instance-valued static there (`_meterStartSession`, the first shape of the meter-start continuity) is the leak `Dispose` promises against.
- **Stage death is silent in production and loud in tests**: the swallow above is exactly what a test must not inherit — a rule dying mid-pass reads as *"the rules found nothing"*, and every parity board keeps agreeing while one column contributes nothing (charm flips that flipped nothing, R5 claims that never replayed). Both test assemblies set `ClassificationRules.FailFastStages = true` in `[AssemblyInitialize]` (`src/AssemblyLifecycle.cs`), so a stage exception rethrows with its real stack wherever the suite runs it — free coverage for all 26 test files that call `Apply`, no per-test assert to forget. The one sanctioned exception is `ClassificationRuleHealthTest`, which pins the swallow-on-purpose semantics and flips the flag off in Setup and back on in Cleanup; anything new that drives the guard's counting behaviour belongs inside that class, not beside it.
- **"Active data was cleared" belongs to the clear PATHS, not to a store**: `CombatEvents.ActiveDataCleared` is what blanks the seven grids/charts, and it has exactly two raisers — `LifecycleManager.Clear` (after the fan-out; the log closed / another opens) and the legacy fight list's Clear All (`FightTable.ClearClick` raises `FireActiveDataCleared(true)` beside `FightManager.Clear()`, which is the payload `Clear`'s default used to pass through). It no longer fires inside `FightManager.Clear`: that made the app's clearest signal die with whichever store was scheduled for deletion, and a board still showing last night's raid after "new log" is worse than an empty one. Deleting either raiser while views subscribe is the failure this pins; `LifecycleManagerTest` asserts the path raises with its payload and a bare store reset raises nothing.
- **The cheap lane folds over the timeline INSTANCE the last full pass produced, on two clocks**: `ProjectionOnly` runs no rule book at all (measured:
  classification is 186-261 ms of a pass, projection of a live increment 0-5 ms), so `DeriveEngine` carries `_carriedTimeline` and hands that same object
  to `FightProjection.Project`. Carrying the instance is what makes `EntityTimeline.StateStamp()` come back unchanged, so the existing stamp gate keeps
  folding from its watermark and a re-classified full pass earns its rebuild by itself — the cache never learned that lanes exist. **Two traps.** (1)
  `Full` is paced by `_sinceFullPass`, the cheap lane by `_sinceAnyPass`: if a cheap pass restarted the clock that paces classification, a busy tail would
  run all night on the verdicts it happened to have at minute one and pets/charms would quietly stop folding (`ACheapPassNeverPushesTheExpensiveOneAway`).
  (2) Only a pass that classified may restart that clock, store `_lastFullPassSeconds`, write `IdentityPriorStore`, leave verdicts behind **or write an Info log
  line** — the cheap lane runs twice a second, and its bookkeeping would push the raid out of the file this app writes (`Note` above the passes). Accepted
  staleness is bounded and stated in Core: a verdict readable only from newer facts lands one full cadence later — never "facts went missing", which is
  what `TwoCheapPassesOverOneSetOfVerdictsMatchASingleFold` holds (three folds under one timeline == one fold, and the carry must survive consecutive cheap
  passes or the cheap lane is slower than no cheap lane). With the pump at 100 ms and `LogReader`'s tail delay left at **200 ms** (that loop's watcher
  ignores `Changed`, so its poll *is* the app's tail latency, and a line waits half of it; it was cut to 75 during this work and given back, because with
  folding on a 0.5 s floor a later drain usually lands while the next pass is still not due) a refresh lands in ~0.8-1.2 s instead of ~3.4 s, for
  ~7-12 % of the ingest gate. The unmeasured term is `DerivedFightRows.Build`,
  which every lane pays over every row the capture ever made: its bound lives in `EQLogParser.Wpf.Test/src/control/util/DerivedSnapshotCostTest.cs`
  (Windows-only). Widen the cheap lane by row count before touching either floor. Numbers: docs/DesignNotes.md -> "How long a meter update takes".
  **The surface around it must never read as broken**: the fight list docks at startup whatever `dockSite.xml` says
  (the `EnableCombatMirror` dial is deleted - a settings.txt still carrying the word simply stops being read; an
  experimental-era layout that had hidden "the old one" must not produce a restarted app with NO fight list, which
  shipped once as "it stopped working after I restart"), and opening a log **starts a session immediately** (docking
  only in the open-log path once left the window present but sessionless - the exact uncheck/re-check experiment the
  user reaches for). During a bulk load the derived list is legitimately empty (both derive lanes park), so `FightTable`
  shows a loading band over the table whose **one phrase is "Building derived fight list..." from EOF to the first snapshot**, and **only on an open nobody
  chose**: `FightTable.AllowsLoadBand` is off unless MainWindow's startup auto-monitor open turns it on, because through File / Recent the operator is watching
  the application status line count their own click (`AManuallyOpenedLogStaysSilent` pins the silent half; a future open path that forgets the flag loses an
  announcement rather than gaining an overlay nobody wanted) - `ReportCaptureProgress`
  still rides the reader pump, but a sub-100 tick says NOTHING in this window: the application-wide status line counts that same pump's percent and
  seconds already, and a second copy in the dock was removed as duplication. The session's first snapshot takes the band down for good - legacy filled rows
  per parsed line, the projection cannot (a cheap lane during bulk steals the ingest gate, reverted on measurement). The pane's top-right status section is
  gone entirely (removed on request - no status messages there at all): no derive stats line, no override verdict, no placeholder, no failure text; a
  selection speaks through the boards it feeds, and a failed pass only through eqlogparser.log (`FightTableLoadBandTest` pins what the band may still say). A
  mid-log attach is forward-only by construction: no backfill exists (facts before the tap were never captured), and the chat sink was fixed when
  the file opened (`LogProcessor` holds it), so drink evidence and chat identity join at the next open while facts and heals flow from now.
  One line per session in eqlogparser.log - `capture: started (file)` at open, `derive: first pass - N facts, M rows`
  at the first pass - so a list that silently fails to fill is diagnosed by which of the two lines is missing.
- **The Names pane is current while open and silent while shut** (2026-11; it used to be "asked, never fed"): the census (a full classification pass) ran on
  explicit doors plus a **Refresh** button, and never on `DeriveEngine.Derived` — the reasoning (auto-hide dock slides and tab switches each fired a rebuild, which
  reads as "the table keeps updating itself" while somebody tries to read 4,000 rows) was right about the cadence and wrong about the conclusion: what it bought was a
  window showing last night's raid over a live one, and **an operator who does not know a button exists concludes the feature is broken**. So `IsVisibleChanged` now
  `FollowSession`/`UnfollowSession` around ONE `Derived` handler (rebuild on show, then one pass per derive floored at **2 s** — `AutoRefreshFloorMs`; a live raid hands
  out passes ~2/s and the census walks every name plus the heal stream), `ActiveChanged` keeps it attached to the live engine only, the button is deleted rather than kept,
  and with no session the grid is **emptied** (`ClearRows`) instead of holding last night's names. Anything asserting this must keep the floor (a test that pumps every pass
  measures nothing) and the unsubscribe (a hidden pane costing census time is the bug this replaced). **Following the derive is not the same as moving the list**:
  `MergeRows` replaces a row's CELLS at its own index (collection indexer, not Clear+Add), drops names the census lost and appends newcomers at the **bottom**, while
  `_rankOnApply` — set on show and on every capture change — is the only path that re-ranks, so ranking arrives when somebody navigates to the tab rather than twice a
  second under their cursor (Clear+Add cost both the order and the selection, and rows are ranked by evidence, which moves). Because rows are replaced rather than
  mutated, **the selection and an open popup are handed back by NAME** (`FindRow`): the instance is gone even though the line never moved, and a Type cell that opens with
  a blank preselect reads as a classifier bug instead of a refresh bug. Pinned by `ARefreshKeepsEveryRowWhereTheReaderLeftIt`,
  `ANameTheCensusDropsLeavesTheList`, `OpeningTheTabAgainReRanksTheList` (Windows-only assembly, but no WPF is constructed). Four columns sorted by name (Name | Type | Why | Class): Damage/Healing left because an identity list must not rank
  names by output (`ClassificationReport` still totals them — that is its own row order), and Owner left because `PlayerRegistry` answers it and the Pet Owners
  window lists the same pairs. Two things moved to the **Why cell's tooltip** instead of being deleted: the cast a spell verdict rests on
  (`ClassificationReport.Row.ReasonDetail` — taken per **gate**, not per caster, because "Hobble of Spirits VI" is a prefix of the pet's "Hobble of Spirits Snare
  VI" and one flat substring test let the pet's spell name itself in a Player row's tooltip; never by widening the claim source, since `StateStamp` hashes
  sources and `IdentityPriorStore` persists them — `CensusCastProofTest`) and the lines no column can carry — "You chose NPC" / "Claim taken back"
  are the only way to tell a row an operator wrote from one the rules inferred (`ProvenanceFor`, pinned by `NamesTableTest`, Windows-only). **No status line and no
  header**: the pane is the grid alone — the shape Pet Owners has, because the dock tab already says "Player/NPC Identity" — so nothing on screen prints the census
  counters that survive (`TotalNames`/`UnresolvedInCapture`/the five kind counts are computed and tested with no surface — the whole-capture `Disagreements` count went out with its header, since "counted but invisible" is not a reason to compute); see the docking law below.
- **An outcome word decides; a tag never un-happens it, and a name never carries the sentence's period** (2026-11, both measured on
  `eqlog_Kizant_xegony-2.txt`). (1) `riposte!`/`ripostes!` had an extra guard, `"(Strikethrough)" != split[^1]`, that made the parser
  return **no record** for `X tries to bite Y, but Y ripostes! (Strikethrough)` — **16,076 of that capture's 21,277 ripostes**, reaching
  nothing at all: `StatsUtil` counts `RiposteHits`/`MeleeAttempts` from the LABEL, the modifier tally needs a record to carry the mask, and
  R7/R15 read facts. Blocks/parries/dodges tagged the same were always kept, and the branch's own comment block lists that exact shape as
  supported — so a `missType` arm that returns nothing for a documented shape is a bug to measure, not behaviour to preserve. The real
  distinction lives in the MASK (`LineModifiersParser.IsRiposte` = Riposte bit **without** Strikethrough, "attacker struck through a riposte")
  and is asserted off `record.ModifiersMask`: **sub-set by asking the mask, never by dropping the line**
  (`TestBlock_RiposteOfStrikethroughYou`/`Other`, whose `Assert.IsNull` pins were flipped with the measurement written in; the mask-only
  `but miss! (Riposte Strikethrough)` → Miss shape is untouched). (2) In `X has taken N damage from <empty> by Y.` — blank spell slot, the
  effect's own name in the caster slot — the arm that copies the by-slot into `spell` ran **before** the trailing-period fix, so the name and
  the subtype key kept the sentence's `.`: 3 of 262 pool names ended in '.', each a twin (`Infected Magic.`/`Infected Magic`, `Burning Glob
  Burst.`, `Arcstone Rock Fall.`), and since R21's third proof is spells.txt — which cannot answer a dotted key, like the ordinal subtype table
  it also poisoned — the clean twin read *Spell* while its shadow sat as **Unplaced**. Same defect class as case-insensitivity in
  `NamePoolTest`: a name differing from itself by punctuation. `TestTaken_EmptySpellSlotLeavesTheSentencePeriodOutOfTheName` pins the strip
  **and** that the `by .` caster-less shape still substitutes and flags the spell (a lone `.` is not a name with a stray dot). Cost of the pair:
  facts 2,270,292 → **2,286,368 (+16,076)**, pool 262 → **259**, aim-only facts 5.9% → 6.5%. Reasoning and the retracted grep-based claim it
  required: docs/DesignNotes.md → "Two capture gaps a hover question exposed".
- **The Type cell carries the judgement, the hover carries the reasons** (2026-11): a Spell row reads **NPC Spell** when every
  fact it dealt landed on a name that reads player-side (something on the other side cast it), **Player Spell** when every one
  landed on an NPC, and plain **Spell** when the targets disagree or read Unknown — "both" is not a judgement about who owns a DoT,
  so the cell declines and the hover says *Damaged Players and NPCs*. **The words are named by the caster, not by the reader**:
  "Our Spell"/"Enemy Spell" were the first attempt and both died on the wording — "our" claims the reader's own side (no evidence in
  a capture says who cast a caster-less DoT) and "enemy" states a relation to the reader instead of naming what is on screen. Only Spell rows can get a direction word (`IdentityVocabulary.TypeWordFor`), `Kind` itself never
  becomes a side-word, and the pane uses `Row.TypeDisplay` with a fallback to `TypeWord(kind)` so a hand-built Row is never blank.
  Below the head proof line the hover lists **every other rule that claimed the name plus the one fact clause**, one phrase per line,
  built in Core (`Row.OtherEvidence`) and appended by `NamesTable.ProvenanceFor` as `proof + "\n" + tail`: **a single-claim row whose
  facts point nowhere keeps hovering as exactly one line** (the existing one-line tests still hold). Ordering is
  `IdentityVocabulary.ClaimRanks` — operator 100 → target frame 90 → chat/guild 80 → behaviour 70 → **fact clause 75** → inference 60
  → npcdb/grammar/spell-data 50 → the two memory lanes 20, unknown codes at `UnrankedClaimRank` 45 — ties by strength then ordinal, cap
  **nine extra lines (ten with the head)** — raised from four/five on measurement, not for tidiness: a busy raider (`Coas`, `Reisil`,
  `Ammeren`) has nine to show and had four of them cut off. It is still a cap, and ranking is what decides WHICH ten. **A Spell row
  also says whether the shipped spell database knows the name** (`In the Spell DB` / `Not in the Spell DB`, worded beside the existing
  *In the NPC DB*, never a filename) because two of R21's three proofs say nothing about the data — askable on real captures only as
  "a rank newer than this build" vs "a name that only spelled like a spell". It sorts at 50 (below the fact clause, which stays first)
  and `IdentityVocabulary.IsSpellListClaim` stops it printing beside an existing *Name of a Known Spell* claim; one dictionary lookup,
  asked **only of rows that already read Spell**, never per name or per fact. On `eqlog_Kizant_xegony-2.txt`: 56 of 57 Spell rows print
  it, and the exception (`Frost`) carries membership in its head line instead — asserted both directions by
  `ASpellRowSaysWhetherTheSpellDatabaseKnowsTheName`. `EverySourceThatCanBeRankedIsRankedAndEveryRankHasAWord` holds `ClaimRanks` and `WhyWords` to
  covering **each other in both directions**, so a new rule cannot reach a hover and sort by accident. Two traps that look like free
  wins: an evidence line may **not** be just a verdict word, but it MAY name a source containing one (*In the NPC DB*) — that
  assertion was written too broadly and the test caught it (the app's word for a hostile name is **NPC**, never "monster" or "mob");
  **AND NOTHING IS LONG**: a static hover clause is capped at 28 characters (`EveryRuleWordHasAWordWorthPrinting`), measured max 27
  (*Name Begins With an Article*), because the first sweep shipped `Pet Cast Hobble of Spirits Snare VI` — 35 characters of spell name
  nobody verifies, now **Cast Pet Spell**. Only two clauses may outgrow it, and only by carrying their own proof's name: R4's cast and
  RegistrySeed's pet-map owner. Cell words stay ≤ 18 (*Owner in Pet Name* is the widest).
  and direction is decided from the **final (+∞)** verdicts so a sentence can
  never contradict the Type column beside it (charm windows are deliberately not consulted: *"Damaged Players"* means "hit names this
  capture called players"). **The cost law came with the request** ("I dont want memory to go up significantly"): `IdentityKind[]` +
  two `int[]` are resolved/incremented **pool-sized, never per fact** (an `IdentityAt` inside the 2.27 M-fact loop costs ~0.2 s a census
  does not have), `EntityTimeline.ClaimsOf` returns the timeline's own list **without copying**, and the only retained thing per row is
  the one tooltip string the pane already kept — nothing allocates for a single-claim row. Tests: `EvidenceLinesTest`,
  `IdentityVocabularyTest`, (Windows) `NamesTableTest.ExtraEvidenceRidesBelowTheProofLineInTheHover`.
  Reasoning and measured fixtures: docs/DesignNotes.md → "The cell carries the judgement, the hover carries the reasons".
- **The two identity panes share one right-hand strip** (2026-11): Player/NPC Identity sits with Pet Owners (`State="AutoHidden"`, `SideInDockedMode="Right"`) instead of
  tabbing with the fight list — and it is `AutoHidden`, never `Float`, because a floated window is a separate OS window that **cannot** share a tab strip, so "floating +
  tabbed together" has exactly one reading. Three laws ride on that. (1) **One width for both tabs**, from
  `NamesTable.DesiredPaneWidth()` (its four columns + row header + scrollbar — **≈ 568 px at 12 pt**; the Type term is `TypeColumnWidth()`, sized from the longest word that column can ever print, not a theme bucket) fed to `EQIdentityStripWidth` by `ThemeConfig`: a panel whose two tabs
  want different widths jumps on every switch, and a strip narrower than the table hides the Why column behind a horizontal scrollbar in a pane that cannot be widened past
  the panel — never replace the call with a constant. (2) **Markup loses to `dockSite.xml`**, so moving a pane means repairing the saved layout once:
  `MainWindow.MigrateIdentityPaneIntoRightStrip()` runs after `LoadDockState` beside the standing `npcWindow`/`mirrorFightWindow` fixups, is gated by the
  `IdentityStripMigrated` config flag (someone who moves it later keeps their choice; Reset Window State stays the escape hatch) and is wrapped in `try`/`Log.Debug` because
  a throw on this path costs the main window. (3) **A menu item shows or hides, it never relocates**: `MenuItemNamesClick` calls `SyncFusionUtil.ToggleWindow` like the
  "Pet Owners" item beside it; the old `SetState(namesWindow, Dock)` pulled the pane out of its strip into the middle of the layout.
  **The rule is enforced inside `ToggleWindow` now, because that ladder was the violator**: showing asks
  `SyncFusionUtil.ShowStateFor`, which consults the window's own declared `SideInDockedMode` FIRST — an edge returns
  `AutoHidden`, and only a window with no side earns Document/Dock/Float (`DockingPaneToggleTest`, Windows-only: attached
  properties need an STA thread, while a realized DockingManager needs a live window, so the DECISION is all a test can reach).
  The show test is `force || state == Hidden || !control.IsVisible` — a pane docked behind another tab is not on screen
  either, and under `state == Hidden` alone its menu item used to HIDE it. Two companions from the same handler: it starts no
  census of its own (`IsVisibleChanged`/`FollowSession` rebuilds on show; forcing `Refresh()` as well cost two classification
  passes per click), and `SetSideInDockedMode` BEFORE `SetState` is what makes two auto-hidden panes share one strip.
- **Identity WORDS live in `IdentityVocabulary` (Core), its coverage is asserted twice, and each tooltip LINE is one short phrase**: the Why column used to print `R15-healed` /
  `Prior:R7-graph` at 256 px — the rule book's private vocabulary rendered where a person reads instead of greps. `WhyWord` maps each code to two words (`Healed`, `Chat`,
  `Who`, `Class Spell`, `Owner in Pet Name`, `NPC DB`, `Chosen`, `Spell`, `Legacy`…) — the cell stays short and the HOVER names the store (*From the Old Verified List* for players.txt, *In the Pet Map as X’s*); **says the same word for a borrowed verdict as for a local one** (`Prior:R7-graph` → *Attacks NPC*;
  the ledger stores the rule so the word is true) and puts the borrow in the tooltip instead — `Attacks NPCs in previous log x2`.
  **STYLE IS ASSERTED, NOT ASKED FOR**: Title Case (first word capital, short words like in/and/the/by lower inside a phrase), and
  ONLY this application's nouns — Player, Pet, Mercenary, NPC, Spell. "Monster", "mob" and "our" are deleted vocabulary: the app says
  NPC everywhere else on screen, and a hover that says otherwise reads like another program's. R4's cell is `Class Spell` while R21's is
  `Spell`, because both were plain `Spell` for opposite reasons (this one CAST a rank; that one IS a spell), and the Type cell already
  carries direction for those rows, so `TypeWord(Merc)` says **Mercenary** to match the dropdown entry it preselects. The `(earlier)` cell marker was dropped
  on request after being tried twice: column width for a provenance footnote, and one cell carrying two mysteries. **The hover's head line is exactly one short proof clause** (no `Cast:`/`Earlier logs:` labels, no "Not in this log" — that read as an error on an ordinary hand-written verdict), and anything further is a SEPARATE line from `Row.OtherEvidence`, never a second clause glued on with a dash. And it is **never blank**: an unplaced name answers *"Nothing identified it yet — click the pencil to say what it is"*, because the empty cell is the row somebody hovers. `NameRow.Provenance` asserts that its **head line** carries no newline for every state (asserted again on the refusal row and the disagreement row, the two most likely to grow one; a refusal's own sentence **supersedes** the proof clause), while `ExtraEvidenceRidesBelowTheProofLineInTheHover` holds the cap — head plus at most four `OtherEvidence` lines, five in total. **No hover names a file** — the roster's opinion used to ride behind the proof (`From Chat · players.txt says Player`) and read as a second
  verdict from a source this window cannot open. What it pointed at is still a fact ON THE ROW (`Row.IsDisagreement`); the whole-capture count it used to feed was deleted together with the header that printed it, so no number about disagreements exists in either direction — printing nothing is the pane's law, and computing an unread figure for nobody is not a substitute. **An unmapped code echoes itself rather than being guessed at** — a fallback like
  "Evidence" would file a new kind of proof under an old meaning, and provenance is the one thing this pane must be honest about. `IdentityVocabularyTest` checks
  the explicit list in both directions (missing word *and* stale word) **and** runs the rules fixture asserting no row reaches the screen wearing its code; that
  corpus check immediately caught a code nobody had written a word for (`R3-chat`). **A code whose only producer is state a test clears cannot be caught by that corpus run**:
  `RegistrySeed` (identity coming from this application's own memory rather than from this capture) reached the screen as its own name because `PipelineHarness` calls
  `PlayerRegistry.Instance.Clear()`, so no fixture can ever produce one — it is therefore listed in `RuleWords` explicitly, reads **Legacy**, and hovers which store it came
  from (`On the roster this app saved`, or `In the Pet Map as Sancus's`). Say the word "Legacy", never a filename and never the seam's name. This matters because exactly two
  stores persist — players.txt (verified players) and petmapping.txt — while `_verifiedPets` and `_mercs` are cleared by `Init()` and filled only while a
  log is open, so the seed's verified-pet and mercenary branches are this-session memory and must never be described as saved state. Same discipline as the FCT and `HitLabel` vocabularies: a new rule arrives with
  a word here, and `HealedByCasters` (the number behind *Healed*) is gated exactly like R15 — distinct raid-side Strong casters, self-heals skipped, **0 means "not
  asked" rather than "nobody healed them"** (`CensusHealProofTest`).
- **A parser placeholder is never a person, and victims are named in groups.** `Labels.Unattributed` ("Unattributed Damage") fills
  an attacker slot the log left empty. "Reverse DS" (a damage-TYPE word) used to sit there and it read as an entity — **69,651 facts
  and 5.74 billion damage** on one capture, arriving with `Player · A Spell` because R21's spell recognizer matched a parser constant.
  `ParserUtil.IsUnattributedName` (that word plus the old `"Reverse DS"` plus `Labels.Unk`) is the ONE recognizer: R7 skips those names
  as attacker and counts them unclassified as defender, so the number stays on the board and the imaginary actor goes. Separately, a
  row's fact clause says WHO was hit in **eight closed sentences** (`IdentityVocabulary`: Players / NPCs / Pets, their three pairs, all
  three together, "Damaged Itself"): pets are not players (798 of `Ashenback`'s facts land on pets), and **self is its own group** — a
  raider whose entire raid-side tally was her own reflected beam must not hover "Damaged Players". Self is excluded from the raid
  bucket; pets still count raid-side for the *other* question (`TypeWordFor`: whose spell was it). Longest sentence is 30 characters =
  `MaxClauseLength`, asserted against the table in `EvidenceLinesTest`. A new victim group arrives with a word, a bit in that switch and
  tests — never by widening a phrase. And before writing a "missing damage" branch, run it through the PIPELINE: `DamageLineParser.ParseLine`
  takes the action WITHOUT its `[timestamp]`, and a stamped line yields garbage (that is how nine billion already-captured points looked
  lost; docs/DesignNotes.md → "Damage the capture never saw, and damage it invented").

- **Identity has two writes and one unset — and no veto.** `ClassificationCommands` is the whole vocabulary: `SetVerdict`
  (Player / Pet / Merc / NPC into `identity-overrides.txt` — the name it ships under; the pre-release spelling
  `mirror-overrides.txt` never reached an installed build, so nothing reads it and no migration code exists) and
  `ClearVerdict`, which the Type dropdown's **"Clear claim"**
  calls. That entry is the *only* unset, so "isn't there already an option to clear the name?" is answered by the same
  five words in that dropdown (plus `Remove` on the roster) and by nothing else. **Nothing writes or reads a `players.txt`
  `!Name` rejection any more.** That tombstone arrived 2026-09-28 16:05 (`9de0f230`) and its sole door — Verified Players'
  ✕, meaning the write inside `RemoveVerifiedPlayer` — lost its menu entry **76 minutes later** (`3b92e096`), three days
  after the newest tag `2.4.1`; no installed build could ever have made one, so it is deleted rather than parked and there
  is no file in the wild to migrate. Three consequences to hold: a removal is an **eviction**, so later evidence AND the
  prior ledger are free to speak about that name again (`ARemovedNameLeavesTheFileAndCanBeLearnedAgain`,
  `ALedgerFillsSilenceAndNeverContradictsEvidence`); `petmapping.txt` never cascades, because "X's pet" and "X is a
  raider" are two statements (`RemovingAPlayerKeepsItsPetMapping`); and a permanent "not one of ours" would arrive as a
  sixth dropdown word over `Set as NPC` — an assertion the rules can weigh — never as a silence that refuses evidence.
  Reasoning: docs/DesignNotes.md → "A veto nobody could switch on".
- **Roster memory is the ledger's second lane; one import fills it, and `players.txt` stays live until the commit that stops
  writing it.** `identity-priors.txt` carries per name both the rule verdict *and* "this application called this name one of
  ours" (`Ours`, plus the class it was seen casting) under the provenance word `Imported` — the code inside the file, which the
  Names pane renders as **Saved Roster** / *On the Saved Player Roster* (codes stay machine words; only what a person reads changed). Membership is **not** a verdict:
  Kind stays `Unknown`, `Record` can neither set nor upgrade the bit (a name on the list for a decade may be called Npc by
  every pass), and roster rows are exempt from the rule lane's 90-day and `MaxEntries` pruning — they age on the single dial
  `PlayerRegistry.StaleDays` = **200** against `DateTime.Now`, and `SeenAtS <= 0` is a statement that never retires.
  `RosterImport.ImportPlayersFileOnce(server)` runs at log open **once per folder**, gated on "does this ledger already carry
  roster rows" rather than a marker file — so an edited or restored players.txt is **not** re-imported, and the call costs
  one `File.Exists` afterwards. It writes **no stamps of its own** (the file's ticks survive verbatim; stamping today would
  restart every name's 200-day clock on the day it was copied), batches through ONE `FlushChanges()` instead of rewriting the
  ledger per row, **refuses a folder the loaded ledger does not answer for**, and **leaves the source file where it is** (its
  rename to `players.imported.txt` waits for the commit that deletes the writing code — archiving while the registry still
  saves it leaves a stale archive and a live file of the same names inside one session). **The load order in MainWindow's
  per-server block is overrides → priors → import → `PlayerRegistry.Init`, and it is not cosmetic**: `IdentityPriorStore.Save()`
  files every row under the server name *it* holds, and the registry used to load first, so a switch would have written one
  server's raid into another folder (the same guard is re-checked inside the import and inside the seed). `PlayerRegistry.Init()`
  then seeds from that lane after players.txt/petmapping.txt with `init: true` everywhere — **"a load is not a sighting"** is
  the law over all three files (`AddPetToPlayer` once ended with `AddVerifiedPet(pet)`, stamping **96.6 %** of petmapping.txt
  every startup so nothing could ever expire). `TryReadRosterLine` is the ONE reader of `Name[=<ticks>[,Class]]` — Init and
  the importer cannot drift about which lines are names — and the importer deliberately does **not** apply
  `IsPossiblePlayerName`: it wants letters only, so it would drop curated names (`Akini, Xanathan` = one summon, two masters);
  measured over 18 local roster files (**1,850 rows, 777 of them with a class**, zero undated, zero `!`) it refuses nothing
  today, and the asymmetry still decides — a junk row is one line, a silently dropped curated name is memory that disagrees
  with its own file. Ask "is this one of ours?" through **`IdentityLookup.IsOneOfUs(name[, t])`** — operator override → the
  open session's timeline → memory (the roster lane and `PlayerRegistry`, its in-memory mirror) — never a store of its own;
  the frozen person words answer before any store via `PlayerRegistry.IsPersonWord`, and a live `Unknown` **falls through**
  instead of answering no. Tests: `RosterImportTest`, `IdentityLookupTest`, `IdentityPriorStoreTest`. Numbers and reasoning:
  docs/DesignNotes.md → "The one-time roster import", "A load is not a sighting".
- **A spell effect is not a fighter (R21), and it takes THREE proofs to say so — and the kind it earns is `Spell`, not NPC**:
  `IdentityKind.Spell` (Core) is neither side. Three predicates were pinned to the NPC arm when it was added, because every board
  was measured under the old reading: `FightProjection.SideAt` (both branches), `EntityTimeline.IsRaidVictimAt` (`not Npc and not Pet`
  alone would have made an effect's name in a defender slot "one of us being beaten on" and silently widened the tank board) and
  R7's defender switch (`default` there means Player/Pet/Merc, so a Spell fall-through would hand out enemyhood to its attacker).
  The dropdown offers **Spell** as a verdict an operator can write; it credits and vetoes nobody, which is why it is safe to offer.
  Pinned by `ASpellNameBelongsToNeitherSide` and by the A/B in docs/DesignNotes.md → "The Spell kind" (57 rows move Npc→Spell on
  `eqlog_Kizant_xegony-2.txt`; fight rows **277** and row damage **792,266,411,943** identical, as are Σ begin/last/hits/taken).
  The same A/B surfaced a **pre-existing** nondeterminism worth knowing before you trust any global parity hash: two runs of the
  same binary over the same file give different *row-name sets* (same 277 rows, same every magnitude, no case twins) — compare
  boards per person, never by fingerprint. when the client writes `Goratoar has taken 18724 damage from Slicing Energy by .` there is no
  caster for the attacker field, so `DamageLineParser` substitutes **the spell** and sets `AttackerIsSpell` (`CombatCapture` carries it onto the fact) — and a spell in an
  attacker field is an actor as far as the identity rules are concerned. Measured on `eqlog_Kizant_xegony-09-03-26.txt`: **706** lines end `by .` and **135** read `from your <spell>.`
  (against 45,904 that name their caster normally); over its first 250 MB `ApplySpellEffects` places **49** names out of a 227-name pool and **471** of their facts are aimed at
  **mobs** — the raid's own dots on the raid's own targets, which is exactly the evidence R7 reads as "one of ours" (`Tsikut's Chant of Frost Rk. III` 24, `Strangle XVII Rk. III` 26,
  `Spiter Blood Rk. II` 21). That is how a curse reached the roster column as "Player | Our side"; the 717 raid-directed facts keep their side and only gain the truth (Type **NPC Spell**, Why `Spell`).
  Four laws. (1) **Early stage, Strong not Certain** — an independent identity (R6 npcdb, R9 charm, a name some spell also matches) keeps its better provenance
  (`BetterEvidenceOutranksTheSpellDictionary`), so a rule that fires on dictionary membership may never paint over evidence. (2) **Three proofs in the order a person would trust them: what the LINE said, then what the game said somebody CAST, then what
  `spells.txt` says.** The flag does not depend on this build shipping this expansion's data; the second feed is `X begins casting Y.` — `CastLineParser` already resolves that name (calling
  `AddUnknownSpell` when the database has no row) and hands it over as the aux of an `EvCast` evidence row, so ranks postdating this build are known the day they print. It is **pool-gated**
  (`facts.NameIndexOf(spell) >= 0`, a lookup that never interns — interning from a read path grows the pool whose size it is asking about): a name no combat line ever used gets **no timeline entry**.
  Measured on `eqlog_Kizant_xegony-09-20-25.txt`: **1,085** cast tokens, **41** refused by law (3), and **0 of the remaining 1,044 appear as an attacker or defender**, so claiming them would add ~1,000
  rows to a window about fighters and move `StateStamp()` on every first-time cast, buying a full rebuild over verdicts no board reads; where such a name *is* in the pool the claim still lands, which is why
  this feed is a rule and not a lookup. Its real place is the report — see the next bullet. (3) **A token wearing a creature's shape claims nothing**: articles and possessives (`LooksLikeEntityName`) are how
  this log names fighters, 1,085 trusted tokens include them, and a Strong spell verdict arriving first would freeze out R14's article rule and R5's ownership rule at equal strength
  (`ACastTokenWearingACreatureShapeClaimsNothing`). Learned while pinning that: `AddUnknownSpell` makes **R14 decline a name the spell store has heard**, so such a token can legitimately stay unplaced — assert
  "not a spell verdict", never "must be NPC". (4) **A spell row says A Spell and cites the line that said so** (`R21-spellshape` *No Caster in Spell Damage*, `R21-spellcast` *Seen Being Cast*, `R21-spelleffect`
  *Name of a Known Spell*, all three typing as **Spell** in the Why cell — never a side-word for something that is not an "it"), and it gets **no pencil**: `IdentityVocabulary.CanOverrule` consults the same `SpellNamed`/`IsSpellEffect`
  recognizer the rule uses — a rule and the words shown for its verdict must never disagree about where a spell name begins, same discipline as `EyeSummonOwnerInName`. **`… Feedback XII.` on `You have taken`
  gets NO verdict at all** — that is the local player's own spell bouncing back: NPC is as wrong as Player and no entity exists; R7 refuses those edges and R21 refuses the name, both pinned, because a guard on
  one path only is how this survived.
- **A remembered verdict loses to what this capture watched happen**: the pane listed `Asphyxiating Grasp Rk. III` as **Player**, and the mechanism was memory, not the graph — that name has **0 facts** on
  `eqlog_Kizant_xegony-09-20-25.txt` (pool index −1; it prints only as `Controla begins casting …`, in `… has taken N damage from … by Controla.` lines that do NOT set `AttackerIsSpell`, and in interrupt
  lines), and the prior store is consulted precisely when this log says nothing. So `ClassificationReport.BuildCastNames` collects the capture's `EvCast` aux names **only when a ledger is actually being read**
  (`priors.Count == 0` builds nothing) and `AddRow`'s last-resort borrow steps aside for it: the row answers `NPC · Spell · Seen Being Cast` instead of *"… in previous log"*. Deliberately not a rule claim (law 2 above,
  same numbers) and the ledger FILE is left alone — memory is refused per pass, not rewritten, so an operator's file keeps its history and the screen keeps the fresher word. Pinned by
  `ARememberedFighterThisLogWatchedBeingCastSaysAspell`. Tests: `SpellEffectIdentityTest` (real line shapes both directions, the flag-only rank, the feedback refusal, evidence outranks the dictionary, the pool gate,
  the grammar guard, the memory correction) and `IdentityVocabularyTest` (the words and the one-line law).
- **A verdict is edited in its own cell; a context menu hides verbs** (2026-11): the Names grid has **no** ContextMenu at all — right-drag is how a person grabs a
  block of rows out of a long table, and this pane's menu carried its one most-important verb (*clear my claim*) invisibly while offering two identical NPC lines.
  Type and Class each carry a pencil that opens a `ComboBox` in a popup over the clicked cell (`UiElementUtil.OpenCellPopup`, the same helper MainWindow's Pet Owners edit and DamageSummary's Group cell call - joined, not re-written, because its
  cell placement, sizing, focus-back and close hook are the parts the operator meant by "i put a lot of work into getting that working well"). Four laws: the option list shows
  **its own row's** vocabulary, not the whole one (`NameRow.TypeChoices` ← `IdentityVocabulary.TypeOptionsFor`; the five answers exist in `IdentityVocabulary.TypeOptions`, each written exactly once, "Clear claim" carrying
  `IdentityKind.Unknown` because that is what `ClassificationCommands.ClearVerdict` writes); the combo **preselects the row's current verdict** so the handler can
  refuse a click that changes nothing (a no-op must not spend a derive pass or rewrite identity-overrides.txt); a cell edit edits its cell — batching lives in the
  fight grids' menu alone now, this pane has no buttons at all; and **the class pencil appears only on a `Player` row** (`NameRow.ClassEditable`) because `SetDefaultPlayerClass` verifies
  the name AND writes players.txt — an icon on an NPC row is exactly the pollution this window exists to catch, and Pet/Merc rows have nothing to persist. Any write
  **Three kinds are trimmed by the same recognizers that hide a pencil** — one recognizer decides the menu AND the write guard (`TypeSelectionChanged` re-checks
  `row.TypeChoices.Contains(option)`), because two rules judging one dropdown is how a pane offers what its own write path then refuses: **Mercenary is not a verdict an
  operator can put on a name** (it is what `/target` reported — R3-merc/R13-merc — and typing it onto a raider moves her damage onto a column nothing else fills, so the
  entry survives only on a row that already reads Mercenary); **an eye is NPC or nothing** (`Eye of <name>` never acts — docs/DesignNotes.md → "Breadth of evidence, measured" — and a Pet row for it would sit beside its owner's real pets and split one person's output); and **`Clear claim` is never trimmed** (take-back must stay
  reachable). The row's own answer is always in the list because the popup preselects it: a value missing from its own dropdown reads as a blank cell. An operator's own
  claim (`R10-manual`/`Manual`/`Override`) keeps every entry — a wrong click has to stay correctable by another click.
  Two more from the same list: **some rows get no pencil at all** (`Overrulable` → `CanOverrule`) — a summon whose spelling carries its master and a row whose VERDICT is an R21 spell shape (current or `Prior:`-remembered) each have one
  right answer, and offering four wrong ones invites typing "Player" over `Sonic Bang`; an operator's own verdict always keeps its icon so a wrong click is never permanent (so
  *trim*, do not *hide*: `TypeOptionsFor` consults `CanOverrule` rather than duplicating it). **The refusal reads the evidence, never the string**: `CanOverrule` used to end in `!SpellNamed(name)`, which stripped the pencil from raid members whose
  names happen to be spells — measured on `eqlog_Kizant_xegony-2.txt`, **Strangle** (9,346 attack facts) and **Rune** (19,339), both Player via R4-spell, both names the shipped spells.txt answers for (level 128 / mask 8192, level 126 / mask 8192) —
  and a wrong verdict on a real person then could not be taken back from this window at all, the one failure mode every other rule here exists to avoid. A behaviour verdict (graph, heals, /who, chat, a cast) is somebody ACTING, so it stays
  correctable even when the word doubles as a spell. The same measurement refused a parser "fix": `Boom!` keeps its bang because spells.txt carries both `Boom` (13031) and `Boom!` (54752), and "is this string a spell NAME?" must consult
  `_spellsNameDb` only — abbreviations legitimately carry punctuation. Docs/DesignNotes.md → "A name that equals a spell is not a spell row". And
  **"Clear claim" removes the ledger entry too** (`IdentityPriorStore.Remove` beside `ClassificationCommands.ClearVerdict`), or the row comes straight back on the next pass wearing
  *"… in previous log"*, which is the opposite of what the click looked like it did. Any write
  here (dropdown **or** band icon) goes through `Reconcile()` = `RederiveAsync()` + `Refresh()`, like `FightTable.ApplyOverride` always did: the census is right on its
  own, but every other surface reads the LAST derive's snapshot. And `BuildNameCensus` classifies with a **throwaway** `ClassificationState` — it builds its own
  `EntityTimeline`, so carried aggregates have nothing to be incremental over, and sharing them would put a UI-thread walk inside the derive pump's cursor tables
  (census swallows, derive retires a stage after five failures). Column widths come from the theme table (`CurrentNameWidth` + short/medium sums + font-scaled icon
  allowance: **771 px fixed → ~385 px** at 12 pt) but through this pane's own `ApplyColumnWidths`, NOT `DataGridUtil.RefreshTableColumns` — adding `Type`/`PlayerClass`
  to its mapping list would resize every other grid that maps those words.
- **A fight is "still going" on the capture's clock, and one file answers all three meter questions**: the overlay's numbers moved to the
  derived engine first, which left it painting derived figures while `FightManager` still decided whether you ever saw them (open on launch,
  close-for-real-when-hidden, open-yourself-on-a-pull). Those now go through **`DerivedMeter`** — three members and **no dial at all**: the
  per-surface `OverlayDamageFromMirror` was folded into `EnableCombatMirror`, and that went out with the legacy engine (`e514ff6f`), so "which
  engine is the meter reading" has exactly one address and nothing left to switch (a `settings.txt` still carrying either word stops being read;
  "no session" answers *false* rather than falling back, because there is no second engine to fall back to) — over **`LiveFights`** (Core: a row is live when it is not `Dead` and its last activity in
  **either** direction window sits inside the gap). Three rules that must not be "simplified": (1) **now = the capture's newest event, never
  wall time**, because a log file is never rotated — measured spans between first and last damage fact inside one file: **329 h**
  (`eqlog_Kizant_xegony.txt`), **616 days** (Incogitable) — so a wall-clock rule is a rule about when the raid logged out, and it cannot see the
  dense end of a farm night (09-20-25: 3.0 h file, 143.9 min of it live); keyed to the capture, "live" means *at the end of what we have* — **except the meter's quiet dial**: how long a quiet board stays up is a promise in real seconds (mode 0 = the gap,
  else N) and is keyed on `DateTime.Now`, because a lag in file growth (client buffering, parse stall) freezes the capture clock and would hold the board
  past what the user chose — a capture-keyed pass was tried and reverted for exactly this. A file
  ending after a kill therefore answers **0 live rows**, which is correct — so `LiveFightsRealLogTest` reports that number and asserts each row is
  live at its *own* last activity instead. (2) **the gap is the meter's dial** (`TimeoutFor`: `OverlayDamageMode` 0 = on kill = `EngagementGapS`,
  else N seconds), so a window cannot be held open by one rule and blanked by another; pin it as *behaviour* (live at 29 s, dead at 31 s), since
  comparing two constant spellings of 30 passes even after somebody edits it to 300. (3) **the X closes the window and disables nothing — so the next damage
  brings the board back, onto the same seconds**: `DeriveEngine.LiveDamageObserved` fires while damage is fresh (`LiveFights.HasFreshDamage` = activity newer than the
  last announcement AND something still live), because legacy's `EventsNewOverlayFight` fired on **every damage line** of a fight (`UpdateIfNewFightMap` raises it whenever
  `DamageHits > 0`, outside the new-fight branch) and that is what its X felt like. Continuity needs the other half: `DamageOverlayWindow._meterWindowT` (the second the board
  adds up from) is **static**, since a derived board holds nothing between ticks and an instance field would be reborn at "now" — closing a window is not a reset, only the clear
  button and the dial's own quiet rule (`LiveFights.WindowStartFor`) move it. Both halves were defects in the first port: announcing one row per life kept a closed meter shut
  until the *next pull*, and the instance start made a reopened board forget the seconds already spent. Rate is per derive rather than per line, bounded by
  `DeriveCadence` — measured **~421 to 2,107** announces over a night carrying 1.9 M damage facts, each one a window-already-open check while the board is up. The per-life
  census still matters as the shape of a night: starts per life ≈ 1 (269/269 Kizant, 377/377 on 09-20-25, **4,542 over 4,519** on Incogitable with 23 restarts, longest mid-row
  pause **121 s**, which exists because rows split on silence in *any* traffic while the live rule reads only the two direction windows), and coverage **105 min** of a 329 h file
  (Kizant) / **3,875 min** (Incogitable). It is **static** on `DeriveEngine` (like
  `ActiveChanged`) because its reader outlives a capture, raised in its own `try` *after* `Derived` so a subscriber cannot make the derive look
  broken, and unsubscribed through `Subscribe/UnsubscribeOverlayFights()` rather than at scattered call sites. Visible differences from legacy are
  deliberate: launch opens only if the capture's last moments hold a fight (legacy opened if the 616-day file ever had one) and a hidden derived
  meter closes instead of lingering. Numbers, reasoning and the re-measure command: docs/DesignNotes.md → "A fight that is still going".
- **A derive pass continues where the last one stopped, and exactly two gates open the carry**: `FightProjectionCache` (held by `DeriveEngine`)
  keeps `ProjectionState` *and* the `FightFactIndex` from pass to pass — the fold is forward-only, so the open row per name, the last **closed**
  row per name (the pet→encounter chain `CharmPetRows` needs, since a pet's facts begin after that row ends), the completed rows and the name-keyed
  death queues all have to travel, and the index must travel *with* the rows it was filled beside or its ordinal lists describe a different walk.
  A carry is allowed when (1) `ProjectionState.Covers(facts)` still recognises the facts under the watermark — not "same length", a swapped table
  satisfies that (`ACoincidentallyEqualTableIsNotAContinuation`) — and (2) `EntityTimeline.StateStamp()` matches the stamp the carried rows were
  folded under; a moved verdict buys a **full rebuild**, never a stale row. **The stamp is an incremental content digest, summed in at insert time:**
  the walking version hashed 2,436 names for ~250 ms, more than the 433 ms fold it guarded (measured on `eqlog_Incogitable_xegony.txt`, 1.89M facts:
  full pass **732 ms** → continued pass **0-5 ms of projection**, with the unchanged rule replay's ~265 ms left as the floor). Three properties are
  load-bearing, all pinned by `EntityTimelineDigestTest`: replaying the same evidence must **not** move it (the rule book re-runs every pass and the
  mutators drop re-assertions — put a timestamp in a `source` string, or remove that dedupe, and every pass rebuilds while still looking fine);
  *content* must move it, not volume (an insertion **count** would carry rows across a reclassification); and **order must not** — the term is
  *added* (commutative) because `RegistrySeed` walks `PlayerRegistry`, whose enumeration order shifts as the registry grows, and added rather than
  XOR'd because one tuple can legitimately be recorded in both stores where an XOR pair cancels to nothing. **This leans on classification being
  append-deterministic**: replaying over a longer fact prefix reproduces every earlier insertion, so a rule that *revises* a conclusion in place, or
  any removal/revision API on `EntityTimeline`, must move the digest itself — nothing else would notice and the failure is a stale row with plausible
  numbers. `DeriveIncrementBenchmarkTest` (gated `EQLP_DERIVE_INCREMENT=<log>`) prints the projection/classification split.
  Classification IS incremental now too (`ClassificationState`, passed by `DeriveEngine` to `ClassificationRules.Apply`), and its design law is that the
  **timeline stays fresh per pass** — each rule carries only its OWN aggregates (cursors + edge/candidate tables + registered claim lists) and replays them
  onto the fresh store in stage order, so no stage ever sees another stage's claims that the from-zero replay would have hidden. Each gated rule (R5, R9,
  R15, R7, R18; evidence claims need no gate — they are pure functions of facts) compares this pass's **boundary `StateStamp()`** taken at its own stage
  entry against the stamp its aggregates were built under: moved verdicts ⇒ discard aggregates AND cursors and walk from zero. Three laws each learned by a
  real failure: (1) partial rules must **register every claim they make** for replay — R5 swept with a persistent `claimed` set but recorded nothing, so
  carried passes lost all `R5-owner` verdicts and `Odin` quietly reappeared as `Player/R15-healed` (a laundered source changes who owns the damage); (2) an
  unfreeze must reset the **cursor** with the aggregate — R7 cleared `GraphAggs` but resumed at the old fact cursor and rebuilt a graph from only the newest
  edges; (3) R15/R18's healer gate reads **Strong**, which `RegistrySeed` (strength 8) deliberately does not satisfy, so tests must give healers an evidence
  line (`EvJoinedRaid`) rather than a registry entry. A carried quiet pass over Incogitable measures **2 ms** against ~380 ms from zero; equality with a
  from-zero replay is the law — `IncrementalClassificationTest` pins it in fixtures (incl. a late roster join re-counting every older heal) and
  `CarriedPassMatchesAFullReplayOnRealLog` runs it over the gated capture. Numbers and reasoning: docs/DesignNotes.md → "A derive pass that starts where the last one stopped".
- **A fight's duration counts seconds inclusively**: `DerivedFight.DurationSeconds` is `Math.Max(1, LastTime - BeginTime + 1)` because
  that +1 is what the product already calls a duration — `TimeSegment.Total` (`end - begin + 1`) is the DPS denominator of every board number, and
  `FightManager`'s tooltip (`Time Alive: Ns`) used it, so an exclusive span made the grid print `00:00` for a mob hit once inside one second while its
  own summary said "Time Alive: 1s" and divided by it. Keep the arithmetic on the row, not in the formatter. Two exemptions: the
  `Inactivity > mm:ss` divider is a gap nobody fought in (stays exclusive), and `FightSummarySource`'s tooltip keeps the legacy tooltip's own expression
  because that summary's bounds can be window-clipped. Pinned by `DerivedFightTest`, which cross-checks the row against `TimeSegment.Total` so the two
  cannot drift apart quietly.
- **A death closes only a row that was alive when it happened**: the slain queue is keyed by NAME while one name carries many mobs
  (`A corrupted egg` is slain at 18:52:40/:45/:46/:52/:56/:56 on one capture), so a queued death can predate the row created after it.
  Applying it anyway killed that newborn row at its own first second — the grid listed the same name twice with the same begin, one row
  living 0 s and holding the killing blow's damage (legacy: one row of 6.32M; derived: 3.74M + a 0-second row of 2.57M). So a death closes a
  row only when `dt >= row.BeginTime`; an older entry is dropped and the next is asked, and **a row with no death inside it gets no death
  marker** — "still open" is the honest word, while inventing one is what wrote the fake fight. This guard is separate from legacy's inherited
  same-second rule (`dt < t`, so the killing blow lands in the dying fight) and both are needed. 19 fabricated rows removed on that capture
  (projected 289 → 270 vs legacy 262). Pinned by `ADeathOlderThanARowCannotCloseIt`, `ANameReusedBySuccessiveMobs_ClosesOneRowPerDeath` — assert
  who died inside which row, never "no zero-length rows": a mob hit and killed in the same second is real and looks identical on the span column.
- **A charmed mob is a pet, and a pet has no fight row**: `CharmPetRows.Visible` keeps `DerivedFight.RaidPet` rows off the
  grid (`DerivedFightRows`) while the encounter row the charm closed stays listed as `dead, charmed` — same rule that keeps
  ``Ziggy`s pet`` out of the legacy table. **Hiding is display-only**: `DerivedSnapshot.AllFights` keeps every row and
  `DeriveEngine.BuildSummaryInput` hands back the hidden ones through `CharmPetRows.WithHiddenPets`, because a board is
  built from whatever a click selected and 6 hidden rows on Incogitable carry up to **90,646,488** damage between them.
  A pet row's span begins *after* its encounter closes, so overlap can never reach it — that is what `DerivedFight.EncounterRow`
  (the row the charm closed, chained across a pull's reopening) is for; rows whose mob was charmed without ever being fought
  have no link (**3 of Incogitable's 6**) and come back on span overlap alone. `RaidPet` is **not** `CharmedOwned`: a charmed
  raid member carries `CharmedOwned`, stays listed, and reads `charmed` — because `"X has been charmed."` registers X as an NPC
  at R9-charm's *Strong* strength, the winning identity says NPC for her too, so the question FightProjection asks is whether
  the name has an NPC reason that is not the charm line (`EntityTimeline.HasIndependentIdentity`). Numbers: visible **4,076 of
  4,082** and **1,037 of 1,041**, every hidden row an article-shaped mob; reasoning in the same design-doc section. Pinned by
  `CharmRowProjectionTest` (`APetRowIsNotOnTheFightList`, `HidingAPetRowDoesNotDeleteItsDamage`,
  `WithHiddenPetsAddsPairedOrOverlappingRowsOnly`, `ACharmedRaidMemberStaysOnTheList`).
- **A finished load hands its doubling slack back once per doubling, at the gate**: both fact tables grow by
  `Array.Resize(×2)` and that law stays pinned (`HealFactCaptureTest` — extra growth steps are paid for on the parse thread,
  where half of ingest time goes), so a capture holds up to 100 % more row slots than rows: measured **54.6 MB of the 397 MB
  retained at EOF** on the 467 MB reference capture (2,286,368 facts in a 3,200,000-slot array; 1,247,984 heals in 1,600,000).
  `CombatCapture.CompactRows()` reclaims it under `_gate` (the lock every append takes) from the pass that classified;
  `RowArrays.TrimTo` is the shared mechanics with a **16-row floor**, because an empty array cannot grow — doubling 0 is 0 and
  the next `AddFact` would index past the end. Three laws. (1) **Never twice inside one doubling**: a trim leaves capacity equal
  to count, so the next fact doubles the buffer again and slack is instantly large; trimming per pass would copy the whole table
  once per new fact — quadratic where doubling was amortized. Hence the `_factsAtLastCompact` watermark and the
  `MinSlackToCompact` (4 MB) floor, pinned by `FactTableCompactionTest.TheCaptureTrimsOncePerDoublingAndNeverBetween`, whose
  middle step asserts **0 bytes** for "one fact past a trim, slack large again" (verified: deleting the watermark fails exactly
  that assert, nothing else). (2) **A shrink renumbers nothing**: `FightFactIndex` stores ordinals and the damage/tanking blocks
  are contiguous runs, so rows keep their indices — asserted row-by-row, plus "a span taken *before* the trim still reads its
  capture", which is why no reader-side lock exists (content identical, `_factCount` never moves down). (3) `EstimatedBytes`
  shrinks with it, and that is the revisit trigger's input, so it now says what the session holds rather than what it once
  reserved. Cost **34.9 ms** one-time, retained **397.2 → 342.6 MB (−13.8 %)**; numbers, the floor's reasoning and the
  `-l "console;verbosity=detailed"` probe recipe: docs/DesignNotes.md → "The slots a finished load stopped writing".
- **Healing is a second fact table, never extra columns on the damage one**: `HealFactTable` shares the damage table's name
  pool and its sequence counter (`IFactTable.NextSeq()`), and nothing else. One array of the union (42 B) sat **57 % full of
  zeros** at capacity — measured with a temporary probe, not estimated — because each side's tail fields are words the other's
  log lines cannot write; split, they are **32 B each** — and the heal row reaches 32 by *field order*, not by trimming:
  its ten fields want 30 bytes, but declaring `Seq` (int) before `TimeS` (long) opened four bytes of alignment pad and
  eight of tail pad for **40**, so a new field goes where its width earns a place (wide → narrow: long, `uint`s, `int`,
  two-byte fields, bytes) and `AFactIsItsMeasuredSizeNotWishes` is what sees it drift — the CLR may reorder fields, so a
  struct size is a fact about this build, and only the assertion makes padding change visible (32 MB on a 4 M-heal night,
  all of it overlapping any future chunking/compression work, so don't count that saving twice). The stronger reason is that `DamageFactsByFight` is a **contiguous
  ordinal run** (the spine is fight-major, time ascending inside it), so a shared array would force every heal to carry a
  `FightId`, and heals outside a fight are the healing report, not an edge case: 16,947 events on the reference capture, heal-only,
  because a heal opens no fight. Three laws, all pinned by `HealFactCaptureTest`: **one sequence across both tables** (each
  stream is ordered by it and the merged order is reconstructible — that is how heals get fight attribution later, without either
  table knowing about fights); **names resolve through the damage table's pool** or the same raider gets two indices and a merge
  splits her in half; **quiescence counts heals too** (`DeriveEngine.CapturedTotal`), else a healing-only stretch reads as quiet
  and fires a derive over facts still arriving. `HealFact.OverTotal` keeps the log's two meanings apart — with a paren it is the
  amount *after* over-heal, without one it is `0` = "the line said no more", never "zero was asked".
- **Only an INSTANCE method group allocates — measure before caching a delegate**: passing `X.Instance.Method` as an argument
  converts a fresh delegate every call (measured **64 B/line**, ~640 MB over a 10-million-line capture), while a
  non-capturing lambda and a **static** method group are both compiler-cached at **0 B** — so `LogProcessor` holds one readonly
  `_addMerc` bound in its constructor and leaves the two sibling callbacks written inline, and "cache every delegate" would buy
  two fields and zero bytes. Allocation traffic is not retained memory: no speedup is claimed (the probe found none inside
  run-to-run variation) and the number lives in docs/DesignNotes.md → "Allocation traffic is not retained memory".
  `LogProcessorIdentityCallbackTest` pins the **wiring** of both callbacks through the real consumer loop and deliberately does
  NOT assert allocation — a line's legitimate work (substring, `Split`, interning) is ~1 KB, so no GC counter separates 64 bytes
  from it and any threshold would be machine-specific. Same fixture, related law: **EQ writes `X joined the raid.`** (1,151 times
  across twelve captures; `has joined the raid.` **zero**, though the group line keeps its `has`) because the raid branch tests the
  whole prefix with a name check that refuses spaces — the tidier sentence enters the branch, fails the check and verifies nobody.
- **The read loop never runs on the UI thread, and two halves make that true**: every `await` in `LogReader` is
  `ConfigureAwait(false)` **and** MainWindow starts it with `Task.Run(...)`, because the first segment — which includes
  `logProcessor.LinkTo`, where the parse lane's own task is born — runs before any await can move it. The old shape was a
  dispatcher callback that read the file: `ReadLineAsync` suspends once per read buffer (147 KB ≈ 1,250 lines of EQ log), so
  ~1,250 lines continued inline on the thread that paints the window and the minority that suspended posted their continuation
  back anyway; with the handoff queue at its bound (100,000 items) a big open was mostly the UI thread **parked in `Add`**.
  `LogReader` wants no affinity (no dispatcher, no WPF type, and `FileSystemWatcher` already drives this same code on pool
  threads), so it takes none. Two follow-throughs: `StartAsync` walks away quietly when started on a cancelled/disposed reader
  (a queued start can now lose the race to the next open, and a throw inside a fire-and-forget task is unobserved), and
  `_chatSink.Init()` + `LogArchiveManager.QueueFileArchiveAsync` run on pool threads — both verified UI-free first. Load
  diagnostics live with it: `load: read loop on thread N, sync context = …` once per open (pre-fix that names WPF's context),
  and with `PerfJournal.Enabled` a `load: % | queue d/100000 | lines/s | gen | MB` line every ~2 s — **queue at the bound means the
  parse lane is the bottleneck, near-empty means the reader/disk is**, which is the one question Linux cannot answer here. Pinned by
  `LogReaderThreadTest` (Windows-only assembly): a `SynchronizationContext` that accepts posts and never runs them must still let a
  whole file through. Docs: docs/DesignNotes.md → "The read loop was running on the UI thread".
- **A recent-cast query may be bounded only by the store's own proof of order**: `RecordsStore.GetCastsBySpellName` answered
  "what did this ambiguous spell name cast recently" by allocating capacity for the spell's whole history and walking all of it
  (**149,232,044 entries visited across 214,487 queries** on Incogitable for 164,263 matches — ~700 examined per match; the bound
  scan reads 376,345 and returns identical matches). Now: result grown on demand (measured 0.77 matches/query — the old capacity
  was ~900× the answer), and the backwards scan breaks at the first entry older than the window **only while `CastHistory.AscendingTime`
  holds** — one backwards append retires the fast path for that spell name and the full walk comes back, because a restored or
  concatenated log really can hand one name's seconds out of order, and an assumed-order scan there silently returns *nothing*
  (no throw; a board that looks fine while an abbreviation resolved to the wrong rank). The window anchor stays **the store's
  newest cast** — not this spell's, not wall time: a quirk, and every existing resolution depends on it, so bounding cost was not
  the day to change which casts are "recent". `RecentCastQueryTest` pins *which casts come back* (window arithmetic with the
  inclusive boundary, newest-first because the caller takes the first usable one, same-second bursts intact, one name never
  borrowing another's, and eleven matches surviving a backwards append) and was verified against a deliberately naive unconditional
  `break`, which fails it — a guard no test can fire is not a guard. Visit counts are not asserted (only elapsed time sees them,
  which is machine-specific): docs/DesignNotes.md → "A question about eight seconds must not read the whole night".
- **The healing board's derived door is a record seam, not another Fight**: `HealingStatsBuilder` never reads a
  `Fight` — it takes `(time, HealRecord)` pairs and windows them itself against `AllRanges`, so the derivation reaches it
  through `GenerateStatsOptions.Heals`, filled by `HealSummarySource.Materialize`. Four rules. (1) **null means "say
  nothing about healing" (read the record store), a non-null EMPTY list means "there was none"** — inverting that is a
  derived selection quietly keeping last click's grid. (2) The window is the selection's own `AllRanges`, because a heal
  belongs to no fight (`HealFactTable` has no fight id, by design): a derived click means "every heal in this span",
  which is exactly what legacy does with the whole store. (3) The list must be **time ascending** — the builder locates
  its window with `FindIndex(first >= begin)` and walks forward, so an unordered input loses part of a segment without
  complaining; fact order is ingest order, which is why nothing sorts. (4) Materialize `OverTotal` **verbatim** (never
  `AskedFor`, or `MaxPotentialHit` doubles on every plain heal line) and fall back to `Labels.SelfHeal` for the spell,
  because that is what `HealingLineParser` stores when the line has none. Measured on `heal-board.txt`: raid **3,401**,
  Rune **2,776** across 5 heals with **2,388** overheal and a **4,012** ask, Kilsa **625** across 2 — identical through
  either door (`DerivedHealBoardTest`). That test clears `RecordsStore` + `HealingLineParser.ClearCaches()` in setup like
  `HealFactCaptureTest`: re-parsing a fixture inside one process doubles the board, and 3,401 becoming **6,802** is what
  that looks like when you forget.
- **A record's modifier mask is captured on the fact, because filters read it**: `DamageFact.ModifiersMask` rides in the padding
  bytes that `OverTotal` (a heal-only amount) had spent on damage, so `DamageFact` stays **32 B** — and a materialized record with
  mask `0` would have excluded *nothing*, making every derived total read high the moment one of the six `DamageValidator` settings
  (assassinate, headshot, slay-undead, …) is switched off. `HitLogViewer` shows that same mask as a column, so it is also what a
  reproduced row needs. This is the `HitRecord` rule applied one layer down: a field belongs to the type whose lines can write it.
  Numbers and the refused heal shapes (10,194 pet self-heals / 2,688 pet-healed-passive / 120 HoT = 2.9 % of heal-action lines, all
  healer-less or self-directed): docs/DesignNotes.md → "The byte that filters live in", "Healing joins the capture".
- **A fact carries what its own line says, never what the registry believed**: `CombatCapture` no longer asks
  `PlayerRegistry` about attacker/defender/healer/healed — those four `IsPetOrPlayerOrMerc` lookups (~15 M per large
  capture) stamped bits that RangeSpike's census showed to be **unreproducible**: 32 of 999 attacker names carried BOTH
  values of one side bit in a single sequential pass (`Betebeatz`: side=0 on 9,512 facts, side=1 on 5,342), 28 healer
  names flipped across 52,302 heal facts. When the registry learned something travels honestly as `IdentityEvent` (`Seq`,
  `TimeS`); what a name IS is decided per name by the rule book. Two laws from the deletion: **a flag on a fact is only
  ever line-derived** — `DamageFact.LineDerivedFlagMask` / `HealFact.LineDerivedFlagMask` name that set, and
  `FactFlagLawTest` asserts nothing outside it appears on either stream (verified players now produce a fact with *no*
  flags; prove a new stamp bites by adding one and watching three tests fail). **Retired bit values stay retired instead
  of being reused** — 4/8 on damage and 2/4 on heal hold an older build's opinion inside existing `*.spool` files, so a
  new flag takes a higher bit; the census is quoted in the table files so it is not re-derived. Measured reachability on
  `eqlog_Kizant_xegony-2.txt`: damage `OwnerInLine` **546,376** / 2,285,746 (R5 and `FightSummarySource.OwnerOf` read it),
  `AttackerIsSpell` **1,149**; heal `OwnerInLine` **0 of 1,243,469**, because `HealingLineParser` refuses a non-player-shaped
  healer (`Reisil\`s pet healed itself …` makes no record) — that bit stays as padding, and
  `APossessivePetHealLineReachesNoFactAtAll` names the shape to re-measure if the heal parser ever accepts it. Same class
  as the parser-callback law in `LogProcessorIdentityCallbackTest`: a registry *verdict* reaches the identity channel, never
  a fact. Numbers: docs/DesignNotes.md → "A fact carries what its line says, not what the registry thought".
- **Damage taken is a second ordinal set on the index, never extra columns on the damage one**: `FightFactIndex`
  keeps `_damageOrdinals` (facts aimed AT the row's owner) and `_tankingOrdinals` (facts that owner dealt), routed by
  the flag from the single `ownerSink?.Invoke(fact, ordinal, row, key == defName)` call in `FightProjection` — the
  same expression that splits `DamageToOwner` from `DamageByOwner`, which is what keeps a materialized block sum
  unable to drift from the row it belongs to. A derived row then carries both sides on one `Fight`:
  `DamageBlocks` / `DamageSegments` and `TankingBlocks` / `TankSegments` (+ `Begin/LastTankingTime`, `TankHits`,
  `TankTotal`), because the two boards key a row differently: raid-on-mob facts are away-facing, a player being
  beaten on is toward-facing. `PlayerDamageTotals`/`PlayerTankTotals` stay empty — nothing has read them since the legacy overlay engine was
  deleted, and filling them would mean running the six-setting `DamageValidator` filter a second time. Three laws, all pinned by
  `FightSummarySourceTest`. (1) **A row with no incoming damage still gets an empty tanking section** so the board
  lists every row it lists; the group id comes from `Sectionizer` and rides the row, so one walk stamps both sides
  of a mixed selection. (2) **An outcome taken with no number is still an outcome**: `TankHits` counts
  unconditionally, like the legacy tank branch's — assert the hit COUNT for zero-total shapes, never `> 0` on the total,
  because zero passes every magnitude test (that is how half of R9's friendly-fire exemption nearly shipped).
  (3) **Every fact of a row lands on exactly one of its two boards**, and a tank block's `Attacker` is always the
  row's own name — that is why `+Pets` folds a pet's output on either board.
- **Two numbers here are traps, both measured on `data/derive/tank-fight.txt`.** (a) `FightProjection` sums BOTH
  directions into `DerivedFight.DamageTotal` and splits them into `DamageToOwner` / `DamageByOwner`; it never fills
  `TankTotal`/`TankHits`/`TankRollup` on the *derived* row (those belonged to the legacy engine's row building; nothing
  writes them here at all). So "materialized tank total == row `TankTotal`"
  compares a real number against a zero nothing writes, and passes on any log that happens to contain no damage
  taken — hold materialized totals against `FightFactIndex.TankingOrdinalsFor` instead. That is exactly how the
  real-log test stayed green before the tanking side existed. (b) Legacy does not hand a mob's victim her own row:
  the legacy `FightManager.Get` keyed on `defender ? record.Defender : record.Attacker`, so on this fixture legacy produced one
  row, "an ice giant priest", whose `TankTotal` (834 = the mob's own three swings) is damage dealt, not taken, and
  `PlayerTankTotals` stays empty for Rune. Which is why `TankingStatsBuilder` groups by `record.Defender`
  (`StatsUtil.CreatePlayerStats(individualStats, record.Defender)`) rather than reading either rollup, and why the
  retired parity check was per raider over each engine's own block input (both read Rune 322 / Kilsa 512); today
  `FightSummarySourceTest` pins those absolute figures and the row-level split on the derived side alone. Related: the legacy parser answered a spell line naming a
  defender by re-deciding it (`record.AttackerIsSpell && defender` → `!IsPetOrPlayerOrMerc(record.Defender)`,
  line ~255), which is the one non-melee shape that reaches the tanking side at all. **A swing that makes no number
  is already in the capture** — do not go build one. The `X tries to <verb> Y, but …` family (`blocks!`, `dodges!`,
  `miss!`/`misses!`, `parries!`, `INVULNERABLE!`, `riposte[s]!`, `… absorbs the blow!`) is parsed into a record with
  total 0 and its own label, so it rides the same `EventsDamageProcessed` seam as damage and lands in `DamageFact`
  with the verb kept as subtype; `StatsUtil.UpdateDamageStats` then fills `MeleeAttempts`/`Misses`/`Blocks`/`Dodges`/
  `Parries`/`RiposteHits`/`Absorbs`/`Invulnerable` from that label on either engine, and the mask-dwellers (`Crit`,
  `Flurry`, `Rampage`, `Strikethrough`) follow because `DamageFact.ModifiersMask` came along. The retired
  `OutcomeParityTest` pinned both boards per raider, legacy vs derived (691/691 and 4,473/4,473 field matches),
  **with the absolute counts asserted first** - a lesson that outlives it: an equality alone
  would also pass on a capture bug that zeroed both sides. Two traps. (a) A row's `# Hits` is *not* its attempt count:
  damage-side `DamageHits` filters on `LabelTypes.IsHit`, which counts `Block` as a hit type and excludes the other
  five outcomes, while the legacy tank branch counted unconditionally (5 vs 7 on that fixture). (b) A column whose
  word the log never writes reads 0 on both engines — Kicker/Fumble/Vex are absent from the 2024 capture (Flurry 94,453,
  Riposte 13,709, Rampage 1,151), and that is the capture being faithful, not a gap to fill.
- **Identity rules read a closed vocabulary and never reason from their own guesses**: the four rules added after reading six
  captures (2022-2026) cold are pinned by `IdentityRuleExtensionsTest`, and each has a shape that looked safe until it was measured.
  **R5 ownership is five words, in one list** — `OwnerSuffixes` = `` `s pet``, `` `s warder``, `` `s ward``, `` `s familiar``,
  `` `s mount``, matched case-insensitively (the logs do print `` `s Warder ``), and `ClassificationRules.OwnerInName` is the single
  copy that `CombatCapture`'s fact flag, `FightDeriver`'s `PetOwner` and a record's `AttackerOwner` all read; a new word arrives with a
  `` `s <word>`` census over several eras, never from one log. The possessives that turned up and stayed out (`corpse`, `Acolyte`,
  `Heart`) are an NPC's own text, not ownership. Ownership is claimed by sweeping the **name pool**, not the facts, because a ward is
  overwhelmingly something the raid heals and never something it watches attack ("a summon nobody owns is a stray row forever") — and
  an owner text containing a comma (`Akini, Xanathan`s Warder` = one summon, two masters) claims the pet but must not invent a raid
  member named `Akini, Xanathan`. **R14 (the article is the game's own "this is a thing" marker)** claims `a `/`an `/`the ` names as Npc
  at **Medium**, yields to anything already holding Medium so npcs.txt keeps its better provenance (`ADatabaseNameKeepsItsOwnReason` is
  the only thing stopping this rule from relabelling every database hit as a guess about grammar), never claims a name the spell DB
  answers for, and loses to line evidence. **R17 (a consume line names a player)** reads the actor of
`Glug, glug, glug...  Bithika takes a drink from their Water Flask.` / `Chomp, chomp, chomp...  Bithika takes a bite from their Fresh
Fish.` — the **vessel is not part of the match** (any item) and both sound effect and verb match without case, because EQ writes them from
client emote text; server-qualified (`Name.Server`) names are rejected as everywhere else. Strong, not Certain, so `Targeted (NPC)` still
wins a name that somehow drinks. Measured: 370 lines / 67 distinct actors over three captures, none article-shaped, one sharing an entry with
npcs.txt; it is the *only* evidence for **6** names (Incogitable) and **4** (Kizant-01-06-24) while player recall stays **87.9 % / 85.6 %**.
`PreLineParser.TryGetConsumer` is the single recognizer (it feeds the legacy verified-player registry too). **R7's unknown allowance is a share (2 %) of that attacker's own edges**, while opposition
  from the other side stays an absolute veto — that veto is what keeps a charmed raid from inventing NPCs out of players; one unnamed
  defender used to be enough to leave `Squirticus` (8,909 attack edges, a handful pointing at names nothing had
  yet named) unclassified for a whole capture. **R15 (our side keeps
  healing it) accepts a healer only at ≥ Strong**, so no Medium inference can launder a name onto our side; it needs ≥10 heal lines
  from ≥2 such casters, and any swing back above the friendly-fire share vetoes it, because raid AoE waters the mob stack too
  ("Yokii healed an arcborn wraith for 2 hit points") and a boss answers for itself by swinging at the raid. R15 alone moves **6-14 %**
  of each capture's damage facts onto a side with **zero** overlap against `Targeted (NPC)` or `npcs.txt`; derived-vs-legacy parity is
  unchanged (2024: 691/691 field-matched, Incogitable: 4,473/4,473). **`Targeted (NPC)` means "not a player", never "not ours"** — it
  fires on pets: `Useless` reads `Npc:R1-target` in Incogitable with 303,554 attack edges while the raid healed it 47,752 times from 52
  casters (`Dragon`, `Bark`, `Dangle`, `Speedbump`, `Cutie`, `Funky` likewise; 0.4-18.8 % of a capture's facts sit on the enemy column this
  way). What tells a pet from a mob that merely gets raid AoE is WHO heals it: pets take heals from **19-52 distinct casters**, and every
  genuine hostile measured (Zelnithak, Captain Kar, Rufus Invictus, Tallongast, `an echo`) tops out at **10** — so heal volume alone must never
  flip an NPC verdict, caster breadth is the discriminator. Which also means R15's yield is mostly custom-named pets, not unverified people
  (20 of the 25 registry pets in the 2026 capture read `R15-healed`; petmapping.txt holds 96 owner pairs while the log's own possessive lines
  prove 18) — `Player` is written as "raid-side, never verified", and owner attribution needs the registry. Tests are cold by construction:
  `PipelineHarness` calls `PlayerRegistry.Instance.Clear()` and only `MainWindow` ever calls its `Init()`, so `RegistrySeed` contributes nothing
  in any measurement here. Two names wearing two jobs is not a collision in this table, because identity is keyed on the NAME: `Sancus` is a
  raider (in players.txt, and the subject of "Sancus is called to it owner.", which names the SUMMONER — next bullet), while `Sancus`s pet`
  is its own row that R5-owner claims as Pet. Numbers and the reading procedure: docs/DesignNotes.md → "Breadth of evidence, measured".
- **"X is called to it owner." names the SUMMONER, never the summon.** The sentence argues the other way, and this rule read it the wrong way
  for its whole life: `R5-called` stamped Pet Certain and put raid members on the sheet as pets (Beorun, Romance, Sancus, Segejin).
  Measured instead: **33 of 33** occurrences in `eqlog_Kizant_xegony-09-20-25.txt` and **15 of 15** in `-01-06-24` sit within three lines of
  that same name's own ``X begins casting Summon …`` (`Romance begins casting Summon Companion II.` → `Romance is called to it owner.`), and
  every subject across six captures casts player spells (Romance **13,958** cast lines, Beorun 7,215, Sancus 3,434) and never swings — a pet-subject
  form was never observed once. So the case is `EvCompanionCalled` → **Player at Strong** (like R17's drink and R19's eye: a behaviour yields to a
  `Targeted (NPC)` frame) under code **`R5-companion`**, with **no Friendly interval** (a summoner's side needs none; the interval this rule used to
  write is what made the old reading look plausible in `SideAt`). Two follow-throughs. (1) The ledger refuses `R5-called` rows **by name** at load and
  rewrites them out: a remembered `Pet` for a real person holds them off the board for the whole 90-day window, and no live evidence should have to
  outvote it (`ABugEraCompanionClaimIsRefusedAtLoadAndRewrittenOut`). (2) Ownership does NOT come from this line — it names one participant, so the
  pair still arrives from the possessive words and petmapping.txt. Numbers: docs/DesignNotes.md → "The companion line names the summoner".
- **Three shapes of the Names and Identities pane, all pinned in `NamesTableTest`.** (1) Column order is **Name, Type, Class, Why** — the two cells
  that answer to a click sit together and the read-only explanation goes last. (2) A row that offers **no** pencil still **reserves its width**:
  `PlaceholderVisibilityConverter` answers `Hidden`, never `Collapsed`, because Collapsed hands the icon's ~20 px back and the words in that column stop
  starting at the same pixel — a difference in what a row *allows* (spell effects and self-naming summons get no Type pencil; only a Player row gets a
  Class one) was being rendered as a ragged left edge (`ARowWithoutAPencilStillPaysForOne`). (3) **No hover names a file.** The roster flag
  ("players.txt says Player", "in players.txt") is gone from `ProvenanceFor`: it read as a second verdict from a source the operator cannot open, and worst
  on rows the rules had merely classified. The disagreement itself survives as a row fact (`Row.IsDisagreement`: roster says Player, verdict says NPC) with **no aggregate over it** — the census-wide counter was deleted with the header that used to print it, and a per-row badge would paint half the list; the two proof clauses that quoted filenames say what those files are (`On the NPC List`, `The Name of a Spell`).
- **An eye is not a combatant; hitting your own eye says the striker is a player.** `Eye of <name>` is the magian
  summon (`Eye of Zamul`, ranks named after people: `Eye of Zomm`) and it never acts — **0** hostile lines in eight
  captures, all **348** damage lines against one worth exactly **1 point**, **346 / 348** of them struck by the raider
  whose name the eye carries. So eyes are refused at the door of **both** fact tables (damage always was, through
  `DamageValidator.InIgnoreList`; heals were not, which is how one `Jondolar healed Eye of Shennron for 148 points`
  line left an `Eye of X | Unknown` row in the identity list), and the eye branch of `DamageLineParser` reports
  `EvEyeOwnedStrike` on `PreLineParser.ReportEvidence` → **R19** claims the *striker* Player at Strong. Two refusals,
  both deliberate: the **cast line is not evidence** (`begins casting Eye of Zomm` prints even where no eye ever
  materialises — Incogitable has 6 casters and 0 entities — and one magian can cast on another player's eye), and
  **ownership is never modelled**, because anyone can kill an eye (5 of 376 eye deaths name a foreign raider) and a wrong
  owner is what poisons R15. One recognizer, `ClassificationRules.EyeSummonOwnerInName`, is shared by the ignore gate and
  the rule (a meter and a rule must never disagree about where an eye name begins), and it refuses **before**
  `CheckOwner`/`CombatCapture.AddDamage` — leave the shape in the tables and the day a sixth word joins `OwnerSuffixes`,
  R5's name-pool sweep adopts every eye in the raid. The three eyes that *do* count (`Veeshan`, `Despair`, `Mother`) are
  legacy's own exceptions and the list is closed: `TheCountableEyeListIsThreeWordsNoMore`. Measured yield: R19 wins **2 names** (`Depravity`, `Pixnn`) and both also held an
  equal-strength chat/presence claim, so nothing moved Unknown→Player on this corpus — it is a last resort plus a
  ledger entry (`R19-` is in `IdentityPriorStore.RememberedRules`). Numbers and reasoning:
  docs/DesignNotes.md → "Breadth of evidence, measured" (the eye census); pinned by `EyeSummonTest`.
- **A versioned cast line is tier-1 identity; the pet's Snare claims Pet, not the player.**
  `EQDataStore.ClassSafeSpellFamilies` (closed at **15**: Boastful Bellow/Boastful Conclusion → Bard,
  Frenzy/Paragon/Focused Paragon of Spirit + Hobble of Spirits → Beastlord, Tireless Sprint → Berserker,
  Celestial Regeneration/Focused Celestial Regeneration → Cleric, Spirit of the Wood/Nature's Boon →
  Druid (their casters Tilwedarx/Tuona cross-check the two families), Gather Mana/Eldritch Rune →
  Enchanter, Battle Leap Warcry/Battle Leap → Warrior **or** Berserker) claims the caster of
  `X begins casting <family> <roman>.` as Player Certain through R4-spell. **The rank is part of the
  match and must be the whole tail**: versionless never prints (measured: 0 lines) and matches nothing,
  and a rank absent from `spells.txt` passes the text gate but fails the data gate — new
  expansion content is **silent, never guessed**. A family entry may be **multi-bit** (War|Ber): those
  ranks claim Player Certain while `GetSpellClass` **stays null and no class is ever written** —
  seeding puts them in `_classAmbiguousFamilyRanks` only, and `ClassificationRules.IsClassSafeCast`
  (internal for tests) accepts label-or-ambiguous as "the data knows this rank". Assert both directions
  (`IsClassSafeCast` true + `GetSpellClass` null): the null half is what fails silently if seeding ever
  regresses into the label path, and a coin-flipped class column is worse than silence. Census 2022→2026 + THJ: 17,304 Bellow + 14,689
  Conclusion lines, ~25k Frenzy/Paragon, **zero article-shaped and zero possessive-pet casters**;
  engine yield 3 real names with no other evidence (Chori/Dram/Metalplayer, Unknown→Player).
  **`Hobble of Spirits Snare <rank>` is a different spell than `Hobble of Spirits <rank>`** — the pet's
  (~24k lines: Stormclaw/Cutie/Bark) vs the beastlord's own (10 lines, cast by Paragon casters): the pet
  form claims caster **Pet Strong "R20-petspell", no owner invented**; the rank anchor is exactly what
  stops the player prefix swallowing `Snare VI`, and a verified Player claim outranks the Pet one.
  Stormclaw's own `says, 'My leader is Beorun.'` is already read by legacy `ChatLineParser` — ownership
  keeps coming from those lines and the possessive words, never from this rule. **Targets claim nothing:**
  Paragon healed raid members (Tolzol/Covennx land melee procs), so no target-side or owner↔pet inference
  rides on heal-by shapes. **`Finishing Blow` was refused as a rule** — 8,204 lines, zero mob/pet
  attackers, but every attacker already held a stronger claim (zero yield = bug surface), so modifier
  masks stay stats-only. Class flows with no new system: the seeded `_spellsToClass` feeds
  `CastLineParser`'s existing registry `SetActivePlayerClass` path. Pinned by `IdentityRulesTest`
  (`VersionedFamilyListsAreFifteenAndOneNoMore`; `DerivedFightsMatchLiveParseFactForFact` is where the
  Hobble split lives in a test). Battle Leap Warcry itself prints
  **zero lines** in every local capture — it ships on user assertion plus the spell DB's War|Ber column,
  with plain Battle Leap's 470 zero-article casts as nearest corroboration; numbers:
  docs/DesignNotes.md → "A versioned class-spell family claims its caster".
- **The frenzy verb claims a class, never a person — and classes are time-bounded.** `X frenzies on Y for N`
  is the berserker frenzy AA (85,685 lines, zero article-shaped actors; a monster's frenzy reads
  `is struck by a frenzied assault` and names no attacker), so `DamageLineParser`'s melee branch — gated on
  `ParserUtil.IsHitTypeAddition`, the same seam that skips the verb's `on` — writes
  `SetActivePlayerClass(attacker, GetClassLabel(SpellClass.Ber), 2, lineTime)` and **no identity**: no
  verified-player, no timeline verdict. Whoever frenzies *is* a berserker, not necessarily a player; class
  changes mid-log (Covennx: frenzy lines only in the 2025/26 captures), which is why class lives only in
  `PlayerRegistry`'s **time-windowed** records: confidence 2 (peer of `CastLineParser`) commits the first
  window immediately but an opposing class needs `LowConfidenceThreshold` = **8** sightings, committed from
  the first odd sighting's own second — one stray cast cannot flip a class, a real reclass flips in a pull —
  and `GetPlayerClass(name, t)` reads per second (before the first record it answers that record; not backdating).
  Headless class writes need the App.xaml.cs host hook (`CombatRecordLookup.IsValidClassName`) wired or they
  fail in silence — see `FrenzyClassTest` setup. Frenzy Strike AA casts (`... VII Caza`) stay unused: the AA
  rank word breaks the roman-rank anchor; extending it is a measured step of its own. Numbers:
  docs/DesignNotes.md → "Breadth of evidence, measured" (the frenzy census).
- **A cold miss is usually a name this capture never puts into combat — measure the shape before writing a rule for it**
  (2026-11). On Incogitable, of the roster names a cold pass cannot place: **1** has any attack fact, **4** take any heal,
  **0** reach R15's floor — they live only in tells, achievement pings, buff lines and cast lines. So "we still see them
  attacking what everyone attacks / being healed by players" is not a missing rule: **R7-graph and R15-healed are those
  rules**, and they place 147 of the 165 placeable names. `players.txt` is also a **superset of any one night** (165 of its
  209 rows are named anywhere in Incogitable, 62 in another capture), which caps what any single-file classifier can score
  — quote recall against named rows, not file rows. Three unread chat shapes were then counted on every local capture:
  **`Your guildmate X has …` is clean** (0 names among them read Npc/Pet on four captures, article-free, recovers 7/3 roster
  misses) and is the one candidate rule; **`X tells …` is refused** (`Bane`, `Paul` are in npcs.txt while their lines are
  bazaar hailers — identity is keyed by NAME, so spelling cannot fix it); **`X begins singing …` is refused**
  (`Shalowain begins singing her Rhapsody of Pain.` is an NPC placed by npcs.txt *and* by being attacked). Numbers and the
  per-capture table: docs/DesignNotes.md → "What the cold misses actually are".
- **A gated real-log test is disposable; a print harness is not a test.** The `EQLP_*` gates exist to answer one question
  over a local capture. Once it is answered, **delete the gate and move the numbers to docs/DesignNotes.md** — that is
  where findings live, and an assertion-free `[TestMethod]` that skips everywhere proves nothing except that it printed
  ("Passed" on zero `Assert.` calls is not coverage; a stale gate also rots: it cites paths and plans nobody has).
  Keep a gated test only when something must be *asserted* on real data no fixture can reach: `LiveFightsRealLogTest`
  (a row live at its own last activity), `IncrementalClassificationTest`/`DeriveIncrementBenchmarkTest` (carried pass ==
  full replay, plus the projection/classification split named as the re-measure command), `UnrowedFactsTest`,
  `MeterBoardCostRealLogTest`/`RealLogBenchTest` (named cost probes) and `LiveTailChangeProbeTest`
  (`EQLP_LIVE_TAIL_PROBE` — replays a capture as growing prefixes and prints which rows a pass actually changes; its answer
  killed the equality-gate proposal and is in DesignNotes). Deleted on those terms:
  `RegistryRebuildTest` (phase exit gate satisfied, recall figures recorded in DesignNotes) and `HitByNpcCensusTest`
  (premise dead with the rule it proposed) — so a comment or doc that cites a deleted census is a bug to fix, not a
  reference to preserve. Before deleting anything here, run it: a one-off measurement is worth more executed twice
  than argued about (docs/DesignNotes.md → "What a capture proves with empty memory").
- **Identical input gives identical output inside one process, and it is asserted** (2026-11; this replaces the
  "one real-log capture per process" discipline that was written down while the cause was unknown):
  `CaptureReproducibilityTest` parses a capture twice and compares verified-pet set, row count, tanking-side hits, unrouted
  facts, both damage sums and every name's verdict+source — fixture always, real capture under `EQLP_REPRODUCIBLE=<log>`
  (green on Incogitable: pets 182 = 182, tank hits 101,254 = 101,254). The drift was one store missing from
  **`PipelineHarness`'s reset list**: `EQDataStore.FindPreviousCast` resolves an ambiguous spell-name match through
  `RecordsStore.GetCastsBySpellName`, so a previous parse's leftover cast records changed how many rows a line resolved to, and
  `CastLineParser` registers a custom-named pet **only when that resolution yields exactly one row** — measured directly as the same
  spell returning n=1 on pass 1 and n=6/16/24/40 on pass 2 for identical lines. Production never had the gap (`RecordsStore`
  registers with `LifecycleManager`, so a log close/open clears it). Do not remove that clear from the harness, and do not
  re-isolate the things already ruled out by measurement: the ledger, the override file, the learned-spell set, a freshly
  constructed `EQDataStore`, `HealingLineParser.ClearCaches()`. Reasoning and the rest of the numbers: docs/DesignNotes.md →
  "Pet-ness is a parse side effect". **The related rule idea stays refused on measurement**: "a spell whose `spells.txt` Target is
  Pet/Pet2 landed here, so this name is a pet" — over Incogitable those 65 candidate names read Pet 26 / **Player 17 (including the
  raider Beorun)** / Npc 13 / Unknown 9, because pet-target rows also land on raiders and mobs. Owner folding needs the possessive
  words and petmapping.txt; this shape is not evidence.
- **`EQLogParser.Wpf.Test`** is the Windows-only assembly (WPF and Skia surfaces, `EnableWindowsTargeting`): it builds everywhere but its
  tests need Windows to run, so `dotnet test` on the other assembly says nothing about it. Build it explicitly when touching app UI code.
- **Releases**: when touching `sign.cmd` or `EQLogParserInstall/*.iss`, read `docs/ReleaseChecklist.md`

## Post-Implementation Checklist
After completing work, verify:
- **Zero build warnings**: the solution-wide count command above prints `0`, and nothing was suppressed to get there (per
  CodingStandards.md → "Build Warnings"); a file that annotates with `string?`/`T?` opens with `#nullable enable annotations`
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
