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
- **Corpses need no rule; do not add one.** A raised raider-corpse (`<name>'s corpse rises to serve <master>.`, *Wake the Dead*) never
  fights under its own name — it attacks as ``<master>`s pet``, exactly like any swarm pet (server-verified on a necro called Kazcro; in six
  captures a master's possessive-pet share jumps ×3.6–×8.7 right after a raise burst while controls move ≤ ×2.3), so the existing R5 owner
  cut already credits the master. Two bans. **Never mint a pet label** for it: an `X`s pets` row would sit beside the real `X`s pet` row and
  split one player's output. **Never decide side or identity from the `'s corpse` shape**: that shape also carries 488 M HP of lingering boss
  DoT across 29 never-raised names, every `falls in battle.` line belongs to a respawning husk mob, and `ParserUtil.UpdateAttacker` strips
  `'s corpse` off **attackers** before a record exists — so a servant's hit is indistinguishable from a player who is alive again. A named
  corpse belongs in the fight list only because facts put it there (raid members hitting it keeps it; defender names keep the suffix), and a
  corpse DoT that hits us is an ordinary hostile under its stripped name — `EQLogParser.Test/src/parsing/mirror/RaisedCorpseTest.cs` pins all
  four cases. Reasoning: docs/combat-mirror-design.md → "Corpses need no rule".
- **Compare boards, not just rows, before replacing a grid**: `MirrorRealLogBoardsTest` (gated on
  `EQLP_MIRROR_BOARDS=<log>`, with `EQLP_MIRROR_DIAG=name,name` for "folded under another key or lost?") runs the real
  builders twice over one capture and censuses every column per raider, because a grid is per person — two tables can
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
- **Classify before asking an identity question in a test**: `PipelineHarness.RunFileWithMirror` hands back a timeline
  with `RegistrySeed` on it and **no rule table** — R6 (npcs.txt), R14 (article shape), R15 (healed by our side) and the
  graph run only inside the app's derive (`MirrorSession`), or in a test that calls them. Measured on Incogitable:
  "damage facts whose defender no rule placed" reads **1,525,786 / 583 B** on the seeded timeline against **4,323 /
  1.69 B (0.25 % of hit facts)** after `ClassificationRules.Apply` — a 350× difference, because that is how many mobs the
  rules place out of the way. Two documents were written on the wrong side of this before it was caught (`HitByNpcCensusTest`
  proposed a rule from it; `IsRaidVictimAt` quoted its numbers from it), and the proposal died once the rules ran: with a
  classified timeline, "a mob keeps hitting it" identifies **zero** roster players and its top names are mobs the NPC
  database does not know (`Herald of the Outer Brood`, `War Trainer Prime`), so no such rule exists. Build the timeline
  the way `MirrorRealLogBoardsTest.Classified` does — fresh `EntityTimeline`, `RegistrySeed.Apply`,
  `ClassificationRules.Apply(facts, timeline, heals)` — for anything that reads identity, pets, charm or ownership.
- **The tank board is damage our people received, decided by three fact targets, not two directions**:
  `FightProjection.FactTarget` is `AtOwner` (aimed at the row's anchor — the raid's output), `RaidSide` (landed on one
  of us — the tanking half) or `Neither` (mob on mob, mob on somebody's pet: captured, counted in
  `MirrorDamageIndex.UnroutedFactCount`, filed nowhere). Three rules, each learned the
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
- **A charm window is the NPC's death; its pet's death is not one**: `DerivedFight.EndReason` says *why* a row ended while
  `Dead` stays the flag every consumer reads (overlay, grid styling, `MirrorSummaryFights`). A `has been charmed.` sighting closes that name's row as `Charmed`
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
  `Gap` counts that used to hide them; charmed-owned rows **0→6** and **0→5** once the exemption landed) and reasoning:
  docs/combat-mirror-design.md → "What a window does to the list and the board"; pinned by `CharmRowProjectionTest`.
- **A row is one life, and the split gap is legacy's expiry (30 s)**: `FightProjection.EngagementGapS = FightManager.FightTimeout`,
  one number for both lists — not legacy's pair (60 s until boss-directed damage lands), because a row that has not hurt anybody for
  half a minute is over either way, and a threshold keyed on which side the first hit went makes boundaries a function of damage
  direction. It was **300** until one pull was read off `eqlog_Kizant_xegony.txt`: `Waxwork Abolishion` fades at 18:34:53 after 46 s and
  respawns 135 s later, and the wide gap made both lives ONE row ("5 minutes", one entry short of the encounter) while also welding the
  fade into the DPS clock — legacy's `TimeRange.Add` drops silences ≥6 s, so its three rows summed to 324 s where the merged span charged
  343 s. Two consequences that must not be "simplified" back: a row's **direction windows** (`Begin/LastDamageTime`,
  `Begin/LastTankingTime`) are written by the same `aimedAtAnchor` comparison as `DamageToOwner`/`DamageByOwner` and `MirrorDamageIndex`,
  with **NaN meaning "never happened"** (a row nobody hit must not report a zero damage time; `Sectionizer` skips an empty window, since
  `Math.Max(x, NaN)` is NaN and one such row would silence every later "Fight N" divider), and the grid's **duration column prints seconds**
  (`FormatTicks`, `HMSCompact`) because `FormatGeneralTime` returns an *empty string* under a minute and collapses 112 s and 162 s into
  "1 minute"/"2 minutes" — only the inactivity divider keeps the words, where matching legacy is the point. Cost on that capture: rows
  **237→289** (visible **215→267**) vs legacy **262**; reasoning and fixtures: docs/DesignNotes.md → "One row per life"; pinned by
  `ABossThatFadesWhileItsAddsDie_GetsOneRowPerLife`, `AQuarterMinuteOfQuietIsStillTheSameFight`,
  `ARowRemembersItsDamageTimeApartFromItsTankingTime`, `ARowNobodyHit_HasNoDamageTimeRatherThanAFakeOne` and (Windows) `MirrorFightRowsTest`.
- **Quiescence is a completion detector, and the cadence answers WHICH pass, not merely whether**: the mirror used to derive only when the captured
  count held still for two ticks, which answers "is the file done loading" and nothing else — a live raid tail never offers two silent ticks, so every
  surface reading the snapshot (fight list, click summaries, the damage meter) froze for the whole encounter and moved only on Re-derive.
  `MirrorDeriveCadence.Decide(...) -> DeriveKind { None, ProjectionOnly, Full }` is the rule now (Core, so it is testable without a dispatcher; there is
  no `ShouldDerive` anymore — assert *which* pass is due, or a legitimate half-second refresh reads as "no pass"). Check order is hazard by hazard: nothing
  new since the last pass of **either** lane => `None`; count held still for `QuietSeconds` => **`Full`** (never answer that moment cheaply — the end of a
  load is when the rules finally see the whole capture, which is when they learn what a refresh cannot); growth **rate** >= `BulkFactsPerSecond` (25,000/s;
  a file reads at ~170k facts/s against tens/s while tailing) => `None`, and that parks **both** lanes because the cheap one still holds the
  `CombatMirror._gate` the loader needs; full clock due => `Full`; else cheap clock due => `ProjectionOnly`. The ceiling stays load-bearing:
  `BuildMirrorUpdate` zeroes the board after the meter's quiet window (`LiveFights.TimeoutFor`, 30 s at the default dial) of quiet measured against *the
  snapshot's* last fact, so a slower cadence makes the refresh rule and the expiry rule argue and the meter blanks on a live raid. Opening a derived meter
  also calls `RederiveAsync()` directly (the expensive lane), since an empty window reads as broken. Every threshold is a duration or a rate rather than a
  tick count — the pump (now **100 ms**) only decides how close to its moment a pass lands, and a per-check allowance would shorten the quiet window and the
  bulk guard purely by asking more often (`TheRuleDoesNotDependOnHowOftenItIsAsked`). Note bulk **parks**, it does not fire: a load ends in quiet.
- **The cheap lane folds over the timeline INSTANCE the last full pass produced, on two clocks**: `ProjectionOnly` runs no rule book at all (measured:
  classification is 186-261 ms of a pass, projection of a live increment 0-5 ms), so `MirrorSession` carries `_carriedTimeline` and hands that same object
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
  ~7-12 % of the ingest gate. The unmeasured term is `MirrorFightRows.Build`,
  which every lane pays over every row the capture ever made: its bound lives in `EQLogParser.Wpf.Test/src/control/util/MirrorSnapshotCostTest.cs`
  (Windows-only). Widen the cheap lane by row count before touching either floor. Numbers: docs/DesignNotes.md -> "How long a meter update takes".
  **The surface around it must never read as broken**: with `EnableCombatMirror` set, `MainWindow` docks the window at startup (a checked menu item
  means the window is there - "Mirror: no log" until one opens; docking only in the open-log path left a restarted app "checked but invisible",
  which shipped reading as "it stopped working after I restart"), and toggling ON over a live parse **starts a session immediately** (it used to
  re-dock the window with no session, so uncheck/re-check did nothing until the next log open - the exact experiment the user reaches for). A
  mid-log attach is forward-only by construction: no backfill exists (facts before the tap were never captured), and the chat sink was fixed when
  the file opened (`LogProcessor` holds it), so drink evidence and chat identity join at the next open while facts and heals flow from now.
  One line per session in eqlogparser.log - `combat mirror: session started (file)` at open, `combat mirror: first derive - N facts, M rows`
  at the first pass - so a list that silently fails to fill is diagnosed by which of the two lines is missing.
- **A fight is "still going" on the capture's clock, and one file answers all three meter questions**: the overlay's numbers moved to the
  mirror first, which left it painting derived figures while `FightManager` still decided whether you ever saw them (open on launch,
  close-for-real-when-hidden, open-yourself-on-a-pull). Those now go through **`MirrorMeter`** — the only reader of
  `OverlayDamageFromMirror`, so "which engine is the meter reading" has one address and no legacy fallback anywhere on the path ("no session"
  answers *false*, it does not consult FightManager) — over **`LiveFights`** (Core: a row is live when it is not `Dead` and its last activity in
  **either** direction window sits inside the gap). Three rules that must not be "simplified": (1) **now = the capture's newest event, never
  wall time**, because a log file is never rotated — measured spans between first and last damage fact inside one file: **329 h**
  (`eqlog_Kizant_xegony.txt`), **616 days** (Incogitable) — so a wall-clock rule is a rule about when the raid logged out, and it cannot see the
  dense end of a farm night (09-20-25: 3.0 h file, 143.9 min of it live); keyed to the capture, "live" means *at the end of what we have*. A file
  ending after a kill therefore answers **0 live rows**, which is correct — so `LiveFightsRealLogTest` reports that number and asserts each row is
  live at its *own* last activity instead. (2) **the gap is the meter's dial** (`TimeoutFor`: `OverlayDamageMode` 0 = on kill = `EngagementGapS`,
  else N seconds), so a window cannot be held open by one rule and blanked by another; pin it as *behaviour* (live at 29 s, dead at 31 s), since
  comparing two constant spellings of 30 passes even after somebody edits it to 300. (3) **the X closes the window and disables nothing — so the next damage
  brings the board back, onto the same seconds**: `MirrorSession.LiveDamageObserved` fires while damage is fresh (`LiveFights.HasFreshDamage` = activity newer than the
  last announcement AND something still live), because legacy's `EventsNewOverlayFight` fired on **every damage line** of a fight (`UpdateIfNewFightMap` raises it whenever
  `DamageHits > 0`, outside the new-fight branch) and that is what its X felt like. Continuity needs the other half: `DamageOverlayWindow._mirrorWindowT` (the second the board
  adds up from) is **static**, since a derived board holds nothing between ticks and an instance field would be reborn at "now" — closing a window is not a reset, only the clear
  button and the dial's own quiet rule (`LiveFights.WindowStartFor`) move it. Both halves were defects in the first port: announcing one row per life kept a closed meter shut
  until the *next pull*, and the instance start made a reopened board forget the seconds already spent. Rate is per derive rather than per line, bounded by
  `MirrorDeriveCadence` — measured **~421 to 2,107** announces over a night carrying 1.9 M damage facts, each one a window-already-open check while the board is up. The per-life
  census still matters as the shape of a night: starts per life ≈ 1 (269/269 Kizant, 377/377 on 09-20-25, **4,542 over 4,519** on Incogitable with 23 restarts, longest mid-row
  pause **121 s**, which exists because rows split on silence in *any* traffic while the live rule reads only the two direction windows), and coverage **105 min** of a 329 h file
  (Kizant) / **3,875 min** (Incogitable). It is **static** on `MirrorSession` (like
  `ActiveChanged`) because its reader outlives a capture, raised in its own `try` *after* `Derived` so a subscriber cannot make the derive look
  broken, and unsubscribed through `Subscribe/UnsubscribeOverlayFights()` rather than at scattered call sites. Visible differences from legacy are
  deliberate: launch opens only if the capture's last moments hold a fight (legacy opened if the 616-day file ever had one) and a hidden derived
  meter closes instead of lingering. Numbers, reasoning and the re-measure command: docs/DesignNotes.md → "A fight that is still going".
- **A mirror pass continues where the last one stopped, and exactly two gates open the carry**: `FightProjectionCache` (held by `MirrorSession`)
  keeps `ProjectionState` *and* the `MirrorDamageIndex` from pass to pass — the fold is forward-only, so the open row per name, the last **closed**
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
  numbers. `MirrorIncrementBenchmarkTest` (gated `EQLP_MIRROR_INCREMENT=<log>`) prints the projection/classification split; classification is what is
  left, and it is not one thing — on Incogitable: **R9 charm windows 83 ms, R18 healed-pet intervals 67, R15 heal breadth 54, R7 graph 36, line evidence
  15-25, R5 ownership sweep 15**, and the rest ~0. Making those incremental means a per-rule watermark plus a gate for roster/override/npcs.txt edits, which
  move verdicts without adding facts — and note that when a rule does find something new the projection rebuilds anyway, so only a 3 s→1 s floor is at stake. Numbers and reasoning: docs/DesignNotes.md → "A mirror pass that starts where the last one stopped".
- **A fight's duration counts seconds inclusively**: `DerivedFight.DurationSeconds` is `Math.Max(1, LastTime - BeginTime + 1)` because
  that +1 is what the product already calls a duration — `TimeSegment.Total` (`end - begin + 1`) is the DPS denominator of every board number, and
  `FightManager`'s tooltip (`Time Alive: Ns`) uses it, so an exclusive span made the grid print `00:00` for a mob hit once inside one second while its
  own summary said "Time Alive: 1s" and divided by it. Keep the arithmetic on the row, not in the formatter. Two exemptions: the
  `Inactivity > mm:ss` divider is a gap nobody fought in (stays exclusive), and `MirrorSummaryFights`'s tooltip keeps FightManager's own expression
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
  grid (`MirrorFightRows`) while the encounter row the charm closed stays listed as `dead, charmed` — same rule that keeps
  ``Ziggy`s pet`` out of the legacy table. **Hiding is display-only**: `MirrorSnapshot.AllFights` keeps every row and
  `MirrorSession.BuildSummaryInput` hands back the hidden ones through `CharmPetRows.WithHiddenPets`, because a board is
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
- **The healing board's derived door is a record seam, not another Fight**: `HealingStatsBuilder` never reads a
  `Fight` — it takes `(time, HealRecord)` pairs and windows them itself against `AllRanges`, so the mirror reaches it
  through `GenerateStatsOptions.Heals`, filled by `MirrorSummaryHeals.Materialize`. Four rules. (1) **null means "say
  nothing about healing" (read the record store), a non-null EMPTY list means "there was none"** — inverting that is a
  derived selection quietly keeping last click's grid. (2) The window is the selection's own `AllRanges`, because a heal
  belongs to no fight (`HealFactTable` has no fight id, by design): a derived click means "every heal in this span",
  which is exactly what legacy does with the whole store. (3) The list must be **time ascending** — the builder locates
  its window with `FindIndex(first >= begin)` and walks forward, so an unordered input loses part of a segment without
  complaining; fact order is ingest order, which is why nothing sorts. (4) Materialize `OverTotal` **verbatim** (never
  `AskedFor`, or `MaxPotentialHit` doubles on every plain heal line) and fall back to `Labels.SelfHeal` for the spell,
  because that is what `HealingLineParser` stores when the line has none. Measured on `heal-board.txt`: raid **3,401**,
  Rune **2,776** across 5 heals with **2,388** overheal and a **4,012** ask, Kilsa **625** across 2 — identical through
  either door (`MirrorHealBoardTest`). That test clears `RecordsStore` + `HealingLineParser.ClearCaches()` in setup like
  `MirrorHealCaptureTest`: re-parsing a fixture inside one process doubles the board, and 3,401 becoming **6,802** is what
  that looks like when you forget.
- **A record's modifier mask is captured on the fact, because filters read it**: `DamageFact.ModifiersMask` rides in the padding
  bytes that `OverTotal` (a heal-only amount) had spent on damage, so `DamageFact` stays **32 B** — and a materialized record with
  mask `0` would have excluded *nothing*, making every derived total read high the moment one of the six `DamageValidator` settings
  (assassinate, headshot, slay-undead, …) is switched off. `HitLogViewer` shows that same mask as a column, so it is also what a
  reproduced row needs. This is the `HitRecord` rule applied one layer down: a field belongs to the type whose lines can write it.
  Numbers and the refused heal shapes (10,194 pet self-heals / 2,688 pet-healed-passive / 120 HoT = 2.9 % of heal-action lines, all
  healer-less or self-directed): docs/DesignNotes.md → "The byte that filters live in", "Healing joins the capture".
- **Damage taken is a second ordinal set on the index, never extra columns on the damage one**: `MirrorDamageIndex`
  keeps `_damageOrdinals` (facts aimed AT the row's owner) and `_tankingOrdinals` (facts that owner dealt), routed by
  the flag from the single `ownerSink?.Invoke(fact, ordinal, row, key == defName)` call in `FightProjection` — the
  same expression that splits `DamageToOwner` from `DamageByOwner`, which is what keeps a materialized block sum
  unable to drift from the row it belongs to. A derived row then carries both sides on one `Fight`:
  `DamageBlocks` / `DamageSegments` and `TankingBlocks` / `TankSegments` (+ `Begin/LastTankingTime`, `TankHits`,
  `TankTotal`), because the two boards key a row differently: raid-on-mob facts are away-facing, a player being
  beaten on is toward-facing. `PlayerDamageTotals`/`PlayerTankTotals` stay empty — only
  `DamageOverlayStatsBuilder` reads those, and it is not fed from this path yet. Three laws, all pinned by
  `MirrorSummaryFightsTest`. (1) **A row with no incoming damage still gets an empty tanking section** so the board
  lists every row it lists; the group id comes from `Sectionizer` and rides the row, so one walk stamps both sides
  of a mixed selection. (2) **An outcome taken with no number is still an outcome**: `TankHits` counts
  unconditionally, like `FightManager`'s — assert the hit COUNT for zero-total shapes, never `> 0` on the total,
  because zero passes every magnitude test (that is how half of R9's friendly-fire exemption nearly shipped).
  (3) **Every fact of a row lands on exactly one of its two boards**, and a tank block's `Attacker` is always the
  row's own name — that is why `+Pets` folds a pet's output on either board.
- **Two numbers here are traps, both measured on `data/mirror/tank-fight.txt`.** (a) `FightProjection` sums BOTH
  directions into `DerivedFight.DamageTotal` and splits them into `DamageToOwner` / `DamageByOwner`; it never fills
  `TankTotal`/`TankHits`/`TankRollup` on the *derived* row (those belong to `FightDeriver`, whose legacy-keyed list
  is retired from display and lives on only in the parity tests). So "materialized tank total == row `TankTotal`"
  compares a real number against a zero nothing writes, and passes on any log that happens to contain no damage
  taken — hold materialized totals against `MirrorDamageIndex.TankingOrdinalsFor` instead. That is exactly how the
  real-log test stayed green before the tanking side existed. (b) Legacy does not hand a mob's victim her own row:
  `FightManager.Get` keys on `defender ? record.Defender : record.Attacker`, so on this fixture legacy produces one
  row, "an ice giant priest", whose `TankTotal` (834 = the mob's own three swings) is damage dealt, not taken, and
  `PlayerTankTotals` stays empty for Rune. Which is why `TankingStatsBuilder` groups by `record.Defender`
  (`StatsUtil.CreatePlayerStats(individualStats, record.Defender)`) rather than reading either rollup, and why the
  derived-vs-legacy check is per raider over each engine's own block input — both read Rune 322 / Kilsa 512, with
  the row-level difference asserted rather than smoothed over. Related: `FightManager` answers a spell line naming a
  defender by re-deciding it (`record.AttackerIsSpell && defender` → `!IsPetOrPlayerOrMerc(record.Defender)`,
  line ~255), which is the one non-melee shape that reaches the tanking side at all. **A swing that makes no number
  is already in the capture** — do not go build one. The `X tries to <verb> Y, but …` family (`blocks!`, `dodges!`,
  `miss!`/`misses!`, `parries!`, `INVULNERABLE!`, `riposte[s]!`, `… absorbs the blow!`) is parsed into a record with
  total 0 and its own label, so it rides the same `EventsDamageProcessed` seam as damage and lands in `DamageFact`
  with the verb kept as subtype; `StatsUtil.UpdateDamageStats` then fills `MeleeAttempts`/`Misses`/`Blocks`/`Dodges`/
  `Parries`/`RiposteHits`/`Absorbs`/`Invulnerable` from that label on either engine, and the mask-dwellers (`Crit`,
  `Flurry`, `Rampage`, `Strikethrough`) follow because `DamageFact.ModifiersMask` came along. `MirrorOutcomeParityTest`
  pins both boards per raider, legacy vs derived, **with the absolute counts asserted first** — an equality alone
  would also pass on a capture bug that zeroed both sides. Two traps. (a) A row's `# Hits` is *not* its attempt count:
  damage-side `DamageHits` filters on `LabelTypes.IsHit`, which counts `Block` as a hit type and excludes the other
  five outcomes, while `FightManager`'s tank branch counts unconditionally (5 vs 7 on that fixture). (b) A column whose
  word the log never writes reads 0 on both engines — Kicker/Fumble/Vex are absent from the 2024 capture (Flurry 94,453,
  Riposte 13,709, Rampage 1,151), and that is the capture being faithful, not a gap to fill.
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
  in any measurement here; one unfixable-by-registry case is a pet *named like a raider* (`Sancus`: in players.txt, owns `Sancus`s pet`, and
  stamped Pet Certain by "Sancus is called to it owner.") because identity is keyed on the name, not the owner. Numbers and the reading
  procedure: docs/combat-mirror-design.md → "Fourth audit".
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
  `CheckOwner`/`CombatMirror.AddDamage` — leave the shape in the tables and the day a sixth word joins `OwnerSuffixes`,
  R5's name-pool sweep adopts every eye in the raid. The three eyes that *do* count (`Veeshan`, `Despair`, `Mother`) are
  legacy's own exceptions and the list is closed: `TheCountableEyeListIsThreeWordsNoMore`. Measured yield: R19 wins **2 names** (`Depravity`, `Pixnn`) and both also held an
  equal-strength chat/presence claim, so nothing moved Unknown→Player on this corpus — it is a last resort plus a
  ledger entry (`R19-` is in `IdentityPriorStore.RememberedRules`). Numbers and reasoning:
  docs/combat-mirror-design.md → "An eye is not a combatant"; pinned by `EyeSummonTest`.
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
