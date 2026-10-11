# Project Rules & Guidelines

You are an expert AI assistant maintaining this C#/WPF/.NET 10 project.

## Core Principles
- **Standards**: read `docs/CodingStandards.md` and follow it.
- **Structure**: prefer small files and atomic commits.
- **Git**: detailed commit messages, **never push** — pushing is the user's call.
- **Docs in git are exactly four**: `docs/DesignNotes.md`, `docs/CodingStandards.md`, `docs/ReleaseChecklist.md`,
  `docs/TtsPacks.md` (the `.gitignore` whitelist). Anything else under `docs/` is a local working note: never `git add`
  it, never assume it exists in a fresh clone, delete it once its durable half is in `DesignNotes.md`.
- **A comment may not cite what a fresh clone cannot read**: no non-whitelisted `docs/<note>.md`, and no type or UI verb
  that no longer exists. If a name in a comment has zero declarations (`rg -c '\bTypeName\b'`) the comment is fiction.
- **Numbers and history live in `docs/DesignNotes.md`**, not here: this file states the law and the anchor, and points.
- **No heavy dependencies** without explicit user approval.

## Build (Linux, this machine)
- `export PATH="$HOME/.dotnet:$PATH"` first — .NET 10 SDK in `~/.dotnet`; system dotnet 8 fails `global.json`.
- Full solution: `dotnet build EQLogParser.sln -p:EnableWindowsTargeting=true`.
- **Zero warnings, counted not hoped for**:
  `dotnet build EQLogParser.sln -p:EnableWindowsTargeting=true --no-incremental --nologo 2>&1 | grep -cE ": (warning|error) [A-Z]+[0-9]+"`
  prints `0` before a commit. `--no-incremental` is load-bearing (MSBuild only reports for projects it rebuilds). Nothing
  in this repo suppresses a warning; fix the cause. One pragma fixes a whole class (`#nullable enable annotations`).

## Testing
- `dotnet test EQLogParser.Test/EQLogParser.Test.csproj` is the headless suite (~2,000 tests, plain `net10.0`).
  `EQLogParser.Wpf.Test` targets `net10.0-windows`: it **builds everywhere and runs only on Windows**, so a Linux run says
  nothing about it — never claim a WPF test passes from here. A `[TestMethod]` grep is not an executed count.
- **Real logs are gitignored**: `local/logs/live/` (live format) and `local/logs/emu/` (THJ/TSS/Heroes Forge shapes).
  Gated tests run them with **`EQLP_EMU=1`**, which sets `AppSettings.IsEmuParsingEnabled` for a `PipelineHarness` run and
  restores it after (process-global; live logs misparse with it on).
- **A gated real-log test is disposable; a print harness is not a test.** Once its question is answered, delete the gate
  and move the numbers to `DesignNotes.md`. Keep a gate only when something must be *asserted* on data no fixture reaches.
- **No GC/allocation probe tests and no perf-debug code** (2026-11): heap ledgers, allocation floors, `GC.Collect`+probe
  assertions all went. The only perf surface left is `StatsBuildTrace` (per-stage milliseconds + allocated bytes on the
  Debug line) and `GcTidyUp` (see Memory). If a specific path needs measuring, instrument it temporarily and delete it.
- **Process-global test state**: FCT dials (`FctScale.*`, `FctLayout.*`) reset via `FctAmbient.Reset()` in
  `[TestInitialize]`; both assemblies declare `[assembly: DoNotParallelize]`; `ClassificationRules.FailFastStages = true`
  is set in each `[AssemblyInitialize]` so a dying rule stage rethrows with its stack in tests instead of reading as
  "the rules found nothing" (only `ClassificationRuleHealthTest` flips it, and restores it).
- **The spell/data files are parsed once per test process** (`PipelineHarness.SharedDataStore()`, and `EQDataStoreTest`'s
  store cache keyed by the label set it was built with): constructing an `EQDataStore` costs ~0.6 s and five classes used to pay
  it per test — most of the suite's wall time. A test needing a store under different conditions (missing data files, its own
  class labels) builds its own; everything mutable is still cleared per test.
- **`PipelineHarness` clears the stores** (`PlayerRegistry`, `RecordsStore`, `HealingLineParser.ClearCaches()`), so
  re-parsing a fixture in one process cannot double counts, and `RegistrySeed` contributes nothing — tests that need seed
  memory must build it themselves; tests that need healers as healers must give them an evidence line (`EvJoinedRaid`), not
  a registry entry.
- **WPF tests run MTA**: constructing any `FrameworkElement` needs `Sta.Run(...)`; every DependencyObject read belongs
  *inside* the body (capture plain bools, assert outside); flush at the priority handlers queued at (**Normal**, never
  lower). The assembly's `[AssemblyInitialize]` sets `SyncfusionLicenseProvider.IsLicenseExceptionShown` before any
  `SfDataGrid` is built, or an unlicensed process wedges inside the grid constructor. Neither app nor tests call
  `RegisterLicense` directly — both go through `SyncFusionUtil.LoadLicense()`.
- **Identical input gives identical output in one process** (`CaptureReproducibilityTest`); do not remove resets from the
  harness or re-isolate what measurement already ruled out.

## Parsing, records, facts
- **A recognized line is taken.** `PreLineParser.NeedProcessing` sets `found = true` for every shape it consumes — the
  tilde emote (`~ Kizant wonders.`), `Targeted (NPC)`/`Targeted (Player)`, guildmate achievements, raid/group join-leave,
  raid leader, drink/eat — so the four real parsers never re-walk a line that already paid for itself. Each branch's slice
  needs its **own** length check (prefix 14–17 chars, name at 15/19): a bare prefix is claimed, not sliced.
- **A record carries only what its own lines can write.** `OverTotal` lives on `HealRecord`, not `HitRecord`; nothing in the
  codebase takes a `HitRecord` (`StatsUtil.UpdateDamageStats(DamageRecord)` / `UpdateHealStats(HealRecord)`,
  `LineModifiersParser.UpdateStats(short mask, uint total, …)`). `DamageRecord` deliberately has no defender owner, but
  `CheckOwner(defender, out _)` stays in its `CreateDamageRecord` — registering the pet with its player is why it runs.
  A miss must not spend its modifier mask (`(Riposte)` rides on a line the parser called a miss), which is guarded in
  `UpdateDamageStats`, not in `LineModifiersParser`.
- **An outcome word decides; a tag never un-happens it.** Riposte/block/parry/dodge lines always produce a record;
  sub-setting happens off `record.ModifiersMask` (`LineModifiersParser.IsRiposte` = Riposte without Strikethrough), never by
  dropping the line. A name never keeps the sentence's period (`from <blank> by Y.` spell names).
- **A record's kind is one byte**: `HitLabel : byte` + `HitLabels` pairs each word with its `Labels` constant, so
  `record.Type` still returns the interned literal. `None` reads back as null; a word outside the table maps to `None` and
  is counted in `HitLabels.Unmapped`, never guessed (ordinal matching). Size is pinned by `Unsafe.SizeOf<HitLabel>()`.
- **A maybe may not charge every no**: lazy candidates (`ReceivedSpell` allocates only via `AddAmbiguity`; readers ask
  `HasAmbiguity`; the unambiguous answer is ONE shared empty list, asserted with `AreSame`).
- **Entity names are looked up without case** (`PlayerRegistry`, `EntityTimeline` identity/affiliation keys,
  `DamageFactTable.InternName`, which stores `CapitalizeFirst(name)`); subtype/label tables stay ordinal. A name comes from
  a subject span, never "the token after some punctuation" — `Rk. II` is not a sentence end (`IsSentenceEnd`).
- **A parser placeholder is never a person**: `ParserUtil.IsUnattributedName` (`Labels.Unattributed`, legacy `"Reverse DS"`,
  `Labels.Unk`) is the one recognizer; R7 skips those names as attacker, counts them unclassified as defender.
- **Chat is a closed claim list**: R3-chat claims a speaker only on guild/raid/group/fellowship — never tell/shout/ooc/zone
  chatter (bazaar hailers, and those names sit in `npcs.txt`), never `begins singing`. The one announcement shape is
  **R22-guildmate** (`Your guildmate X has completed …`, fire-only, letters-only name). Display words say *Achievement*, never
  "guildmate"; the code word stays `R22-guildmate`.
- **Corpses need no rule**: a raised corpse fights as ``<master>`s pet``, so R5 already credits the master. Never mint a
  pet label for one, never decide side or identity from the `'s corpse` shape (it also carries boss DoTs and respawning
  husks).
- **An eye is not a combatant**: `Eye of <name>` is refused at the door of both fact tables; `EvEyeOwnedStrike` claims the
  *striker* Player (R19) — never the eye, never an owner, never from the cast line. The countable-eye list is closed at three.

## Memory, facts storage, GC
- **Healing is a second fact table** (`HealFactTable`) sharing only the name pool and sequence counter with damage
  (`IFactTable.NextSeq()`); quiescence counts heals too (`DeriveEngine.CapturedTotal`). Struct size is asserted
  (`AFactIsItsMeasuredSizeNotWishes`) — field order wide→narrow, `DamageFact` stays 32 B with `ModifiersMask` in the padding.
- **A fact carries what its own line says, never what the registry believed**: only `LineDerivedFlagMask` bits appear on a
  fact; registry verdicts travel as `IdentityEvent`. Retired bit values stay retired (old `.spool` files hold them).
- **Chunks go cold, rows never move**: a superseded chunk encodes (delta varint + fixed planes + Deflate) and its row array
  is dropped; publish the blob before clearing the slot, encoding verifies by decoding inside a catch, ordinals remain the
  currency (`At`/`RefAt`/`FactsIn` decode on touch), and `EstimatedBytes` counts blobs because it feeds the spill trigger.
- **Doubling still applies to the small queues**; `CompactRows` reclaims slack **once per doubling** under `_gate` with a
  16-row floor (an empty array cannot grow), and a shrink renumbers nothing.
- **A capacity hint belongs to the door that reads**: `FactCapacity.HintForOpen(minBack, fileBytes)` hints only
  `minBack < 0`; `minBack == 0` is follow-from-end and must not precommit the file. `origin` has no default value.
- **A board-row read does not allocate**: `PlayerRegistry.GetPlayerClass` binary-searches inside the per-name lock rather
  than copying the boundary list out.
- **Only an INSTANCE method group allocates** — measure before caching a delegate (`LogProcessor._addMerc`).
- **GC (2026-11)**: `GcTidyUp.Request` fires at three moments only — a load that actually read lines, `Clear All`, and a
  finished stats build — each a re-armed settle waiting for *quiet*, rate-limited, never on log close (close is almost
  always the first half of an open). No timers, no collectors on the parse path. Why the post-load collection is worth it:
  `docs/DesignNotes.md` → "Garbage collection after loading a log".

## The derive engine
- **`DeriveCadence.Decide(...) -> { None, ProjectionOnly, Full }`** is the only rule (Core, dispatcher-free). Check order:
  nothing new ⇒ `None`; count held still ⇒ `Full`; growth rate ≥ `BulkFactsPerSecond` parks **both** lanes; full clock due ⇒
  `Full`; cheap clock due ⇒ `ProjectionOnly`. Bulk **parks**, it does not fire. Every threshold is a duration or a rate, never
  a tick count. `BuildMeterUpdate` stays tied to `LiveFights.TimeoutFor`, and opening a derived meter forces `RederiveAsync()`.
- **Only a gesture stamps the re-derive ledger** (`DeriveRequests`); the pump dispatches through `StartPass`, which stamps
  nothing, and paying a debt may not take one out.
- **Two clocks**: `Full` is paced by `_sinceFullPass`, the cheap lane by `_sinceAnyPass`; only a pass that classified may
  restart the full clock, write the ledger, or emit an Info line. The cheap lane folds over the `EntityTimeline` INSTANCE the
  last full pass produced, so `StateStamp()` stays put and the stamp gate keeps folding from its watermark.
- **A pass continues where the last stopped** (`FightProjectionCache`: rows, open/closed per name, death queues, and the
  `FightFactIndex` travelling with them). A carry needs `ProjectionState.Covers(facts)` **and** a matching
  `EntityTimeline.StateStamp()`; a moved verdict buys a full rebuild. The digest is incremental (added, commutative; never
  timestamps, never volume) and leans on classification being append-deterministic.
- **Classification is incremental per rule**: each gated rule keeps its own cursors/aggregates + **registers every claim it
  makes** for replay, and compares the boundary stamp at its own stage entry. Unfreezing resets the cursor with the aggregate.
- **Failure degrades one stage at a time**: `RunStage` guards each rule (a throw keeps what it added), reports on
  `ClassificationOutcome.FailedRules`, retires a rule after five consecutive failures; session-level throws retry through one
  Core decision (`DecideFailureRetry`: 1 s → 60 s, never into a load, capped quiet retries then one parked line). A latched
  auto-derive-off and a manual "re-derive" recovery are both gone.

## Identity: rules, words, and their doors
- **Vocabulary is closed everywhere.** `IdentityKind` (Player/Pet/Merc/NPC/Spell), `IdentityVocabulary` words,
  `OwnerSuffixes` (`` `s pet|warder|ward|familiar|mount`` — one list, read by `ClassificationRules.OwnerInName`), class-safe
  spell families, and the eight victim sentences. A new entry arrives with data, a word, and tests.
- **Classify before asking an identity question in a test**: a seeded timeline without `ClassificationRules.Apply` is the
  wrong side of every measurement (350×). Build `EntityTimeline` + `RegistrySeed.Apply` + `ClassificationRules.Apply`.
- **`Unknown` means "no rule placed this name"**, never "not one of ours". Spell names belong to neither side.
- **Direction and ownership**: a charm window is the NPC's death (`EndReason = Charmed`, pet deaths inside stay on the
  fact), a charmed mob becomes a hidden `RaidPet` row whose damage still reaches boards (`CharmPetRows`), a charm-flipped
  defender is the only exemption from the friendly-fire drop, and a row's direction windows are written by the same
  `aimedAtOwner` comparison that splits `DamageToOwner`/`DamageByOwner`/`FightFactIndex`. **Never "fix" the derived-vs-legacy
  pet gap.** Damage our own side earns nothing.
- **The tank board is damage our people received**, decided by `FightTarget` (`AtOwner` / `RaidSide` / `Neither`): ask
  `IsConfirmedRaidPersonAt` before everything, `AtOwner` before the Unknown-victim test; `IsRaidVictimAt` is exclusion, not
  proof; a pet's incoming damage is not a player's.
- **Rule basics**: R4 claims a caster from a versioned class-safe family rank (rank must be the whole tail and exist in
  `spells.txt`; new content is silent, never guessed); `Life Burn` declares `SpellClass.Nec` **overriding** the file's bad
  Shaman half; a multi-bit family means "no class label", and seeding REMOVES any generic label for such a rank (Battle
  Leap/Warcry). R5 ownership sweeps the name pool (a comma in an owner text claims the pet, not a raid member);
  `X is called to it owner.` names the **summoner** (`R5-companion` → Player), never the summon; R14 article ⇒ NPC at Medium;
  R15 needs ≥10 heals from ≥2 Strong casters and any swing back vetoes; R17 drink/eat names a player; R21 spell shapes
  (`… by .`, seen being cast, known spell name) type as **Spell** with no pencil; R24-petslot: `Target = Pet/Pet2` in the
  spell data makes the *defender* a Pet (ask `GetSpellByName`; damage lines only; never invents an owner); R25-roster and
  R26-savedpet claim Unknown-only names in their own folder's pool, last, at Strong.
- **Frenzy claims a class, never a person**; classes are time-windowed (`GetPlayerClass(name, t)`), low-confidence flips need
  `LowConfidenceThreshold` sightings, and headless class writes need the `App.xaml.cs` host hook.
- **Memory lanes**: `identity-priors.txt` holds rule verdicts *and* the saved-roster lane (players.txt has **no writer**;
  its one-time import runs per folder gated on `HasRosterRows`, stamps nothing, and leaves the file alone). `petmapping.txt`
  is a read-only feed into the ledger's Owner lane. **A load is not a sighting** (`init: true`, stamps carried verbatim);
  every stamp is .NET-epoch seconds (`DateUtil.ToDotNetSeconds`); staleness runs on `PlayerRegistry.StaleDays`.
- **Take-back and no veto**: `ClassificationCommands` is the whole vocabulary — `ApplyVerdict` (FORGETS the ledger row and all
  registry claims, then asserts at Manual) and `Recalculate(name)` (the Names pane's take-back button — `Solid_History`, since
  `Solid_Calculator` was the same glyph four summary panes use for OPTIONS: forget + Unknown verdict + `ForgetDefaultClass`). Class is a
  separate lane: kind verdicts never touch it. There is **no** "assert Unknown" entry in any dropdown and no permanent ban mechanism;
  reverting restores the rules' own answer. Ownership writes go through `PetAssignment.Assign` (and ask for a full pass), never into a file.
  **A take-back keeps its row** (2026-11): `Recalculate` notes the name in `TakenBackNames` and `ClassificationReport.Build` lists it as its
  LAST source — `Kind = Unknown`, `Row.Reason = TakenBackNames.Word` (cell *Taken Back*, hover *Claim Taken Back*) — because a verb whose
  result is that its subject vanishes out of the window the click happened in reads as a delete. Display state only: it claims nothing, asks
  the timeline first so a placed name keeps its own rule word, accumulates per name, and clears when the session dies. `ApplyVerdict(name,
  Unknown)` is a different gesture and stamps nothing (`RecalculateIdentityTest`).
- **Words live in `IdentityVocabulary`**, coverage asserted in both directions: cell words ≤ 18 chars, static tooltip clauses
  ≤ 28, hover head line exactly one clause plus capped extra lines ranked by `ClaimRanks`, Title Case, the app's own nouns only
  (Player/Pet/Mercenary/NPC/Spell — never "monster", "mob", "our"). **No surface names a file.** An unplaced row answers
  *Not Placed* / **Nothing Identified It**; an unmapped code echoes itself rather than being guessed. Direction words exist only
  for Spell rows, decided from final verdicts.

## Panes and UI
- **A cell editor lives IN the cell.** Never open a `Popup` yourself for an interactive pick: mouse capture kills the pick.
  `NameRow.EditingColumn` shows a `ComboBox` in the cell template; bracket programmatic preselects in `_suppressSelection`;
  close the drop-down before clearing the editor flag; end every edit path releasing the census hold exactly once.
- **A census does not land while a cell editor is open** (`EditorHold`, counting, newest-held-wins, `Abandon()` on hide or
  capture change), and **a repaint is an ask, not a call** (`CensusRepaint`: busy ⇒ delayed not dropped, coalesced,
  `Invalidate()` marks older censuses unpaintable, the run that pays clears the debt).
- **XAML-fired handlers guard the PANE**, not the sender: gate on what the body reads (`dataGrid?.View != null`, or a
  `_paneReady` flag set on the constructor's last line) because fields are not all wired during `InitializeComponent`.
- **A pane built during MainWindow's own `InitializeComponent` cannot read a theme width** (`ThemeConfig.Current*Width` are
  assigned later): apply at `Loaded`, follow `EventsThemeChanged`, detach on `Unloaded`; a constructor read is only valid for
  lazily opened windows.
- **Nothing on a worker thread may touch a DependencyObject**: UI values enter a board build as plain data captured on the UI
  thread (`_fightPane`, `tankingDamageType` read with `Volatile`), and optional work runs *after* the builders under its own try.
- **"No rows to show" is an empty collection, never null** (2026-11): the panes hand back `SummaryTable.NoResultsList` from `ClearData`,
  from STARTED and from a build with no NPCs/no data. A null `ItemsSource` is SfDataGrid's own word for "the data has not arrived" and it
  answers with an uncontrollable spinner in the middle of the table (that is what an operator saw: "the datagrid itself puts a busy icon in
  the center … it wasnt happening before this branch" — null had become the no-board value). The **one** surviving null is Damage Summary's
  group re-slice, where assigning null *before* the grouped list exists to make the tree grid see a change: a control workaround, not a
  state, and do not "clean it up". Related law unchanged: no pane asks its grid whether an answer exists — those guards read `CurrentStats`.
- **A busy state is earned** (`DeferredBusyState`: arm on STARTED, show only if still running after `PerfJournal.SlowPassMs`;
  every event cancels first; blanking paths cancel explicitly). Where work blocks the UI thread, `await
  Dispatcher.Yield(DispatcherPriority.Render)`.
- **A checkbox cannot tell a click from a load**: "unsaved?" compares the model against the last saved row (see
  `TriggersView.SlidingResetUnsaved`, `TriggerPropertyGridAdapter.Coalesce`); never mark dirty from a raw widget event.
- **Docking**: an auto-hidden pane's declared `SideInDockedMode` decides its show state (`SyncFusionUtil.ShowStateFor`), and a
  menu item shows/hides — it never relocates. Markup loses to `dockSite.xml`, so moving a pane needs a one-time migration
  beside the standing fixups. **A retired window leaves a hidden name-only stub** (a saved layout naming a dead window wipes
  the operator's arrangement) and its registry events go with it. The identity strip has one width, asked of the grid
  (`NamesTable.DesiredPaneWidth()`), fed by `ThemeConfig`.
- **No window may keep its own copy of identity state**, and **no window holds a `DeriveEngine` past its session** — tag state
  with `DeriveEngine.SessionId`; a `Dispose`d engine keeps every fact table of the closed log.

## Fight list, boards, charts
- **A row is one life**: `FightProjection.EngagementGapS = 30 s` for both lists; duration counts seconds inclusively
  (`Math.Max(1, Last - Begin + 1)`, matching `TimeSegment.Total`); a death closes only a row that was alive at `dt`; an empty
  window prints nothing rather than a fake zero. A list that never fills is diagnosed by which of `capture: started` /
  `derive: first pass` is missing (both Debug; run with `Debug=True`).
- **Follow, don't yank**: the list follows its newest row unless a hand is on it (`FightListFollow`: pointer-over or selection
  changed within the settle window parks following; a wholesale snapshot re-arms to the newest row); group gestures act on the
  row the pointer opened the menu on and **un-select never toggles** (an id the grid no longer has clears silently).
- **One announce per gesture** (`SelectionSettle` + one DispatcherTimer at 600 ms; `MenuOpen` via
  `ContextOpening/ContextClosed`; `ButtonHeld` for *both* mouse buttons plus capture loss). `AnnounceSelection` is the single
  door for the boards on the pass-driven reasons, deduped by (ids, content stamp) — **but the first answer of a capture always
  goes out** (`_announcedOnce`): an empty selection and a fresh pane are otherwise identical to arithmetic.
- **A refresh answers for itself; only a machine's refresh may decline** (`UnaskedRefresh`: a hand's gesture builds at any
  size; `ContentMoved`/`RowEdited`/`SnapshotSwap` are priced against wall time since the last accepted unasked rebuild and log
  one Debug line per deferral episode; a decline records nothing; `OweGesture` is a single slot consumed as `Manual`, cleared on
  capture change). Never decline a click.
- **One board build in flight, newest question wins, identical inputs dropped** (`SummaryBuildGate`): the key is every builder
  input including `DeriveEngine.SessionId`; never wrap `_summaryGate.Request(...)` in another `Task.Run`; a throwing build
  returns the gate to idle; a build whose session was disposed returns before painting.
- **Boards refresh on CLASSIFICATION, not on arriving damage** (`IdentityWatch`, scoped to names on screen), and every door
  into the boards names itself: `GenerateStatsOptions.Source` carries the caller's own words (a null source prints
  **UNLABELLED** and warns), `StatsBuildTrace` numbers each build process-wide and reports per-stage ms + allocated bytes, and
  nesting on one thread is not overlap (builders wrap bodies in try/finally traced wrappers).
- **Log levels**: the per-build/per-ask trace class is Debug (a player's log carries none of it without `Debug=True`); Info is
  for a thing a person did or something broke, including one line per deliberate identity write.
- **Three boards, three workers above `BoardFanout.MinOutcomes = 50_000`**: each worker re-asks that its capture is open,
  carries its own try/catch, `RunParallel` waits for all, worker count stays three, and a build never adds to a `TimeRange` it
  adopted. Quote end-to-end ratios, never the build wall.
- **A window is searched, never walked**: `TimeRange` keeps spans sorted/disjoint and answers `Contains`; heal rows ascend and
  the join is walked with a cursor (`IHealFactTable.AscendingTime` is the precondition; an unordered store pays a full scan).
- **A record walk hands out ONE `DataPoint`**; consumers aggregate in their own loop and retain nothing (no LINQ over a walk).
  Identity questions are per-name and memoized per fresh walk; composed row keys are memoized; `SubStatIndex` mirrors the
  published `Key` (`CreateRecordKey`, and a null key lives in `NoName`), and its pair lane is an accelerator only.
- **A histogram is counted when the chart asks, never during a build** (`Attempt.RecordHitTotal` → materialize on first read,
  release the raw list; the materialized dictionary is assign-once; no board build may read it).
- **A chart event names the build behind it and a repeat question is dropped**: `DataPointEvent.DataGeneration` + full
  question compare after `AutoSelectViewOption`; `-1` means "walk it"; a build always restamps; the record filter belongs to the
  view option's `ShouldSkipRecord`.
- **Materialized rows continue**: `FightFactIndex.SummaryFightFor` resumes from the cached counts (ordinal prefix is the proof,
  never a content hash), a published `Fight` is never mutated (growth returns a new Fight sharing closed blocks, cloning each
  direction's tail), and everything accumulated continues by cloning keys rather than records. A per-name board *cache* was
  built, measured and reverted — don't re-add it; the standing targets are block adoption, per-record dictionary probes, and
  materialize-before-accumulate peaks.
- **The heal board's door is a record seam**: `GenerateStatsOptions.Heals` from `HealSummarySource.Materialize`, with
  `null` = whole capture (read through `HealRecordSource`) and an empty list = "there was none"; the list must be time-ascending
  and `OverTotal` verbatim.
- **Clear All is a monitor re-open** (`OpenLogFile(CurrentLogFile, 0)`), because a projection can be unloaded, never deleted;
  identity memory survives it (it does not register with `LifecycleManager`). `CombatEvents.ActiveDataCleared` has exactly one
  raiser: `LifecycleManager.Clear`.
- **A repaint belongs to the capture that is open**: every subscriber checks `DerivedSnapshot.SessionId` before painting, and a
  disposed engine stops raising. `FightTable.ClearForNewCapture` runs on both sides of `ActiveChanged`.

## FCT overlay
- **Vocabulary closed**: region schemes `Bands`/`ByType`, styles `Freeze`/`Fountain`/`Spray`/`Arc` (+ `Straight`), and
  `FctArcBend` = `out|left|right` (junk ⇒ `out`). A new word arrives with a settings entry, a panel item, and tests.
- **A leftward lean turns its rows around** (`HangRight` swaps extents, flips `ArcedX`, puts the glyph outside), stamped at spawn
  and in `SpineFor`; only an arc in split changes anything. Assert a lean's **depth**, not its sign. `LabelDemands` reserves a
  seated label's floor before any name exists; `FitSource` cuts names against what the lean leaves.
- The timer overlay's lifecycle rules live in Core (`TimerLifecycle.ClampDuration`, refusals at the door, `ReapForgottenRows`,
  render flag cleared in a `finally`); keep `EQLogParser.Test/src/util/TimerLifecycleTest.cs` green rather than moving math back
  into the window.

## Nullable and misc
- **Opening `#nullable enable annotations` in an `EQLogParser.Core` file is public-visible** (Core builds `Nullable=disable`,
  both test assemblies `enable`): annotate narrowly, or declare without `?`.
- **Namespaces stay flat**: the app and Core compile everything into `namespace EQLogParser`; folders say what something is.
- **The fight pane shows no status text at all**; failures reach `eqlogparser.log`, triage by `grep -E "board ask|stats build"`,
  and `grep -c "Derived damage summary error"` is the first question when a board looks stale (a swallowed exception froze every
  grid once — a field a dictionary keys on must be non-null at BOTH ends when stored data can be old).
- **`EQLogParser.Wpf.Test`** is the Windows-only assembly; build it explicitly (`EnableWindowsTargeting`) when touching UI code.
- **Releases**: when touching `sign.cmd` or `EQLogParserInstall/*.iss`, read `docs/ReleaseChecklist.md`.

## Post-Implementation Checklist
- Zero-warning solution build command above prints `0`; nothing was suppressed; any file annotating with `string?`/`T?` opens
  with `#nullable enable annotations`.
- Method visibility ordering: public → internal → private.
- Pattern matching: `is not T` / `is T var`, never `as T` + null check.
- No leftover or unnecessary `using` statements.

## Agent Behaviour
- **Ask for clarification** when a requirement is ambiguous.
- **Syncfusion questions**: look under `syncfusion-wpf` first.
- **Never use Claude/Anthropic models or external VLM services** — vision work uses `read` and local tools.

## Never Touch
- `BackupUtil`
- `MaterialDarkCustom`
