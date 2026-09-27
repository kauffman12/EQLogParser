# Project Rules & Guidelines

You are an expert AI assistant tasked with maintaining this C#/WPF/.net 10.0 project.

## Core Principles
- **Follow Coding Standards** read and follow the standards under docs/CodingStandards.md
- **File structure**: Prefer small files and atomic commits.
- **Git**: Commit with detailed messages, but **never push to remote**. Pushing is the user's call.
- **Docs are local discussion**: most files under `docs/` are untracked working documents (see `.gitignore`). Never `git add`/commit new files there — only the ones already tracked (`git ls-files docs/`) belong in the repo.
- **Searching**: All files are under the current directoy. 
- **Do not** add heavy dependencies without explicit user approval.

## Build Environment (Linux, this machine)
- .NET 10 SDK (10.0.4xx) lives in `~/.dotnet`; prepend it on every shell: `export PATH="$HOME/.dotnet:$PATH"`. The system's dotnet 8 will not satisfy `global.json` (`"10.0"`, `rollForward: latestPatch`).
- Full solution on Linux: `dotnet build EQLogParser.sln -p:EnableWindowsTargeting=true` (flag lets the WPF projects cross-compile; no source changes needed).
- Tests: `dotnet test EQLogParser.Test/EQLogParser.Test.csproj` is the non-WPF suite (~1,120 tests, plain `net10.0`). `EQLogParser.Wpf.Test` targets `net10.0-windows` and only runs on Windows.

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
- **The mirror's damage summary decides direction, and never "fixes" the pet gap**: `MirrorDamageIndex` is filled inside
  `FightProjection` through `FactOwnershipHandler`, whose flag is `towardOwner` — the row's own name was the defender,
  which is the same comparison that splits `DamageToOwner` from `DamageByOwner`. It is **not** "the attacker was
  player-side": an unclassified name hitting a known NPC belongs in those blocks too (legacy agrees), and one expression
  for both is what keeps an index sum from drifting from the row's number (`MirrorSummaryFightsTest`). Two silent-nothing
  traps live in materializing: seed-and-compare on `double.NaN` leaves a fight with no end (`time > NaN` is false, so the
  summary divides by an empty window and nothing throws), and a record's `SubType` may never be null —
  `StatsUtil.UpdateDamageStats` keys a `ConcurrentDictionary` on it, inside `DamageStatsBuilder`'s catch that logs and
  carries on, so a null is an empty board. A materialization test therefore asserts real time bounds **and** pushes the
  result through the real builder. On `mini-fight.txt` the derived board reads **+19.5 %** over legacy with every raider
  legacy placed reading identically: two pets whose owner the registry never learned contribute nothing to the legacy
  board at all, while the line's own ownership word (`ClassificationRules.OwnerInName` → `AttackerOwner`) folds them under
  their raiders here. Do not force the two sides into agreement — the gap is the experiment; the test pins its shape.
- **Healing is a second fact table, never extra columns on the damage one**: `HealFactTable` shares the damage table's name
  pool and its sequence counter (`IFactTable.NextSeq()`), and nothing else. One array of the union (42 B) sat **57 % full of
  zeros** at capacity — measured with a temporary probe, not estimated — because each side's tail fields are words the other's
  log lines cannot write; split, they are 32 B and 40 B. The stronger reason is that `DamageFactsByFight` is a **contiguous
  ordinal run** (the spine is fight-major, time ascending inside it), so a shared array would force every heal to carry a
  `FightId`, and heals outside a fight are the healing report, not an edge case: 16,947 events on the reference capture, heal-only,
  because a heal opens no fight. Three laws, all pinned by `MirrorHealCaptureTest`: **one sequence across both tables** (each
  stream is ordered by it and the merged order is reconstructible — that is how heals get fight attribution later, without either
  table knowing about fights); **names resolve through the damage table's pool** or the same raider gets two indices and a merge
  splits her in half; **quiescence counts heals too** (`MirrorSession.CapturedTotal`), else a healing-only stretch reads as quiet
  and fires a derive over facts still arriving. `HealFact.OverTotal` keeps the log's two meanings apart — with a paren it is the
  amount *after* over-heal, without one it is `0` = "the line said no more", never "zero was asked".
- **A record's modifier mask is captured on the fact, because filters read it**: `DamageFact.ModifiersMask` rides in the padding
  bytes that `OverTotal` (a heal-only amount) had spent on damage, so `DamageFact` stays **32 B** — and a materialized record with
  mask `0` would have excluded *nothing*, making every derived total read high the moment one of the six `DamageValidator` settings
  (assassinate, headshot, slay-undead, …) is switched off. `HitLogViewer` shows that same mask as a column, so it is also what a
  reproduced row needs. This is the `HitRecord` rule applied one layer down: a field belongs to the type whose lines can write it.
  Numbers and the refused heal shapes (10,194 pet self-heals / 2,688 pet-healed-passive / 120 HoT = 2.9 % of heal-action lines, all
  healer-less or self-directed): docs/DesignNotes.md → "The byte that filters live in", "Healing joins the capture".
- **Identity rules read a closed vocabulary and never reason from their own guesses**: the four rules added after reading six
  captures (2022-2026) cold are pinned by `MirrorRuleExtensionsTest`, and each has a shape that looked safe until it was measured.
  **R5 ownership is five words, in one list** — `OwnerSuffixes` = `` `s pet``, `` `s warder``, `` `s ward``, `` `s familiar``,
  `` `s mount``, matched case-insensitively (the logs do print `` `s Warder ``), and `ClassificationRules.OwnerInName` is the single
  copy that `CombatMirror`'s fact flag, `FightDeriver`'s `PetOwner` and a record's `AttackerOwner` all read; a new word arrives with a
  `` `s <word>`` census over several eras, never from one log. The possessives that turned up and stayed out (`corpse`, `Acolyte`,
  `Heart`) are an NPC's own text, not ownership. Ownership is claimed by sweeping the **name pool**, not the facts, because a ward is
  overwhelmingly something the raid heals and never something it watches attack ("a summon nobody owns is a stray row forever") — and
  an owner text containing a comma (`Akini, Xanathan`s Warder` = one summon, two masters) claims the pet but must not invent a raid
  member named `Akini, Xanathan`. **R14 (the article is the game's own "this is a thing" marker)** claims `a `/`an `/`the ` names as Npc
  at **Medium**, yields to anything already holding Medium so npcs.txt keeps its better provenance (`ADatabaseNameKeepsItsOwnReason` is
  the only thing stopping this rule from relabelling every database hit as a guess about grammar), never claims a name the spell DB
  answers for, and loses to line evidence. **R7's unknown allowance is a share (2 %) of that attacker's own edges**, while opposition
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
  in any measurement here; one unfixable-by-registry case is a pet *named like a raider* (`Sancus`: in players.txt, owns `Sancus`s pet`, and
  stamped Pet Certain by "Sancus is called to it owner.") because identity is keyed on the name, not the owner. Numbers and the reading
  procedure: docs/combat-mirror-design.md → "Fourth audit".
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
