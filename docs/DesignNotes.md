# EQLogParser Design Notes

Behaviour that is deliberate but not obvious from the code, and the rules that keep it that way.
Read the relevant section before changing import, sharing or migration code so decisions are not
quietly reversed. Style rules live in [CodingStandards.md](CodingStandards.md); this file answers
*"why is it like this?"* and *"what must not change without a decision?"*.

## The parsing direction: what we are building and why (2026-10)

*Orientation chapter — the shape of the work before its details. Every law summarized here has a full section in this
file (dates in headings), a short form in `AGENTS.md`, and tests; nothing new is decided on this page. It exists so
that the next session — human or machine — starts from where the design actually is.*

### The one-sentence version

The app no longer tells the story of a raid *while parsing it*. Lines are parsed cheaply into **facts** (damage,
healing, outcomes) held in append-only tables; a background **derivation** folds facts plus an identity rule book into
**rows** and boards on a cadence; every surface — fight list, all three boards, the overlay meter — reads that derived
side behind one dial (`EnableCombatMirror`). The legacy per-line accumulation (`FightManager`, parse-time `StatsUtil`)
still runs, but it now feeds nothing the user sees except its own retired table, and its deletion is an ordered work
queue, not an experiment.

### Why parse-time accumulation was replaced

- **Identity decisions froze at line time.** Is `Dangle` a pet, a merc, a charmed mob, an unverified raider? The old
  pipeline had to answer the instant a line arrived and live with it all night. The evidence that answers correctly —
  who heals it, what owns it, did it die — arrives later, spread across the log.
- **Two clocks leaked into everything.** Expiry, durations, "is a fight still going" mixed wall time and per-line
  order; re-reading the same file could disagree with itself (a real file spans **329 hours**; another **616 days**).
- **Every view paid at ingest.** The meter's arithmetic ran whether or not anyone was looking, per line, on the parse
  path.
- **Nothing was re-decidable.** A better rule discovered next week could not correct last night's board. Derivation
  re-derives; rows migrate; overrides ride the replay.

### The pipeline as built

```
lines ──parsers──▶ CombatCapture
                    ├─ DamageFactTable (32 B/fact: names, spell, label mask, direction flags)
                    └─ HealFactTable   (40 B/fact: asks/landed/over; one shared name pool,
                                        ONE sequence across both tables)
                          │
             DeriveCadence.Decide ── None | ProjectionOnly | Full
                          │                     (quiet 1 s ⇒ Full; ≥25k facts/s ⇒ park both)
             ClassificationRules.Apply (Full only)
                    ├─ R0–R20 rule book → EntityTimeline (kind + strength + reason per name)
                    ├─ overrides (user law, persisted) and the prior ledger beat inference
                    └─ per-rule aggregates carried between passes (ClassificationState); a
                       stage whose boundary digest is unchanged replays in microseconds
                          │
             FightProjection.Project (every lane) — facts → DerivedFight rows + FightFactIndex
                    ├─ one row per life (30 s split gap = legacy's expiry, both directions)
                    ├─ charm windows: the mob's death; its pet gets no row, its damage folds
                    │  under the charmer (`+Pets`) — hidden from the list, never deleted
                    └─ two ordinal sets per row: facts AT the owner / facts BY the owner
                          │
        ┌─────────────────┼──────────────────────┬─────────────────────┐
   MirrorFightTable   FightSummarySource/Heals  MirrorMeter + LiveFights   Names window /
   (legacy's 3 cols,  (the REAL builders get     ("still going" on the      identity census
    sections, search,  materialized records —    capture's clock; the
    overrides)         same code as legacy)      meter's quiet dial is the
                                                  one wall-time exception)
```

The load-bearing invariants: **one sequence** so heals and hits can be merged into any later order truthfully;
**names case-insensitive everywhere** (the parser capitalizes, evidence lines keep what EQ wrote); a fact carries the
**modifier mask** because filters read it off the reproduced record; a row's two boards are split by the *same*
comparison that fills the index, so a materialized block cannot drift from the row it came from. The deep dives are
§"Derived damage summary from captured facts", §"A row is one life", §"Damage taken…", and the `AGENTS.md` bullets they generated.

### The identity rule book (R0–R20) in one breath

Evidence has a **strength ladder** (Certain > Strong > Medium), every claim names its reason, and the Names window
shows the ledger — an identity on the board is traceable to the lines that earned it. Sketch: R0 the log's own "You",
R1 raid-targeting, R2/R3 who-was-who chat and presence, R4 the spell DB, R5 ownership (five possessive words — `` `s
pet/warder/ward/familiar/mount`` — swept from the name pool, not the facts), R6 the shipped NPC database, R7 the fight
graph (opposition is an absolute veto; unknown defenders a 2 % allowance), R9 charm windows (a charmed mob IS a pet:
owner-folded damage, no fight row), R10 manual overrides, R13 mercenaries, R14 the article test (`A bone walker` — the
game's own "this is a thing" marker), R15 our side keeps healing it (≥10 lines from ≥2 Strong casters; caster BREADTH,
never volume — bosses get raid AoE too), R16 comma titles, R17 drink/eat lines name a player, R18 healed pets, R19 eyes
of X (an eye is not a combatant; hitting yours proves you exist), R20 the pet's own Snare claims Pet. The vocabularies
are **closed and asserted at their size** (`OwnerSuffixes`, `HitLabels`' sixteen words in one byte, the three countable
eyes) — a new word arrives with a census over several eras, settings text if user-visible, and tests; nothing is
inferred into them.

### Cadence and cost: when derivation runs, and what it carries

- The pump ticks at **100 ms**; `DeriveCadence.Decide` answers WHICH pass, not whether: nothing new ⇒ None;
  count quiet 1 s ⇒ **Full** (never answer the end-of-load moment cheaply — that is when rules finally see the whole
  capture); growth ≥ **25 k facts/s** ⇒ park BOTH lanes (bulk reads at ~170k/s; a parked lane still holds the gate the
  loader needs); else Full every ~3–15 s (cost-scaled) and **ProjectionOnly** at a 0.5 s floor in between. A refresh
  lands ~0.8–1.2 s behind live play for ~7–12 % of the ingest gate.
- The cheap lane folds over the **timeline INSTANCE the last Full pass produced** — no rule book runs (classification
  measured 186–261 ms of a pass; projection of a live increment is 0–5 ms). Two clocks: `_sinceAnyPass` paces the
  cheap lane, `_sinceFullPass` the expensive one, and only a pass that classified may restart the latter.
- Both lanes carry state between passes: `FightProjectionCache` (rows, open row per name, death queues, the index)
  opens on `Covers(facts)` **plus** an incremental content digest of the timeline — additive, order-free, replay-stable.
  Classification carries per-rule aggregates gated by each stage's own boundary digest; the timeline store stays FRESH
  per pass (each rule replays its carried claims in stage order) because a carried timeline leaked verdicts upstream
  rules must not see. A quiet Full tick measured ~380 ms of rules → **2 ms** on Incogitable (1.89 M facts).
- Failure degrades one stage at a time: swallow-and-report, retire after five IN A ROW, session-level retry ladder
  1 s→60 s; and in TESTS the same guard **rethrows** (`FailFastStages`, raised by both assemblies at init) because a
  silently dead rule reads exactly like an empty column.

### One clock law: the capture's time is the clock

Every "still going / expired / how long" question answers against **the capture's newest event**, never wall time —
except the meter's own quiet dial, which is a promise in real seconds to the user and stays on `DateTime.Now` (a file-
growth stall must not hold the board up). Durations count seconds **inclusively** (`+1`, matching `TimeSegment.Total`,
the DPS denominator every board already uses); a row splits at 30 s of silence in EITHER direction; a death closes
only a row that was alive when it happened, and a row with no death inside it honestly says "still open". The legacy
60-second boss pair and the old 300-second gap are both history, measured away on real logs.

### What the surfaces may say (deliberate, since users read them)

The derived list wears legacy's face: **Initial Hit Time | HP | Name**, inactivity dividers in legacy's warning color,
tooltips, selection restored across every re-derive by name+begin (`FightKey`, since projection renumbers). What it no
longer says, on purpose (2026-10, from live feedback): no reading-progress duplication inside the dock — the
application-wide status line owns percent-and-seconds; the band's one phrase is "Building derived fight list…" (EOF →
first snapshot, default color, indeterminate); no `Derived HH:mm:ss - N fights, M facts, X ms` stats line in a header
narrower than its text. The header's remaining honest jobs: which session, an override verdict, a selection meaning, a
failure being retried. The meter opens at launch only if the capture's LAST moments hold a fight; its X closes the
window but disables nothing — next damage brings the board back onto the same seconds (the start second is static for
exactly this).

### Lifecycle signals belong to PATHS, not stores

`CombatEvents.ActiveDataCleared` — what blanks seven grids/charts when a log closes or opens — is raised by the two
CLEAR PATHS (`LifecycleManager.Clear` after its fan-out; the fight list's Clear All), never from inside
`FightManager.Clear`. A signal that lives in a store scheduled for deletion silently stops firing the day it goes, and
a board frozen on last night's raid is worse than an empty one. Pinned by `LifecycleManagerTest` both ways: the path
raises with its payload; a bare store reset raises nothing.

### The measurement culture (how we know any of this)

Parity runs the REAL builders twice over one capture and compares **boards, not just rows** (`EQLP_DERIVE_BOARDS=…`,
per-person column pairs; healing exact at 403k heals/373 healers; damage's +0.54 % and tanking's people-vs-NPC split
are documented in those runs, not smoothed over). Identity questions are asked only against a **classified** timeline
(`ClassificationRules.Apply`) — the same census on a seeded one lied by ×350 and spawned two wrong documents before it
was caught. Real-log tests report what they find instead of asserting constants that match accidents; absolute counts
go before equalities; findings need **several captures** before any of them becomes a rule. The suites run
`DoNotParallelize`; on Linux the plain suite (~1,560 tests) runs everything except WPF/Skia surfaces — Windows is the
only place `EQLogParser.Wpf.Test` executes, so its laws must also be readable from code.

### What is left (the deletion queue, in order)

Done and measured: fight list, all three boards, the meter — each fully behind `EnableCombatMirror`, no fallback to
legacy anywhere on those paths (no session answers *false*; it does not consult the old pipeline — fallbacks
hide errors). Remaining, from the working map (`docs/legacy-replacement-map.md`, untracked — the queue is mirrored here so this file is the durable home):

1. **`EventViewer.IsLifetimeNpc` → timeline** — one consumer of one call; same question the Names window already answers.
2. **The product call: flip `EnableCombatMirror` default-on**, burn it in, then delete `DamageOverlayStatsBuilder`.
   This is judgment, not code — and it is the hinge that lets the legacy `FightTable` go next.
3. **Line viewers onto the fact tables** (`HitLogViewer`, damage/heal/tank grids, per-name lifetimes) — *the main real
   work left*. The facts hold the rows; what they lack is readers (and per-name lifetime materialization).
4. **Stop parse-time accumulation** (`StatsUtil.UpdateDamageStats/UpdateHealStats` per line, then `RecordsStore`, then
   `FightManager`/`Fight`/`BattleRow`) — only possible after 3, because the viewers still read those stores.

Open costs stated honestly: classification's remaining ~250 ms floor is a per-rule watermark problem (R9 charm 83 ms,
R18 67, R15 54 on Incogitable) that rebuilds anyway whenever a rule finds something — so only a 3 s→1 s Full-pass floor
is still at stake there; the `Wpf.Test` assembly has never executed anywhere (Linux builds it, cannot run it); and the
GINA `{counter}` semantics question (local working doc `docs/counter-variable-issue.md`) is an open user-facing decision, unrelated to
the derivation.

### Standing decisions — reopen only with a decision, not a refactor

- **No fallbacks from derived surfaces to legacy.** They hide errors; absence must read as absence until the bug is fixed.
- **Where the derived side disagrees with legacy because legacy was wrong** (pets folded via the line's own possessive,
  a mob's victim never noise, one row per life), the difference is asserted AS the difference — numbers pinned, not
  averaged into a parity percentage.
- **Hidden means hidden, not deleted**: a pet row's damage still counts in every board its click could reach; hiding
  lives in the display list, never in the snapshot.
- **Vocabularies are closed** (FCT shapes, hit labels, owner suffixes, eyes, casting signatures): each arrives with a
  settings word, a surface entry, and a test that refuses a smuggled fifth/sixteenth/third member.
- **Numbers belong in the notes next to the law they justify.** Several of this file's sections exist because a
  "simplification" measured worse; check them before re-simplifying anything.

## Trigger and Overlay Import

"Import" means taking an `ExportTriggerNode` tree from some producer and merging it into the tree
stored by `TriggerStateDB`. The producers differ in transport and — importantly — in what identity
the payload carries:

| Producer | Wire form | Network | Identity carried by the payload |
| --- | --- | --- | --- |
| File import `*.tgf.gz` (triggers) | gzip JSON `List<ExportTriggerNode>` | no | none |
| File import `*.ogf.gz` (overlays) | same | no | overlay leaves carry their EQLP `Id` |
| File import `*.gtp` | GINA file export: zip containing XML | no | none — **triggers only** |
| Quick Share `{EQLPT:key}` / `{EQLPO:key}` | key in an EQ chat line; payload fetched by key from the share host (`TriggerUtil.RunQuickShareTaskAsync`) | yes | same as the file exports |
| GINA share `{GINA:key}` | SOAP `DownloadPackageChunk`, up to 100 sequential chunks (`GinaUtil.RunGinaTaskAsync`) | yes | none — **triggers only** |
| NAG migration | local JSON chosen through the NAG directory picker | no | triggers: `OriginalId = nagTriggerId`; overlays: `OverlayData.Source = "nag:{overlayId}"` and **no Id** |

### The overlay tree is flat, the trigger tree is not

- Triggers live under the `Triggers` root, may contain folders, and are enabled per character.
- Overlays live under a single `Overlays` root as a **flat list**. Nothing in the product can create an
  overlay folder: the overlay context menu (`TriggersTreeView.xaml`) has no **Folder** command, and
  drag-and-drop refuses nesting for both trees (`ItemDropping` rejects `DropAsChild` unless the target
  `IsDir()`). The exporter therefore only ever writes overlay leaves directly under the root.
- Consequences to respect:
  - `ImportOverlays` always merges into the `Overlays` root, whatever was selected — the parent picked
    in the file dialog is used for triggers only.
  - Do not add folder-merge semantics, or features that assume grouping, to the overlay tree. Real
    grouping would need a product decision plus a data-model change and migration — not a patch in the
    import branch.
  - Overlays have no per-character enabled state: `ImportOverlays` passes no character ids, so nothing
    is written to `TriggerState.Enabled` for an overlay. Only triggers are enabled per character.

### Identity: what counts as "the same node" on re-import

This is the core contract of importing. Matching lives in `TriggerImportPlanner` (triggers) and in the
overlay branch of `TriggerStateDB.Import`.

**Triggers**
1. If the payload carries `OriginalId` (NAG), match on that. When more than one stored sibling — or
   more than one member of the incoming batch — shares the id, the family is disambiguated by `Name`,
   because one NAG trigger can expand into several siblings (phrase + timer variants, counter resets).
2. Otherwise match on `Name`.

`OriginalId` exists because NAG allows duplicate trigger names and the importer renames collisions
(`X` → `X (2)`), so name alone would break re-import and pile up duplicates. Trigger payloads carry no
node ids, so ids are never a trigger match key.

**Overlays — `Id` if present, `Source` only when there is no `Id`, never the name**
1. Match the sibling with the same EQLP `Id`.
2. If the payload has no `Id`, and `OverlayData.Source` is non-empty, match the sibling with that
   `Source`.
3. Otherwise insert a new overlay.

Rationale:
- The exporter writes `Id` only for overlay leaves, so `Id` is the only handle an EQLP→EQLP share or
  `.ogf.gz` file can offer, and it identifies content exactly. Checking it first is correct.
- `Source` is **not** a cross-user identity. NAG mints overlay ids as random 16-character nanoids per
  install, so `nag:{id}` values are only comparable within one person's own lineage. Its whole purpose
  is that re-running the NAG migration on the same install refreshes the overlays it created earlier
  instead of adding a second copy — which works precisely because NAG payloads carry no `Id`.
- Never match overlays by name. NAG's setup wizard creates overlays named things like *Detrimental
  Timers* and *Beneficial Timers*, so two players' same-named overlays are unrelated; a name match
  would silently overwrite someone else's work.
- On a `Source` match the stored overlay is also **renamed** to the incoming name, so content and name
  follow the latest migration. That is reachable only when no `Id` was supplied — i.e. NAG payloads —
  which is why it cannot clobber a shared overlay's local name.
- Accepted consequence: if one person migrates the same NAG database on two machines and then shares
  an overlay between them, they get one extra copy to delete. Do not "fix" this by matching on name or
  by consulting `Source` before `Id`; it would require inventing an identity NAG never had, and the
  alternative is silent clobbering.

**Kind safety (both trees)** — a payload-carrying leaf may only update an existing leaf, and a folder
wrapper may only merge into an existing folder (`MatchesReimportKind`). Node kind is defined by payload
presence (`TriggerNode.TriggerData`/`OverlayData`), there is no kind field. Without this, a folder
wrapper matching a same-named trigger would reach the overwrite branch with null data and erase it.

### Node ids when inserting

- Imported **triggers** always get a store-generated id, whatever the payload contains.
- An imported **overlay** keeps its exported `_id` only while that id is free in the collection; if it
  is taken (importing the same share into a second place — routine) a new id is generated instead.
  `_id` is unique across the whole collection, so trusting an exported id would throw and roll back the
  entire import. This is silent on purpose: it is expected behaviour, not an error (see
  CodingStandards → Logging).

### Threading in the share pipeline

- A share download must never block the dispatcher. Either be genuinely async
  (`TriggerUtil.RunQuickShareTaskAsync` uses `GetAsync`/`ReadAsStreamAsync`/`CopyToAsync`) or make sure
  the continuation resumes off the caller's context (`GinaUtil.RunGinaTaskAsync` uses
  `ConfigureAwait(false)` because its chunk loop is synchronous). The entry points are click handlers,
  so a naive `await` captures the WPF context and resumes the whole transfer on the UI thread.
- Core must not touch WPF. Dialogs and messages go through host hooks (`GinaPlatform`,
  `TriggerStorePlatform`) that marshal to the UI thread; `App.xaml.cs` wires them at startup. The Core
  defaults are no-ops / fail-open so a host that forgets a hook degrades instead of throwing — which
  means **a new hook must be wired in `App.xaml.cs` in the same change**, or it silently does nothing.

### Media validation, badges and fixups during import

- Icon, sound-file and sprite validation runs through `TriggerStorePlatform` hooks (see above).
- The missing-media result must **accumulate** (`hasMissingMedia |= CheckMissingMedia(…)`): the return
  value is what flags the containing folder, so assigning it lets a later clean sibling erase an earlier
  hit and the folder badge disappears.
- A trigger's `SelectedOverlays` references are filtered to overlays that exist in the tree
  (`ValidateOverlays`). Dangling references are dropped, never remapped — do not turn this into a "pick
  something similar" repair.
- `RecentlyMerged` and `MissingMedia` are in-memory session badges surfaced by the view builder and
  cleared from the *Clear Recently Merged* menu. Nothing persists them; don't rely on them across
  restarts, and don't use them as import bookkeeping.
- Imported overlays pass through `SetVerticalAlignment`, which repairs alignment stored by older
  versions at a hard-coded original resolution. That is why an imported overlay can move.

### Names and whitespace

LiteDB trims leading/trailing whitespace on stored strings. Every incoming name is trimmed before any
matching happens (`NormalizeName`), because the NAG dump contains padded names like `" Emollious
colours…"`, and an untrimmed name never matches its trimmed stored twin — each re-import would add a
duplicate instead of updating.

### Keep matching logic out of the store

`TriggerImportPlanner` is pure: it takes the target folder's siblings plus the `OriginalIds` that occur
more than once in the incoming batch and returns an `ImportDecision`; `TriggerStateDB.Import` applies it
to LiteDB. New trigger matching rules belong there, with unit tests under
`EQLogParser.Test/src/store`, so they are covered on any platform rather than only in a WPF session.

### Known warts, deliberately unfixed

Recorded so they are not mistaken for open bugs — each was reviewed and left alone:
- The overlay **Import**/**Export** tooltips mention "the Selected Folder" / "Selected Folders" although
  overlay import ignores the selection and always merges into the `Overlays` root. Text copied from the
  trigger menu; cosmetic.
- The generic walker in `TriggerStateDB.Import` will still create a directory node in the overlay tree
  if a hand-edited or foreign payload contains a node with neither payload type. EQLP's exporter cannot
  produce one, so nothing validates against it today.
- `GinaUtil` builds its SOAP envelope by concatenating the session id taken from the chat line, and sets
  `Content-Length` by hand to a character count. The GINA service is effectively offline, so this path
  fails fast; if it ever matters again, build the request with a real XML writer instead of appending.

### Decisions that were reconsidered and rejected

- Matching overlays by name (clobbers unrelated players' overlays).
- Consulting `Source` before `Id`, or always loading all siblings to find a `Source` match.
- Logging overlay id collisions, re-imports, or other expected outcomes of normal sharing.
- Adding folder-merge semantics to overlay import without a product decision.

## Speech synthesis and TTS engines

The audio subsystem can speak trigger callouts with one of three engines: the Windows speech API, Piper, or
Kokoro. One speaks at a time. Which one is a user setting (`TtsEngine`), applied at startup and switchable while the
app runs.

### One engine behind ITtsEngine

Each engine implements `ITtsEngine` (`EQLogParser.Audio/src`) and owns its own per player voice state. A factory walks
the configured preference (settings key `TtsEngine`, set from the TTS Engine item of the **Tools** dropdown on the
Triggers toolbar, alongside Dictionary and Quick Shares) and
falls back through the remaining engines in order - a Piper whose pack is missing or will not load reaches an installed
Kokoro before the Windows voices, which are the last resort and need no files at all - so a missing model or voice pack
is a silent downgrade rather than an error dialog. Engine names are matched case insensitively at every boundary
(`TtsEngineFactory.Normalize`) because the setting is plain text somebody can edit.

The session's first engine is built on the thread pool from the `AudioManager` constructor, not inline: a Kokoro
session over the 156 MB model graph takes seconds, and that constructor runs on the UI thread the moment startup
touches `Instance`. `LoadValidVoicesAsync` is the single point that waits for the build - startup awaits it before the
main window shows, players register only after it - so the engine field can be null only in that window.

Before this seam `AudioManager` carried `_usePiper` / `_useKokoro` booleans consulted at a dozen sites — voice listing,
default voice, per player voice binding, synthesis, sample rates, shutdown — and kept a synth object per engine inside
its player records. Every engine therefore touched every other engine's code, and none of it could be exercised
without the native library behind it.

### What a voice is called

`GetVoices()` hands out ids - `af_nicole`, `en_US-lessac-medium`, `Microsoft David Desktop` - and those exact strings are
what a character's config stores and what an engine is told to speak. The pickers show something shorter through
`ITtsEngine.GetVoiceDisplayName`, reached from the view layer by one converter (`VoiceNameConverter`) on the three voice
dropdowns, so the item itself stays the id: whatever saves, matches or speaks a voice never sees the label, and no stored
value changed.

Where the label comes from is each engine's business. Kokoro reads the accent straight out of its own naming convention,
`[locale][gender]_name`, which turns `af_nicole` into "Nicole (US)" and `bf_emma` into "Emma (GB)" - worth showing now
that the pack carries British voices too. Piper labels a voice with the locale its model declares in `language.code`
(`voices.json` may say so outright), settled once when the engine starts because it costs a small file read per voice;
a name that cannot be located goes without a suffix. Windows answers with the name it was handed: "Microsoft David
Desktop" needs no improvement, and the `(Legacy) ` marking on a System.Speech voice is information rather than noise.

A third form exists because one place says a voice's name out loud. Picking a row in a voice dropdown previews it by
speaking that name, so `ITtsEngine.GetVoiceSpokenName` returns the name with neither the identifier's leading letters nor
the locale tag — "Nicole" from `af_nicole`, "Cori" from a Piper pack whose display label reads "Cori (GB)". Handing the
stored value to a synthesizer instead spells it: Kokoro reads `af_heart` as letters, so choosing Heart was answered by
somebody reciting "ay eff heart".

### Synthesis threading and cache

`SpeakTtsAsync`, `TestSpeakTtsAsync`, `SpeakOrSaveTtsAsync` and `TestSpeakFileAsync` are fire-and-forget for their
callers, so they hand the work to the thread pool and never resume on the caller's context. Synthesis happens inside
the engine behind `Task.Run`, because ONNX Runtime and SAPI block: a Kokoro sentence costs a few hundred milliseconds
and used to run on the UI thread, freezing the window mid callout. One `SemaphoreSlim` still serializes synthesis —
the neural engines are CPU-bound and overlapping calls only slow every caller down.

Synthesized PCM is cached in the same memory cache used for audio files, keyed by engine, voice and a hash of the text
(60 minute sliding expiry, sized in bytes so the existing 100 MB budget accounts for it). A line like `Got the level
90` plays dozens of times a raid, so only the first occurrence pays for inference. The text is hashed rather than used
verbatim so a long custom callout cannot produce an unbounded key.

A cached phrase needs neither the gate nor an engine: those bytes belong to an engine, a voice and a text, not to
whichever engine happens to be speaking, so the first lookup answers from the cache alone. That is what keeps twenty
familiar callouts from queueing behind one new sentence being synthesized. Everything that can miss runs under the gate,
where the engine cannot change underneath it.

Synthesis is not the only thing that touches an engine, and the gate alone was not enough: `SetVoice`, `RemoveVoice`,
`GetVoices` and `GetVoice` are called by the UI, synchronously, and one of them arriving while a switch releases an
engine's native state is a use-after-free in Piper's voice table rather than a caught exception. They now run under a
separate `_engineLock`, which the swap and the retirement also take. It is deliberately not held across synthesis — a
callout must never wait behind a dropdown, and a dropdown must never wait behind a model.

### Switching engines while running

`AudioManager.SwitchEngineAsync` builds the requested engine, lets it discover its voices, re-binds every player, swaps
it in, disposes the one it replaced, and then warms the voices that are still bound. The **TTS Engine** dialog calls it
when you press **Use**, and after a runtime pack finishes downloading, so switching takes effect on the next callout
instead of on the next start. The saved
setting still decides what a fresh launch uses, and a switch that cannot be honored (model missing, native library
refusing to load) leaves the current engine speaking rather than leaving the app without speech.

Two things make swapping safe rather than merely convenient:

- The whole switch runs under the synthesis `SemaphoreSlim`, so an engine is created and destroyed while nothing can be
  speaking through it. Synthesis re-reads `_tts` *after* acquiring that gate, which is why a callout that arrives mid
  switch cannot end up using a retired engine, nor cache PCM under the wrong engine's key. The swap itself and the
  disposal of the retired engine additionally run under `_engineLock`, so the quick UI-side engine calls described above
  cannot straddle them either. An engine that was built but never became active is disposed at the same point, which is
  why a switch that fails part way through does not leak a 300 MB inference session.
- Warm-up happens *after* the switch returns the gate, never inside it. Warm-up enters that gate only when it is free,
  so asking from a method that already holds it means every warm-up after a switch quietly gives up.
- A voice name from one engine means nothing to another, so `AudioManager` remembers what the host asked each player
  to speak with (`_requestedVoices`) and replays those names to the new engine. The engine binds the names it has and
  drops the rest, which sends that player back to the engine's default voice. Kokoro deliberately refuses to remember
  a name it does not have: a stale name would otherwise cling to a player for the rest of its life and be spoken
  quietly as a different voice.

### Warming a voice before anything has to wait on it

Speaking costs time in three separate places, and only some of it is the model: building a Piper voice is an ONNX
session over the voice model plus the espeak-ng dictionaries; *any* engine's first synthesis warms the runtime behind it
(arena sizing, kernel selection, the phonemizer's tables); and the audio device opens lazily on first playback. Cached
phrases skip all of it, so a trigger that says the same dozen sentences pays once per session — the delay is on the
first thing said with a voice, which is usually the trigger someone is waiting for.

`AudioManager.WarmUpVoice` covers the parts that can be paid in advance:

- **When:** a player is registered (`Add`), its voice changes (`SetVoice` — this is what the voice dropdown does, both
  when you pick one and when an engine switch repopulates the list and selects a default) and after an engine switch,
  for each distinct voice still bound to a player.
- **What:** `ITtsEngine.WarmUpVoiceAsync`. Piper builds the native voice; Kokoro and Windows have nothing per voice to
  build — every embedding arrives with the session made at engine creation — so they speak one short word into memory
  and throw it away, which is what warms inference.
- **Never audible and never in the way:** warm-up enters the synthesis gate only when it is free, retries briefly if
  something is talking, and gives up rather than standing in line. Choosing a voice also speaks an audible preview, and
  waiting behind a warm-up for that would be worse than staying cold. Whatever the retry window misses costs speed and
  nothing else.
- **One worker, one voice at a time:** requests queue rather than each starting a task of its own, deduped by engine and
  voice, because registering thirty characters at a zone-in is thirty voices asking for CPU at the same moment. A voice
  counts as prepared only when it actually got through; recording one that gave up would suppress the next attempt and
  leave a player cold for the rest of the evening.

Piper holds **one** prepared voice outside the players, under a reserved id in the native table. Previews used to build
a voice, speak and destroy it, so every different text paid for the model again; now `ResolveAdHocSpeaker` keeps it,
and takes the previous one out when the selection moves — which is the whole point, since otherwise an afternoon of
trying voices leaks a hundred megabyte model per change. Two further rules keep memory flat: a preview of a voice that
some player already speaks uses *that player's* session rather than building a second copy, and rebinding a player to a
different voice removes the old native voice first, because replacing a table entry is not documented as releasing it.

### Windows voices are proven, not assumed

Windows is the only engine with no files to check: the voices live in the operating system, so "is it available?" has no
answer short of asking one to speak. Historically the code answered *yes* unconditionally and swallowed whatever came
back, which reads as silence with nothing in the log.

Now `LoadVoicesAsync` records the verdict (`WindowsTtsEngine.IsAvailable`) and everything downstream — engine choice at
startup, what the picker lets you click, whether a switch is honored — reads it:

- **Wine is answered before anything has to fail.** `ntdll.dll` exports `wine_get_version` and real Windows never has,
  so asking for that export is not a heuristic about build numbers or registry keys a service pack can move. The check
  is worth its cost because the two errors are not equally bad: wrongly concluding "this is Wine" switches off the only
  engine a machine has, while a wrong answer in the other direction just leaves the runtime probe below to catch it.
  Loading `ntdll.dll` pinned to System32 keeps that from being spoofable by a planted copy next to the executable, and
  the result is cached. Whisky, Bottles and CrossOver are all Wine underneath and land here too; a real Windows install
  in a VM on Linux keeps its voices, which is the correct answer.
- **Unknown counts as available.** The probe only runs for the engine that actually starts, so an unprobed engine must
  not be hidden. This is the last engine standing; hiding it on a guess would silence someone who is fine.
- **False from the runtime probe means both APIs came back empty.** WinRT `SpeechSynthesizer` and legacy SAPI are
  checked independently and either one is enough: a machine with only legacy voices installed stays available. Windows
  images with the speech runtime removed produce nothing from both, which is the case this catches that Wine does not.
- **An engine with no voices is not a switch target.** `SwitchEngineAsync` asks the new engine for its voice list after
  `LoadVoicesAsync` and refuses it if empty, staying on the current engine instead of reporting a successful switch
  into silence.
- If nothing at startup turns out to be usable, `LoadValidVoicesAsync` logs it. That line is the difference between a
  bug report saying "no audio" and one that says what to fix.
- **What this engine holds per player is two synthesizer objects, not a lock.** `_lock` covers the player table only;
  nothing in this class serializes an utterance, on either API. Concurrency belongs to `AudioManager`, which lets one
  synthesis run at a time across all engines — worth knowing before adding a second gate here, and before assuming a
  second callout can be synthesized concurrently through `System.Speech`, which it cannot.

The picker greys an engine out only when there is neither a way to use it nor a way to get it: Piper and Kokoro stay
clickable while not installed because clicking them is how they get downloaded, and Windows goes grey when it has been
caught having no voices. `GetEngineDescription` in the dialog says so in words too — the Windows voices come from the
OS, which is why a Wine or Linux session usually has none.

### What installs and what downloads

The installer carries the app plus two small assemblies that `EQLogParser.Audio.dll` is compiled against
(`KokoroSharp.dll`, `Microsoft.ML.OnnxRuntime.dll`) so the seam types resolve and an engine reports itself unavailable
rather than failing. Everything heavy is fetched into per-user storage on demand:

```
%LOCALAPPDATA%\EQLogParser\kokoro\   bin\ (MisakiSharp, NumSharp, OpenTK, Numerics.Tensors)
                                     native\ (onnxruntime.dll, providers_shared)
                                     voices\ (*.npy + LICENSE)
                                     model\kokoro-fp16.onnx
%LOCALAPPDATA%\EQLogParser\piper-tts\  piperApi.dll and friends, voices\, espeak-ng-data\
```

`LocalApplicationData`, not `ApplicationData`: the Roaming folder is copied at logon and logoff on profile-redirected
machines, and 230 MB of re-downloadable binaries is the worst possible thing to put on that path. EQLP's own state
(`config\`, `logs\`, `archive\`) stays in Roaming where it belongs — roam what the user made, download what we ship.
`TtsPackManager` owns those directories: it resolves them, downloads and verifies packs, and deletes them. Publish order
is fixed by one fact — signing rewrites a file's tail, so the SHA-256 manifest the app verifies has to be generated from
the signed bytes (`sign.cmd`, then manifest, then upload).

Nothing here needs to load at startup: .NET resolves assembly references on first use, which is what makes hosting the
engines remotely possible at all. Two hooks cover a pack once it exists:

- `AssemblyLoadContext.Default.Resolving` answers `MisakiSharp`, `NumSharp`, `OpenTK*` and `System.Numerics.Tensors`
  from `<kokoro>\bin`, using `Assembly.LoadFrom` on the default context rather than a private `AssemblyLoadContext`.
  A second copy of a shared dependency in another context would not bind to the KokoroSharp that installs beside the
  executable, and type identity across the seam matters more than isolation here.
- `ResolvingUnmanagedDll` answers `onnxruntime.dll` and its provider stub from `<kokoro>\native`, which is what
  `Microsoft.ML.OnnxRuntime`'s own P/Invoke stubs ask for. Piper needs neither: its import resolver loads
  `piperApi.dll` by full path and the OS takes the dependencies sitting beside it.

Both return "not mine" (null / `IntPtr.Zero`) for anything they do not have, so unrelated loads are untouched.
The hooks are registered once, before any engine is constructed.

Neither hook is a guarantee, and that distinction turned out to matter: they are asked **after** the default search has
failed to find a name. `onnxruntime.dll` is a case where something else may answer first — see
[Which onnxruntime.dll wins](#which-onnxruntimedll-wins) — which is why the runtime is claimed rather than resolved.

There is no fallback to a copy beside the executable, and there deliberately is not one. Reading `{app}\piper-tts` when it
was complete -- which earlier releases did, so that upgrading did not cost a re-download -- meant an engine could be
running off files the dialog cannot update, cannot remove, and cannot match against a pinned digest, and worse: those
files are still sitting in old build outputs, so development runs reported a working Piper nobody had downloaded. One
location per engine, `%LOCALAPPDATA%\EQLogParser\<engine>`, owned end to end by `TtsPackManager`. `[InstallDelete]` now
deletes what installs before packs left under `{app}`; the files are inert whether or not they are removed, so that entry
is about reclaiming space, not about behavior.

### Downloading a pack, and getting out of one

An install is four jobs wearing one progress bar, and the bar is divided the way the work is: nine tenths for bytes off
the network, then slices for hashing the archive, unpacking it, and checking every extracted file against
`manifest.json`. Nothing sits at 90 % for a minute while the app works, which is the part people could not otherwise
distinguish from a freeze — reading a few hundred megabytes back takes tens of seconds after a fast download.

Every one of those loops watches the Cancel button through one `CancellationToken`: the transfer, the archive digest,
each extracted entry, each verified file. `ZipArchiveEntry.ExtractToFile` is deliberately not used, because it neither
reports bytes nor aborts before an entry finishes and one entry here is the 156 MB model. Cancellation is worth this much
because of where the work happens: unpacking and verification run in `<engine>.staging`, an existing pack moves to
`<engine>.retired` only once the new copy is complete and verified, and a promotion that fails puts the old copy back.
Give up halfway and the only trace is a deleted temp archive; a pack that spoke this morning still speaks.

Two free-space checks guard the job rather than one. Room for the archive is checked before the download begins — that
check exists to save somebody's bandwidth, so it refuses only the case that cannot possibly work. Room for the extracted
tree is checked afterwards from the zip's own central directory, which states the uncompressed size of every entry, so it
is a measured number rather than a guessed compression ratio and it fires before files start landing. An unreadable drive
or an unmeasurable archive counts as room enough in both: this is not the place where a disk gets argued with, and a
disk that fills up anyway reports the real figure itself.

### Kokoro model integrity

The Kokoro graph (156 MB) is not part of the installer. It arrives over HTTPS inside the Kokoro runtime pack the first
time a user opts in, which means the file the app later executes with its own privileges came off the network rather
than out of a signed package.

- `TtsPackManager` pins the SHA-256 of the archive and verifies every file in it against the pack's `manifest.json`
  before promoting it into place, and `KokoroTtsEngine` independently pins the graph itself
  (`ModelSha256`) and re-checks it before handing the path to onnxruntime. Two independent pins: a pack that was
  built wrong, or changed after install, still does not get to run.
- A verified model gets a `kokoro-fp16.onnx.sha256` marker beside it, so the hash pass costs nothing at every
  start. A hand-placed or previously downloaded model pays for it once, then writes the marker.
- We do not delete a model that fails verification, and we do not re-hash on every load: a mismatch is reported in
  the log once, the engine reports itself unavailable, and the existing fallback chain picks up. Deleting a user's
  156 MB download over a checksum we could have mispinned is the worse failure.
- Changing `ModelFileName` (for example back to the fp32 graph) means updating `ModelSha256` in the same commit.
  The two constants are the pin.

### Piper native lookup

`piperApi.dll` lives in the Piper runtime pack (`%LOCALAPPDATA%\EQLogParser\piper-tts`) and nowhere else, so it needs a
search path of its own. The
first implementation called `SetDllDirectory`, which is process-global and single-slot: it applied to every later
native load by anyone in the process, silently replaced any other caller's directory, and was the cause of a real
bug where listing Windows voices initialized Piper as a side effect.

`PiperTtsEngine` now registers a `NativeLibrary` import resolver that answers exactly one library name, `piperApi.dll`,
from whichever pack directory is in play (the engine's own copy is captured when it is built, so downloading a pack
while an older Piper is alive cannot move files out from under it — and `initialize()` re-runs against the new
espeak-ng data when the directory changes). Everything else returns `IntPtr.Zero` and resolves normally. Piper's own
dependencies (`onnxruntime.dll`, `espeak-ng.dll`, `piper_phonemize.dll`) sit beside `piperApi.dll`, which the altered
search path used by `NativeLibrary.Load` covers.

### Which onnxruntime.dll wins

Both speech engines load `onnxruntime.dll`, and Windows keeps **one native module per base name** for the life of a
process: whoever maps it first holds that name, and every later request — an import from `piperApi.dll`, a P/Invoke from
`Microsoft.ML.OnnxRuntime.dll`, even a load by absolute path to a different file — is answered out of the module list.
So one ONNX Runtime serves the whole session, and the question is which file that is.

The answer cannot be left to resolution order, because an old copy is easy to hit. `onnxruntime.dll` is not a Windows
system DLL, but other programs install it there anyway: a machine with a 2021-vintage `C:\Windows\System32\
onnxruntime.dll` (1.7.x) answers `DllImport("onnxruntime")` from System32 and refuses Kokoro's graph with something like
"Unsupported model IR version" about a download that is perfectly fine. Two facts make that fatal rather than merely
untidy:

- The default search — the host's deps.json native assets, then `LoadLibraryEx` over the usual directories including
  System32 — runs **before** .NET asks anyone's resolver. A search that *succeeds* with somebody else's file never asks
  `ResolvingUnmanagedDll`, and a resolver registered for `Microsoft.ML.OnnxRuntime` never runs either.
- `EQLogParser.deps.json` lists `runtimes/win-x64/native/onnxruntime.dll`, which the installer deliberately does not
  ship (that is ~12 MB of a ~20 MB installer, and the packs carry it). A declared native asset that is not on disk is
  simply not found, so a clean install falls through to the operating system while a development run — whose build
  output does have the file — resolves it correctly. That asymmetry is why this bug showed up on a VM and not on the
  machine that reproduced everything else.

What works instead is being **resident first**, which is `TtsPackManager.PreferMatchingOnnxRuntime()`: it loads EQLP's
own copy by absolute path, so from then on every request for the name is answered with ours. Candidate order is
`<kokoro>\native`, then `<piper-tts>`: Kokoro's copy leads because it is published together with the managed wrapper
installed beside the executable and repacked whenever that wrapper moves, while Piper's pack carries the same build today
and either serves. It runs once, from the thread pool, before the session's first engine is built (`AudioManager`) — not
from the constructor, because mapping a 12 MB runtime is not startup work for the UI thread — and again from both engines
for a pack downloaded mid-session. Whichever engine gets there first is fine; that is the point of keeping the choice in
one place rather than in an engine.

Three supports around the claim, each covering a case the others cannot:

- `EnsureOnnxRuntimeImportResolver` pins `Microsoft.ML.OnnxRuntime`'s own imports to the approved folder. It cannot beat
  System32 — nothing registered from managed code can, once the default search finds a file — so its job is the mirror
  failure: when EQLP's copy is **missing** it throws instead of returning `IntPtr.Zero`, because handing the name back
  is exactly how a foreign runtime gets in. Fail loudly on a decision we own.
- After claiming, `IsForeignOnnxRuntimeResident()` asks which file is actually mapped — an absolute-path load returns the
  already-resident module, so success alone does not prove it is ours — and Kokoro refuses to start when the answer is a
  path outside its packs and program folder. The alternative is 156 MB of "re-download your model" advice aimed at a
  file that is fine.
- `WarnOnRuntimeDrift` compares the mapped module's version with the wrapper installed beside the executable, since the
  pack publishes the two together and a mismatch means one of them moved alone. Warn, not refuse: major.minor is not the
  whole contract and an engine that speaks beats a tidy log.

### The MSVC runtime the same way

`onnxruntime.dll` imports `msvcp140.dll`, `msvcp140_1.dll`, `vcruntime140.dll` and `vcruntime140_1.dll`. Those four
install app-local beside `EQLogParser.exe` (`EQLogParser\redist`, Microsoft-signed and left that way) so a machine with no
Visual C++ redistributable can still speak — historically that was exactly where Kokoro failed on Wine while Piper worked,
and the difference was never the ONNX build.

They are claimed by name in the same call, before ONNX Runtime is mapped, and that claim is the load-bearing part. A
runtime loaded from `<kokoro>\native` is mapped with an altered search path — its own directory, then the system
Directories — so the program folder is **not** in that list, and four DLLs beside `EQLogParser.exe` would do nothing for
it unless those names were already resident. Claiming them first means ONNX's imports are answered from the module list,
which is also why the claim runs before the runtime and not after.

The consequence of installing them flat in `{app}` is worth stating plainly: the search order puts the program folder
ahead of System32, so these four are what this process maps even on a machine that has a newer redistributable. That is
Microsoft's *local deployment* of the CRT and it holds under one condition — **the checked-in copies stay current**
(`EQLogParser\redist\README.md` says how to refresh them). The CRT serves older binaries forward, so an up-to-date
app-local copy is a safe floor for everything in the process: Syncfusion's natives, NAudio's, ONNX Runtime's. Which file
answered each name is visible twice over — in `scripts\MeasureLoadedAssemblies.ps1` output, and in the Debug lines
`ClaimVisualCppRuntimes` writes.

Whether that makes Bottles' `vcredist2022` dependency redundant is a separate question with a fresh-prefix test in front
of it (`bottles/Games/eqlogparser.yml` keeps it until someone runs one).

## Floating Combat Text

`View → Floating Combat Text` shows the player's own combat numbers from live log records. The rendering choice is settled and recorded in
the local working doc `docs/NagFctReference.md` (untracked; SkiaSharp beat a WPF vector path roughly 100 fps to 30 at ×10 raid scale), and the loser has since been deleted rather
than kept as a reference - see "Shared policy, one renderer" below. This section covers why the *plumbing* is shaped the way it is, because that is
the part a later change is most likely to undo by accident.

### One queue, drained on the render tick

`FctManager` does not raise an event per record. It pushes `FctHitCommand`s onto a `ConcurrentQueue` and the
overlay drains it from the canvas's `EventsFrame` callback — once per painted frame, at most 60 times a second.

The earlier shape posted one dispatcher item per record, which is the worst of both worlds: a raid AoE window
of ~200 hits/s became 200 cross-thread posts and 200 layout invalidations, and a UI stall replayed the whole
burst seconds after it stopped mattering. Draining on the frame clock batches for free (the frame is the batch)
and makes staleness cheap to reason about: anything older than `FctManager.MaxQueueAgeMs` is dropped and counted
rather than drawn. A combat number that arrives half a second late describes a swing the player has already
reacted to; showing it is worse than losing it, because it lies about what is on global right now.

Two counters make overload visible instead of mysterious: `FctManager.DroppedCount` (queue lost the UI could not
draw) and `FctSkiaCanvas.DroppedCount` on the canvas, which forwards the ingest's (hits the lane caps refused). The header shows their sum
as "N dropped" only when it is non-zero, so a healthy overlay stays quiet.

### Parsing costs nothing when nobody is looking

`FctManager.Enabled` is a volatile flag gated at the top of both handlers, and it is set from the overlay's
`IsVisibleChanged`. Hiding the overlay therefore stops the feed at the parser: no player-name comparisons, no
pet-owner registry lookups, no command allocation. `DamageLineParser`/`HealingLineParser` additionally hoist
their static event delegate before raising, so with no subscriber even the `*ProcessedEvent` wrapper is not
allocated — FCT is the only reason those events exist, and it should not tax a user who never opens it.

A window owns exactly one manager: `FctManager.Create()` on construction, `Dispose()` (which unsubscribes from
both parsers) on close. Without that pairing a closed overlay keeps a live handler chain feeding a queue nobody
drains, which is invisible until the next session shows yesterday's fight.

### Shared policy, one renderer

The two canvases used to carry near-copies of the same layout and motion code, and they drifted within days. That is why `FctHitState` is plain data
and the decisions live in one place - and why there is now one renderer instead of two behind an interface. The second backend survived its own A/B
verdict and stayed "for reference", during which the frame pump existed twice; when the configure-mode demo was added, `FctSkiaCanvas` learned to
ask for frames on its behalf and `FctSimCanvas` did not, and the loop ran at about two frames a second - numbers frozen between cues, then jumping.
Nothing was expensive; half the code that asks for drawing had never heard of the thing animating. A seam with one implementation is not a seam, it
is a second copy of a rule:

- `FctIngest` — fold a repeat into the number already showing that exact hit, spawn, take a full lane's slot from a less
  significant number, or count a drop.
- `FctLayout` — which band of the canvas a lane lives in, spawn position, travel, the protected middle.
- `FctPlacement` — for travelling text, throwing that spawn several times and keeping the one whose flight least crosses the
  numbers already in flight (§"Travelling numbers pick a gap to go through").
- `FctMotion` — position, scale and opacity as pure functions of `(hit, age)`, plus the rule for the main line (one hit's
  face value, plus how many identical hits it stands for).
- `FctStyle` — lane → font size/color as `0xAARRGGBB` ints, so no renderer owns a palette copy.
- `FctLifeController` — adaptive lifetime.

What is left in `FctSkiaCanvas` is what genuinely belongs to a renderer: substrate resources (`SKFont`/`SKPaint`/halo sprites), the frame pump and
the blit. That split is also what makes the animation unit-testable (`EQLogParser.Wpf.Test/src/ui/control/Fct*Test.cs`) without a window, dispatcher
or GPU — worth keeping in mind before moving maths back into a canvas.

`FctMotion.RefreshText` having the text rule is deliberate and fixes a real bug: zero-damage records (Dodge,
Parry, Invulnerable) carry `Value == 0`, and a renderer that recomputed the numeric string each frame overwrote
the label with "0" — which reads as a legal absorb rather than an obvious mistake. A hit with `FixedText` set now
can only ever draw that text.

**A fold counts, it never adds.** Folding is for EQ's habit of repeating exact values — every DoT tick, every fixed-damage
proc — and what it produces is one number that says how many: `2,040 ×2`, `412 ×5`. The key is lane + side + proc-or-direct +
periodic-or-direct + ability name + **the value as it is drawn**, so a fold can only ever combine hits the player cannot tell
apart. Same-ability-but-different is not close enough: matching on lane alone once let an Immolation tick grow a number
labelled "Spinning Attack", which is a wrong total wearing a true label, and summing identical hits was the same mistake at a
smaller scale — 4,080 is an amount no hit landed for, it makes the player divide to find out what happened, and eight routine
ticks wearing the face value of a big one is precisely the confusion the fold exists to remove.

The lane cap decides who owns a slot rather than whether the information survives. The fold is tried before the cap and does
not care about occupancy, so a stream of identical hits never consumes slots at all — 20 seconds of the same 900 at eleven a
second measured two live numbers and zero drops. What cannot fold goes next to eviction: take the least significant number on
screen, but only if the newcomer clearly outranks it, where significance is what a number *stands for* — face value times its
count, with a proc discounted because one is subordinate by design. Only then is a drop counted. Healing direct casts are
excluded from folding at every occupancy — players read heals one cast at a time, and two identical heals are still two
casts of two different targets — which is why they need eviction: without it, "no folding" would mean "the thirteenth heal in
a raid-wide panic is silently invisible", and the point of showing healing at all is that a missed one matters.

### Raster at most 60 times a second, and never on a beat pattern

`CompositionTarget.Rendering` fires at display refresh, so on a 144 Hz monitor an animated canvas would raster a
full surface 144 times a second for text nobody can read faster. The cap lives in `FctFramePacer`, kept out of the canvas so the rule is testable
against synthetic tick streams; the tick still fires `EventsFrame` (the simulation paces its record schedule off it) but the surface memset + draw +
blit does not run. On an 800×560 overlay at 150% scaling, each skipped raster is ~2.3 MB of pixel work avoided.

**The pump asks about every list that moves.** It invalidates while there is animated content, and the configure-mode demo is animated content that
deliberately does not live in the canvas's own hit list - keeping it out is what protects the counters. A pump keyed on that one list repaints twice a
second during configuring. `FctDemo.Animated` is the demo's answer to the question, and `Animated_DemandsAFrameForEveryFlight` counts frames rather
than trusting intent: over 90 % of ticks across the busy part of a cycle must have something in flight.

**The cap counts whole ticks, and that detail is the difference between smooth and juddery.** The first version asked
"has `TargetFrameMs` (16.67 ms) elapsed since the last paint?" — a threshold tuned to 60 Hz sitting on top of a 60 Hz
stream. Real frames arrive at 16.4, 16.9, 16.6…; whenever one lands a hair early it is skipped, the next frame is two
refreshes later, and the cadence settles into an alternating one-frame/two-frames pattern. Positions are exact and the
average fps looks perfect while the text visibly stutters, most on the display where the cap is not skipping at all in
principle. `FctFramePacer` measures the refresh interval from the tick spacing itself (light EWMA, samples outside
0.5–200 ms discarded so a tab-out or a GC pause cannot retune it) and then paints on a whole number of ticks nearest
the target ratio: every tick at 60 Hz, every second at 120 Hz, every second at 144 Hz (72 fps, not the 48 that a time
threshold produced), every fourth at 240 Hz. Never faster than the display, never a beat pattern, and it re-derives
itself when the window moves to another monitor mid-fight.

Because smoothness lives in the tail and not the mean, `FctSkiaCanvas` reports `MaxFrameMs` (worst frame in the stats
window) alongside the average, plus `DisplayHz` so painted fps can be read against the real refresh rate: 72 fps under a
144 Hz display is pacing, 60 fps under a 60 Hz display with a 40 ms max frame is overload. The simulation window prints
both; the gameplay overlay deliberately does not, because a stats line that moves every second is a distraction in a pull.
`FctFramePacerTest` feeds synthetic tick streams — including a jittered 60 Hz one, where it asserts the old time-threshold
rule really did skip frames — so the cadence is pinned without a monitor.

The destination bitmap and its copy buffer are allocated once per size and reused. Allocating a `WriteableBitmap`
plus a fresh `byte[w*h*4]` every frame put ~5 MB/frame on the large-object heap and forced a new GPU texture
upload each time instead of updating the existing one; both are now per-resize, not per-frame. The surface is
explicitly `Bgra8888`/`Premul`, byte-identical to WPF's `Bgra32`, so `ReadPixels` is a memcpy with no conversion —
and it fails closed (skip the frame) rather than blitting wrong bytes if Skia ever hands back another format.

The remaining copy (surface → snapshot → pinned array → back buffer) is the known next step: `D3DImage` with a
shared Skia surface would remove it. It is not taken here because it needs a real GPU to validate against, and
three memcpys are not what limits this renderer today.

### Two region schemes: bands and split

The overlay cannot know where the player's target ring, cast bar or spell gems sit in the game window, so every scheme is
a promise about what stays where. There are two of them, chosen on the configure row (`FctOverlayMode`), and both live on one
geometry path: `FctStage` resolves (choice, canvas size) into the questions the layout actually asks — whose region is this,
which way does it travel, and what do the style amplitudes measure against. Bands answers "the whole canvas, out up and in
down"; split answers "one of four columns, each owned by whichever category booked it, each travelling the way that category
was told".

**Bands** keeps the original promise: a strip across the middle stays empty, mine rise above it, hits on me sink below it,
and both travel *away* from the gap (`GapTopFrac`/`GapBottomFrac`). The history that got it there: the first scheme spent
clearance horizontally — incoming lanes on the left half, outgoing on the right, a reserved centre column and a per-frame
clamp keeping even a blowout crit off it. It worked for numbers and failed for everything else (left/right is a convention
that has to be learned, with no analogue in EQ's own UI), so direction went vertical, which is how nearly every game with
floating text does it. Three things came out of that for free:

- The empty middle is empty **by construction** rather than by clamping traffic out of it. Diverging travel cannot
  cross the gap it started outside.
- Direction has two independent carriers (band and direction of motion) instead of one, so it survives a glance,
  peripheral vision and a colorblind player.
- The x axis stopped meaning *who* and went back to meaning *what*: damage sits toward the middle of its band, healing out
  wide, crits and labels centered. Lane slots are still needed — overloading one axis with two meanings was the sin, not the
  slots.

**Split** is the columns: four of them (`FctRailLane`), named left 1, left 2, right 1, right 2 from where the player sits, with
healing and the two damage kinds each booking one by setting. Why four and not two sides is a section of its own below ("Split
counts its lanes"); what belongs here is that split is a *region* scheme and not an animation. `FctStage` answers "which column
owns this lane", and everything downstream — spawn, travel, collision, the room a source line gets — measures against that
column's rect rather than the canvas's, which is the same rule bands applies to its bands and the reason both schemes share one
placement path.

Because direction is the *who* carrier in bands, the lane and per-category direction controls belong to split: position says what
a number is there, and each category's dial is free. The settings window shows those controls in split and hides them in bands — a
setting with no effect is a control that should not be on screen. Nothing on the overlay itself explains which scheme is running:
the arrows that once spelled it out in the panel ("heals ← to me →") were deleted, because the columns they described move live two
inches away (see "Two dials and a short loop: what configuring is for").

A third scheme lived here for a while: **halves**, the genre's two side-by-side areas — Mik's Scrolling Battle Text ships incoming
left and outgoing right, both scrolling down with a parabola bow (fork `Placidina/MikScrollingBattleText`, `MSBTProfiles.lua`:
classic master profile L175–196, retail L1640–1668, both `animationStyle = "Parabola"`, `direction = "Down"`). It bought one thing
bands could not: position carries *who*, which frees direction to be a per-side setting. Split buys the same thing more precisely —
a column says *what* as well as *who*, and three categories can be moved instead of two sides — so halves finished with no name in
any menu, no ini word anyone types, and an engine mode whose only job was keeping `FctStage`, `FctPlacement` and `FctIngest`
general enough for a shape nobody could select. It went, along with the burst-scoring placement search it existed to serve and the
per-row congestion ladder that came with it: 605 lines removed. The geometry stays general (a stage is still "some regions, some
directions") because that is what makes bands and split one code path rather than two.

A settings.ini from that era carries `FctOverlayLayout` written as an enum name (`"halves"` / `"bytype"` / `"bands"`) by a build
that never released. The key is not read any more — nothing shipped, so nothing migrates — and the mode row writes
`FctOverlayMode`: `split`, or anything else, including absent, which is fountain. That is one boolean rather than a parse table
because there are two modes and one of them is the opinion a first run should land on; a hand-typed word cannot reach the geometry
as garbage when it can only ever resolve to those two. The lane and direction keys have one fallback each and it is the shipped one
(`FctOverlaySettings`).

A fountain's choreography — travel, then accelerate under gravity while shrinking — points *down* on its way out, and an
incoming band is the last thing before the bottom of the screen. A literal fall there parks the number against its own
band edge for half its life, which reads as stuck rather than as physics, so `FctIngest.AssignLifetime` **mirrors** it:
`FallDist` is signed screen-relative (positive falls toward the bottom, negative back up toward the gap) and an incoming
hit gets half its sink distance back on the way out. Both sides then have the same overshoot-and-settle
shape in opposite signs, which is what "one animation, two directions" was supposed to mean, and the return cannot reach
the protected strip because it is a fraction of travel already spent below it.

The layout keeps its promises at any window size: a band that a short window would invert falls back to "inside the edges"
instead of throwing — an inverted clamp band used to crash every frame on a small overlay. `FctLayoutTest` pins it at
100–240 px, across the whole motion and at crit scale.

### The source label ships hidden; below, left and right are the seats it takes when asked

`(slash)` — the little name under a number saying what made it — had exactly one seat: below, which is where this overlay
drew names for years. A settings row (`label`) offers **left / below / right / none**, and **none ships**: the amount is
the currency of this overlay and the name who caused it is opt-in. Nag-style readers take number
and name as one token and want them on one line, which is what left and right are for. Two rules keep it from becoming a layout wobble:

**The number never moves for its label.** `hit.X` is the amount's own center in every placement; inline labels hang off
the measured edge of the value (baseline-shared, word-space gap), so a column of amounts keeps one visual spine whether
the words live below, left, or right. That is why composition lives entirely in the draw pass: placement is read per
frame, changing it repaints, and no number in flight is thrown away by a typography choice.

**The measurement that makes it possible rides with the other one.** The label's width is measured when glyphs are
rebuilt (which already measures the value for the travel clamp), not per frame — folds change the value's face, so both
widths get recomputed together and stay honest. `FctOverlayLabelSide` persists it ("left"/"below"/"right"/"none", absent =
none); it is typography rather than layout, so the row appears in both modes. The absent case is the one that changes hands:
a `settings.ini` written before the row existed — or one whose owner never touched it — now opens silent, so anyone who liked
the names under their numbers asks for them once.

Both directions name every seat (`FctOverlaySettings.ShippedLabelSide` / `WordForLabelSide`) rather than letting a fall-through carry one
of them. While "below" *was* that fall-through, absent and chosen were the same line of code, and moving the shipped answer would have
overruled everybody who had picked it without their word ever being asked; `FctShippedLabelTest` pins absent → none and saved → chosen so
the swap can only happen on purpose.

### Configure mode moved out of the overlay into a settings window of its own

**The panel speaks the app's language, literally.** Like every other window in the application it stamps itself with
the active skin at construction (`ThemeConfig.SetCurrentTheme(this)` before `InitializeComponent`) — without that stamp
its ComboBoxes and numeric spinner render as bare WPF instead of wearing the theme — and it re-stamps on
`ThemeConfig.EventsThemeChanged`, unsubscribing when it closes since that static event outlives every listener. Brushes
and `EQDescriptionSize` itself need no such care: they are application resources, and DynamicResource swaps them live.
Its fonts come from the theme
(`TextElement.FontSize = {DynamicResource EQDescriptionSize}` on the panel root — one inherited attribute, so the whole
window shrinks and grows with the application's own font scale like every other surface); its category picker is the
app's checkbox-in-a-dropdown combo (`ComboBoxItemTemplateSelector`, closing the dropdown commits, and the closed face
counts: "3 categories Selected"); its threshold is the trigger grid's numeric `UpDown` (0…9,999,999, typed or spun, no
ladder); and it has no close mark and no Esc — **Save and Cancel are labeled buttons, and clicking one of them is the
only way in or out**, keyboard included. Leaving a configuration session is a decision with two visible names; a
keystroke should not silently discard what a click was willing to name. Procs joined the category list as the fourth
kind while that combo was being born.

**The panel is ordered by how permanent things are, not by the order features were born.** Above the first hairline sit
the always-applies — sliders first, then dropdowns, then number boxes (speed, text size, crit size, label, show, hide
below); below the second hairline sit the layout decisions the mode actually obeys (mode, shape, and
the three categories alphabetical: damage to me, heals, my damage). The threshold crossed into the permanent block on
purpose: a threshold is a statement about numbers, not layout, and fountain filters with it too — hiding it there had
been a UI politeness the engine never shared. The footer under the last hairline lost its legend arrows ("heals ← to me
→") in favor of the live frame counter beside the sample-data checkbox: explaining columns with arrows in a panel whose
overlay shows the columns moving live two inches away was explaining the weather through a diagram.

Every control living on a strip inside the overlay worked until there were enough of them to care about the same pixels
as the numbers they were previewing — in a small window the row wrapped straight into the demo it existed to show. The
settings are now a second, owned window (`FctSettingsWindow`): fixed width, height hugged to content, a vertical property
grid with each name beside its control, and the application's own theme rather than the overlay's translucent panel
chrome. The overlay during configure is nothing but numbers; both windows stay over the game together because it is owned
by the overlay and Topmost like it.

The split keeps one source of truth by construction. A plain state object (`FctConfigState`) crosses the boundary in both
directions: `LoadFrom` hands the panel what to display, every control change raises a fresh snapshot for the live preview,
Save hands the final copy back as the only road to settings.ini, and Cancel asks the overlay to put everything back. The panel owns no canvas and no keys, which is what stops a second topmost
window from becoming a second authority: `StagedState()` in the overlay is the single list of staged values, so
"leaving without saving restores exactly this" cannot drift.

Two small behaviors worth naming. The panel parks itself to the left of the overlay (right if the screen objects) and
follows the overlay while it is dragged — unless somebody moved the panel on purpose, in which case its place is kept
until configure reopens; and hiding the overlay takes the panel out of sight with it. The sample-data checkbox came along
too, still session-only and never written: it rides the state for exactly as long as configure mode lasts.

POSITION is where numbers enter. Four boxes — height, left, top, width — edit the overlay's rectangle directly in
virtual-screen coordinates, so a window can be placed exactly without pixel-hunting with the mouse; typing moves the
window at once, and dragging or resizing it rewrites the boxes to match. Numbers and mouse are two hands on one
rectangle: there is no separate applied geometry and no new keys — Save persists the live bounds the same way closing
always did, and Cancel restores the rectangle snapshotted when setup opened, because abandoning a geometry edit should
behave like abandoning a settings edit.

### The configure row says fountain and split now: the mode layer over the schemes

The engine commit above made two words honest, and this one puts them on the row. The layout combo (halves/by type/bands)
and the motion combo (hold/fountain/pulse/spray/parabola) are gone — retired rather than hidden, because a player should
not meet the geometry vocabulary to choose between looks. What replaces them:

**mode: fountain | split.** *Fountain* is the engine's bands geometry wearing spray motion — numbers pop near the middle
and spew out and fall, which is the look every classic FCT draws with; its controls are a shape pick of **spray | freeze**
— spray being what makes it a fountain, freeze the same bands drifting their numbers out and stopping instead — plus three
direction dials, the show switches, size and speed. Healing's dial speaks in fountain too: **heals rise while damage
sprays down** is the classic FCT look, and until this dial was honoured (in `FctStage.UpFor(hit)` and threaded through
the bands factory) a fountain silently chained healing to the damage-in direction — the row that controls it was hidden
whole, because hiding a lane pick had swept up the direction with it. A band gives heals no column, only travel, and
travel is all the dial asks for. *Split* is the side columns (internally by type) with each category
assigned its own **lane and direction** — damage in, damage out, heals, six picks over four columns, any of them sharing
a lane — plus **shape: arc | line**, one rail machinery under both, with or without the bend. Split offers no
rest state on purpose: a row parked part-way down a column is not a calmer queue, it is a broken chain, which is the
one thing the column exists to prevent — so freeze belongs to fountain and nowhere else. Controls a mode does not
obey collapse rather than sit disabled — in fountain that means the **lane pickers only**: every direction dial stays,
because every mode has travel to answer for, and the threshold lives above the mode row entirely (it filters numbers,
not layouts; hiding it there was retired with the minimal-UI era). The legend re-sentences itself from the staged
choice either way. The shape row serves both modes with **two combos in one
cell** rather than one list of illegal promises: WPF items cannot live in two lists, and a scheme should never show a
motion it would only degrade, so each mode owns its list and the mode swap shows one.

**settings.ini speaks the player's words.** `FctOverlayMode` ("fountain"/"split", absent = fountain — the mode that needs no explanation opens the first run) and
`FctOverlayShape` ("arc"/"line"/"spray"/"freeze"; the retired "straight" spelling still reads, because it names the same
rail — the older "parabola"/"hold" spellings do not read at all: they were never in a release, so an ini still carrying one
resolves to its mode's own shape and nothing complains) name the mode;
per-category keys carry the rest (`FctOverlayHealDirection`,
`FctOverlayTakenDamageLane`, `FctOverlayDealtDamageLane` beside the existing direction keys; the older `…Side` spellings
still parse, to the side's outer lane). The combos-era
`FctOverlayLayout` key stops being read — nothing of this shipped, so nothing migrates — and an omitted direction still
reaches the constructor as *null* so bands keeps its outward invariant without anybody having chosen it. Because the mode
layer cannot express an illegal combination (fountain cannot store a rail), the load-time legality repair the combos era
needed is gone too: there is nothing left to fix up. `FctOverlaySettings.ClampShape` is where that promise lives, so a
stale key, a hand-edited ini or a half-built state resolves to the mode's own motion — fountain cannot even accidentally
run a rail.

**One word per shape, panel to engine.** The dropdown, settings.ini and the enum now agree: **arc**, **line**, **spray**,
**freeze**. Carrying a player word and an engine word for the same shape meant a hand-edited ini said one thing while the
combo showed another, and only one of them was checked on load. One exception survives on purpose: the panel saves and shows
`line` while the engine says `Straight` (MSBT's name, and what the geometry tests assert). The style once labelled **settle**
was renamed rather than relabelled — "hold", its old engine word, names a beat in an animation timeline and reads as a frozen
UI; **freeze** states the thing a player has to predict: it travels, stops, and fades in place.

Engine names stay where geometry carries tests: `FctLayoutMode.Bands`/`ByType` and `FctMotionStyle.Spray`/`Arc`/
`Straight`/`Freeze`, and that is the whole list — a style that was tried and removed (pulse, with the cell grid built to place it) is described
where its lesson is written down, below. "fountain" and "split" name UI states; "bands" and "by type" name the geometry under them.

### Two simple modes need three engine facts: category columns, steerable bands, and a bow-less rail

The configure experience is converging on two modes — **fountain** (numbers pop near the centre and spew out and fall;
two direction dials, the show switches, size, speed; nothing else) and **split** (the side columns, with each category —
healing, damage on me, my damage — assigned its own side *and* its own direction; shape chosen between **arc** and
**straight** — since grown into arc | line). The modes are a settings slice; three engine facts had to
exist first, and each says goodbye to an old shortcut:

**Directions became per-category.** `FctLayoutChoice` gained `HealUp`, `IncomingDamageSide`, and `OutgoingDamageSide`:
the side and the way a number travels now follow *what it is*, so healing can rise on the right while damage on the same
column sinks. The cell-grid pool followed the resolved column rather than the bit that chose it — with three categories
picking two columns, only the column itself identifies the territory a grid belongs to.

**Bands reads its directions now.** Its outward scroll was hard-coded in `UpFor` — the strip invariant as an if-statement
nobody could dial — which the fountain mode's two dials made wrong. The choice constructor distinguishes *omitted* from
*stated* (`bool?`): omitted directions on bands still ARE in-sinks/out-rises, so every pre-existing behaviour and test is
untouched, while a stated direction wins. "Direction is the who-carrier in bands" turned out to be a convention this
project inherited from itself, not a law — the protected strip stays protected because travel and fall are measured from
the band's own edges whatever sign they run at.

**`Straight` is a rail style, not new choreography.** MSBT ships Straight next to Parabola, and it is exactly what it
looks like: the same constant-speed scroll with the bow zeroed — shared entrance, one rate per lane, the column's braid
braids included, because all of that keyed off travel and placement, never off the arc. `FctMotionStyles.IsRail` is now
the only correct question ("does this ride a rail?"); naming one style in a branch would let the shapes drift apart
silently. Braiding itself splits by shape though, because sideways means something different on each: an arc crowds
into emergency columns beside its lane (bows sweep across them, so they read as part of the dance), while **a line
steps INTO its travel** — a row denied the mouth enters one text line further along the same column. Sideways offsets on
straight parked whole categories beside the lane permanently, which is exactly how misses and parries ended up in their
own little column every fight (frequent enough to always be crowded) while resists sat centre (rare enough never to be).
With a shared scroll rate the spawn-time gap locks for the whole flight, so crowded line rows read as one queue that
entered slightly staggered. Sideways columns stay underneath as the valve for past-throughput storms — a clean second
 column during a burst beats two numbers printed on each other, and no number is ever dropped — but they are no longer
 where an ordinary fight parks its words. Bands degrades both rail styles to freeze exactly as it degraded the arc.

### Category switches: what story an overlay tells

A request that layout modes cannot answer — *"outgoing damage left, healing right, and just turn incoming damage off"* —
is a content question, not a geometry one, and it splits the overlay's output into switchable categories: **my
damage**, **damage to me**, **healing** — plus **procs**, whose own switch arrived later because a proc line can read as
double-counting the swing that triggered it (`ShowProcs` is an extra opt-out on top of category: hiding my damage hides its
procs too, and this one only ever removes more). The gate lives in `FctIngest.Accept` next to the threshold and reads
the two bits spawn already computes (`heal`, `incoming`) — three comparisons and a proc test, zero new routing:

```csharp
if (!(heal ? ShowHeals : incoming ? ShowTaken : ShowDealt)) { FilteredCount++; return null; }
```

Three separations keep the two filters honest, and each has a test. **Identity before noise:** a category that is off is
not even measured against the threshold, so a filtered hit never spends the hidden count's budget — `filtered` and
`hidden` are different numbers for different choices, surfaced beside `dropped`. **Above the fold:** like the threshold,
the gate sits before folding, so an invisible tick can never inflate a visible `×N`; a category that was off simply has
no history when it returns. **Words follow their side:** the label exemptions that protect words from the *threshold* do
not extend here — "Miss" belongs to whoever missed, and defense words (`Defensive` routes as incoming in
`FctLayout.IsIncoming`) belong to the story of the spell that came in, so switching off `damage on me` also quiets the
resists and blocks won against you. Off means **off**, down to zero visible categories if somebody wants that; nothing
is lost silently while it is.

**Each word stands alone.** The words with no number — miss, parry, dodge, block, riposte, resist, absorb,
invulnerable: `FctManager`'s `IsDefensiveLabel` set plus Resist, which is everything the parser can write — collect a
different complaint from the category one. It is never "fewer words"; it is *that* word: misses during a whiff storm,
resists against the one spell that keeps failing its check. So the **show** combo grew to twelve checkboxes — the four
categories and every word as its own — rather than gaining a second dropdown: words answer the same question the
categories do ("what may draw"), and two dropdowns asking half of one question each is panel furniture with an opinion.
The twelve sit **alphabetically**: the first arrangement was "fight order" (categories, quiet defensive words, the loud
pair), but a dropdown is a lookup list, and ordering it by narrative turned finding a word into a memory test. The
items are named fields so re-sorting never means rewriting twelve positional indices in two blocks.
Same opt-out semantics (absent is shown; only an explicit 0 mutes, because a junk value must never eat somebody's
"resist") and the same `filtered` accounting — a muted word visits `hidden` never, since the threshold's count is
about numbers and these are not numbers. Three rules keep the layer small: words stay exempt from the threshold; the
switches stack *under* the categories (defense words belong to "damage on me" still, and a word switch only ever
removes more); and unknown text always draws — `WordShown` answers true for anything outside the eight, because a
word these switches have never heard of is nobody's implicit opt-out. A word *is* its Labels constant, so the name-to-
switch map lives with the switches (`FctIngest.WordShown`/`SetWordShown`) and the canvas forwards one method instead
of wearing eight properties. The demo cycle shows all eight words once — a switch nobody can preview is a switch
nobody finds — and that copy path also caught a stale promise: `FctDemo.Advance` had been copying threshold and three
categories for pages of notes about "copies the switches", missing procs since the day they shipped. The word tests
pin the copy now, procs included.

The configure row was already full, so the checkboxes live behind one dropdown that names what is ON — "categories" on
the closed face, counting what stays ("everything" in the default state; the common case must not look like a setting),
all staged like every other control: Save writes, Cancel puts back.

**The demo gate was never connected.** `FctDemo` runs a private `FctIngest` — that design is load-bearing, the loop must
never touch real counters — but the same design meant the dial's threshold only ever reached the *real* feed: the demo
spawned its script numbers through an ingest that had never been told about it, and the commit that shipped the ladder
claimed the preview showed it. It did not; nothing pinned it either. `FctDemo.Advance` now takes the real ingest as a
final `gates` parameter and copies threshold and switches every frame — the same per-frame-copy discipline that style
and layout arrived at after "selecting pulse played hold" — and `TheConfigureDemoObeysTheSwitches` is the test that
keeps that claim provable.

### The show list: nine rows of numbers, and a side you can switch off

Three categories turned out to be three switches too few and one question too many. The requests that arrived were not "less damage
to me" — they were narrower than that and specific: **no crits** during a pull where every swing is a crit, **my pet** out of the picture
during a parse, **heals but not the big green ones**, **spell noise off, keep my melee**. And the fourth request, "hide one side entirely",
was being answered twice over: by a category switch *and* by sending that category's lane somewhere silly. So the show list is now **nine
rows** — melee hits, melee crits, spell hits, spell crits, procs, pet melee, pet spells, healing, healing crits — beside the eight words it
already carried, seventeen checkboxes in the same single alphabetical dropdown, and the closed face counts them as **"types" selected** —
the noun every other multi-pick dropdown in the app already uses ("event types", "spell types", "types"), so FCT is not the one
panel naming the same idea differently.

**One record, one row.** The rule that keeps seventeen switches from becoming seventeen ways to be confused: every number resolves to
exactly one row, in one place (`FctManager.DamageRow`), while the parse still knows who fired it. Procs outrank everything — a proc is the
event a player watches for and does not care whether it came off a swing or a spell, and muting "melee crits" must not be a way of seeing a
proc that crit. Then the pet: its numbers are its own, which is the entire content of "get my pet out of my parse", and its crit stays inside
its row rather than jumping to mine because crits pool on screen. Only then kind and crit: hits/crits, melee/spell. An ambiguous record would
answer to two switches at once (mute one, still see it under the other), and an unassigned one would be a number nothing can hide — both read
to a player as an overlay ignoring its own settings. Three tests hold the line: `FctShowListTest.EveryRowHasExactlyOneSwitch` (every `FctRow` but
`Word` appears exactly once in the table, so a row added to the enum without a line in it cannot quietly become unhideable — that one lives in
`EQLogParser.Wpf.Test`, which needs Windows to run), `FctFilterTest.EachRowAnswersForItselfAlone` (muting one row must leave its neighbour visible,
which is what an implementation resolving several rows onto one switch fails) and `FctDemoTest.Script_CoversEverySwitchInTheShowList` (every row and
word has a cue, so every switch can be seen working without a fight).

**Which words those rows use, and why not MSBT's.** "Spell" means every non-melee kind the parser produces — direct damage, bane, damage
shield, reverse DS, other damage — because that is EverQuest's own vocabulary and MSBT's "skill" is a World of Warcraft word that means
something else here. A damage-over-time tick lands in the spell rows with everything else that did its damage slowly, crit ticks included:
the eye cannot pick a tick out of a fold (`FctIngest` collapses identical hits into `×N`), so a switch for it alone would be a switch for
something nobody can see. "Hits" means everything that did not crit, which is why each pair sits beside its own crit row in the table — read
down and the sentence explains itself without a tooltip having to say "hits here excludes crits".

**Words are not rows.** They carry `FctRow.Word`, meaning not row-gated, and keep the switch they have always had: their own text, because
the complaint about words has never been "fewer words", it is *that* word. What changed is that they were made honest about being attacks:
a word belongs to whoever did it (`FctManager` routes `Defensive` against incoming and `Missed` against outgoing), so a hidden side takes its
words with it — hiding damage taken quiets the blocks and ripostes you caused, and hiding your damage quiets your own misses and resists. The
direction gate still runs before the word gate, and the order is the whole meaning: AND of everything applicable, never one overriding another.

**The side switches are gone; a lane can be `none`.** "damage in", "damage out" and the old parent "healing" checkbox left the dropdown,
because split mode already carries three lane combos and a fourth answer to the same question was the duplication. A lane set to **none** hides
that category entirely (`FctConfigState.OutgoingShown`/`IncomingShown`/`HealingShown`), which serves the sentence the categories were invented
for — damage left, healing right, incoming gone — with one control per side instead of two that could contradict each other. Fountain has no
columns to hand out, so it reads `none` as shown: nothing was placed anywhere to switch off, and the setting is written down untouched so going
back to split restores exactly what was hidden. Geometry never sees the missing lane: `FctConfigState.Placed` parks a hidden category in its
shipped column, since nothing spawns there to decide what it looks like — `FctIngest` stops it upstream, before folding, so a hidden row cannot
inflate a visible `×N`, and it visits `filtered` rather than vanishing.

**Healing is inbound only now.** The rows are "healing" and "healing crits", which is an honest pair only if both mean the same story: what
lands on you. A heal you cast on someone else is dropped at the feed (`FctManager.HandleHeal`), because the overlay pictures your fight and you
know what you cast — and a pet-targeted heal goes with it, since the pet has rows for its damage and none for what it gets healed by. The rule is
a plain name comparison and stays that way: `HealingLineParser` already runs every name through `ParserUtil.ReplacePlayer`, which is what turns
"You have been healed over time for 1063 hit points by Roar of the Lion" into a record carrying my character name, so a second pronoun list down
in the feed would only be a second place to be wrong about who "you" is. That also makes the HoT case the interesting one — ticks arrive on me
through the same rule as direct heals, with no branch of their own to get wrong.

**No migration, deliberately.** `FctOverlayShowDealt/Taken/Heals` are not read and not translated; the new rows own new keys and default to
shown. This is the first release of the row model, no shipped settings file has rows in it, and a translation layer would have had to invent a
meaning for an old "healing off" that could mean either of two new rows — which is a coin flip wearing a compatibility hat. All seventeen are
opt-outs (`FctShowList`, absent or junk means ON, so a corrupt value fails toward information rather than toward silence), and the table is the
single source for label, ini key, row and word: the dropdown builds itself from it, the canvas applies gates through it, `LoadConfig`/`SaveConfig`
walk it, which is what keeps a row from ever being added twice or half-added with a switch that saves under one name and reads under another.

The **configure demo owes every switch a cue** (`Script_CoversEverySwitchInTheShowList`). A row with no sample is a switch that appears broken,
and it gets tested in exactly the direction that hides nothing — the player mutes "pet melee" and watches for something to disappear — so the
loop gained a pet swinging ("Claw") and casting ("Sonic Shock") beside mine, thirty-two events which is what the event ceiling is actually spent
on. That rule is also the reason spell cues name their row explicitly: a cue whose row disagreed with what it looks like would make the switch
seem broken in the one direction the player is checking.

### By type: columns owned by category, directions sharing a rail

The *heals left, damage right, mine up, theirs down* request is nearly MSBT's default geometry, and MSBT cannot actually
serve it: a scroll area scrolls **one** way (the direction argument to `AnimationManager.Add`), so his users build this
layout out of Add Scroll Area plus re-mapping heal events into the extra area. The layout asks two independent questions,
and conflating them is what made it look impossible: **ownership** of a column and **travel** inside it (still each direction's own setting). `FctLayoutMode.ByType` therefore flips exactly one bit:

```
owner = mode is ByType ? hit.Heal : hit.Incoming     // FctStage.RegionFor(hit)
```

Because every region, territory and travel rule already reads through that one function, the mode came free: resize mapping,
shape legality, and the lane's one-beat tempo all ask the stage, never the mode. `Heal` is captured at spawn the way `Incoming`
was — same reason one layer up: a heal crit lands on `FctLane.Crit`, where the lane no longer says what it was. Ownership that
ignores direction does introduce one genuine novelty: two categories in one column whose dials disagree. A shared lane *and* a
shared direction is one queue by design (`FctConveyor.KeyOf` keys a lane by column plus travel sign), and two clocks sharing one
region is what the settings refuse on load (`FctConfigState.ResolveLaneConflicts`) rather than draw head-on. The panel offers one
lane picker and one direction dial per category, the shape row offers the bend or the line, and `FctByTypeTest` pins the ownership
both ways, the traffic in a shared column (no losses, no crossings), and the rail running here as it runs anywhere.

### Split counts its lanes: left 1, left 2, right 1, right 2

Sides turned out to be half-lies. What a player sees on a split are COLUMNS, and what they ask for is "incoming in that
column, heals in that one" — but with only halves to configure, placement kept inventing columns on its own: two categories
sharing a side got woven into neighbouring sub-columns by the burst scorer (measured x = 200, 271, 343 inside one half),
so the settings said *side* and the screen showed something else, and nobody could predict which column a number would
use or why two categories refused to share one. `FctRailLane` makes the visible thing the configurable thing: four named
columns; the lanes a category actually books **tile their half** — two claimants take it in quarters in screen order, one
claimant takes the whole half, because a lane nobody books is air the number may use rather than a wall. Every category names the
lane it travels down. Categories that name the **same** lane genuinely share it — identical region, identical queue, chained like any two hits
of one category; categories that name different lanes can never touch each other's pixels, because a lane's territory is the span the
tiling gives it and those spans are disjoint by construction. Directions stay per category: opposite ways
in a shared lane means trains passing, which was always placement's puzzle to solve.

Two things fall out for free. The old side spellings parse to a side's OUTER lane — and when that lane owns its whole half, as in the
shipped default, its spine sits exactly where the half used to be centred, so stored configs land visually unchanged; and the
panel's words finally match everyone's: **damage in** and **damage out**, not "damage to me" and "my damage", in the
rows (before heals — the streams come first, the reacting category last), in the categories combo, everywhere a human
reads them. The two-word versions won over "incoming damage"/"outgoing damage" for the same reason the rest of the row
is lowercase: a settings label is read at a glance across a game window, and "damage in" says it in half the width. The shipped split spread puts healing in
left 1 rising, damage in in left 2 sinking, damage out in right 1 rising — each category its own column on first sight — and leaves right 2 free, so the
outgoing-damage column owns its whole half of the screen while the two left-hand categories share theirs in quarters. That is where split's label budget used to die (a name beside numbers had a quarter-lane minus the digits),
and it shows in setup mode: while configure mode holds, every booked lane is outlined with its own spelling — left 1, right 2 and the rest
(`FctSkiaCanvas.DrawLaneGuide`) — under whatever the canvas draws there, sample-data numbers or not (the guide belongs to setup itself;
sample data only puts numbers on it). An unbooked lane gets no outline because it has no rect left; that is the feature
wearing a thin white line.

The guide also taught the branch's hardest debugging lesson. Its outlines showed a second lane wall through the middle of
any half that a single category owned — a ghost line at exactly three quarters of the window width, reproducible only on the
player's machine, invisible to every unit test because the arithmetic in `FctStage` was innocent: logs proved the stage handed
out x=627 w=627 while the screen kept a wall at 940. Ten probe builds later (arithmetic stamp, pixel scan of the Skia surface,
per-phase column snapshots, paint-state dump, and one whole frame smuggled out as a PNG) the pixels and the numbers were still
contradictory until someone reread the draw call itself: `canvas.DrawRect(x, y, x + w, h, paint)` — and SkiaSharp's four-float
overload means **(x, y, width, height)**. The guide had been passing right/bottom where the API read width/height; left2's box
really was drawn from 313.5 across 627 px, its outline landing at ≈940 every frame. Every measurement was honest; only the
delivery was illiterate. The calls now build explicit `SKRect`s, which are unambiguous by construction. The moral, priced at
≈10 builds: when logs and pixels disagree, one of them is being read wrong — re-read the API signature before you re-read
the algorithm, and never log your intent when you could log the call.

The one promise lanes had to keep is that a lane moves as ONE thing. The first attempt shared a duration per side and
still showed per-category speeds — bigger text reserves more road, so equal times meant unequal px/s (see *The rail's tempo*
below); the rail runs to one scroll RATE now, measured identical for damage, procs, crits and
words in both directions. Fountain stays the deliberate exception: there procs are small and quick (below), because a
fountain is read as a whole — nothing in it is a scale you measure gaps against.

### Four motion styles, and the one thing none of them may change

The fountain began as a checkbox, which was honest while there were two choices and became a lie as soon as players
wanted text that stays put or fans out. `FctMotionStyle` is that axis now: **freeze** (travel away from the strip, stop,
be read, fade — the default in bands, and the overlay's very first behaviour before styles existed at all; split does not
offer it at all, since parking mid-scroll breaks the chain a column is made of),
**fountain** (one ballistic flight: thrown out of the spawn at its fastest, apex at mid-life, back down to the line it was
born on; mirrored upward on the lower band), **spray** (a random cone out of the lane slot, then a
short fall) and **arc** (a constant-speed scroll bowing out to a vertex at half height and back — MSBT's parabola, and the shape the scrolling-text genre
ships as its own default, and split's default for the same reason; see below). A fifth style, **pulse**, lived here with a cell grid of its
own; what it taught is kept further down. Where a hit goes, how it moves and how numbers stack are three
orthogonal decisions, and cramming two of them into one boolean was how "fountain" came to mean several things at once.

Three rules keep five styles from becoming five behaviours:

- **Motion is presentation, never information.** Band (or half) and direction of travel still say who acted whichever
  style is running, which is what makes "try each during the next pull" a safe thing to offer. The combo therefore applies
  to hits spawned *after* the change rather than restyling what is already on screen.
- **A hit keeps the style it was born with** (`FctHitState.Style`, snapshotted by `FctIngest.Accept`). If the renderer
  read one live setting, switching fountain → freeze mid-flight would hand every arc in progress a different
  velocity for its remaining frames. Snapshotting an enum per hit is what makes switching free.
- **The protected middle stays clear by construction, for every style allowed in bands.** Rail styles do not run there at
  all (they degrade to freeze, below), so the styles sharing the canvas with the strip are the ones measured against it; spray
  returns the whole of its own throw (`SprayFallRiseRatio`), mirrored upward on the lower band; freeze
  never passes its clamp. Outgoing fountain falls the whole climb back (`FountainFallRiseRatio` = 1), which ends the flight
  on its own spawn point — inside the band by construction, so the strip is safe because of where the fall *finishes*
  rather than because the clamp caught it (a test still sweeps the curve and asserts the drawn box never enters the gap). The rails live in split, where there is no strip to cross
  because the regions do not overlap; if settings.ini forces one into bands anyway, `FctIngest` degrades it to freeze rather
  than run it (a test sweeps each style across its whole curve asserting nothing enters the strip or leaves the window,
  so a future sixth style has to pass the same bar).

**The arc is split's default because it is what the genre ships.** MSBT's profiles are all of them
`animationStyle = "Parabola"` (see *Two region schemes* for the citations): numbers scroll at constant speed and bow —
x is a function of y². FCT keeps its tempo system and borrows the shape as MSBT actually writes it rather than as it
reads in a still: `ScrollLeft/RightParabola…` computes `x = y²/4a` with **y measured from the area's mid-point**
(`MSBTAnimationStyles.lua`), so the vertex is *mid-flight* — a number leaves its column straight up or down, bows out
to the widest point at half height, and is back on the column by the time it fades. That symmetry is the semicircle
chain in Mik's demos, each value tracing its neighbour's path; the monotonic outward drift this project shipped first
(`x = X0 + Bow·t²`, vertex at spawn) never came back, and looked like nothing in the genre. The vertical run stays
`y = Y0 − Rise·t` — linear, not eased, because easing it would leave the equation behind and draw a bend that only looks
like the genre's curve — and with y linear the mid-point formula comes out as `x = X0 + Bow·4t(1−t)`. `Bow` is signed
**away from the middle of the overlay** — the one direction a bow may go without reaching a neighbour's lane — and measures a share of that column's
territory (`ArcBowFrac`, 34%): MSBT's own curve swings a full area width, text running off its area while still
fading, and 34% is as far an arc as this overlay can keep inside the half — far enough to read as a sweeping curve,
near enough that the widest crit draw still clears both walls at the vertex (`FctArcTest` pins containment for
life, both sides, both directions). With right-aligned values (§ *The odometer*) the vertex needs the **whole** drawn
width on the inward side, not half, and `AssignTravel` trims the formula to whatever the territory actually offers
before the flight is scored — a clipped vertex would score one shape and draw another.

Two consequences follow from "the shape only". The speed dial works unchanged: it stretches `MotionMs` and the lifetime
and nothing about where the number ends, so a slower arc is the same curve drawn more slowly, which is what a tempo
dial should do for a constant-speed motion (a test checks the endpoints agree to the bit at both dial extremes). And a
resize maps `Bow` by the x factor exactly like `Sway`, so a number mid-scroll still ends in its new half. Legality lives in
one place — `FctStage.DefaultMotion`: split → arc, bands → freeze — and the configure row enforces it twice over: the
arc is never even offered in bands (each mode owns its shape list), and choosing bands while previewing an arc swaps the
preview to that scheme's default instead of showing a motion ingest is about to degrade anyway. A first-time split user gets
that default even though nothing was saved, because an absent key and a junk one land on the same arm of `LoadShape` — and here that is not a
loss: both mean nobody chose, and both are answered by the scheme's own shape.

Spray needed one constant that measurement forced: `SprayReachFactor`, roughly twice the depth of a band, with height
capped at what the band offers. Given only the band's own travel budget (`usable * sin(theta)`), spray's horizontal
coverage came out **identical to freeze's** — freeze already adds 12% of width in sideways sway plus a lane-slot jitter, so a shallow
cone sat entirely inside noise that was already there. A reach longer than the band lets the wide angles of the cone
actually move sideways while the steep ones simply top out against the clamp.

Widening the cone then exposed the real reason spray and fountain looked alike: **both axes ran on one ease curve**. When x
and y advance by the same fraction of their totals, every trajectory is a straight line from origin to apex — the fan
existed only in where numbers ended up, never in how they got there, so mid-flight a wide spray was a slanted fountain. That
diagnosis was right and the prescription it produced has since been replaced: spray was put on an ease-out sideways while its
vertical stayed eased, which bent the path but described no motion anyone could name. Once `TravelledY` became a projectile the
same reasoning that made the fountain one settled the sideways axis too — **a number in flight keeps its horizontal speed**,
so `FctMotion.LateralProgress` is linear for anything carrying a fall and eased for everything else. The kink the old ease-out
was defending against was a lateral that *stops* at the apex; constant sideways velocity never stops, so there was nothing left
to hide, and what the ease-out had actually been doing was spending all the sideways distance before the descent began — at an
800 px canvas the fan's outer numbers were already still in x and fell straight down for the last 40% of their lives, with the
widest point of the arc in its middle instead of at its end. Freeze keeps one eased curve on both axes: it claims no gravity,
so a straight climb is what it should draw. Tests pin both halves — freeze's drawn point never leaves the line between spawn
and apex, spray's must leave it by more than 20 px, and an airborne number must cover equal sideways distance in equal slices
of life while freeze's first tenth stays far short of its middle tenth.

Two smaller changes went the same way. The cone opened from ±38° to ±49°, with `SprayMaxLateralFrac` moving from 0.30 to
0.34 of width — that cap has to travel with the angle or every wide draw stops at the same wall and the fan comes out flat
topped — and spray's choreography got its own tempo, `SprayMotionWindowMs` (1700 ms against fountain's 2000), because
shared flight time was the other half of the resemblance: two 2 second arcs read as one effect whatever path they draw, and
shrapnel is supposed to look quick. Measured at 980×640 with these numbers, spray covers 519 px of x mid-flight where freeze
covers 246 and fountain 242.

### Fountain is a projectile now, and the fall is lit

Players read the old fountain correctly and described it correctly: the numbers went up, started to fade, "but they don't
really fall much". Measuring the flight through `FctIngest` at an 800 px canvas said why, and it was not the distance. The
placement cost hands a number about 260 px of climb there against a `H × 0.28` = 224 px fall, which is a respectable 87%
return on paper — but the fade was set to span *exactly* the fall, and with the eased fade's own shape that left 13% of the
descent visible at three-quarters brightness and 25% at half: **29 px out of 224**. Worse, that visible sliver was the
slowest part of a curve that left its apex from a dead stop — about 225 px/s at the moment it went half-bright. So the number
spent its bright life climbing and its dim life starting to fall, which is not a fountain; it is a hang followed by a melt.
Two changes, both of them about physics and light rather than tuning.

**One parabola, no seam.** `FctMotion.TravelledY` now flies the choreographed styles as a single projectile:
`height = Rise·(2s − s²)` in `s = t/a`, with the apex fraction `a = 1/(1 + √k)` solved from the two endpoints
(`k` = fall depth over climb). Nothing is chosen per phase any more — `k` is the only input the curve has, so a shape that
drifted away from ballistics would have to be re-derived rather than nudged. The flight leaves the spawn at its fastest and
brakes at one constant rate through the apex — which is the *only* still point in it, which is what an apex is — and arrives
moving downward as fast as it left, so the descent is already underway instead of starting from parked. Position, velocity and
acceleration are continuous end to end because there is one expression; the eased climb plus ease-in fall had no velocity kink
but reversed acceleration in a single frame. `a` comes out at exactly 0.5 for a full return, so nothing about where numbers
peak moved: what changed is that they leave fast and keep going.

**Every depth belongs to the throw, not the canvas.** The outgoing fall went from `H × 0.28` to the whole climb back
(`FountainFallRiseRatio = 1`), and each style's depth is now measured against the distance *that number* threw itself instead
of against the window. That is what
a return leg is, and it buys invariance for free: `FctResize` scales `Rise` and `FallDist` by the same factor, so `k` — and
with it the apex fraction — survives a vertical drag untouched, which a canvas-relative depth could not claim (stretching the
window used to move the peak of every number already in the air).

**The fall is lit.** `FallFadeFrac = 0.5`: the fade covers the last half of the descent instead of all of it, which puts
the same rule on the books that every shipping implementation already runs — GW2-SCT holds alpha at 1 until the last 20% of a
message's life (`src/ScrollArea.cpp`, `fadeLength = 0.2f`, at a constant `scrollSpeed` of 90 px/s), MSBT drives scroll and
fade on separate clocks (`MSBTAnimationStyles.lua`: position is pure progress, `scrollTime = scrollHeight × 3/260`, i.e.
~87 px/s), and even NAG holds opacity at 1.0 through its whole rise and spends only its last 23% reaching zero. Fading across
a fall was the outlier; it just had three implementations in front of us that no one had compared against.

Net effect, measured through `FctIngest` at two canvas sizes (H=800 / H=500). Fall depth 224 → 259 and 140 → 157 px, which is
not much — the depth was never the main problem. What the lighting rule buys is the part of that depth a player can see:
descent visible at ≥75% brightness **29 → 120 px and 18 → 75 px** (13% of the fall becoming 46%), and at ≥50% brightness
56 → 146 and 35 → 92 px. Speed while still half-lit 225 → 443 px/s, and it leaves the nozzle at ~590 px/s instead of at rest.
The shrink moved onto the fade's clock too (`ScaleOf`): a number keeps the size its font was given while it can be read and
collapses as it dims, because a thing getting smaller reads as a thing standing still, which was the other half of the melt.

### Spray obeys the same law, and freeze was measured and left alone

Having fixed the fountain it was worth auditing its two siblings with the same instrument rather than assuming the diagnosis
travelled. `SprayFallFrac` — 0.4 of the number's own rise — was the residue of a taste decision, and it measured as a
**119 px descent against the fountain's 259** at an 800 px canvas: the style whose entire identity is flying outward had the
least visible fall of any of them (55 lit px against fountain's 120). It also carried a false justification — "falling less
than it rose means no angle in the cone can return a number to the band edge, so the strip stays clear by construction" — which
is exactly as true of a full return, since a full return ends on the spawn line. `SprayFallRiseRatio = 1.0` replaced it: a
fragment comes back to the height it left from, the cone lives in the launch angle, and spray's shorter tempo (`SprayMotionWindowMs`,
1491 ms as measured) now makes its descent genuinely quicker than fountain's — 573 px/s at half brightness against fountain's
502 — which is what "shrapnel looks quick" was supposed to mean all along. Post-change, spray sees 132 px of its descent at
≥75% brightness where it saw 55, and `SprayFallsAsFarAsItThrew` sweeps forty cone draws asserting each one lands on its own
spawn line without touching the strip.

**Freeze is fine, and now there is a record of checking.** Measured at H=800 through `FctIngest`: 307 px of climb, 1754 ms of
motion inside a 3070 ms life, fade 767 ms starting at 75% of life — with **0.0 px of travel left** at the moment the fade
begins and 100% of its path drawn above three-quarters brightness. It has no fall to light and no seam to remove, so none of
the fountain's defects apply; it is the one style whose every pixel of motion is seen. Two things about it are worth naming so
they are not "fixed" later: it leaves the spawn at zero speed and its arrival is asymptotic, which is smootherstep doing its
job on a style that claims no gravity (and is the deliberate contrast with a ballistic fountain's 600 px/s launch), and its
travel duration rather than its speed is pinned to the canvas, so a taller overlay means faster numbers — true of every style
here, and adjustable with the tempo dial. Fixing that properly would mean pricing motion in px/s instead of in windows, which
is a different change on a different axis.

**What pulse taught, and what is left of it.** A fifth style lived here for a while: **pulse**, text that never travelled —
it swelled where it appeared. It failed on measurement rather than on taste: free placement is fine for numbers that move, because each
occupies a spot for a moment, but a number that stays is read for its whole life and two of them sharing a place is mush. Every combat log
UI lands on the same answer — slot allocation — so pulse grew a fixed cell grid (one cell per hit, oldest taken when a block fills, procs in
the outer row, rows filling centre-out), and that grid held up until the geometry around it was rebuilt: measured against the *canvas* rather
than its own region, its outer columns collapsed onto one place — 4 overlapping numbers in 8. The lesson outlived the style, and it is the
reason `FctStage` exists: **geometry depends on the region and the canvas size, never on the hit being placed**, and every rect a row may
occupy is measured against the region it was assigned. Nothing translates any more without a reason; the reduced-motion reading that pulse
served is served today by freeze, which travels once and then rests.

What was deliberately **not** built is anchored-follow (a number tracking its own mob across the screen). EQ's log
never contains actor positions, only names, so there is nothing to anchor to; that absence is the whole reason the
design leans on bands and an empty strip instead of "numbers above your target".

### Travelling numbers pick a gap to go through

Choosing a lane slot and throwing a small random jitter at it — about an eighth of the band's depth, which is all the layout used
to do — turned out not to be enough. Measured on a 980×640 overlay with a number arriving every 700 ms, **58% of fountain pairs**
shared a spot somewhere in their overlapping lives; six held numbers in one band, **71% of pairs**. Two numbers climbing nearly
the same path are unreadable for the whole flight, and they did it while most of the band around them sat empty. That is a
legibility defect wearing the clothes of a polish item.

So travelling text asks where it would actually go from a grid of legal launch points across its band, and keeps the one with the
least crowding (`FctPlacement`). Three things keep this from becoming a second layout:

- **Every candidate comes out of `FctLayout.Spawn`**, asked for an origin instead of a dice roll. The band, its reserve against
  the protected strip and the window edges apply to a requested launch point exactly as they do to a random one, so no candidate
  can sit somewhere the layout would forbid.
- **Cost is measured along the flight, not at the origin**, because that is what the player watches: fountain text falls back
  through the band it climbed, so two spawns that start apart can still collide on the way down. Samples are wall-clock aligned,
  so an older number is compared at where it really is rather than at the same phase of its animation.
- **The first candidate is the layout's own throw and pays nothing**, and the rest pay a small penalty for having searched plus a
  graded one for drifting from where the lane puts things. An uncrowded overlay therefore draws exactly what the layout alone
  would have drawn; the search is only paid for when it buys space.

Probing is systematic rather than random, which took learning twice: twelve random throws at a crowded band found far less of the
room that was there than walking the band does. The grid is three columns by six rows — six rows because that is about how many
rows of text a band's depth allows, three columns because that is about how many numbers can sit side by side in one column — and
every launch point is nudged off its lattice position afterwards so nothing marches in lockstep.

Nothing is refused: if every launch point crowds, the least crowded is used anyway. Capacity belongs to the lane cap and the life
shortener, and losing numbers belongs to nobody.

Text that never moves does not come through here either: a row that rests does it in bands, after travelling once, and a row that holds a
column is queued by `FctConveyor` instead of placed at all. The evidence for that split is old: the deleted left/right preset's "floating"
checkbox was free-float placement with nothing moving, measured at **85% of pairs overlapping**.

**Depth is free and sideways is not, so the two axes are searched differently, and that is a learned lesson too.** The first
version widened both by the same factor — five times the layout's jitter — which is how a hit came to start at the far left border
of the overlay and sway inland on the way up. The damage column sat at 0.42 of the width then, so a ±0.45 throw went off the left edge,
the clamp pinned it to the wall, and the search scored that wall as an empty gap: measured afterwards, numbers averaged 22% of the
overlay away from their own column and 7% launched flush against an edge. Healing had the same trap waiting on the right. So depth
is searched across the whole band, while sideways reach is measured **in widths of the number's own text** — the question "could a
neighbour sit beside this one?" is about how wide the text is, not how wide the window is — and capped at 0.22 of the overlay
width. Numbers now average 10–15% from their column, none start at an edge, and overlap is still cut by a factor of four to six.

Final figures for the same measurement, on 980×640: three fountains 58% → **9.2%** of pairs colliding, six held numbers 71% →
**16.5%**, eight spray numbers 40% → **~10%**, at about 1.5 µs per placement with a full lane live. Held numbers are the worst case
because nothing about them moves to help: six in one band genuinely do not fit without touching, and that remainder is what the cap
and the life shortener are for, not what placement can solve.

### The plume rises out of the middle, and the anchor pays for it

Players looked at bands and said *the fountain is a little left of centre*, which was correct twice over. `FctLayout.LaneSlot` parked its columns at
0.42 / 0.50 / 0.52 / 0.63 of the width — damage (both directions), words, crits, heals — a table left behind by the era when a SIDE of the window meant
WHO. But bands says who by band: out rises, in sinks. Sideways is therefore not used for the person at all, only for the thing — damage, heal, word — and
what that table actually did was stand the biggest stream off-centre and put its own crits a tenth of the width to the right of it, so a raid-built toon,
which crits most of what it deals, drew two plumes instead of one. Half the complaint was left over after that: `X0` is the **right-align rail** everywhere
in this engine (the odometer hangs a value's right edge on it), so a number sitting "on the middle line" still draws half a text to the left of it — 4.9 % of
a 1600 px panel with the slot dead centre, measured.

So bands centres the **box** and lets the rail pay (`FctLayout.ColumnCentre`, asked in one place so the layout's own throw and `FctPlacement`'s search cannot
disagree about where a column's middle is), and split keeps the rail welded to the spine — there the rail IS what lines a column up, and numbers hanging off
one spine in one file is precisely why a column reads as a single stream. Median drawn box centre, bands with the shipped spray, mixed stream at raid-ish
cadence:

| what | before, W=1600 | now | before, W=980 | now |
|---|---|---|---|---|
| ordinary damage out | −277 px | **+10 px** | −176 px | **−22 px** |
| damage taken | −161 px | **−4 px** | −101 px | +5 px |
| crits | +111 px | **0 px** | −5 px | 0 px |
| words (miss, dodge) | −2 px | +42 px | −21 px | +35 px |
| healing | +211 px | +187 px | +74 px | +124 px |

Everything the eye follows now comes out of the middle within a few pixels, and the residual scatter in the last column is `FctPlacement` walking off to find
room — the ±9 % launch jitter and the lateral reach, both measured from the slot, so re-centring the slot carried them without re-tuning a constant. Healing
deliberately keeps its own column to the right of the plume: with who handled by band and colour by class, "these are the heals" is the one sentence sideways
still has to carry on its own, and a glance must get it before reading does. `FctLayoutTest.ThePlumeRisesFromTheMiddleAndHealsKeepTheirColumn` pins both
halves — the four slots on the middle line and the drawn box actually landing there, with heals still reading as their own stream.

### The rail's tempo: one rate per column, stamped where the flight becomes final

MSBT's display areas are rows, not positions: `MIN_VERTICAL_SPACING` is 8 px between them (its columns keep `MIN_HORIZONTAL_SPACING` = 10),
and that is what people mean when they say the genre "feels tidy" where free-float FCT sprays. Most of the discipline costs no computation:
every row in a lane shares one scroll rate, so two rows born half a second apart stay half a second of travel apart, on the same curve, for
their whole lives — the scroll *is* the queue. Split buys that spacing at entry (`FctConveyor`, next section) instead of scoring it.

The part that stayed behind from the scoring days is the **tempo law**, and it survived because it is what makes the queue provable. A rail
row's life is pure travel — distance over the shared px-per-second — with no rest phase: *travel, then stop at the end of the travel, then fade
in place* is harmless where numbers land wherever, and fatal for rows moving in single file, because every row ends at the same terminus and a
column becomes a queue for a parking space. Motion spans the life, so nothing parks, and the fade arrives while the row is still moving.

What a player's correction bought: sharing one *duration* per region is not sharing a speed. Rows of different font sizes travel only a few
percent apart, so the theory looked sound, and it was measurably wrong on screen — 195.6 px/s for damage against 201.8 for words, side by side
in neighbouring columns. A lane therefore shares a RATE, not a duration: each row's time is its own travel over the lane's px-per-second
(`FctIngest.FinalizeRailTempo`), which keeps every chain property (a common rate is exactly what makes birth-time gaps permanent) and makes
"they all move at the same speed" literally true. Because a row's real travel can be decided late — the pinned edge, a resize — the tempo is
stamped as an estimate before anything is priced and restamped when the flight becomes final, including on resize: stretching the window buys a
row more time, not more speed. The conveyor does the same arithmetic once per lane instead of once per row, which is precisely the difference
between a convoy and a crowd.

### Congestion: press the lane, wait at the mouth, then refuse and count

The complaint arrived as typography, not arithmetic: at raid density a number lands on the *word* under the number above it. The reserve was
never missing — `TextHeight` has always charged a source line under every labelled row — the problem was throughput: past a column's capacity
the scorer knowingly accepts the least-bad overlap, and "least-bad" once meant 17 px of value box through somebody's label band. A reserve
cannot conjure room; only speed or subtraction can. (The genre always knew: MSBT's areas *drop* text when they overflow — its throttle sat
upstream, but the loss was always there.)

Split answers it structurally now, and every rung is counted (`FctIngest.Accept`'s conveyor branch, `FctConveyor.Ramp`):

1. **The lane presses its own clock.** A lane holds a certain number of rows at the dialled pace — its travel over the pitch it pays at entry —
   and a lane carrying more than that runs faster, floored at `FctConveyor.PressFloor` (0.38) so even a jam stays readable and clears inside
   about a second instead of typing on after the fight stopped. Press belongs to the lane; it is copied onto each row for
   observability (`FctHitState.RailPress`) and one row's acceleration is never another row's, because an odometer that changes speed mid-flight
   is lying about where it is going.

   Two numbers here come from one measured session (`fct.drop: 572` spread over eleven minutes of fighting, `fct.dropLive = fct.drop`, queue shut at
   `BacklogCap`) and neither was arrived at by argument. First, **a full queue is a clipped signal, not a mild one** (`FctConveyor.Ramp`): the ratio
   reads what the lane *has*, so a column of about twenty rows holding twelve more asked for 0.63 and sat there with half its accelerator in hand while
   the door stayed shut and a number was refused every second — which is why `Waiting >= BacklogCap` now drives straight to the floor instead of through
   the ratio (`ASaturatedColumnRunsAtTheFloorOfItsTravel` fails at press 0.55 without that rule, and passes at 0.38 with it). Second, **the floor came
   down from 0.45**, because a lane should not be spending an accelerator it measured as unusable while numbers are being lost; both changes are invisible
   in a calm fight (press leaves 1.0 only under load) and both are one constant to walk back if a jammed column now reads too quick — `fct.drop` is the
   number that says whether walking it back costs anything.
2. **Room is bought before the row is visible.** An arrival whose slot has not reached the mouth waits off-column — invisible, still folding
   duplicates into whatever it will merge with — and the lane carries its turn to the front. Congestion in a column therefore shows up as delay
   rather than as overlap.
3. **A full backlog refuses, and says so.** Past `FctConveyor.BacklogCap` the lane cannot promise spacing at its fastest, so the arrival is turned
   away and counted in `DroppedCount`. The loss is the design, so the loss is visible; identical ticks already folded, categories can be spread
   over four columns, and the speed dial is a rate rather than a lifetime.

What went with the deleted placement ladder: a birth-time accelerator stamped per row from how many neighbours happened to be on screen (once
`RailPress` meant *this row's* crowd), one re-thread of a row that still covered a neighbour, and a depth-based "label fence" that charged a
plunge through a word band more heavily than a side kiss between columns (`LabelBitten`, deleted with the fence). The fence went because a queue cannot plunge
into itself, not because it failed: measured on a 200-event raid burst it took label bites 7 → 0 and value smears 193 → 1, paid for in counted
drops — the number that justified making eviction visible in the first place. Placement still prices a deep overlap above a shallow one
(the worst-overlap term in `FctPlacement.Cost`), which is what the scatter styles have to work with.

### Split's rails are a conveyor: one clock per column, spacing bought at entry

Split is not choreography, it is a ledger: whoever selects it wants to read the column top to bottom and miss nothing. That promise
covers **both** rails the mode offers — the straight line, and the arc that ships there by default. The shape dial chooses the path up
the column (a straight climb, or MSBT's bow that leaves the column at its widest point and returns); it does not choose whether the traffic
is ordered, because a lane you are reading has to keep its spacing. Neither shape can therefore keep the promise by placing rows and giving each its own flight time, which is what the rail
used to do. A row born into a crowd was given a shorter life than the row in front of it (the per-row accelerator the old placement stamped
`RailPress` at birth), so it travelled faster, overtook the row ahead, and the two were drawn through each other for the rest of
the trip — the same pixels at two speeds, which is why the report reads "some numbers move faster than others" rather than
"overlapping". Repositioning did not help either: `FctPlacement` always answers with the least-bad slot, and a least-bad overlap
survives the whole flight because nothing re-scores it afterwards.

The genre solved this structurally instead of arithmetically. NAG's scroll areas never position a number at all —
`.fct-content { display:flex; flex-direction:column }` (renderer.js) makes spacing exact by layout and the stack moves as one unit
when a line arrives or leaves. MSBT queues rows into an area at one scroll rate with `MIN_VERTICAL_SPACING` between them. Neither
tool overlaps two numbers to make room, neither speeds up one row on its own, and both let overflow leave the area rather than
pile up. `FctConveyor` keeps that discipline and makes the loss honest — counted in `DroppedCount`, not clipped off a window edge.

Three rules, and they are the whole design:

1. **One clock per lane.** A lane owns a *phase* in pixels; every row on it sits at (phase − its own birth phase). The distance
   between two rows is therefore whatever it was at entry, forever, nothing can overtake anything, and "the lane sped up" is one
   number changing that moves everybody in the same frame. Position comes from the lane rather than from `ageMs`, which is also
   what lets a congested lane finish a row in half the nominal time without that row blinking out early in mid-column.
2. **Spacing is bought at entry, once.** A new row pays for its slot behind the last one enrolled: the **taller** of the two, rounded **up** to
   whole pixels so a scrolling column cannot shimmer by a third of a pixel between frames, which reads as a fault even when every value moves at
   exactly the right rate. Height is what `FctLayout.TextHeight` answers — a crit's own font, and a source line when the labels are drawn below (see
   *what a row is* just below) — the same lesson the cell grid learned before it went. There is no line gap on top of that any more. `LaneGapPx` used
   to add 10 px above the reserve (MSBT's own is 8) and the two together put roughly a fifth of the column in empty air, which is what made four ledger
   columns scroll while they still had room for the fight: the reserve IS the gap now. It is 1.2 em of leading around glyphs that need about 1.05
   (`TextHeightFactor`, shortened from the borrowed web value of 1.35), so two neighbours are separated by the slack inside their own boxes, which is real
   and invisible; the halos that meet between them are translucent blooms brightening a seam rather than numbers hiding numbers. A class that swells on arrival
   carries that size in its font, so the height it pays for is the height it draws (`FctLayout.TextHeight`). For the
   uniform column a fight is mostly made of, "taller of the two" *is* one row height: exact line spacing, nothing thrown away.
3. **Congestion scales the lane, never the row.** Load (on-column + waiting) against what the column can hold gives the lane's
   accelerator — Little's law again, spent once per column instead of once per row, floored at `FctConveyor.PressFloor` so even a
   full emergency still reads as text and clears inside about a second and a half. It ramps: fast to speed up (the traffic is
   already here), five times slower to relax (the load signal is bursty, and a clock that snaps back replays the same burst as a
   visible stutter), and it is slew-limited as well (`MaxPressStep`, three percent of pace per frame): a lane that lurches looks broken even
   when its traffic is perfectly spaced, and since the queue behind the mouth holds arrivals anyway, a smooth pickup costs nothing but a
   slightly later one.

**What a row is tall enough to need.** Where the label sits decides what a row *is*, vertically. With `(source)` drawn under the amount it is
a second line and must be paid for, or the next value walks into the word under the one above it; drawn beside the amount it shares that
number's baseline, so the row is exactly as tall as its value and nothing else (`FctLayout.LabelSide`, stamped by the canvas when settings
load). That is why choosing left or right in the label dropdown pulls every column on screen visibly tighter — and why rows with a source and
rows without one then share one line pitch instead of alternating wide and narrow down the lane. Uniformity, here, is not cosmetics: it is what
turns "about evenly spaced" into something a reader can scan.

**A column's spine and its bend are decided for the widest row it can ever draw, not for the row that arrived.** `FctLayout.RailReserve` prices a rail
against a crit-class number at its dial's size — six of the widest digit with a thousands comma, plus room for a special event's mark — and Spawn places the
spine so that box fits *and* leaves `ArcBowFrac` of clear room on the side the column bows to; AssignTravel caps the bow by that same reserve rather than by
the arriving number's own width. Both halves matter, and the second was a bug lived with for a long time: charged per arriving row, a wide crit spends the whole
column on its own glyphs, finds no room left for an arc, and goes up the screen in a dead straight line while every row around it curves — the loudest number in
the column being the only one without a shape. Deciding it once per column keeps the odometer honest too (a crit no longer slides inward to suit itself, which
used to put fifteen pixels between two neighbours' right edges) and gives every row on a lane ONE path, which is what the spacing model has always assumed:
identical bows cancel when the distance to the next row is measured.

Every row on a lane enters at **one edge** and travels **one distance**, whatever class of number it is: a per-class start — a
proc's inset from the spawn edge, a slack share off the flight, the depth jitter — spends part of a neighbour's gap before the
first frame is drawn, and no later arithmetic buys it back. So `FctLayout` takes no inset and rails take no travel slack for a row
the queue asked for (`Pin`), which is also why capacity per column is a single number rather than an average.

Arrivals that cannot be shown yet **wait behind the mouth** at a negative distance: invisible (`FctMotion` draws nothing before
the edge), still folding duplicates while they wait — so a DoT barrage costs one row already carrying its count instead of six
queued ones, and folding happens *before* the queue is consulted, which is what stops "never miss anything" and "stay compact"
from fighting. Past `BacklogCap` (12 waiting) the lane is genuinely full at its fastest and the next arrival is refused, counted;
one column can therefore hold about 24 numbers (on it + queued) before anything is lost.

**Legibility on this rail is measured in distance, not duration.** A row is at full strength for all but the last
`ConveyorFadeOutFrac` of its own flight and fades over that slice alone — a fixed number of rail pixels whatever the lane's pace —
with the first 24 px eased as an arrival rather than a pop. The crit's collapse shares that window, so a crit that queued for a
moment cannot arrive already shrunk and dying in the middle of the column. Fading by age was the old cost: on a lane whose flight
length congestion dictates, it made the tail of every number vanish while the number was still sitting in the middle of the
screen, in the one mode chosen in order to read.

**One train per column.** The queue is keyed by column *and* travel sign, so two categories pointed at the same column with the
same direction are one queue on purpose (that is what "heals in my damage's column" means to a rail), while opposite directions
through one queue are refused twice over: the settings panel cannot offer them (`FctConfigState.LaneAvailable` greys the choice,
`ResolveLaneConflicts` re-homes a hand-written `settings.ini`, priority my damage → taken → heals), and the stage itself
normalises direction for anything sharing a lane, so even a dial turned mid-fight cannot lay two trains over one set of pixels.
Words ride their own side's queue — an attack that failed is a row of that column, not a separate queue to dodge around — drawn
one point smaller (`FctStyle.WordSizeStepPt`).

The arithmetic of a lane (640 px column, default dial ≈ 4.4 ms per pixel, ~50 px pitch): one row every ~219 ms at the configured
tempo, so **≈ 4.5 rows/s per column**, rising to ≈ 10 rows/s at the floor; four columns is 18–40/s before anything is turned away.
Steady solo traffic on one column — a swing every 1.2–1.9 s plus DoT ticks and words, ≈ 4 events/s — sits at or under capacity: no
loss, backlog peaking at two rows, press dipping to ~0.75 for a moment and relaxing back. What *does* drop is a spike arriving
faster than the column can physically separate at its fastest — more than about 24 rows landing in the same frames — which is the
case MSBT answers by letting text leave the area, and this overlay answers with a counted number instead of silence. If that count
moves: identical ticks already fold themselves, categories can be spread over the four columns, and the speed dial scales the whole
lane (it is a rate, not a lifetime).

Fountain, spray and freeze keep the scatter and its congestion ladder above — those styles are an event to watch,
not a column to read, and `UseConveyor` deliberately restricts the queue to split's rails — arc and line alike, since the dial there
chooses a path and not a discipline (bands degrade rails to freeze, so there is nothing to catch there). Read `FctConveyorTest` for the invariants as assertions: one rate per frame per lane, gaps that never
change, no pair on a column ever closer than the taller of the two, folded-while-queued duplicates, ordered drain, counted refusal.

### The odometer: values hang their right edge on the rail

A column of centre-anchored numbers is a column whose ones digits jog: 950 centres its three glyphs where 12,040
centres six, and spam reads as ragged confetti. Mik ships the answer as an option — per-area text alignment, and
right-justify is what damage columns actually use — and this overlay ships it the same way everyone who uses MSBT
ends up configuring it: **on, permanently, with no knob**. `FctMotion.ArcedX` interprets a travelling row's spine
(`X0 + lateral`) as the value's **right edge**: every row in a lane keeps the same right edge at rest and under every
fold's width change, and centres sit wherever their own widths put them — new digits extend the number, they do not
shift it. The crit blowout's scale animation is the one exception, anchored at the **centre** rather than the rail:
pinning the edge through an animated scale made a dying crit walk sideways toward its rail as it collapsed (up-and-
RIGHT on a straight line, while every ordinary row rose straight), and the genre anchors pops at the text's centre for
this reason. Since the blowout envelope never exceeds 1.0, a centred collapse can only tuck the drawn box further
inside the rail — the flush edge survives every frame of it.


The rail itself is placed for **the widest amount the lane can roll** (`FctLayout.RailReserve`: crit-class digits at
the crit's own size, plus a mark's room) rather than for the row being spawned. A spine derived from each row's own
width is not one spine: `9` would sit where `18.3m` cannot, and the column would move under every number that landed.
The same reasoning excludes a row's *words* from the arithmetic — see the next section — and the reservation covers
both rail shapes, not just the bowing one, because alignment does not care which path a number takes up its column.
Where a region cannot offer a spine and a bend at once, containment wins and every row on that column bows equally
less, which is a flatter curve rather than a broken one.

The same discipline reads vertically, and it is stated here because the two axes differ on purpose: a row is anchored by its OWN
height, so every row of a lane enters at one *edge* while their Y0 values legitimately differ. A crit starts lower and a short word
starts higher, and both rest on the same line — measured at 632.00 for crit, ordinary hit, proc and word alike, with each row's
travel plus its height equal to that same number. Height is meaning, so it is not flattened to make a field comparison convenient;
what belongs to the lane is the mouth and the run. The tests assert edges rather than Y0, which is also a note on where an assertion
is written: one aimed at the wrong field will report a fault in a layout that is correct, and this one did.

The alignment is geometry, not a draw trick, because placement collision is geometry: `FctPlacement.Block` scores the
same right-anchored box (`Block` scales its half-width around `ArcedX`'s centre, so scoring and drawing agree to the bit),
the spawn clamp reserves its margin on the left, and the arc's bow budget measures the full hang (above). Two
small costs were accepted with measurement: an artificially-over-capacity half grazes to ~30% of a block instead of
25% (the label tests pin the new ceiling — right-alignment is worth half a column of squeeze), and deep-entry line
rows price their sideways valve off full widths too. Words align like numbers ("miss", "resist"), so a lane reads as
one flush ledger, labels included.

### The row is wider than its number: labels measured, names trimmed to fit

Every horizontal clamp in the overlay charged for the *digits*, which was true of the arrangement that existed when they were written — the
label underneath, inside the reserve TextHeight already pays for. Making labels sit beside their amounts broke that quietly: DrawHit paints a
second string at an offset out from the number, and nothing had ever measured it, so a "(Complete Heal)" reached past the column boundary into
its neighbour. The genre cannot make that mistake. NAG's inline label is another span in the same flex line as its damage number
(renderer.js), so the row grows to hold it and the layout moves everything else out of the way; MSBT stamps one text object per line, so a
line is exactly as wide as what it stamped. Both reserve what they draw. Ours drew into room that had never been asked for.

`FctLayout.BlockFromRail` is where the drawn block is defined, once: the value's box at the size it draws, the special-event glyph hanging outside its
left edge, and the source label wherever the label side put it. Everything that tests a row against a wall charges these numbers — `Spawn`'s clamp,
`ArcedX`'s per-frame clamp (the resize safety net, so it is the one that really matters), and the arc's bow budget. Reading the same block about the
value's *centre* rather than its rail is a question only the tests still ask: row-against-row spacing went with the deleted stream, whose placement scorer
was the last production caller of it, and the conveyor spaces a column by time rather than by measured width. The reach is deliberately asymmetric because
the odometer above makes it so: with the rail being the value's *right* edge, a label on the left deepens a reach that side already had, while a label on the
right opens one the row did not have at all. Each label side is translated on its own terms, and the case that matters most is the one
that was got wrong first: a second line is centred under its **amount**, whose centre sits half a width left of the rail, so it overhangs the
value's own edges by half the difference — not the rail's. Charging `words/2` against the rail instead asked a long spell name for 86 px of
clearance and `"(Crush)"` for none, which is how a crit came to be drawn 46 px away from the hits under it, out of line with the column it was
supposed to flush every number in. A centred word line is also charged against the column boundary but *not* against the spacing between rows,
whose vertical reserve already covers it; charging it twice would make rows braid into neighbouring columns to dodge words that were never in
their way.

**Words never place the spine.** The rail a number hangs on comes from the widest amount the lane can roll (above), and a label — which belongs
to its own row, while two rows in one column routinely carry different ones — gets whatever the spine leaves over. `FitSource` asks for that
budget per side of the rail rather than as a total for the region, because that is where each label side draws: an inline label cannot borrow from
the hand it is not drawn in, while a centred line may reach past its amount on either side. The measured consequence is worth choosing a setting
on, at default fonts in a 1280 px overlay: about **twenty-three characters** of a name below the number against about **ten** beside it, since
beside shares one line with the amount while below spends both halves of the column. Height runs the other way — 41 px a row beside, 66 px below —
so inline columns hold roughly sixty percent more numbers at once. Neither arrangement is better; they trade the same pixels.

A name that does not fit its column is shortened, and only then (`FctLayout.FitSource`). Two bounds do the work: `MaxSourceChars` — forty, which is as long
as a name gets to be and nothing more (raised from thirty once the first-run size stopped being a guess at a small screen: a ceiling sitting below what the
layout can pay for is one more way to cut a name that was in nobody's way) — and whatever width survives after the amount itself. Nothing is trimmed
that fits, so the same name survives whole at a smaller font or in a wider window; nothing is cut below three letters, where an ellipsis would
cost more than the name it replaces and "(...)" names nobody. Two more small things came out of the same complaint: a cut that lands on a space or a comma
walks back to the last real character, because "(Champion of …)" reads as a typo rather than as an ending being withheld; and the space between an amount and
its bracket came down from half a source font to a third (`LabelGapFrac`), prose spacing being wasteful between a number and a parenthetical that opens with
a bracket to say where it starts. Two measurers take the same decision at different moments — an estimate at spawn,
before any font exists, and the real glyphs in `RebuildGlyphs`, whose answer is what the player reads — and because the *full* source stays on
the hit, the decision is re-taken whenever the room changes: a resize marks the text dirty (widen the window and a trimmed name comes back), and
so does switching label side, since left, right and below leave different amounts of column behind.

Seeding that estimate surfaced an ordering bug worth naming: `FctIngest` stamped `ValueWidth` *after* calling `Spawn`, so every number in the
history of this overlay was placed against a zero-width block and only the per-frame draw clamp ever caught up — a candidate scored as occupying
a tenth of the room it would actually take. The estimate now precedes placement, which is also the point at which the label's room exists.
### Two states: numbers only, or configuring

Locked is not a setting — it is what the overlay *is*. It opens locked, it is played with locked, and it has no header: while locked
the controls row is hidden, the panel background and border are transparent, the resize bands are gone, and clicks pass through to
EverQuest. What is on the screen is damage numbers and nothing else.

That last part was a defect hiding inside a feature. The header (`FCT`, motion combo, a "lock (click-through)" checkbox, a
sentence-long hint, stats) and a dark rounded panel used to paint in both states, over the middle of the game view — furniture nobody
asked to look at while fighting, on a window whose whole job is to be out of the way. Making it numbers-only was not a cosmetics pass.
The row that remains got read for clutter at the same time: the hint sentence and the `motion` label in front of a combo that can only
be a motion combo are gone, and so is the checkbox — see below.

**Configuration is entered deliberately from the app menu** (View → Floating Combat Text → Setup). Not from a button on the overlay: this
window lives where the player is looking, and the Damage Meter gets away with an on-window toolbar because you park that in a corner.
The hidden header keeps its height rather than collapsing, so entering configure mode cannot move a single number — the moment you are
positioning them is precisely when the layout must not shift.

**Save is what writes**, and **Cancel is the way out without writing**. Both sit at the right end of the configure row, stacked because there is no
width to spare. Leaving without saving is a normal way to finish a look around — you came to see what the dials do, not to change them — and for a while
the only way to do that was a sentence printed on the panel explaining the Esc key, which described the ordinary exit as the absence of an action. A
button that says "Cancel" beside one that says "Save" removes the sentence and the reading — and then **Esc left too**: if the buttons are labeled,
a key with no label on screen should not be a third exit, silent about whether it saved or discarded whatever was staged. Setup clicked again from the
menu still backs out, and every one of those paths puts back whatever was
saved before, because abandoning a configuration session is not the same gesture as approving one. The motion combo previews live (the next numbers use
the new style) and writes nothing, so trying a style costs a click and un-trying it costs nothing.

That replaces a "lock (click-through)" checkbox, which was never about locking. It was the only way out of configure mode wearing a
side effect as its label, so the player who clicked it to finish got their mouse taken away and no way to notice they had also not
saved anything. A button that says what happens is worth more than a toggle that guesses. Placement is the one thing saved without
being asked: geometry is written when a drag or resize is released, because where you left the window is never ambiguous.

Lock state is deliberately **not persisted**. A saved "unlocked" is a state that outlives the session it was meant for: next launch,
the overlay is an invisible rectangle eating clicks over the game, and the player's only diagnosis is that the UI feels haunted. Same
reasoning retired `FctOverlayLocked` outright rather than defaulting it to true — inert in existing settings.ini files, like every
other retired key here.

### Two dials and a short loop: what configuring is for

A feature whose range a player cannot adjust has whatever opinion the implementer happened to hold, shipped as theirs. So the configure row carries two
dials — **size** and **speed** — the size ones −50 % to +110 % and speed ±50 %, stepped at 5 %, so a setting is a place you can park rather than a value you have to hit by eye. Each dial
is three lines of its own: what it is, the track with a bold `-` and `+` either end, and where it landed underneath — **with its own percent sign**,
because a bare 50 next to a slider reads like a count of something. That shape is about room. The row started as one line with each number read out
beside its track, which worked until it didn't: this panel is going to acquire more settings, and a layout that grows sideways runs out of window at some
width somebody chose — so the numbers moved below, where they cost nothing, and another dial can be added beside them. The marks carry the size rather
than the labels because they are what you consult while a thumb is moving. Nothing gets a row to itself: the direction legend (`↑ yours ↓ on you`) sits
on that same bottom line rather than underneath everything, which is why the header is about 60 px and not three bands of it. Everything wraps: a narrow
overlay drops the speed dial under the size dial, never a button off the edge.

**The second dial is speed, not time — it used to be called time, and it ran backwards.** A control labelled "time" whose right-hand end makes numbers
appear and clear sooner is a control whose label fights the gesture, and that was reported by a player rather than noticed in review. So it says speed,
bigger and faster at the right, and stores `FctOverlaySpeed`. What it is *measured in* is percent of how long a number stays up: **+50 % takes half as
long on screen, −50 % lasts half again as long**, and the middle is nothing. Naming it speed and measuring it in time are two different things and both
were needed — the name is for the gesture, the unit is for the eye, and the inversion lives in one function (`FctScale.TimeFromPercent`) so that nothing
downstream holds a reciprocal in its head. `FctScale.Time` stays exactly what `FctIngest` has always multiplied: how long a number lives, travels and
fades.

**Both dials centre on their default now, and getting there moved the middle of this one.** It used to run −30 % to +90 % of a tempo — asymmetric because
playing with the feature said the usable band sat faster than the measured baseline (twice as long on screen is unplayable; half again as fast was not
enough) — and an asymmetric dial cannot be parked by feel. So the centre became the midpoint of that band's two *results*: numbers lived 1.24× as long at
one end and 0.51× at the other, and halfway between those is `TimeDefault = 0.877` of the measured time — about 1.14× the tempo, which is all but the pace
the dial had already settled on shipping at. ±50 % of that gives 0.44× at the fast end (past where the old dial stopped) and 1.32× at the slow (nowhere
near the twice-as-long nobody can fight under). The shape of the usable band survived; it just moved out of two asymmetric ends and into one number.
Re-scaling choreography, layout budgets and the adaptive controller to make that the new 1.0 would have been the same opinion with forty constants in it,
plus re-measuring everything measured at 1.0.

Size runs −50 % to +110 %, and it stopped being symmetric because its ends are set by what they are for: the floor draws as small as anybody actually plays
(what the old ±75 dial's −60 % mark drew) and the ceiling reaches double the tier table — the look the old dial could only name as +100 %. The middle moved
too: text normal ships at 0.9 of the tier table, because that is where shipped numbers looked right in game; everything the layout measures (lane columns as
fractions of width, the vertical reserve `FctLayout.TextHeight`, the adaptive lifetime) still scales from the same dial, so no constant had to move with it.
Past about one-and-a-half times the tier table that stops describing the feature comfortably — damage and healing columns begin to occupy each other at
ordinary window sizes — so the top of each dial is for wide overlays rather than a mistake. Settings store the multiplier itself, so values saved under any
earlier window stay legal: re-pegging the dial needed no migration, only a re-centred readout.

**An asymmetric *percent* band still gets a symmetric *track*: every dash sits at the exact middle of its slider.** A −50 %…+110 % track put its nothing a
third of the way along, and that read as a thumb left somewhere by accident whatever the number beside it said — a slider is seen as two halves before it is
read as a value. So the two size dials run their own symmetric axis (`FctScale.DialExtent`, −100…+100) whose halves carry different amounts of percent: half
the track climbs the 50 points down to the floor, half spends the same pixels on the 110 up to double the tier table. Nothing else moved — every percent stays
reachable, still means the same multiplier, still lands on the 5 % grid (as unevenly-spaced snap positions, `FctScale.SizeDialTicks`, since one `TickFrequency`
cannot express two halves) and settings.ini is untouched; only pixels-per-percent differ between the halves, with the roomier side being the way down, where
sizes sit close together and are hardest to tell apart. The speed dial needed none of this: ±50 was already centred, which is why its dash never moved. Text's
dash is also its shipped value; crit's dash means "a crit exactly as big as a plain hit", so the crit dial rests one step right of centre at +10 % — that row's
default and its nothing are different places, and are now at least visibly one step apart rather than a third of a track.

**Two size dials, two classes, one rule — after two couplings that each made a dial lie.** *Text size* sizes every ordinary number; *crit size* sizes the big
class — crits and the marked special attacks — which shares one font because it already shares a lane, a colour and a draw pass. Both speak percent over the
same shipped middle across the same −50 %…+110 % band, differing only in whose numbers they move and where their middles ship (text at 0 %, crits at +10 % —
which since the retune draws exactly where plain numbers stood before, a shipped crit barely a size louder and still obviously one). That is the whole rule, and the history is
why it has no hidden parts. Version one kept a fixed 40 px crit tier above every lane: parked both dials mid and crits stood clearly bigger than normal hits, so
the crit dial's zero described nothing anyone could see. Version two made the dial a *multiplier over the text dial* — honest parity at 0 %, but stacked:
font × dial × the pop's 1.3 hold, and setting the crit slider to its floor still produced numbers thirty percent over the neighbors, because the multiplication was
hiding in choreography no label mentioned. Both failures are the same failure — **a size that more than one control can reach is not a size you can set** — and the
class rule closes it from both ends: each class has exactly one dial, and `FctStyle.ApplyTo` is where that dial meets the font, once, at birth. In particular the
blowout no longer scales anything: it swells *in from below* full size (arrival reads as growth), rests at exactly 1.0, and
collapses out through the tail — emphasis that cannot lie because it cannot multiply. What a 0 % crit keeps is everything that was never size: the halo, the orange,
the top draw pass, the fold immunity; and pulled to its floor it is two-fifths of the tier table, quiet, faintly absurd orange number, which is now an opinion the UI can actually express.

**Sample data has a checkbox, on by default.** The scripted loop is the reason configure mode teaches anything, but there is a second thing people do in
configure mode: position the overlay over a real fight, where example numbers are noise on top of the numbers they are trying to line up. So the examples
can be switched off next to the speed dial. It is a view aid for the session rather than a setting — nothing writes it, and setup opens with them back on,
because the next time somebody opens this panel they almost certainly want to see what a dial does again.

**"Threshold" is a number you type.** The label reads *threshold*, MSBT's own word (`damageThreshold`, off by default like theirs); it
briefly shipped as "hide below", which described the effect but sat in a panel of nouns as a verb phrase and never matched what players
call the thing. The gate stops drawing
*damage numbers* at or below its value. It began as a six-rung combo — off, 250, 500, 1k, 2k, 5k — because a dropdown cannot offer
eighty positions; the panel now carries the trigger grid's numeric spinner instead, and every whole number from zero to just under
ten million is a legitimate opinion, so the ladder is gone and loading no longer snaps: a stored 300 means 300. Heals and the zero-damage words are exempt by design: they are information,
not volume, and hiding the fact that you are being resisted because the number beside it happened to be small is exactly the surprise
this control must not produce; crits are not exempt, because a small crit is still small noise. Nothing this hides goes quietly — the
stats read `… · 37 hidden` beside the drop count when nonzero, one number per reason text does not appear: "dropped" is the
overlay out of room, "hidden" is the player's own filter working. The gate sits at the top of `FctIngest.Accept`, before folding, so a
hidden tick never inflates an `×N` count that nobody saw anyway. It persists as `FctOverlayThreshold`, and values outside zero…9,999,999
come back **clamped**: a spinner can only promise the range it draws, because a filter that works while its control lies about it is
two bugs for the price of one.

Both are applied **where a number is born**, never while it is on screen: size in `FctStyle.ApplyTo`, speed in `FctIngest.AssignLifetime`. That is not
tidiness — it is the reason dragging a dial cannot tug at text already in flight. Motion is a pure function of (hit, age), so a number whose size or
timing changed mid-flight would have to be re-measured, re-clamped and re-placed, which is the bug class layout exists to prevent. Two consequences
worth stating: changing size leaves everything currently flying at the old size until it fades, and speed moves `LifetimeMs`, `MotionMs` **and**
`FadeMs` together, because scaling only the lifetime leaves a number hanging in mid-air at the old animation speed, which reads as a stutter rather
than as a slower overlay. Floors hold underneath — never below 900 ms of life or 200 ms of fade — so the fast end compounded on top of what
`FctLifeController` does under raid load makes numbers quick rather than flickering.

**A short scripted loop plays while configure mode is up**, because hovering a slider in a game overlay would otherwise mean waiting for combat
to produce one of each type on demand:

```csharp
foreach (var hit in _demo.Hits)   // FctDemo: about twenty events over twelve seconds
```

Melee swings, a crit or two, a spell damage-over-time ticking the same amount three times so it folds into `412 ×3`, a proc, healing received
(including a crit heal), a hit landing on you, and the zero-damage words — the things a player has to be able to tell apart, arriving in roughly
the order a fight sends them. Still frames were built first and were wrong: five exhibits pinned to a board cannot show tempo at all, and the row
looked like a diagram of the overlay instead of the overlay.

It runs through a **private `FctIngest` into a private list**, not the overlay's. Same choreography — style, band, travel, folding, placement,
adaptive lifetime — and the same text building, which is why dragging either dial shows up in the numbers as they land. Separate rather than
shared because the real ingest owns the counters: folding a demo number into a live one, or counting a demo drop as lost data, would put the demo
inside the data. `ActiveCount` and every loss counter stay honest, and a real number that arrives while configuring behaves exactly as it always
has — drawn after the demo, so on top of it.

The script's vocabulary is EverQuest's own, and that is a checked claim rather than an impression: melee verbs are the parser's
(`StatsUtil.RegularMeleeTypes`, in the base form `FctManager.DisplaySource` prints — "Bite", not "Bites"), spell names appear in the shipped
`data/spells.txt`, proc names in `data/procs.txt`, and the words are `Labels` constants so they cannot drift from what the parser assigns.
Invented vocabulary — an early draft said "Backhand" — teaches a player to expect text that never appears. `FctDemoTest` asserts every name in the
script against those files.

Scheduling carries one promise: the last cue lands 7.15 s into a 12 s cycle, so every number has launched, travelled, held and faded before the
loop restarts. A cycle that cleared live text at the seam would look like the overlay truncates fades, which reads as a bug in the feature rather than
as a loop. The four quiet seconds at the end are also what makes it legible as a loop instead of as noise.

Changing any control **restarts the loop from its first cue** (`FctSkiaCanvas.RestartDemo`), on release rather than while a thumb is still being
dragged — restarting mid-drag would blank the very thing being watched, and waiting up to twelve seconds for a cycle to come round to the part where
the change is visible is not an effect anybody can see. Only demo numbers are cleared; a real number that lands while configuring behaves as it always
has, because configure mode never touches play.

And like the controls, the demo starts with configure mode and stops with it — unlike static exhibits, an un-stopped loop keeps asking for frames,
which a window you are fighting in should not spend.

The panel is neutral and translucent: `#3A000000` — about 23 % black — with a hairline brighter than its own fill (`#99FFFFFF`) so the frame stays
findable against snow or other bright ground, plus grey-white labels. It was blue steel (`#5C7A99` on `#0D131A`) over an 80 % black wall, then 45 %:
the app's palette has no blue in it, so framed in steel blue the overlay read as somebody else's addon pasted on top, and either opacity hid exactly
what the numbers are being positioned against. Configure mode is spent looking *through* this panel at the game, which is the reference for where a
column of numbers should sit; the fill exists only to lift the labels off a bright background, so it is now barely there. While locked nothing at all is
drawn behind the numbers. The numbers keep their colours; those are the vocabulary (§Colour answers "what", never "who") and only the furniture around
them changed.

Type on the configure row comes from the app's own font setting (`ThemeConfig.CurrentFontSize`, arriving in markup through `EQContentSize`) rather
than from a size this window chose for itself, which was 12 px throughout — small print sitting over a game whose interface the player had already set
to 13 pt or larger. Labels sit two steps above the base and the `-`/`+` marks seven, re-applied on each entry into configure mode so changing the font
size in Settings needs no restart. The numbers themselves are deliberately *not* sized from the theme: they have their own scale (`FctStyle`), because
combat text has to stay readable at a glance across a whole screen of HUD, and 13 pt of damage is illegible while 13 pt of menu text is correct.

### Under View, beside the Damage Meter, behaving like it

**The first time the feature is switched on, it opens on its controls.** Every default here is defensible and every one of them is this build's opinion,
and an overlay that appears with numbers already moving keeps a player from learning that size, speed and motion are theirs to set. The demo loop shows
all three within a couple of seconds, so enabling enters configure mode until somebody has pressed Save once — recorded as `FctOverlayConfigured`,
written by Save alone. Cancel deliberately does *not* set it: backing out means "not today", and the offer comes back next time rather than a choice
being forced on somebody who looked and decided. One trap lived exactly here, and deleting the `FctOverlay*` keys to re-test the first run walked
straight into it: WPF refuses to make a window that has **never been shown** anybody's Owner, and unlock builds the settings window immediately —
the offer used to fire before `Show()`, so a setting-less first enable died inside `EnsureSettings`. The ordering rule is now stated where it bites:
show first, then unlock; and ownership of the panel is claimed at show-time via `PresentationSource.FromVisual` (WPF has no "has ever been shown" —
`Hide` is as loadable as `Show` — and `ContentRendered` can lag `Show()`), so no entry path can repeat the crash.

`View → Floating Combat Text` offers **Enable Floating Combat Text** (reading **Disable FCT** once running — the menu is already spelled out above, and an
item that repeats it is a sentence), **Reset Position**, **Setup** — the same three shapes as `View → Damage Meter` two rows
above it, using this app's convention for menu state (a check icon plus an Enable/Disable header, not a checkable item) because an
overlay filed in a different menu with different mechanics is something players have to learn twice. Nothing FCT-related sits under
Tools any more; the render simulation that used to live there is a development tool and starts from the command line (`/fctsim`), so a released
build can still be measured where it misbehaves without shipping menu clutter. It took a backend argument while there were two renderers to compare.

**Reset Position** closes the overlay, forgets `FctOverlayLeft/Top/Width/Height`, and rebuilds it only if it was on screen. Rebuilding
rather than moving is deliberate: the shipped size and the centring live in one place (`RestoreSettings`), and a reset that merely
carried the current window elsewhere would leave behind the stored size that caused the problem. The order matters too — a closing
overlay writes where it was, so the forgetting has to happen after the close.

Which connects to the reason a stored position is now validated at all. A chromeless, click-through window that lands off-screen —
because the monitor it lived on was unplugged — is not an annoyance but a feature that silently stopped existing, with no title bar to
drag it back by. So restored geometry must keep a quarter of its area on the desktop, the same rule `App` applies to the main window,
measured in DIPs against `SystemParameters.VirtualScreen*` rather than `Screen.WorkingArea`, which is device pixels and drifts at any
scaling that is not 100%. A negative `Left` — a display sitting left of primary, which the old check rejected as "unset" — is
legitimate and passes. Resizing is bounded by the desktop for the same reason instead of by the primary monitor's work area.

### Drag an edge to resize, and it lands on a size that works

A transparent, chromeless window gets no resize frame from Windows, and the middle of the overlay belongs to the numbers — so the
grip is a 12 px band along each edge: corners size both axes, edges size one. The bands are collapsed while locked, because a
window whose clicks pass through to EverQuest must not offer anything to click, and the header's top inset was raised past them so
no control sits where a drag for size starts.

Moving it is not a treasure hunt either: while configuring, any press that no control and no resize band took moves the window. The
header used to be the only draggable band, which meant hunting for twelve pixels of chrome while a number floated past where you were
aiming. Controls keep their own clicks because a combo or button handles the press before it bubbles, and locked removes the whole
question by making the window click-through.

A resize is **free** (`FctResize`): the drag lands exactly where the hand stops it, each axis clamped only to the screen and to
420×300, the smallest size probed — every style still places every number inside the window and clear of the protected strip
there, and below it a band is shallower than a line of text. It used to settle drags onto a short list of offered sizes (a "magnet,
not a menu"); that was deleted because it made resizing jump, and the setup panel's position fields can name any size exactly
anyway.

The default started at 980×640, came down to **800×560** as what most people need over a HUD, went back up to 1280×720 when the source labels turned out
to be the thing being paid for, and is now **not a size at all**: a first run asks for 65% × 60% of the desktop's work area, floored at that same
1280×720 - which on a common desktop IS the floor, with wider monitors scaling past it
(`FctOverlayWindow.DefaultSize`). It stopped being a constant for arithmetic rather than taste. The shipped split spread books two categories on one side
and one on the other, so the halves do not share the same room: the solo outgoing-damage column owns its whole half (lanes tile their half, above) — 640 px
at the first-run width, past the label ceiling once its own number is out of the way, where a name's length rather than the window decides what gets cut,
and more on wider monitors — while the two left-hand categories split their half in quarters, comfortable for a below-label seat and modest
for an inline one. Nothing else in the
layout moves a name's length nearly that directly, so the one setting that really decides how
much of the screen belongs to the game also decides how much of a name a player can read — and it should answer to the monitor rather than to a number
chosen when the layout was new. The share stops short of the whole work area on purpose (raid frames and buff lines live on those edges, and an overlay
that begins by covering everything teaches the player to shrink it), Fit() still clamps the request to what the desktop has, and a saved size always wins:
a first-run guess is not something to keep coming back for. What the narrow sizes cost is measured and unchanged: worst-instant pair overlap
size (worst-instant pair overlap): fountain 3-in-flight 7.5% → 6.7%, spray 8-in-flight 8.3% → 7.7% — free of charge — while **six
numbers held at once goes 15.5% → 20.7%**. Held text is the one thing that cannot trade space for motion, so it pays for the narrower
window; everything else does not.

**Numbers already flying move with the window.** Motion is a pure function of `(hit, age)` with no canvas argument: bands, side
bounds, travel and the pitch a lane pays are pixels baked at spawn, so a resize that touches nothing leaves them drawing where the old
window used to be. Probed at 980×640 → 620×400: **10 of 15** held numbers drawn outside the overlay and 4 more sitting in the protected
strip. It healed itself as hits expired, which is a way of being wrong politely, not a fix. So `FctResize.Rescale` maps them: free text by
the ratio of each axis (a thrown number keeps its shape relative to the window it is thrown in), a queued row's tempo re-derived from its
new flight (`FctIngest.FinalizeRailTempo`, which is why stretching a window buys a row more time rather than more speed), and bands plus
column bounds are re-derived from the new size by the same function `Spawn` uses, never scaled approximately. Nowhere
above is off-screen or in the strip any more, at 620×400, 1400×900 and at the floor.

Fonts are deliberately not scaled: how big a number is drawn is a style decision, not a layout one. A smaller window therefore means
less room per number, which is absorbed by the lane cap and the life shortener — the mechanism that already decides how many numbers
fit — rather than by type nobody can read at arm's length.

### Colour answers "what", never "who"

`FctStyle` used to paint the successful-defence lane blue, which put direction on colour — and blue in particular
reads as mana, arcane damage or a friendly nameplate to anyone arriving from another MMO, so it was a wrong sign on
top of a redundant one. Nothing is blue now: yellow dealt / deep-orange crit / red taken / green heals, with crit's
hue pushed *deeper* than dealt damage rather than brighter (at 1.3× scale plus the pop, a light orange and the yellow
it must stand apart from converge). The zero-damage labels are hueless — all of them ONE pale now: the former trio
(a defence that worked, my own whiff, an amber `Invulnerable`/`Absorb` shout) merged into a single `Words` colour, because
at overlay distance the WORD already spells out which defence happened and the one ranking still worth encoding lives in
SIZE — loud words keep their taller tier, they just no longer shout in gold. The source line went neutral grey for the
same reason: it must not compete with a value colour for meaning.

That table is advice now rather than law, because the hues moved in: the settings window's COLOR section holds seven
pickers — **damage out, damage in, heals, crits, special, events** (the zero-damage words: block, miss, dodge, all of
them one hue) **and labels**, named by what the number is rather than by engine vocabulary, and deliberately without
per-row reset arrows: Cancel returns every hue, and the pickers keep their own recent-colour memory. The hues joined the
engine's dial family as `FctPalette`, process globals seeded from these constants and applied by the same staged-settings
path as sizes and speed, so the old promises ride unchanged: a picked colour wears the NEXT spawned number, mid-flight
rows keep theirs, and only Save remembers (`FctOverlayColor*` keys, eight uppercase hex digits; `#` and a dropped alpha
survive hand-editing, nonsense lands on shipped and never on the canvas). What stays unfree is small: the crit halo's
warmth still follows `ColorWarms`, and per-word hues stay unoffered until the seven are ironed out.

Healing values additionally wear a **leading plus** ("+9,409", "+12.5k ×3") — a fifth channel that costs one glyph. It
exists for the readers colour is already failing: red/green separation is what roughly one man in twelve cannot do at a
glance, and for them a heal in either band is otherwise readable only by position. The sign follows the value through a fold and survives crit pooling (`FctHitState.Heal` is captured from the producing
lane, before a heal crit lands on `FctLane.Crit`, where its lane no longer says it was a heal).

Lane capacity is two-layered on purpose: `FctLifeController.Capacity` (5–7) is the *target* the adaptive lifetime
aims at, and `FctIngest`'s hard cap (12 per lane) is the backstop for burst windows. The backstop folds a repeat into a live
number before it drops anything, so overload compresses the display instead of eating damage. The age rules are about
readability now rather than correctness: a fold needs a target that is under 2.5 s old *and* has 40% of its life left, so a
count never lands on something already fading out — which matters more than it sounds, because a number that gains a "×3" as
its opacity drops reads as a glitch. Crits refuse to fold outright: each one is the event, and a crit number standing for
several hits is misleading, so crit overload is the case where `DroppedCount` actually moves. `FctIngestTest` pins both
halves — 40 identical hits into one lane leave every one of them counted on screen with zero drops, and none of the numbers
any bigger than a single hit — and 20 crits into a capped crit lane report 8 counted drops.

**Time on screen belongs to the stream, not to the class.** Crits were exempt from the adaptive lifetime for as long as a crit was a rare event:
`CritLifetimeMs`, a flat 2800 ms while everything around it compressed with load. That is not what EverQuest looks like any more — a raid-built
toon crits most of what it deals, and in an old expansion the crits are so scarce that size and colour alone already single them out — so the
exemption stopped being emphasis and became two overlays running on different clocks. Measured through `FctIngest` at 800 px tall, one damage
stream, shipped speed dial, 35% of the hits critting; every figure is the median time a number was actually on screen, eviction included:

| arrival cadence | ordinary number, before | crit, before | ordinary, now | crit, now |
|---|---|---|---|---|
| every 400 ms (calm) | 3072 ms | 2464 ms | 1760 ms | 2128 ms |
| every 200 ms | 1328 ms | 2464 ms | 1760 ms | 2032 ms |
| every 90 ms (raid) | 912 ms | 2464 ms | 1760 ms | 2032 ms |

Before, the ratio between a crit and its neighbour swung from **0.80 to 2.70** depending on load — at calm tempo ordinary text outlived the crits,
at raid tempo the crits outlived everything by nearly three times. Now it stays in **1.07–1.21** across every cadence and crit rate measured (the 85%-crit
column set lands at 1.07–1.15), one rule for every class: `FctLifeController` decides
by stream, and the badge keeps a 15% margin over its own stream (`FctIngest.CritLifeMargin`) as a congestion allowance — never more time in the calm
case, since the baseline already outranks what the flat crit life was. What says "crit" is size, hue, halo and the top draw pass: the four things a
player can point at on a screenshot. Path was never class-dependent (rails share one scroll rate for every row; fountain and spray buy their time from
the choreography) and now neither is tempo. `FctScale.Time` stays the escape hatch — that is what the dial is for.

Two consequences are load-bearing, both pinned in `FctIngestTest`. Occupancy is counted **by stream** (`LiveStreamCount`, keyed on
`FctHitState.TrafficLane`, captured beside `Incoming` and `Heal` before pooling): counting by presentation class reports an empty damage column in the
middle of a crit streak — every one of those numbers is pooled onto `FctLane.Crit` — and hands the calm-case lifetime to a screen that is already full.
That is why ordinary text at 400 ms cadence came *down* by a third: the controller can finally see the traffic. And `FctLifeController.FloorMs`
went **1000 → 2000**, because a floor that low bought headroom by making numbers unreadable at exactly the moment a raid asks for the most reading;
folding repeats and evicting the least significant row are the tools for crowding. Measured at the heaviest case tried (90 ms cadence, 85% crits), the
longer lives did not cost throughput — drops fell from 90 to 73 while landings rose from 133 to 150, because compressing time and dropping text were
paying for the same thing. The floor is a pre-dial number:
at the shipped `FctScale.Time` of 0.877 it reads as 1754 ms, and at the fastest setting the 900 ms `MinLifetimeMs` backstop takes over.

The blowout's **collapse now runs on the hit's own fade** instead of a flat 700 ms (`CritScaleOutMs`, deleted), so size and opacity retire together: a big
number never holds full size into its dim phase and never shrinks while still bright. The constant got both wrong at once, and specifically — at the 2800 ms
crit life it was written for, whose fade works out at 614 ms, the shrink began **86 ms before the dimming**, so the last tenth of a second collapsed at full
brightness; and because the window was fixed while every other blowout (a marked event, the same curve) lived on the lane's adaptive life, a special born into a
jammed lane spent 700 ms of its 877 shrinking. The conveyor already keyed its collapse to the stretch of rail the fade spans; this is that rule on the clock the
other styles use, which also means the swell-in (`CritScaleInMs`) and the deep 0.06 endpoint are the only crit-specific numbers left in the curve.

Folding used to follow NAG's median idea instead: `FctMedianTracker` kept a rolling window per lane and a direct hit under half
the lane's median was treated as routine noise and poured into whatever live number shared its lane. That is gone, and it went
with summation rather than alongside it — the median answered "when is it acceptable to add this amount to somebody else's
number", and once a fold can only collapse identical values there is nothing left to permit. It also refused the case players
actually ask about: two 2,040s sit *at* the median of a lane that deals 2,040s, so the repeat was shown as a second number
while five identical DoT ticks merged happily. (`periodic` DoT/HoT ticks still always fold; healing never does.) Reintroducing
a threshold means bringing the tracker back — see the counted-ignore-tier idea in `local/fct-implementation.md` §12 F4, which
is where a median-relative cut belongs, because it discards on purpose and has to say so.

### Text sizes, and the reserve they imply

The first pass used web-scale type, and every tier went up by at least two points once it was looked at how it is
actually read: across a game window, in peripheral vision, while moving (`FctStyle`: dealt 34 / taken 32 / healing 28 /
crit 40 / labels 24 / smallest numeric tier 23). The *ratios* were sound from the start, so treat an absolute size as a
presentation decision and a ratio as a design one.

Bigger type also made an existing bug impossible to miss: `FctHitState.Y0` is the **top** of the value text, and layouts
that reserved one em ran descenders and the entire source line off the bottom of the overlay — permanently, because
incoming hits travel downwards and spend their last seconds against the bottom edge. Every vertical bound now reserves
`FctLayout.TextHeight(hit)`: value height (`TextHeightFactor`), plus the source line's height when the hit carries one,
and nothing else: no shipped style draws above its measured font (the crit class carries its size in the font and swells in from below), so
this is a leading factor rather than a peak-scale envelope. It is a factor rather than measured glyph metrics for the same reason
`EstimateTextWidth` exists: bands are computed at spawn, before any backend has built text. `FctLayoutTest`
pins it in both region schemes, with a source line present and at crit scale.

### Procs are subordinate by tempo and row, never by size

A proc is not the number anybody aimed at: item and spell procs fire on their own schedule, several times a pull, arriving
on top of the swing or cast whose timing the player is reading. The first answer was to shrink them — `ProcSizeFrac`, 0.78,
turning 34 into about 27 — and looking at it again that was the wrong instrument. A smaller glyph says *this is less important
information*, which is true of a DoT tick and false of a proc: the proc did real damage with its own name on it. What actually
separates the two streams is **when and where**, not how big — so procs wear their lane's full size, and subordination is left
to the two rules that read as urgency rather than as rank: a shorter tempo, and a different row.

The split of size into two dials is what made the reduction visible for what it was. Once the panel asked for *text size* beside a new
*crit size*, "0.78 × normal" became an answer to a question the player never asked — there is no proc dial, and could not be, so
a hidden per-kind multiplier was a fourth dial in nobody's settings.

Row does the separating that size used to claim: procs also start in a different row of
their band: `FctLayout.ProcInsetFrac` (0.15) moves them further *out* from the protected strip — my procs higher up, procs
landing on me lower down — so the two occupy different rows of the same band and the eye can ignore one while reading the
other. Being a share of band depth it is clamped by the band ends, so a small overlay loses separation before it loses text;
a test stacks every combination on a 420×300 canvas to keep that true.
`FctMotion.ProcTimeFrac` (0.7) then shortens the **whole** tempo rather than only its tail - travel, hold and fade
together - so a proc is gone shortly after the hit that provoked it instead of hanging there while that hit fades away.
A choreographed style scales as a unit for the same reason: shortening its life without its motion would run the arc
in slow motion. Where the style IS a shared rail though, the discount stops: on arc and straight a proc crosses at
exactly its lane's beat, because there the common rate is the whole reading instrument and "same speed as the column"
outweighs "gone sooner" - split mode treats a proc as an ordinary value (measured: one scroll rate for hits, procs and
words alike, whatever the font). Fountain keeps the full tempo discount, which is where quick reads as spam exactly the way
the log's own noise should.

A crit proc still takes no tempo discount: `FctIngest.ApplyProcTempo` tests `Blowout`, because a proc crit is the biggest single
number in the log and running the loudest event on the shortest clock would waste the pop. The old "no shrinking a crit proc"
carve-out went with the size rule itself — there is nothing left to exempt — and the test that pinned it now asserts the plainer
thing: a proc and an ordinary hit of the same lane are drawn at exactly the same size, font and source line alike.

What makes this legitimate rather than a guess is that a proc is a **fact** about the record rather than an interpretation
of it: `DamageLineParser` assigns `Labels.Proc` by looking the spell up in `data/procs.txt` (EQ's own proc list, loaded in
`EQDataStore`), not from how a line happens to read. The flag travels on `FctHitCommand.Proc` because it cannot be
recovered downstream - by the time a canvas sees a hit it has a number, a lane and an ability name, none of which say why
the number exists. Same reason `Periodic` is carried as a flag instead of being guessed from a spell's name.

The simulation streams include a proc stream which reuses the ordinary spell names on purpose: the same word appearing at
two sizes side by side is what makes the treatment visible, rather than comparing a proc against some other ability.

### Smoothing, where animated text actually costs

- **Easing is smootherstep** (`6t⁵ − 15t⁴ + 10t³`) rather than ease-out-quad. Ease-out-quad leaves at full speed, which
  is what made a number look thrown onto the screen; smootherstep has zero velocity *and* zero acceleration at both
  ends, which is what "floated" means. The horizontal arc uses the same curve so the path cannot bend oddly mid-flight.
- **Skia's antialias flag defaults to off in SkiaSharp 3**, and every paint in `FctSkiaCanvas` draws glyphs or the
  blurred crit halo, so it is set explicitly: without it a 34 px number has staircase edges.
- **`SKFont.Hinting = None`, `SKFont.Subpixel = true`.** Hinting reshapes a glyph according to which pixel rows it lands
  on, so text that drifts a pixel per frame silently redraws its own outline every frame — the crawl people describe as
  jitter even though the position maths is continuous. Without subpixel positioning Skia snaps each run to a whole
  pixel, quantising exactly the motion `FctMotion` interpolated. Both settings are right for moving text and wrong for
  a static document, so they are commented rather than obvious: do not tidy them back to defaults.
- Pacing counts whole ticks instead of comparing elapsed time, for the reason given in "Raster at most 60 times a
  second": at exactly 60 Hz the old threshold skipped frames on ordinary jitter and produced an alternating cadence.
- **Both ends of the fade are eased.** A linear ramp changes slope abruptly at `fadeStart` — steady, then suddenly
  dimming, then gone — and a linear start is a pop. The trade is a slightly steeper mid-fade, which reads as the text
  holding up and then dissolving rather than draining away.
- **A folded row changes its face only when something folds in.** The first design counted the total up, which meant
  re-measuring glyphs every frame; letting the widening number widen `ValueWidth` too widened the clamp band `ArcedX` reads,
  so the row drifted sideways as it climbed and looked unstable. Counting was dropped rather than pinned — a folded row shows
  one face value plus a hit count (`FctText.FormatHit`'s `×N`, asked for by `FctIngest` when a duplicate arrives) — so the
  width is measured once per visible change and nothing chases it.
- **The crit halo is shaped like the text it glows behind.** Its sprite is rendered with the same `Hinting = None` as
  the crisp pass; a hinted sprite under unhinted glyphs puts the bloom a fraction off the number.

### The one grammatical rule on the source line

`FctManager.DisplaySource` singularises the attack verb ("Crushes" → "Crush") for melee records **and** for the
zero-damage evade lines, because `DamageLineParser` fills `SubType` from `"X tries to crush Y, but Y dodges!"` even
though `Type` carries the label there. Miss that and the same swing reads "Crushes" beside DODGE and "Crush" under its
number — invisible in a log file, obvious in peripheral vision. Nothing else is ever conjugated: every other `SubType`
is a spell name, and proper nouns keep their letters (`Crown of Stars` is not `Crown of Star`).

### Click-through and persistence

In game the overlay must not eat clicks or take focus, so `FctOverlayWindow` runs layered plus
`WS_EX_TRANSPARENT`/`WS_EX_NOACTIVATE` while locked — the same recipe as the timer, text and toolbar overlays: read
the extended styles with `NativeMethods.GetWindowLongPtr`, set or clear the bits, write them back with
`NativeMethods.SetWindowLong` through `GetWindowLongFields.GwlExstyle`. Transparency itself is declared once in XAML
(`AllowsTransparency`) and WPF does not rewrite those bits afterwards, so they are applied on `SourceInitialized`
and on each lock toggle instead of from a window hook — re-writing them per mouse message costs a syscall pair for
every hover over the overlay and buys nothing observable. Geometry persists through `ConfigUtil`
(`FctOverlayLeft/Top/Width/Height/Enabled`) — written when a drag or resize is released, not by Save, because where you left the window
is never ambiguous — and the overlay reopens at startup, locked, if it was open on exit. Lock state itself is stored nowhere (see
*Two states*). The presentation switches — motion style (`FctOverlayMotion`), the three dials (`FctOverlayTextScale` for normal text,
`FctOverlayCritScale` for crits and marked events, `FctOverlaySpeed`; multipliers
rather than percentages because the file is somewhere a person may look, and speed rather than duration because that is what its dial measures) and
the hide-below threshold (`FctOverlayThreshold`, stored as the plain number it shows) —
persist through `FctOverlaySettings` and are written **only by the Save button**, along with `FctOverlayConfigured`, the one-time mark that somebody has
actually chosen. The old `FctOverlayTimeScale` is still read once, inverted, when the speed key is absent; it is never written again. They are read by
the overlay and by the simulation window so a freeze run and a spray
run differ in nothing but the thing being compared. Being presentation-only, these apply immediately and need neither a lock nor a re-parse —
which is the exception that proves the rule above: data-shaped settings have to be re-read by everything, and these are read once when a number
is built. `FctOverlayMotion` also reads the old
`FctOverlayFountain` boolean when its own key is absent: an upgrade should keep the choreography somebody had already
chosen instead of silently resetting them to freeze, and because writes only ever use the new key the legacy entry fades
out on its own rather than needing a migration.

`MainWindow` mirrors the configure state so menu and window never disagree, entering configure mode calls `Activate()` so dragging is live
immediately, and asking to configure while the overlay is hidden shows it first rather than doing nothing silently. The menu item unticking ends
configure mode the same way Cancel does — settings back the way they were, overlay still open.

`NativeMethods` exposes exactly two style vocabulary sets — `ExtendedWindowStyles` and `GetWindowLongFields` — and
neither has a `WS_EX_APPWINDOW` member nor a plain `GWL_STYLE` accessor. Overlay windows stay toolwindows in both
lock states, which is also what keeps them out of Alt+Tab; anything wanting app-window behavior has to add the
constant deliberately rather than assume it is there.

### What is deliberately not here yet

- Party-wide and other-players' heals: fed only when group configuration exists to scope them.
- Resist percentages as data: the parser reports partial resists as reduced totals (which display correctly) and full immunity as
  `Labels.Invulnerable`; there is no "resisted 75%" record to show, so nothing is invented. Full resist *lines* do now reach the overlay
  as the word `Resist` — `MiscLineParser` already recognised and stored them (`Restless Tijoely resisted your Stormjolt Vortex Effect!`)
  but nothing was listening; a new event carries the stored record to `FctManager`, which routes it exactly like a blocked punch — my
  spell failed goes to the Missed lane, I resisted theirs to Defensive — because a punch that gets blocked and a spell that gets resisted
  are the same piece of information wearing different grammar. The spell name rides along as the source line (the parser's "your pet's X"
  quirk gets "pet's " stripped on the way); party mates' resists stay out, like the rest of the feed.
- Per-character or per-lane configuration and a reduced-motion mode. Palette customization used to sit here too, on the
  reasoning that colour is not load-bearing for reading the overlay — direction comes from region plus travel, and the two
  classes that could be confused (my whiff vs a defence that worked) are separated by size and word, not hue. That
  reasoning is exactly why the seven pickers shipped: if a restage can only cost comfort, it belongs to the player; the
  one thing the colours tab did NOT relax is the separations themselves, which are carried by non-colour channels.
- Real GPU presentation via `D3DImage` (see above).

### The purple family: special attacks wear a glyph, not a bigger number

Assassinate, headshot, slay undead, finishing blow, decapitation, mana burn and life burn are the seven
moments in a fight where the log says *something happened beyond the number*. All of them are already in
`HitRecord`'s modifier mask — except decapitation and the two burns, which arrive as spell names. Rather than the genre default of
a bigger, brighter, differently-coloured number (which fights the odometer: a special that changes
the width of a value is a special that breaks the column), each wears a small glyph hanging **outside**
the value's right edge, and all seven share one colour. A family of seven colours would have turned the
overlay into a rainbow and made the marks impossible to learn; one purple says "special" on its own,
and the silhouette says which.

- `LineModifiersParser.SpecialFor(mask, source)` resolves the mark once, Core-side, so the parser is
  the only place that knows what assassinate looks like in a log line. Decapitation matches on the
  spell name and can only be reached through the parser path — `FctManager`'s direct-damage build (a
  resist with no underlying record) has a mask but no name, so it cannot produce one.
- **The burns ride the name rule too**: *Mana Burn* (wizard) and *Life Burn* (necromancer) are logged as
  ordinary spell damage, so a leading-name match beside Decapitation promotes them — every ranked variant
  ("Mana Burn XX") rides the prefix, containment is not the match, and a name that merely contains the
  spell stays plain. The demo cues wear their ranks for the same reason the rule does: it is what the log
  line carries.
- The glyph is **priced into the geometry, then excluded from alignment**: `FctHitState.IconAllowance`
  is added wherever a row's sideways appetite is measured — the pop peak, the bow cap, the drawn half —
  and is not part of `ArcedX`. That asymmetry is the whole trick: the number's right edge still lands on
  the rail whether or not there is an axe beside it. A mark that nudged the digits sideways would have
  been cheaper to implement and worse to read.
- A mark is a member of the **big class** as far as presentation goes: written at the crit size dial's font **whether or not the log
  also called it a crit**, and riding the blowout's size-independent effects — swell-in, halo, wider spray spread, top draw pass.
  What it keeps from its birth lane is column and direction: an assassinate still scrolls in the damage-out column — big, on top,
  purple.
- The glyph itself is **punctuation, deliberately**: 48 % of the digit height with a two-pixel gap, because at the
  first try it stood nearly as tall as the number with a six-pixel gutter and read as a caption beside the sentence
  rather than an accent on it. Both are single constants in `FctStyle`; the geometry charges for them either way.
- The arrow glyph is a **weapon, not a direction sign** — which took three render sessions to earn. The first headshot
  was a triangle on a stick flying to the corner and read as UI furniture; players say *bow and arrow*, so a whole longbow
  stood behind the shot next, and at the 16–18 px marks actually ship at, bow, string and arrow merged into one blob.
  The diagonal quiver-flight icon collapsed into a checkmark, and so did the bow's second cameo: stood VERTICAL beside
  a standing arrow it failed from a different angle for the same arithmetic — two objects side by side each get half a
  silhouette, and half of 17 px is outlines eating the gap (the classical split-bow-around-the-shaft icon drew
  beautifully at 150 px and collapsed into a wreath at 18). What survives: a rounded leaf-blade broadhead on a bare
  shaft with two vanes swept back like real feathering — every part chunky enough to hold its shape in peripheral
  vision, because at mark size an icon is a silhouette and nothing else (the bake-off rig rendered all of them beside
  real `9,214`s to prove it). The first survivor flared its fletch to square shoulders wider than the head and read as
  a trophy stand on a podium; the swept-vane redraw fixed the posture, and a slimmer redraw than that lost its strokes
  at 16 px — "nicer" is allowed to mean thinner only down to the weight budget.
- The burns' glyphs earned their shapes the same way, in the same rig. The **wizard hat** survives on a thick
  brim and a bent tip — the straight triangle read as an arrowhead, then as party furniture — and its dark band
  rides MID-CONE: drawn across the cone/brim joint it re-cut the silhouette into a horn on a pill. The **skeleton**
  is what survived of "just draw a skeleton": anatomy at 16 px rendered as a lightbulb. A reduced bone figure — skull
  mass, eyes, ribs as dark bands across one torso — read honestly enough but looked worse than the plain skull beside
  real numbers, and the skull plainly belongs to the necromancer anyway. So **Life Burn owns the skull** and the
  finishing blow moved to a **tombstone**: a broad arched slab on a flat ground footer — the footer is what keeps it
  from reading as a round blob, since the stone has no eyes — with two dark engraving bars where the eyes would do
  their work. Two eliminations got it there: a reaper scythe fused blade-and-haft into "a slash and a checkmark" at
  shipped size, and a sword driven point-first was the assassinate dagger's own silhouette family. One glyph per
  event again, which is how the set wanted to be drawn.
- Marks **never fold**, in either direction. A marked row is out of folding as a target because it blows out like
  a crit, and `FctIngest` also compares `Special` so an incoming mark cannot quietly fold into a plain row of the
  same number. The alternative — an assassinate swallowed into `×3` on a plain Backstab, or two identical marks
  collapsing to one count — is invisible data loss on exactly the events worth seeing.
- Glyphs are vector paths on a 24-unit grid rather than PNG assets: they scale with both size dials,
  need no new deployment files, and the cut-out details (ghost eyes, skull nose) can be forced to the
  outline colour independently of the fill.

### Accumulation: the knob we built, tried, and deleted

The overlay has collapsed duplicate hits since the first ingest engine — one number reading "412 ×6" instead of six 412s,
keyed to lane, side, kind, ability and *printed* value so the count is a fact. For a while this week it also had a switch:
an "accumulation" checkbox (settings.ini `FctOverlayAccumulate`, default off) with the argument that compacting somebody's
fight without asking was the wrong shape for a feature about what they see. Played for a day, the verdict came back the
other way: nobody wants the second row of an identical hit. The knob is gone — checkbox, config field, ini key — and
duplicate collapse is unconditional again, which is also what MSBT-class overlays ship. The dial for "I want to see less"
already exists and is honest about it: that's THRESHOLD, and it counts what it hides.

What the knob's short life did settle is two policy wideners the playtest actually wanted, kept after the switch died:

- **Crits collapse onto crits.** They used to refuse folding outright, which meant a crit storm could only overflow into
  the drop counter. Identical crits now stack honestly — every crit lives in a pool lane of its own, so plain numbers can
  join a crit stack (or receive one) by construction, not by another key clause; a taken crit still never joins a dealt
  one because whose-story was already in the key. The overload test survives as what real storms are: differing crits
  (900, 1.2k, 1.4k…), which nothing on screen may take, counted when the cap bites.
- **Words collapse too.** Four identical evasions are one fact said four times ("Miss ×2"), and a word carries no value,
  so the face-value rule that protects numbers has nothing to guard — words fold where numbers need the printed-equality
  clause. Numbers and words still never fold across kinds: two sorts of fact keep separate rows.
- **Never summed.** The value on screen is always one real hit's; only the count moves. (Asked again during the knob's
  trial whether players want literal totals — "642k" for two 321k hits — the answer was no: there's no need to literally
  add numbers together, and an amount no single hit landed for is exactly what the original design refused. The row says
  size-of-one and how-many, every claim on screen stays a fact.)

Tests moved with the deletion: the opt-in lines came out of five files, `CritOverloadIsCountedBecauseCritsNeverAbsorb`
became `…WhenTheCritsDiffer`, the crowded-lanes word cadence went from alternating to eight distinct words (alternating
identical words now correctly collapse), and `FctAccumulationTest` lost its two door tests and kept the shape: duplicates
print once with a count, face value frozen, crits only onto crits, words onto words, heals and marks never. 1081/1081.

### The centre gutter: forty pixels reserved for what the player is looking at

Split mode's halves used to meet exactly at the middle of the overlay, which put left 2's right edge and right 1's left
edge — the two lanes a fight actually lives in — directly on top of the spot where the target frame sits. The user's
sentence for it: "that's where you should place the NPC." And long labels made it worse rather than better: FitSource
budgets a label from spine to region wall, and when the wall was dead-centre, an inward-reaching `(Long Spell Name)`
printed across the player's own target.

Split halves tile the track minus a centre gutter (absolute pixels — forty after the first day's play asked for more
air; it began at 20): left ends at (W−G)/2, right starts G past it. The width is a dial since — the LAYOUT row
`center gutter` (settings.ini `FctOverlayGutter`, 0–800 px), because the ultrawide crowd wanted the band wider and the
wall-to-wall crowd wanted it closed; forty remains what an unset config draws, and every stage clamps its own ask to
four fifths of its width, so a number chosen on an ultrawide still tiles honestly in a postage stamp. Everything downstream derives from that one
measure, so the gutter cannot exist in geometry and not in labels: spines shift equally outward (right 1's slot sits a
hair further from the seam, which is still "beside the middle, near the fight"), lane rects and their configure-mode
outlines show the empty band, and label walls — which ARE region walls — now stop long words short of the centre
instead of printing over it. Bands never asks; its protected region is the horizontal strip, not a column. Tests moved
to gutter-aware expressions (Half/Seam computed from the shipped default) rather than new magic numbers, 1081/1081.

The same pass gave the demo switch a memory: SampleData saves to settings.ini like anything else, because "don't play
sample numbers, I'm laying this out over my actual raid" was an answer that had to be re-given every session, which is
the definition of a setting wearing furniture's clothes.

### The bow that ate the label: why a spell called "Desperate Renewal XIII Rk. II" shipped as "(Des)"

The user's screenshot asked the question better than the code answered it: three lanes configured, right 2 freed, and
the left-hand heal showing `+74k (Des)` with a visible acre of empty overlay between it and the neighbour's numbers.
"Isn't there room for more of the label than we're displaying?" — measured, yes, and the culprit was not the label code.

A diagnostic run at the screenshot's width said the true arithmetic: the rail parked at x=294 in a lane whose spine is
168. Not jitter — the arc's **bow carve**. Spawn pre-paid for the outward bend by shoving the rail 126 px inward (the
value box hangs left of the rail, and an outward vertex needs that room or it clips), which left every right-seated
label `SideMax − rail = 33 px` of budget. The lane's air was all there; the name just wasn't allowed to spend it, and
the floor cut produced a bare "(Des)" — three letters wearing no mark that anything was withheld. Two wrongs stacked:
the shove broke the spine weld FctStage promises ("a lane's spine stays welded to its slot"), and the floor dropped the
ellipsis precisely when the cut was most dishonest.

Split lanes are private — there is no neighbour stream to dodge — so "outward" was demoted from law to preference and
replaced by the **open hand**: a split column bows toward whichever side of its spine has more air (lane-decided, equal
for every row), bands keeps the genre rule untouched. The rail holds its slot, the bend spends the leftover, and names
inherit the lane: the diagnostic's heal row now draws "(Desperate Renewal XIII Rk. II)" whole at 147 px of label room,
and where a lane truly cannot carry a name, the floor wears its mark too — "(Des…)", overshoot charged to SourceWidth so
ArcedX tucks the rail back and the ellipsis never crosses a wall.

Two pinned laws came out rewritten rather than broken, which is what measuring is for: `TheArcBowsToAVertex...` now
asserts the vertex sits over the lane's open hand instead of "away from the middle" — that rule has since been retired and the test asks
`FctLayout.BendDirection` for the sign rather than computing one, which is the same lesson one level up — and `ANameFollowsTheRoomItHas`
compares NAME lengths where it compared label lengths — "(Glo)" growing "(Glo…)" as a column narrows is the cut mark
appearing, not the name growing. 1081/1081; the real question was the screenshot's, and the answer was "yes".

### Which way an arc leans: `out`, `left`, `right`

Three words, and `out` is the one a file with no `FctOverlayArcBend` key means: each half bows away from the middle of the overlay. **left** and
**right** stop consulting the lane and say which way, which is the answer for an overlay parked on one side of the screen. Saved as
`FctOverlayArcBend`; the panel puts the item beside `shape` and shows it only while the shape is `arc`, because `line` has no curve to point anywhere and
fountain's two shapes have no spine to bend (`ClampShape` keeps that promise, `FountainIsNeverHandedAnArcToLean` asserts it).

`out` is also why the row alignment had to become a consequence of the lean rather than a constant (below): a mirrored pair cannot be drawn by two columns
whose blocks both hang left, because one half's curve is spending the hand its own digits occupy.

There was a fourth word until recently — `open`, meaning "ask the lane which of its own hands is roomier and spend that on the bend". It was not a rule
anybody would have written down if it had not already been what the code did, and measured on its own merits it does not survive:

- **fountain never heard it.** Bands is not a column layout and is never handed an arc, so the word addressed one scheme only;
- **in split it could not hold a direction still.** One category per half, damage on the right: +99.5 at 900 px, +147.0 at 1280, then **-238.0 from 1440
  up** — resize the window and the same column's arc bends the other way, because the answer is a consequence of lane width and rail reserve rather than
  of anybody's taste. With heals left and damage right at 1920 it put **-319.6 / +319.6**, both halves leaning inward at each other across the gutter,
  which is precisely what a player meant by "the arc goes the wrong way";
- **and it was not even the better curve.** With the row turned to hang in the hand its bend spends (below), `out` bows 146.2 / 210.8 / 238.0 at those same
  widths where `open` managed 99.5 / 147.0 / 238.0.

So the word is gone rather than aliased, and a `settings.txt` written while it existed reads as the default instead of as an error — which is the entire
compatibility debt a setting that never appeared in a release owed.

What reading the code needs to keep is that the retired rule is still the one most of the surrounding arithmetic was written for: `Spawn` pre-pays a bow,
`AssignTravel` caps one, and both were built when "which hand is open" was the only question. Fountain's law (every column bows away from the shared middle
strip, because leaning into the neighbour's stream tangles it) is what `out` hands to split — the "arc away from my target frame" look most people mean
when they say standard — and split had been doing the opposite of it by default since before there was a dial. `FctLayout.BendDirection` is now the single
place that answers the direction, and it reads the word and the spine's side of the middle line, nothing else; `FctArcTest` asks *that function* instead of
restating its arithmetic so a third copy cannot drift out of existence alongside it.

### The lane has to pay for the lean: `FctStage.ParkForBend`

Two of the words still on the dial drew nothing when they shipped — `left` looked like a straight scroll and `out` curved on one side only (and their
predecessor `open`, since deleted, was indistinguishable from `right`). The reason is one-sided by construction: **a rail row is right-aligned** ("right edges ride the
rail", `f3ba116f`), so its number — plus its words when they sit left or below — hangs LEFT of the spine. Every split column therefore keeps its own
glyphs in its left hand and its free air in its right one. Measured at 1280x1080, one category per half: **448 px of air right of the rail, 0 px left of
it.** Not "little": zero, exactly, always — `Spawn` pins the rail to `wall + max(RailReserve, valueWidth)` and `AssignTravel` then caps a bow against
`rail − wall − max(RailReserve, valueWidth)`, the same expression subtracted from the same wall. A leftward bend was refused the very margin spawn had
just created for it, so it collapsed to a straight line; rightward bends were capped by `valueRight`, which is 0 for a rail row, and got the whole lane.
That is also why `open` looked like `right`: asked which hand is open, every column answered "the right one" — except in the arrangement the tests were
written against, where damage sits in the *outer* lane at three quarters of its half (spine 1685) and has air on both hands, so the shipped answer comes
out inward. The word was never stable because it reads the lane, and the lane depends on which column you gave the category.

So a named direction is now a promise, and the lane pays: **`ParkForBend` moves a column off the wall it leans at** until the widest row it can roll fits
there with the full bend on top, bounded by the lane's own walls. What it has to buy is `FctLayout.RailDemands` plus `FctLayout.LabelDemands`: the hand the
block hangs on owes `RailReserve()` (the widest block the column can ever be asked to draw, mark included) and the bare hand owes nothing; then the seated
label's floor is added to whichever hand that seat draws on. Nothing in either bill comes from the arriving row. Measured after that first fix at 1280, one
category per half: `out` parks the healing column from spine 155 to **375** and draws **-210.8** where it used to draw 0.0 — before the row learned to turn
around; see below for what it costs now.

That worked, and it was buying the wrong thing: it paid in column movement for air the row itself could have vacated. See the next section.

Three rules keep that from becoming the park-shove that once put a column out of line with itself (`75e2992a`, which moved the rail *per row* and starved
every right-seated name to `"(Des"`):

- **the shift is a property of the lane**, never of the arriving number — slot, lane rect, `RailReserve()`, `LabelDemands` and the bow share are all
  column-level facts, so a `7` and a `999,999` on one column still share a rail and trace one path (`EveryExplicitLeanDrawsItsFullCurve`,
  `APaidForLeanIsTheSameCurveForEveryRowOnTheColumn`). This was a law stated rather than enforced until `out` became the default: with `open` parked nothing,
  so nobody ever ran the park. The day the default moved, `ACritAndItsNeighbourShareOneSpineAndOneBend` failed at **206 vs 194** — `RailDemands` had been
  asking the arriving `hit` for its measured `ValueWidth` and `IconAllowance`, which priced a crit's column further off the wall than the parry below it.
  The signature is now `RailDemands(bool hangRight)` precisely so a row cannot be passed to it;
- **a rail row takes no origin jitter.** `FctMotionStyles.IsRail` styles are conveyor rows and the conveyor pins every one of them to `SpineFor`, so nothing
  shipped was scattering sideways — but a caller that spawned a rail row directly got a start point within a blowout's jitter of the spine, levelled back
  onto it by the clamp band, which is how "one rail for the column" was passing by accident rather than by construction;
- **every lean on the dial pays.** There is no "the layout's own answer" branch left to exempt, which is why `ParkForBend`'s gate asks only about mode and
  style. The visible price is that a column sits further from the edge it leans away from, and its label hand shrinks by what the bend borrowed — at 1280
  with two columns on a side, the healing column's right-hand air goes 137 px → **32 px**, so names there get cut sooner. That is the trade, not a bug; if
  it bites, narrowing the bow is one constant (`OutPaysTheParkItNamed` pins what the debt buys);
- **a lane too narrow to pay keeps its column where it stands and draws what fits.** At 700 px split into columns, one rail reserve (~156 px at the crit
  ceiling) is most of the column: no park exists, and the answer is a very short curve — never a sign flip, which would hand one column two shapes
  depending on how lucky its digits were.

Rows in flight keep the bow they were born with, for the same reason a resize maps an existing bow instead of recomputing it: the vertex is arithmetic from
the spawn, and re-bending mid-flight tears one number's path in half. Bands takes none of this — it pre-pays its bow at spawn (`FctLayout.Spawn`), and
could not be handed an arc anyway (`ClampShape`, asserted by `FountainIsNeverHandedAnArcToLean`).

### Turning the row around: `FctHitState.HangRight`

The park above worked, and a reader pointed out that it was buying the wrong thing: *"could we change rows to be left aligned when arcing out to the left?
Basically align the row as needed for the arc options, leave straight as is."* Right-alignment exists so a column is an odometer — ones digits under ones
digits, which is the one text alignment anybody who reads parse numbers wants — and nothing in that requirement says which side of the rail the digits sit
on. Turn a left-leaning column around and its numbers line up on their **left** edge instead, in the exact hand its own curve wanted.

One flag, stamped before the spine is asked for (`FctLayout.Spawn`, and in `FctStage.SpineFor` too so a conveyor row pinned at its column's mouth agrees):

- **`BlockFromRail` builds outward/inward and maps them by the hang.** Everything downstream already reads that one function — Spawn's reserved reach,
  `AssignTravel`'s cap, `ParkForBend`'s debt, `FitSource`'s budget — so the swap propagates instead of being re-derived four times;
- **`FctMotion.ArcedX` converts rail→centre the other way** (`rail + ValueWidth/2` rather than `rail − ValueWidth/2`), and that is the entire drawing
  change, because the canvas places every glyph from that centre: the halo, all three label seats, the pop pivot. A turned row keeps its internal
  arrangement exactly — a right-seated name still sits right of its amount — and only swaps which side of the rail the whole arrangement stands on;
- **the special-event mark is the one exception**, since it hangs outside the number's *outer* edge (`FctSkiaCanvas.DrawMark`): turned rows put it on the
  right, which is the side `BlockFromRail` charges `IconAllowance` on. Drawn box and reserved box stay one box.

Who turns: an **arc** in **split** whose dial leans left of its rail (`left`, or `out` on the left half), because that is the only case where the digits
were standing in the hand the curve needs. `line` never turns — it has no lean to make air for, and its exact geometry is the one every player already
knows, so it draws the shipped right-alignment under every word (`AStraightRailKeepsTheShippedAlignmentUnderEveryLean`). The consequence worth stating out
loud: on `out`, the two halves are aligned *differently from each other* — that is what a mirrored pair means once the curves are equal, and every row inside
one column still shares one edge (`ALeftwardLeanTurnsTheRowAround`, `OutHangsEachHalfOffTheHandItsCurveLeavesFree`).

Measured at 1280x1080, one category per half (spine / hang / bow), current code:

| dial | damage column (right half) | healing column (left half) |
|---|---|---|
| `out` (default) | 1061.2 · hangs left · **+210.8** | 218.8 · **hangs right** · **-210.8** |
| `left` | 1115.6 · **hangs right** · **-210.8** | 218.8 · **hangs right** · **-210.8** |
| `right` | 1061.2 · hangs left · **+210.8** | 164.4 · hangs left · **+210.8** |

and at 1920 the turned columns sit at 1685.0 (`left`) and **327.6** (`out`/`left`, was 484 under the reserve-sized park) for the same ±319.6, with `right`'s
unturned healing rail still on its slot at 235.0 (`OutPaysTheParkItNamed` pins the 1920 quartet so a change to slot, reserve, bow share or hang shows up as a
number in a failing message instead of as a quiet shift in where the numbers live). So the row turn costs the column *less* movement than the park alone did
and buys the same depth, because `RailDemands` leaves a turned row nearly nothing to pay on the hand it bends at: `out`'s healing rail parks at **218.8**
instead of 375 for its full **-210.8**.

### A name and a lean want the same pixels, so the lane buys both

Making the fixture carry source names — as ingest does, `FitSource` right after placement — turned up a bug older than any of the above: the cap sizes a bow
against the **amount** (so all rows of a column bend identically) while `ArcedX` clamps against the whole block **including the words**, so a name standing
in the lean's path had the bend taken out of it at draw time, *row by row*. Measured at 1024 px, one column per half, right-seated label: the vertex drew at
**959.7** of the **1016.0** the column was given — 56.3 px of a 153.6 px bow simply absent, and absent by a different amount for `"(Crush)"` than for
`"(Ethereal Fire XIII Rk. III)"`, which is one spine drawing two paths.

`FitSource` now charges the rooms it cuts against: each hand loses what the bow spends there, so names are trimmed to what the drawn curve truly leaves.
That was the whole answer for one build, and it turned out to be too generous to the curve: measured with `out` as the default at 1280 with right-seated
labels, **0 of 234 live row-sightings kept a name at all** — a mirrored pair spends a hand on every half, so there was always a column whose seat stood in
the bend, and the fix had been to delete the annotation. An arc that eats the log line is not worth drawing, so the room is now bought **before any name
exists**, by `FctLayout.LabelDemands`: placement adds "`(00…)` at this lane's source size, plus its gap" to the demand on the hand the seat draws on, in
`ParkForBend`'s debt and in `AssignTravel`'s cap alike. Every row keeps its name (cut to what its hand truly has — `(Probe)` arrives as `(Pro…)` where the
lean owns that seat and whole where the lean goes the other way) *and* every row bows its full distance, at every width from 900 up (`ANameAndALeanBothGetRoom`).

Both bills are column-level on purpose: they read the seat, the font and the dial, never a row's name, so reserving a label's floor cannot make one row curve
differently from its neighbour. `FitSource` still holds the drop as a last resort — it fires only when the two measurers disagree about the room (the canvas
has real glyphs where the spawn pass had an estimate), and the alternative, letting words stand and having `ArcedX` take the bend out per row, is the bug this
section opened with. A column with no lean keeps the floor-plus-ellipsis answer exactly as before, where the rail's own clamp absorbs the overflow, so `line`
and every non-arc label are untouched.

`FctArcBendTest` pins the lot: that the vocabulary is **three words and no more** (`EveryLeanWordIsThreeWordsNoMore` — a fourth enum member would be silently
looped over by every sweep in the class, which is how a retired answer comes back to life), that absent/junk/`open` all read as `out`, the mirrored signs *and
mirrored depths* of `out` at 900/1280/1920/2560 measured against the lane's own `FullCurve`, the **full depth** each word draws on both one-column and
two-column halves (the guard that was missing — every older assertion read a sign, and a bow clamped to exactly zero passes "not positive"), which hand a row
hangs on in every word, the parked spines at 1920, and a sweep over widths × tiling × **every label seat** that asserts the **whole drawn box** (not the
centre) inside `[SideMin, SideMax]` across twenty-four steps of every flight *and* that the vertex lands where the column's bow says it should. The two deeper
ones exist because the shallower ones passed while the feature was broken: box-not-centre catches a mirror that keeps a centre honest while its digits cross a
wall, and vertex-depth catches a curve silently eaten by a label.

## Damage meter setup window

Configure used to be a form painted on top of the thing it configured: pressing the cog swapped the live meter for a
preview instance whose face carried eleven controls, three dropdowns deep, with Save/Cancel/Close buttons that enabled
themselves only after something moved. The FCT panel had already proved the better shape — the overlay stays nothing
but numbers while a companion window holds every decision — so the inline panel is gone and `DamageMeterSettingsWindow`
took its job: two accordion sections that each answer one question — STYLE (thin bars, font size, rows, then bar color
and highlight) and OPTIONS (hide other players, show % damage, streamer mode, class filter, crit rate, reset) — Save and
Cancel as the entire exit vocabulary, everything previewed live on the sample stage and nothing written to ini until Save.

The staging rides a `DamageMeterConfigState` copy that `Load()`s with exactly the overlay constructor's guards, so
setup always opens on what is on screen. The overlay grew three internal verbs — `PreviewMeterState` (apply without
persisting), `CommitMeterState` (apply, persist the same ini keys the old panel wrote, hand back to the game) and
`DiscardMeterSettings` (just end: MainWindow reopens a live meter that reads the untouched values back off disk, so
discarding restores itself and no undo code exists). The `_current*` fields stopped being half-state/half-control —
rows, font, mini bars, percent, streamer and both colors now have working-set fields the Update methods maintain, and
the update tick reads those instead of asking a ComboBox that no longer exists.

Two deliberate inherited oddities. Geometry stays out of the staged object: dragging and resizing the stage is the
geometry editor, and Save captures whatever rectangle the stage ended on (the `heightRectangle` measuring line is all
of the old panel that survives). And Save closes configure rather than lingering — the old form's three exit buttons
enabled and disabled in a dance around each other; here every change is already visible two inches away on the stage,
so "keep these" and "walk away" are enough words.

Placement follows the companion conventions: the window docks right of the meter when the work area has room and left
when it doesn't, clamps into the screen, moves by the stage's drag delta so a pairing the user chose survives being
moved, and closes with its owner automatically because `Owner` says so. The bar pool rebuilds only when the row count
actually changes — measured while wiring: previewing every color-picker stop through `UpdateMaxRows` recreated and
reseeded every `DamageBar` on each intermediate stop, which is a flicker the player never asked for.

## Combat event emitters

The damage meter briefly carried an event ribbon under its target row; the UI was withdrawn (a feed wants its own
surface and design, not leftovers bolted under a ranking list). What remains is only broadcasting: parsers fire
`DamageLineParser.EventsNewDeath` and `MiscLineParser.EventsNewMezBreak` alongside the existing taunt event wherever
those records already hit storage, and `DamageRibbon` (Core) keeps the tested wording rules - mez break names its
breaker, players/pets killed read as a will or a plain death, named pets shorten to their personal name
(`Puksu`, never `Sancus`s pet Puksu`), generic unnamed pets and mob deaths never speak, taunts land on success only,
and `PlayerRegistry.IsVerifiedPlayer` answers the player question behind a settable seam. Nothing subscribes at
startup; whoever builds the real feed takes it from there.

## Instrumenting the UI thread

Reported often, impossible to reproduce on demand: the combat numbers stop for a second or two in the middle of a raid, then carry on
without a restart. That shape is not a leak and not a crash — it is somebody occupying the application's one UI thread, and this
application draws everything on that thread: the overlay's raster and blit (`FctSkiaCanvas.OnRender`), the damage meter's once-a-second
bar rebuild (`DamageOverlayWindow`), every trigger text overlay (`TextOverlayWindow`, 150 ms per tick), Syncfusion grids
(`FightTable`) and open charts, and the `settings.txt` write on the main window's half-minute timer. Any one of them holding the thread
stops all the others, so the report a player files always names whichever window they happened to be looking at. The instrumentation
exists to answer the question the report cannot: whose second was it.

It is deliberately not a profiler. A profiler answers once, on the machine running it, and never on the one where the hitch happened;
this lives in the product, costs tens of nanoseconds per instrumented span, and leaves its evidence in a log file that is still there
the next morning. (Deep profiling is still available from outside — `dotnet-counters monitor -p <pid>` for live GC and thread-pool
numbers, PerfView or WPF ETW traces for frame-level detail — none of it required, and nothing here depends on it.)

### One watchdog on the thread, not one per window

`UiBeatMonitor` posts a beat to the dispatcher from a pool timer. A beat that has not run after `DefaultStallMs` means the UI thread is
somewhere else for that long, and the line it writes says what was open and which named spans were inside at the time. Instrumenting
only the overlay would have printed an innocent "my paint took 1.2 ms" beside every freeze, which is a report with no answer in it.

Four decisions in that design are load-bearing:

- **The beat goes out at `Render` priority**, which is 7 on the scale WPF actually uses — `Send` 10, `Normal` 9, `DataBind` 8, `Render` 7,
  `Background` 4 (where a `DispatcherTimer` fires by default), `ContextIdle` 3. That is above the band which starves while a window is being
  dragged or resized, so dragging an overlay does not read as a stall, and below ordinary queued work, so a late beat's number includes time
  spent behind work the player was also waiting for. It is also why a stall line naming `in progress nothing` has a mundane reading:
  something outside this process's own work had the thread.
- **A stall is counted where it is detected**, on the pool thread, not on the beat that closes it. The episode a process never recovers from
  is precisely the one with no closing beat, and a counter that only moves when things get well again reports a clean session for the worst
  freeze of the night.
- **Detection happens while the process is still stuck**, on the pool thread, and again when the beat finally arrives. If the UI thread
  never comes back the log still holds everything known at the time; waiting only for the resumed beat loses exactly the worst case.
- **Gaps longer than `SleepGuardMs` (30 s) are discarded.** A laptop lid is not a hitch, and reporting one trains the reader to ignore
  the lines that matter.

Those priority numbers are also what make `UiBeatMonitorTest` work, and they were got wrong the first time. The test ends its message loop
by re-posting a budget check; queued at `Normal`, that check won against the `Render` beat when a blocked sleep finished — the loop returned
before the beat that measures the stall ever ran, and the blocked-dispatcher test reported nothing while the watchdog was working correctly.
So work queued by a test sits below `Render` (`ContextIdle` for the busy chain) and the pump's own check between them, which is the only
arrangement where both the beats and the end of pumping are guaranteed their turn. The numbers above read off the reference assembly rather
than from memory for that reason.

Heartbeats (one Info line every 20 s: window length, what was open, worst beat delay, render mode, GC deltas and allocation rate, and the
cost table of instrumented spans) are written only while an instrumented surface is open. They exist to explain overlay stalls; a build
with no overlay running has nothing to explain, and the log rolls over at a few megabytes with game-data errors sharing it — a heartbeat
that pushes out last night's stall line is worse than no heartbeat. Even that is opt-in now: see "Off unless asked" below.

### Off unless asked (`PerfReport`)

Everything the instruments say leaves through **`PerfJournal`**, and `PerfJournal.Enabled` is the door. Normal use leaves it shut — `PerfReport=True` in
`AppData\config\settings.txt` opens it, and `Debug` implies it — because the cheapest thing the watchdog does is cost 90 log lines an hour saying nothing is
wrong, and a player's complaint has to share that file with them. When the answer is off, `App` does not start `UiBeatMonitor` at all: a watchdog with nowhere to
report is only the timer it posts on, plus a `GC.GetGCMemoryInfo()`/pause-delta poll every twenty seconds.

The switch controls **speech, not measurement**. `PerfCounters` still counts (interlocked adds on paths that were already touching those fields), and `GcTidyUp`
still hands memory back at its three chosen moments because that is a feature rather than an instrument. Two consequences worth knowing before reading a log:

```
UI perf reporting on: beat every 20 s, stall threshold 1000 ms
```

- That line is the first thing written when the instruments are on, so silence in someone's log means either "watched and clean" or "nobody was looking", and only
  their `settings.txt` says which. Ask for it before asking about a missing stall line.
- Nothing is throttled while the journal is closed, so the first offender after it opens is reported immediately rather than waiting out a silence that accumulated
  unread (`PerfJournalTest` pins both halves of that, along with `Enabled`'s default).

### What a name means, and where it comes from

`PerfCounters` holds three shapes of entry under one reporting table: timed spans (`Begin`/`End`, count + average + worst), counters
(`Note`, "how many") and levels (`Gauge`, "how much right now"). Names are lowercase dotted, prefixed by the surface they belong to, and
the prefix is also how a stall line reads: `in progress meter.loadstats 812 ms` says which window ate the second. What exists today:

| name | shape | what it covers |
|---|---|---|
| `fct.pump` | span | a render tick that is not rasterizing — prune, demo, DPI check (its duration is also published as `LastFrameMs`) |
| `fct.paint` | span | a whole `OnRender`: clear, every glyph pass, both copies into WPF |
| `fct.feed` | span | the overlay window draining its queue into the canvas — per-record work on the UI thread |
| `fct.bake` | count | halo sprites baked this window (once per distinct crit string, not per frame) |
| `fct.surface` | count | render-surface reallocations; each one also logs its size in megabytes |
| `fct.hits`, `fct.queue`, `fct.drop` | level | live hits on the canvas, records waiting in `FctManager`, and the lifetime sum of everything the overlay turned away (canvas refusals plus queue discards added together) |
| `fct.dropLive` | level | how much of `fct.drop` was refused **while the canvas was still being handed frames** — text that could have been read. The number that decides whether a drop count is a bug report or a browser sitting on the overlay |
| `fct.dropQueued` | level | the part of `fct.drop` that never left `FctManager`'s queue (age-out and cap). A level for the same reason as the two beside it: the per-window `fct.dropStale`/`fct.dropCeiling` counters can be displaced off the line, and an absent counter proves nothing |
| `fct.backlog` | level | the deepest a lane's arrival queue has been all session (`FctConveyor.PeakWaiting`, a lifetime high-water). Read against `BacklogCap` (12), it says whether more room is the fix or the losses came from elsewhere |
| `fct.dropMax`, `fct.dropCrit` | level | the largest number ever refused, and how many refusals were crits. A count cannot say whether anything was missed — a saturated rail turns away its newest arrival whatever that is — so these say what the losses cost. `FctOverlayWindow` prints the same worst figure in the overlay's own stats row |
| *(log line)* `FCT refused its largest number yet: Flurry 3200300 crit` | once per advance, 5 s gate | the same refusal **named**. A bare integer cannot be chased down: a face value no spell in this game produces is either a line parsed into a number it should not be, or a lane fed the wrong figure, and the ability name is which. `FctIngest.WorstDropText`, printed by `FctOverlayWindow.OnCanvasFrame` |
| `meter.build` | span | the stats rebuild under `StatsLock` — **registered off the UI thread**, see below |
| `meter.loadstats` | span | rewriting every bar in both meter lists, on the UI thread, ten times a second |
| `text.render` | span | one trigger text overlay redrawing its blocks |
| `trig.timerTick`, `trig.timerBar` | span | the two UI-thread passes of a trigger timer overlay: rewriting every visible bar's text and progress from the 75 ms loop, and the pass that adds and collapses bars when what is firing changes |
| `trig.timerBars` | level | `TimerBar` elements the overlay is holding, collapsed spares included — the divisor for the two spans above |
| `trig.logGrid` | span | the refresh `TriggersLogView` asks its grid for; nearly free when the grid already sorts by time |
| `trig.logReset` | count | whole-collection invalidations reaching that grid. Each one tells WPF nothing can be done incrementally, so a bound grid rebuilds and re-sorts itself whether or not anything asked |
| `trig.logBatch`, `trig.logEntry` | count | trigger-log appends in the window and the entries inside them (**off the UI thread**): how often any bound grid must reload, and how many rows it has to sort |
| `app.voices`, `app.triggerdb`, `app.mainwindow`, `app.triggmgr`, `app.firstshow` | span | the startup phases that run on the UI thread: voice load, trigger database, main window construction, trigger manager, first `Show` |
| `ui.openlogfile`, `ui.pickfile` | span | opening a log file (restore at startup included), and the modal file dialog inside it |
| `fct.dropLane`, `fct.dropConveyor`, `fct.dropStale`, `fct.dropCeiling` | count | numbers that never reached the screen, split by cause; their lifetime sum is still the `fct.drop` level |
| `ui.configSave` | span | `ConfigUtil.Save()` — writing `settings.txt` from the main window's half-minute timer |
| `ui.computeStats`, `ui.fightTable`, `chart.update` | span | stats recompute, the fights grid's row insertion, one data point into an open chart |
| `chart.clear`, `chart.walk`, `chart.rolling`, `chart.pick`, `chart.reset`, `chart.series`, `chart.refresh` | span | the phases inside one line-chart redraw — emptying the aggregates, walking the records, the 5 s rolling window, choosing which lines to show, emptying the chart control, building its series, handing them over. See *A chart redraw is phases too* |
| `chart.rendergap` | span | what the framework took with the chart after the series were handed to it: a callback posted at `ContextIdle`, so the gap is layout and render (and anything else queued behind it), which is work outside this codebase |
| `chart.column` | span | one page of the players-vs-top-performer chart, which is hand-made WPF elements — a rectangle, labels and a tooltip per column — rebuilt on a 250 ms timer while that window is open |
| `chart.updates`, `chart.plots` | count | data point events arriving versus redraws actually performed; `plots` at twice `updates` is a cascade (a view option changing re-plots before the data even arrives) |
| `chart.backlog` | count | redraw requests that arrived while one was already waiting on the dispatcher — the shape one merged redraw would remove |
| `chart.records`, `chart.lines`, `chart.points` | level | what a redraw was asked to draw: records walked, lines built, data points handed to the control |
| `trig.line` | span | evaluating one log line against every active trigger, on the trigger thread (**off the UI thread**) — count is lines, average is what a line costs |
| `trig.tests` | count | patterns that pass asked for: multiplied out, this is the matching bill (an imported set of 4,227 enabled triggers tests every raid line against all of them) |
| `trig.active` | level | how many triggers every live `TriggerProcessor` is testing lines against, summed (one processor per watched log plus the tester; whichever one wrote last used to print an idle character's 0 over the 608 that were running) — the number that separates a clean soak from somebody's freeze |
| `audio.synth` | span | turning text into samples, measured inside `EQLogParser.Audio` and reported through `AudioManager.PerfSink` because that assembly references no counter code (**off the UI thread**). This is where the engines differ: a neural model on one machine, SAPI or WinRT on the next, and the same build either way |
| `audio.file` | span | reading and decoding a sound file for a player, including the cache miss that has to open it (**off the UI thread**) |
| `ui.worldstop` | span | how late the watchdog's own pool timer ran behind its interval, recorded only past 500 ms — the one in-app figure that sees a stop-the-world, since the collection freezes the watchdog too (**off the UI thread**) |

`Register` is called once per span in a field initializer and the handle is kept, because this runs inside frame paths and looking a name
up per call is the kind of thing that would create the hitch it measures. `Register(name, uiThread: false)` keeps a span's cost on the
heartbeat while keeping its name out of "in progress": the meter's rebuild genuinely runs on a pool thread, and a stall line that blamed it
would point at a window that was holding nothing.

Values are best-effort: fields are plain writes rather than interlocked, two threads colliding on one counter loses a sample of a
diagnostic and nothing else. Do not "fix" that with locking — an instrumented span that can block is worse than an unmeasured one.

`fct.paint` exists because it did not: the canvas has always published pump time (prune, demo, DPI), which never included rasterizing or
the two copies into WPF, so the expensive half of an overlay frame — the half that scales with pixel area rather than with how many
numbers are on screen — was invisible from outside. `FctSkiaCanvas` now reports both, and the simulation header prints them side by side;
`EnsureSurface` writes one line per reallocation, which is how a 3840 × 2160 overlay admitting to a 33 MB memset per frame reaches the log
without anybody having to guess at the size.

**A nonzero drop count is the normal state of an overlay, which is why its accounting is levels.** An overlay refuses a number rather than draw it on top of
another — every combat-text overlay does, and `FctConveyor.BacklogCap` (12) is where ours says no — and it refuses with identical arithmetic whether or not
anybody can see the window, so "564 drops" on its own is neither a bug nor an all-clear. What it takes to read one is: was anybody positioned to see the
number (`fct.dropLive`), did anything die on the way rather than at the rail (`fct.dropQueued`), and how full was the queue that said no (`fct.backlog`).
Those three are levels because of what the counter budget does to a diagnosis: over an eleven-minute session with a browser partly covering the overlay, the
per-window cause counters were **absent from nine of its thirty-three heartbeats** — four counted names share a line, and `fct.dropConveyor`, `trig.logBatch`,
`trig.logEntry` and `trig.tests` filled it nearly every time. A cause split that cannot be *absent* without meaning nothing is not a cause split, and it was the
reason a rewrite of the render pump got proposed and withdrawn in one conversation. The arithmetic now closes on one line: `fct.drop` is the sum, minus
`fct.dropQueued` what the rails refused, of which `fct.dropLive` is the part that happened under a running pump — text somebody could have read. If that last
figure climbs while somebody is watching, `fct.backlog` picks the fix off the page: parked at 12, the rail was too short for that fight (more queue, or shorter
rows); well below it, these losses came from somewhere else.

A follow-up session is what those levels were for, and it settled the diagnosis while leaving the judgement open: 572 refusals, `fct.dropLive` at **572**
(every one with the canvas painting, ~57 fps), `fct.dropQueued` at **0** across eleven minutes, and `fct.backlog` pinned at **12 from the first second of
combat to the last**. That is a rail running with no slack whatsoever — roughly one arrival refused per second, indefinitely — reported by a player who watched
the overlay the whole time and saw nothing wrong. Both statements are correct: a refused row never appears, so saturation has no visible signature, and "it
seemed fine" is not evidence of anything. Those zeroes also buried the other theory this log had already produced — the queue never starved and the compositor
was never missing, and without `fct.dropLive` on the page a rewrite of the render pump would have been written against a mechanism the measurement rules out.

The run with those fields filled in said more than the question it was asked. Refusals carried on at the same rate (~560 per ten minutes, `fct.dropQueued`
still 0, `fct.backlog` still reaching the cap), so spending the accelerator was neither the whole cause nor the whole cure — and `fct.dropMax` came back at
**3,200,300**, with `fct.dropCrit` at **306 of 564**. Two findings sit in those numbers and neither is about tempo. More than half of what the rail throws
away is crits, which is the half a player is reading; and something is arriving on a damage lane with a value no cast in this game does, which is why the
worst refusal now names itself rather than leaving an integer to be argued about — and why that chase leaves the overlay behind, since the same parse feeds
the damage tables.
What saturation costs is the remaining question, and it is a question about size rather than count: a hundred white swings refused a minute is what every
overlay does and what the melee filters exist for, while one big cast refused every few minutes is a rail that needs room.

### Startup and log loading are phases, not windows

Every name above, until this section, belonged to a window a player can see. The startup spans were added because the first measured session
produced two stalls and attributed neither: `UI STALL closed: beat ran 2922 ms late | open none | in progress nothing`, then 1563 ms more a
few seconds later. Both sat in code that had no name — the trigger database opened on the UI thread, the main window's XAML construction,
the trigger manager starting, a log file being restored from the last session — and "nothing" is the one answer a stall line should never
have to give about work this codebase does.

Two consequences worth knowing before reading a startup log:

- `app.firstshow` ends at the call that queues the first layout, not at the pixels. A stall naming `nothing` immediately after it closed is
  the main window's first paint, which is framework work between operations; instrumenting the render pass itself is the next step if the
  evidence says so rather than the guess being made now.
- A span held open across an `await` (the voice load, the trigger manager) reads as running while the thread is idle at the await point. That
  costs nothing here: a beat posted at `Render` runs the moment the thread is free, so if it was late, the thread really was inside that phase.

The file dialog has its own name for a reason too. Whether a modal Win32 dialog starves the beat is a question about WPF's dispatcher, not a
fact anybody should assert; `ui.pickfile` names it as the occupant if it does, and if it never appears in a stall line after a season of use
then a dialog is provably not the freeze and nobody has to wonder again.

The trigger paths were named next, because a measured session had eliminated everything that *was* instrumented — rasterizing at 1.3 ms a
frame, the meter's rebuild at under a millisecond against eight hours of records, a 31 s log load that never delayed a beat by more than
31 ms, GC costing 0.3 s per ten minutes at a 2 GB heap — and still had explained nothing. Two windows a player opens during a raid had no
number against their names: the timer overlay, which redraws every visible bar from a 75 ms loop for as long as any timer is live inside an
`AllowsTransparency` window; and the trigger log, which is bound to a live collection whose batches arrive as whole-collection `Reset`
notifications. Both register in the `open …` field too (`tmr:<title>-<id>`, `triglog`), since "was that window up when the beat went
unanswered?" is half the question and the heartbeat cannot answer what it cannot see.

The `open …` field carries the same improvement: a text overlay used to register as `text` plus its database id, which produced lines like
`open meter+fct+text29d9e8ff-ac7b-…` — true, and useless to read during a raid. It registers under the overlay's own title now, with the
first characters of the id kept so two overlays sharing a title stay two names (`TextOverlaySurfaceNameTest` holds that shape, including the
separators: the title must not be able to write a `+` or a `|` into the line).

### A chart redraw is phases too

The heartbeat that led here said `chart.update n=9 avg 410 max 1693 ms`: nine redraws of an open chart in a twenty second window, one of them
second and a half long, which is a player staring at a chart that will not change while their combat numbers are stopped. That name is the whole
pipeline though — empty the aggregates, walk every record, roll a 5 s window over them, pick the top lines, clear the control, build series,
hand them over — and only one or two of those steps can be the expensive one. Averaging them together measures nothing useful: the fix for a slow
record walk (build it on a pool thread) is the opposite of the fix for a slow control (draw fewer lines), and picking between them by guessing
buys the wrong refactor.

So `PerfBreakdown` registers each phase as its own span — the heartbeat keeps averaging them all session, which is a stronger statement than any
single redraw's line — and writes one line for a pass over 300 ms with the phases beside the sizes that explain them:

```
chart.update 1694 ms | DamageChart UPDATE | walked 41233 records -> 7 lines, 84000 points | plots 2 | chart.clear 0.4 ms chart.walk 310 ms … chart.refresh 1289 ms | budget 300 ms
```

Three things about it are deliberate:

- **Phases never nest; opening one closes the one before it.** A phase abandoned by an exception would keep naming itself in `in progress` for
  the rest of the session (the same hazard `PerfCounters` warns about), and a caller's `finally` calling `Complete`/`Abort` clears whatever is
  open. `APhaseNobodyClosedStopsNamingItself` holds that, and `APhaseOpenedTwiceAddsUp` holds the other half: one redraw that plots twice must
  add its two runs into one phase rather than showing only the last and accounting for less than the total.
- **A redraw asked for by a dropdown or a selection opens a pass of its own**, under the same phase names, so "the chart is slow" is answerable
  whatever the player just clicked and not only when a data point arrives.
- **`chart.rendergap` measures the part that is not ours.** Right after `sfLineChart.Series = series` a callback goes out at `ContextIdle`, which
  sits below WPF's layout and render priorities; the time until it runs is what the framework did with the chart. It is coarse on purpose — it
  also counts anything else queued behind, and the beat lines already say how loaded that queue was — which is why it is reported beside the
  phases rather than inside their sum. A `chart.refresh` of 2 ms next to a `chart.rendergap` of 900 ms says the cost is the control's, and this
  file should stop looking for it in itself.
- Lines are throttled to one per five seconds per breakdown, with what was swallowed counted and printed on the next one, because a redraw that is
  slow three times a second is one solved problem and three hundred lines hides the next thing. Throttling costs no measurement: the spans record
  whether or not a sentence came out (`ASecondSlowPassIsSuppressedAndCounted` asserts both halves).

What is already visible by reading the code, and is what these phases exist to confirm or kill: one `UPDATE` empties the chart control twice and
then replaces it — `Clear()` ends in `Series.Clear()`, `Plot()` ends in `Reset()` which is `Series.Clear()` again, then a fresh collection is
assigned — so three full invalidations of a Syncfusion chart per data point event, every one of them landing on the UI thread. Whether those three
cost 3 ms or 1,300 ms is precisely what `chart.reset` and `chart.refresh` are now instrumented to say.

The three builders each raise their own event and `MainWindow.QueueChartUpdate` dispatches a redraw per event, so a rebuild storm queues redraws
back to back; `chart.backlog` counts how often a request found one already waiting. That is the number that decides between "make a redraw
cheaper" and "ask for fewer of them", which is a decision nobody should make without it.

The number arrived, and it retired an idea. Across a session that opened the tanking and damage charts and selected every fight - 4.66M records
walked, redraws of 1,369 ms - `chart.backlog` never fired once: requests came about 1.3 s apart and each one drew before the next arrived
(`chart.updates×17` against `chart.plots×16`). Redraws do not queue behind each other in normal use, so the merge-queued-UPDATEs change that shipped
on the strength of the measurement above had nothing to merge, and was reverted rather than kept as unearned complexity. Keep `chart.backlog`:
zero is a result worth still seeing.

What that session did say about cost: `chart.rendergap` - Syncfusion laying out what we handed it, measured at `ContextIdle` - averages 84-96 ms on
ordinary redraws against our own 18-25 ms in `chart.update`, and ran 1,737 ms after a select-all walk of 1,341 ms. A select-all view hands over
36,163 points across five lines for a chart about 1,700 px wide, so the remaining lever is drawing fewer points, not asking for fewer redraws.

The first run of this caught two things, one of them a bug this commit itself shipped.

`chart.column` came back at `n=2 avg 0.3 max 0.6 ms`: the players-vs-top-performer page costs about nothing, so that blind spot is closed and
can stop being wondered about.

And the chart phases printed **nothing at all**, because counting the backlog had moved a property read across threads. The three subscriptions
used to read `data => Dispatcher.InvokeAsync(() => HandleChartUpdate(icon.Tag as string, data))`, which evaluates `icon.Tag` *inside* the
dispatched callback, on the UI thread — not an accident of style. Writing `data => QueueChartUpdate(icon.Tag as string, data)` evaluates it on
whatever thread the stats builder reached, and a `FrameworkElement` answers that with `InvalidOperationException: the calling thread cannot
access this object because a different thread owns it`, thrown inside `FireChartEvent`, logged by whichever builder was holding it — 27 stack
traces — with the result that no chart ever received an event, so there was no redraw left to measure. The fix passes the icon as an object and
reads `Tag` in the callback: handing a reference between threads is fine, touching its properties is not.

It cost nothing else, which is worth knowing how to read: `FireChartEvent` is the last statement in each builder's try block, after
`_lastStatsEvent = genEvent` and `EventsGenerationStatus`, so the meter, the grids and the stored results all completed — a frozen chart with a
healthy damage table is the signature of a subscriber that threw, not of a builder that failed.

### What the walk spends its time on, proven without Windows

The walk kept asking for the same information it had already read: `diffs` was a whole dictionary written three times per record and re-read by
each of the four series; `Aggregate` looked up that *and* `lastTimes` again by name; two modifier bit tests and a miss lookup ran once per series
instead of once per record; and `playerName + " +Pets"` allocated a fresh string for every one of those 4.66M records — a cost charged to the
whole application's collector, not just to this loop. Those lookups are now read once in the caller and passed as arguments, the tests run once,
and the `+Pets` name is memoized per run of records. `_hasPets` became a set of names because the byte its inner dictionary carried was never
read.

That is a change to arithmetic inside a UI control that no Linux-runnable test can reach, so it was checked by *executing both versions side by
side* rather than by review: `local/walk-equiv/` (not committed) slices the walk out of `git show HEAD:…` and out of the working tree, compiles
them into one program against `EQLogParser.Core`, feeds both walkers the same synthetic stream, and reflects over every property of every plotted
point in all four series plus the pet map. Identical output on 1M records / 73k points, at 945.6 ms → 847.7 ms (**1.12×**); identical on a
gap-heavy stream at **1.00×**. Keeping a change like this because it is *cleaner* would be guesswork — the harness is what says whether it paid.

### TimeRange: what it means, whether it is correct, and where its time actually goes

`TimeRange` is the activity model behind every "was this player here" question: all the log ever says is that something happened at an exact second,
so spans are laid down as `[begin,end]`, merged when they overlap or touch, and `GetTotal()` answers "how long was this one active". Its `Offset` of 6
closes silences shorter than that — two attacks six seconds apart read as one continuous engagement, **including the silent seconds between them**
(`[0,0] + [6,6]` totals 7), while seven seconds apart totals 2. That fudge is the point of the class, not an accident: a player who pauses between
casts was still in the fight.

**The answers are right.** `EQLogParser.Test/src/util/TimeRangeSpecTest.cs` tests meaning rather than API shape against a brute-force model written
two different ways (a `HashSet` of active seconds; spans joined across short silences, transitively), including adds arriving out of order — which is
what happens when fight ranges get merged into raid ranges or rebuilt by `FilterTimeRange`. A scratch harness (`local/timerange-lab/`, not committed)
pushed harder: 200,712 adds at absolute random positions with `GetTotal()` read after every one, comparing the live class against both an independent
model and a prototype. Zero disagreements. Two things that looked like bugs are not: the bridging pass in `GetTotal()` reaches the full transitive
closure in one pass (verified, not assumed), and **how often you read the range cannot change its total** — closing a silence merges a run's *outer*
bounds only, so materialising a bridge early moves no endpoint a later `Add` could have reached. That independence is pinned by two tests.

**Four sharp edges are real**, all asserted as-they-behave in that file so a fix fails loudly instead of moving numbers quietly: the single-segment
constructor bypasses both guards `Add` has (an inverted span gets in and makes the total *negative*, which then flows out as `TotalSeconds` into every
per-second number; `null` gets in and detonates later as a `NullReferenceException`, as does `new TimeRange((List<TimeSegment>)null)`); that same
constructor stores the caller's `TimeSegment` while the list constructor copies, so a caller moving its own object moves the range (11 seconds becomes
891); `GetTotal()` on an empty range returns 0 and the app divides by totals in about ten places — 5,000 damage over no seconds does not throw, it
saturates to `long.MaxValue`, a chart spike tall enough to flatten everything else; and `TimeCheck(line, start, range, out exceeds)` throws on an
empty range via `TimeSegments.Last()`, which is fine only because its single caller (`MainActions`' save-selected-fights) checks `Count > 0` first —
that check is load bearing and undocumented.

**My earlier blame was wrong.** I wrote that the superlinear chart redraw (20k / 40k / 80k records → 146 / 370 / 1,477 ms) was `GetTotal()`, called once
per inserted point. Measured directly on a LineChart-shaped stream — 400k records, five plotted names, ~6k fight boundaries, 193,745 real
`Add`+`GetTotal` pairs — the whole thing costs **46.5 ms** in the current class. Because `Aggregate` adds `[previousCrossing, now]` every time, each
name's spans chain into **one span** and stay there: a list of one is walked for free, so `GetTotal()` runs about 0.2 µs. The remaining superlinearity
lives somewhere else in the walk and is still unexplained.

What *is* quadratic is **`Add`**: a linear scan from index 0 to find its place, then `List.Insert` shifting everything after it. Fold every fight of a
long log into one range per name (spans stay separate — fights are minutes apart) and the cost climbs with the square: for 40 names carrying
50 / 200 / 800 fight spans apiece, building the ranges takes **3.7 / 21.5 / 166.5 ms**, of which the totals are under 1.5 ms. That is the shape used by
`FightTable` and `EQLogViewer` over all fights, by `DamageOverlayStatsBuilder`'s `allTime` plus one range per name, and by `StatsUtil` inside loops
over names and sub-stats.

**Shipped: `Add` finds its place by halving instead of walking from zero.** The spans are sorted and disjoint, so `EndTime` rises along with
`BeginTime`, and "which span could this merge with" is a binary search (`FirstSpanReaching`) rather than up to *n* iterations of six predicates. From
there it is two facts: widen the found span, then swallow the ones on its right in one pass and drop them with a single `RemoveRange`. Six ordered
predicates reduce to two questions, and six helpers (`CollapseLeft`, `CollapseRight`, `IsSurrounding`, `IsWithin`, `IsLeftOf`, `IsRightOf`) went away
rather than being wrapped. The `Equals` short-circuit is gone because merging with a span of identical bounds *is* the same nothing, done by arithmetic.
Nothing collapses left any more - the span before the found index ends before our begin by definition, and a disjoint list cannot touch a span we only
widen rightward - which is the one place the old code could recurse and this one cannot.

Measured on the shape above (40 names, one range each, every fight of the log folded in): **2.4 to 0.5 ms at 50 fights, 24.7 to 0.6 ms at 200,
165.9 to 1.7 ms at 800 - 100x**. `GetTotal()` was not touched, nor its bridging, nor anything else in the class.

Correctness was demonstrated rather than argued, twice over. The previous implementation is frozen in `local/timerange-lab/OldTimeRange.cs` and both run
side by side in `local/timerange-lab` (`-- equivA`, `-- equivBig`): **1.9M ops** with the *whole segment list* compared after every add - in order, out of
order, exact duplicates, touching endpoints, single-second points, inverted spans, nulls, and reads interleaved at random - plus **300k adds into ranges
of about 2,000 spans**, the size where walking from zero hurt. Zero differences in lists or totals. And because that harness is not part of the build, the
guard that ships is `TimeRangeSpecTest.ManyOutOfOrderAddsProduceExactlyTheBruteForceRuns`, which builds 400 randomly placed spans per trial and compares
every span of the result against a run list computed the obvious way. (It was `...BruteForceUnion` until the tick rule moved into `Add`; see "TimeRange: the
tick rule lives in Add" below.)

**Not shipped: making `GetTotal()` O(1) by carrying the total around.** Worth knowing because it is where the next speed-up lives, and it was prototyped.
A bridged run's length is additive - total = sum of span lengths + sum of (`gap - 1`) over consecutive pairs closer than `Offset` - so an insert only
disturbs the links at its own neighbourhood: keep a running length-sum and a running bridge-bonus and the answer needs no list walk, no allocation, and no
mutation. The prototype agreed with the live class and with an independent model on 200,712 out-of-order adds. Its own bugs are the argument for
differential testing over review: it counted a self-link when appending past the end (one second short), removed a link twice when a merge swallowed a
neighbour (five seconds short), and kept counting a pair that stopped being adjacent after an insert landed between them (four seconds *long*) - each
found by shrinking the failing history to three adds. Shipping it changes one observable thing beyond speed: `GetTotal()` would stop rewriting the segment
list. The *numbers* are provably unaffected (closing a silence moves no outer bound), but `LineChart.UpdateRemaining` reads `TimeSegments.Last().BeginTime`
back into an `Add`, and 15 call sites share this class, so it is a decision rather than an edit. (Superseded on the mutation half: the thing that made a
non-mutating read unsafe was an external segment removal in `DamageSummary`, and that cache is gone - see "TimeRange: merging copies, adding adopts" below.)
The counter itself is still not shipped, and the reason is staleness rather than speed: `TimeSegment`'s bounds are public setters and `TimeSegments` is a
public list, so any cached sum can be quietly invalidated by a caller the class cannot see. `GetTotal()` sums instead - no allocation, no mutation, about
0.1 ms for five asks on an 800-span range - which is most of the win with none of the ways to be wrong.

### Blocked or busy: what the thread was doing while nobody answered

A stall line proves the thread did not run a beat for N milliseconds and, when it says `in progress nothing`, that none of our passes held
it. That is as far as anything inside the process can go on its own, and it leaves two explanations pointing at different code. Either the
thread was *running* — draining framework work nobody times here, layout, binding refresh, rasterizing a window — or it was *waiting*, held
by something outside itself: a lock another thread owns, the render thread, a driver call. No span can separate them, because in the second
case the span would have to surround code this codebase does not contain.

The kernel already tracks the distinction per thread, so `UiThreadProbe` asks it. While an episode lasts the watchdog samples the UI
thread's state on its own 200 ms cadence — `Run`, or `Wait` with a reason (`UserRequest`, `LpcReceive`, `EventPairPort`) — and the closing
line carries the tally with the CPU actually burned:

```
UI STALL closed: beat ran 46031 ms late (first seen at 1000 ms) | open fct+triglog | in progress nothing | ui thread: blocked 229/230 samples waiting (UserRequest), cpu 40 ms
```

Blocked with almost no CPU is a wait, and the hunt is a lock or a call into something outside us; running with wall-matching CPU is work,
and the next step is an external trace (`dotnet-trace collect -p <pid>`) against code we will then know is hot. Sampling only happens during
an episode, and every call is guarded: a probe that threw while reporting a frozen interface would replace a symptom with a crash — the
watchdog's own poll handler stops monitoring when a poll throws, which is the one outcome worse than an unmeasured stall.

Two things learned by running this rather than reasoning about it. `ProcessThread` answers each property from a fresh query instead of one
snapshot, so asking for `WaitReason` a moment after the thread wakes throws ("only available if the ThreadState is Wait") — that is the
common case in a thread that flaps, not the exotic one, so the reason comes back `unknown` and the sample still counts. And `n/a` on its own
could not be told apart from a probe that quietly stopped listening, so the line now names which half failed: no thread id captured, or an id
this process's thread list does not contain. `TheProbeNamesTheThreadItIsStandingOn` checks those two halves separately for the same reason.

A second trap in the same API, found by a test failing on Windows and passing here: **the thread list a `Process` hands back is a snapshot of
the moment that object first read it**. Holding one Process to "avoid the cost" of fetching another makes every thread created afterwards
invisible to it — a worker started five seconds later simply is not in the list, and reports as "not in this process" forever. Measured on a
scratch program before changing anything: absent from the held Process, present after `Refresh()`, present in a fresh `GetCurrentProcess()`.
So each observation takes a fresh Process, reads state, reason and CPU inside it, and keeps nothing; the cost is microseconds once per 200 ms
of an episode, and `AThreadCreatedAfterTheFirstLookIsStillFound` is there to keep it that way. The symptoms of getting this wrong were exactly
the misleading kind: the id was correct, the thread was alive and readable, and the only wrong answer came from the reader's own stale copy.

The threshold that opens an episode is settable — `PerfStallMs` in `AppData\config\settings.txt`, default 1000 ms, floor 100 ms — because one second is the
right number for reporting and the wrong one for measuring. A first run with every surface open showed beat delays of 90 to 235 ms with
nothing of ours running: the same event as a multi-second stall at a fifth of the size, and easiest to catch while it is small. Nobody should
run a raid at 150 ms — the log would fill with passes no player felt — but a measurement session that leaves the default will never see the
probe fire except on a freeze, and freezes are the rare half of this.

### What a clean run looks like, because it is worth knowing one

A 21 minute replay soak with `PerfStallMs=200` — FCT, trigger log, meter and four overlays open, the same raid log played twice — produced
**no stall at all after the first eight seconds**: across 63 heartbeats the worst beat delay outside startup was 47 ms. The three episodes on
record are all launch, and all of them read *running* with CPU close to their wall time, so they are our own slowness rather than a wait:
`app.mainwindow` 3856 ms, `app.firstshow` 1749 ms, `app.voices` 1089 ms (enumerating voices), `app.triggerdb` 227 ms. Every instrumented pass
held its shape while the raid streamed — `fct.paint` ~1.3 ms worst 30 ms, `trig.timerTick` ~36 a second at 0.1 ms, `ui.fightTable` once a second
at ~1 ms (worst 18 ms) — and `trig.logReset` shows up nine to twenty times a window instead of the two it managed
when the log held 605 entries, at 0 ms: the frequency question from an earlier soak, answered by a counter that was already there.

What that run did produce is a memory figure, and it is the one to compare against a machine that really does freeze. The replay allocated
66.9 GB, ran at 619–869 MB/s while parsing with 2,218 gen0 collections in its first minute, then **kept 2.7 GB of heap resident for the
remaining nineteen minutes** at a flat 3.4 GB working set — not climbing, not released. A process sitting at 3.4 GB next to EverQuest on a 16 GB
machine is page-file territory, and paging reads as a freeze with almost no CPU: the shape the probe calls *blocked*, caused by neither a lock
nor our code. Two limits on this measurement, stated plainly: the machine had memory to spare, and the runtime's pause counter (22.4 s over the
run, 10.7 s of it inside minute one, up to 281 ms in a single second) counted collector work that never once stopped our thread — and the `paused`
reading printed in that transcript was the runtime's `PauseTimePercentage`, which this note went on to describe wrongly for a while as "a lifetime share that
dilutes toward zero". It is not a lifetime share; see below, where the figure is built here instead. Read it against the allocation rate either way, never as
this window's cost. GC is cleared for this
run and no other. One correction, since this paragraph sent somebody chasing a flag that does not exist: there is no `EventSource` in this
codebase — `PerfCounters` lives in-process and its only outlet is the heartbeat line — so `-c System.Runtime,EQLogParser` collects nothing of
ours. The pairing that works is the app's own log beside `dotnet-counters -c System.Runtime --sample-interval 1`, matched by wall clock; and the
heartbeat now carries the collector's pause itself, which is half of why that pairing was needed.

Speech is on that timeline now too, because two players on one build can disagree about nearly everything except the log they leave behind, and
audio was the widest gap of that kind. The engine is chosen at startup from which packs happen to exist, so one machine synthesizes through a local
neural model and the next through SAPI or WinRT while both print nothing about it — the announcement used to fire only for the neural engines, on
the reasoning that the boring default needs no introduction. It names all three now, since "kokoro" and silence were two different facts wearing
the same clothes. `audio.synth` and `audio.file` arrive through a hook rather than a span: `EQLogParser.Audio` references no counter code and stays
that way, so durations cross the boundary as numbers (`AudioManager.PerfSink` into `PerfCounters.Record`, which is `End` without the timestamps —
a span cannot straddle an `await` inside another assembly).

### The freeze the watchdog cannot see, and now can

The run that was meant to settle the memory question settled something sharper first: the app watched a two second freeze go by and reported
nothing. An outside collector (`dotnet-counters`, 1 s samples, `dotnet.gc.pause.time`) shows **2,292 ms of pause inside the single second at
19:03:47** — while the heartbeat covering that window reads `beat delay max 0 ms`, `stalls 3` (all three from launch), `gc2 +1`, and heap
falling 2,784 MB → 1,998 MB. A second one, 699 ms at 19:06:34, is the `dotnet-gcdump` suspending the process to walk the heap.

The reason is structural rather than a bug in the watchdog: a full collection stops *every* managed thread, the pool timer that posts beats
included. The beat that gets posted after the world resumes is punctual, so "beat delay" measures what the UI thread owed the interface and
never whether the process existed during the gap. Anything that suspends a whole process — a blocking collection, a gcdump or trace start/stop,
a debugger break — is invisible to a probe living inside it. Two numbers close that hole:

- **`stopped N ms`** on every heartbeat: the window's own stop time, from `GC.GetTotalPauseDuration()`, which is cumulative and keeps no
  appointment, so subtracting two readings recovers pauses no beat ever witnessed.
- **`paused N% since start`** on the same line, which used to be `GC.GetGCMemoryInfo().PauseTimePercentage` and is now the sum of those same window figures
  over the time they cover. The runtime's number was removed because two measurements of one thing disagreed on one line: over a 69 minute run it held exactly
  `5.15%` across three heartbeats spanning 46 quiet minutes, one of which reports 762 ms of collector stop inside it — it is a snapshot as of whichever
  collection the runtime last reported rather than a running total, so between collections it is not a stale measurement, it is no measurement at all — and
  where it did speak it read `0.32%` against 35.5 s of pause across 4,166 s of life, which is 0.85% by the arithmetic of its own neighbouring column: a factor
  of 2.7 between two numbers one clause apart. A reader cannot use a figure beside another that contradicts it, and the one we add up cannot contradict itself.
  Both halves are summed outside the "is a surface open" test — beats are suppressed while the overlays are closed but the monitor's window keeps closing,
  so those minutes enter numerator and denominator together — and neither is zeroed when a log loads, because a ratio that rewinds on every file open is how an
  hour of collector work disappears from its own log.

  **And the replacement was wrong on its first run, by exactly 1000.** The next capture came back `paused 3956.6% since start`, easing down to `345.36%` over
  twelve minutes: the numerator was milliseconds and the denominator was the window length in seconds — a value already sitting in scope for the rest of that
  line — so it computed `pauseMs / seconds`. Summing that same file's figures gives 2,421 ms stopped over 720 s = **0.336%**, which is printed/1000 to four
  digits. The shape is the lesson rather than the arithmetic: a percentage that decays smoothly as uptime grows is a denominator in the wrong unit, not a
  collector calming down, and few things look more believable in a log than a number falling monotonically. The span is a `TimeSpan` now, so the wrong unit does
  not compile; `PausePercent` deliberately does not clamp at 100, because a figure above 100% is the evidence and clamping throws it away; and
  `TheLifetimeSpanAdvancesWithRealTime` asserts that the span gains roughly as much time as actually passed — which needs the beat length to be a parameter of
  `Start`, since a test cannot wait twenty seconds for a window to close.
- **`ui.worldstop`** plus a `STOP-THE-WORLD …` warning: the watchdog now times the interval between its *own* callbacks. A gap of
  `GapReportMs` (500 ms, two and a half polls) counts and maxes into that row; once it reaches the stall threshold it writes its own line,
  attributed by `PerfGap.Classify` against the collector's cumulative pause — "collector (gen2, the full collection), it held every thread for
  2200 of the 2292 ms", or "no collection in that gap: stopped from outside the runtime (profiler or gcdump, power management, or no CPU for
  anybody)". That second wording matters: a measurement freeze caused by our own tools must not arrive looking like our code.

**…except it did, in that sentence's own favour.** The counters can be late. A blocking collection publishes its count and its pause *after* the runtime lets
the other threads run again, so a gap classified inside that window reads "no collection happened": at **11:53:34** a `gc.tidy log loaded` that had held every
thread for **794 ms** printed in the same millisecond as `STOP-THE-WORLD 969 ms … profiler or gcdump, power management, or no CPU for anybody`. No arithmetic
over cumulative counters could have caught that — but the code calling `GC.Collect` knows what it is doing, so it writes it down: `PerfGc.NoteStopStart(reason)`
before the call and `NoteStopEnd()` after (`IntentionalStop`, readable *while* the collection runs, which is exactly when a gap gets classified around it), and
`UiBeatMonitor` asks only whether that stop overlaps the gap before handing it to `PerfGap.Classify`, which puts ours ahead of the counters: "our own gc.tidy
(log loaded) held every thread in that gap: 794 of the 969 ms, timed by the code that asked for it; the pause counter agrees" — or "the counters had not caught
up with it", which is the case that shipped blameless before. A stop still in flight says "still running as this line was written" rather than quoting a
duration nobody measured. An unexplained gap keeps its external suspects and now names its own weakness too ("…or a collection whose count the runtime had not
published when this line was read"), because somebody hunting a profiler deserves to know which way the doubt falls.

What the heap dump says, since 2 GB of it had been unexplained for three soaks: `dotnet-gcdump report` on a 320 MB capture (heap ~2.06 GB, 9.36 M
objects sampled) puts it in the parsed fight records — `DamageRecord` 2.44 M instances / 186 MB, `HealRecord` 1.92 M / 117 MB, `IAction[]` 63 MB
across 37,641 arrays, `SpellData[]` 44 MB, `ReceivedSpell` 679 k / 31 MB, `SpellCast` 551 k / 25 MB. About 484 MB of the sample is
`EQLogParser.Core` types; **WPF and Syncfusion together are under 25 MB**, and nothing audio-related appears at all. So the resident gigabytes are
the data the tabs draw, proportional to the log, not a UI leak — which also explains the pause: a full collection's cost scales with that heap, and
launch pays for it in advance (minute one of this run spent **11.2 s** paused while the heap went 624 → 2,997 MB, ~280 ms at a time — a real share
of a 54 second load). The levers are allocating less during the parse or telling the collector not to compact mid-replay
(`GCSettings.LatencyMode = SustainedLowLatency`); neither is measured, so neither is done.

The trigger path, from the same run at **608 active triggers** with logsim feeding ~2,350 lines/s: `trig.line` held `avg 0.10 ms` every single
window for eleven minutes, worst 6–21 ms, with `trig.tests` at 19–30 M patterns per 20 s window — roughly 1.2 M pattern tests a second, about a
quarter of one core. Linear in the set size, that is ~0.7 ms per line at their 4,227: a few percent of a core in a live raid at 60 lines/s, but
**seven times the parse cost when a whole file is loaded or replayed**. That is a load-time and memory story, not a dropped-frame story, which is
worth saying to a player who reports freezes while *opening* a log versus during a fight.

And audio, on `Using windows-tts`: `audio.synth` at n=5–18 a window, **avg 0.7–2.9 ms, worst 9.5 ms**, no warm-up spike anywhere (the startup
voice enumeration is the 213 ms figure, not a speak call), and no `audio.file` calls at all in eleven minutes. On this machine SAPI synthesis is
not a freeze source. It is still unmeasured under a set that speaks as often as the imported one, so the hypothesis is unfalsified rather than dead.

One instrument was lying, found only because two numbers disagreed: `trig.active` printed 608 on the first heartbeat and then **0 for ten minutes**
in windows whose `trig.tests ÷ trig.line` was exactly 608. There is one `TriggerProcessor` per watched log plus the tester, and a level written by
whichever of them last rebuilt its set prints an idle character's zero over the set that is actually running. It sums across live instances now and
takes each share back on dispose.

One of those sentences shipped broken, in a way worth recording because it is not really about pluralization. `UiBeatMonitor.ClassifyGap` built
`"{collections} collection(s) account for only …"` while its test asserted `"3 collections"`, and both went in the same commit — so what failed was
the assertion nobody on that machine could run. Wording tests for app-side code live in `EQLogParser.Wpf.Test`, which *builds* everywhere but whose
tests need Windows; "the suite passes" meant 1,168 tests in a different assembly and said nothing about these.

The message now reads as a sentence ("1 collection accounts", "3 collections account"), which is what the test wanted. The durable fix is where it
lives: the classifier moved to `EQLogParser.Core/src/perf/PerfGap.cs` as `PerfGap.Classify`, taking its four numbers as arguments exactly as before,
and its wording tests to `EQLogParser.Test/src/perf/PerfGapTest.cs` — ten of them, in 25 ms, on every machine that runs the suite rather than on one with
a Windows desktop runtime. It was worth more than the typo fix: the half-the-gap threshold is now pinned so changing that ratio has to be a decision,
and two tests hold the floor on negative counter deltas, one of them on the branch whose sentence contains a hyphen anyway.

The rule this leaves behind: **pure classifiers and formatters go in Core, where they can be tested; only things that need a dispatcher, a window or
a thread probe belong to the Windows-only assembly.** A test that cannot run on the machine making the change is not a guard rail — it is the reason
the change ships.

### Asking the collector for memory back at chosen moments

No GC setting was changed, because measuring them found nothing left to change. A 10,015,348-line replay with records retained the way the app
retains them (4.80 M damage events → 2.53 M distinct, 2.67 M heals → 627 k distinct, ~600 MB peak), watched by a thread pumping at 16 ms and
reporting every wake that arrived late:

| configuration | wall | gen0/gen1/gen2 | peak RSS | wakes ≥25 ms late |
|---|---|---|---|---|
| shipping default (workstation) | 7,942 ms | 828 / 202 / 6 | 611 MB | none |
| `Concurrent=false` (runtime then reports latency mode `Batch`) | 8,303 ms | 776 / 210 / 7 | 589 MB | 2 (137, 89 ms) |
| `Concurrent=true` | 7,779 ms | 827 / 200 / 6 | 618 MB | none |
| Server GC | 8,083 ms | 98 / 38 / 13 | **916 MB** | none |
| `SustainedLowLatency` | 7,983 ms | 828 / 204 / 6 | 610 MB | none |

The default *is* concurrent background collection — the first and third rows are the same run twice over — and turning concurrency off is the
only thing in the matrix that made the pump arrive late. "More aggressive" has no headroom either: 828 gen0 collections in 7.9 s is one every
9.6 ms and not one of them was noticeable. Server GC buys the same interface for 50% more memory, so it does not ship.

What is left is *when*, which the runtime cannot know and this program can: `GcTidyUp.Request` asks for one full, compacting collection at three
moments a player is not asking the interface for anything — a log file finished loading (`MainWindow.UpdateLoadingProgress`), the fight list was
cleared (`FightTable.ClearClick`, where the record cache and every parsed event drop together), and a stats rebuild finished (all three builders,
after their locks rather than inside them). Two rules make it safe. **A request never collects on the caller's thread**: it waits 1.5 s (`SettleMs`)
so the trigger's own layout finishes, then collects on a pool thread. **And it is rate-limited** to one per `MinIntervalMs` (60 s), counted in
`SuppressedCount`, because flapping a time filter five times is ordinary and five forced compactions would be worse than none — which is also why
there is no timer here, since every forced collection promotes young survivors and a storm makes the heap bigger and the next unasked-for
collection longer.

The cost, measured rather than assumed: an aggressive compacting pass on a ~4.8 GB heap stopped every thread for **3,065 ms**, which is roughly
what a raid-session heap will pay, in a moment chosen to hurt nobody. That the pass reclaims anything was shown in a quiet process — 100 MB of
released large arrays taking the heap from 30 MB to 5 — and cannot be asserted inside a test run, where `HeapSizeBytes` is dominated by unrelated
live data (a suite at 4.8 GB swallowed the signal entirely). Every tidy writes one line so the claim stays checkable:
`gc.tidy log loaded: heap 2901 to 742 MB, working set 3120 to 905 MB, gen2 +1, stopped 812 ms, wall 813 ms`.

A request expires, because the settle delay makes it a promise about a moment that can go away. `Reset()` bumps an epoch, and a task still settling when that
happens stands down instead of collecting: the slot was handed to whoever reset, so collecting would stop the world on behalf of state nobody is waiting for any
more — and it would also take back a slot that belongs to somebody else now. Proven rather than assumed: with the guard removed, a request retired by `Reset()`
still ran its collection (two collections where one was asked for). This is what made `GcTidyUpTest` order-dependent — a stats builder finishing its work asks
for a tidy 1.5 s hence, and in a suite that runs sequentially that timer goes off inside whichever test comes next.

Same file, the tests' own waits: they used to be one condition, `TidyCount == n && Idle`, which cannot distinguish "this machine is slow at compacting" from
"the tidy finished and never released its slot" — the second is a real bug (every later request refused forever), the first is arithmetic, since one pass costs
**3,065 ms** on a 4.8 GB heap and a test host's heap is whatever the rest of the suite has been parsing. Five seconds was measured insufficient on Windows for
the two throttle tests while passing on Linux, so each half now waits and fails separately with a generous budget, quoting counts, whether the slot is claimed
and how big the heap was.

Two details that are decisions rather than accidents. There is deliberately **no "is a fight active" guard**: a loaded file leaves its last fight
marked active until further log lines arrive to expire it, so such a guard would quietly veto the most useful trigger, and the interval bounds what
the stats trigger can cost anyway. And `LargeObjectHeapCompactionMode.CompactOnce` is set on every pass because the runtime clears it afterwards —
leaving compaction permanently on would slow the collections the runtime picks for itself, which a test now refuses to let happen silently.

### What a loaded raid costs in memory

A loaded capture keeps its records, so a raid night that restates the same swing ten thousand times can hold ten thousand objects saying one thing.
Two caches have always existed against that — `FightManager._damageCache` for damage, `HealingLineParser._healCache` for heals — and both were
`Dictionary<Record, Record>` keyed to themselves. Getting this right took measuring the wrong beliefs out of them rather than adding a third cache.
The two pieces are `RepeatStore<T>` (the shared instances) and `RepeatFilter` (a Bloom filter over value hashes), both in
`EQLogParser.Utils/src/`, named next to `StringCache` because they solve the same problem one level up.

Measured on one player's capture — `eqlog_Kizant_xegony.txt`, 6,066,108 lines, 3,359,001 damage events and 461,467 heals, 26 Apr → 6 May 2026 —
driven through the real pipeline (`DamageLineParser`/`HealingLineParser` and the real `FightManager`, so the `Attacker` rewrite happens too), keeping
every damage record reachable the way grids and fight blocks do, then a full blocking collection with LOH compaction and the heap that survives read
back. One process per variant; the variants differ only in the cache:

| what the cache does | damage entries held | heal entries held | heap after GC | RSS | parse wall |
|---|---|---|---|---|---|
| today: `Dictionary<Record, Record>`, every distinct value entered | 1,612,020 | 380,315 | 622 MB | ~720 MB | 14.5 s |
| **A**: `HashSet<Record>` — what a self-keyed dictionary already is | 1,612,020 | 380,315 | **600 MB** | ~713 MB | 14.6 s |
| **B**: plus the gate — a value takes an entry only once it has been seen before | 261,947 | 81,213 | **545 MB** | ~627 MB | 14.8 s |

A is worth 22 MB and B another 55, for 77 MB of heap and roughly 95 MB of working set on a night like this one, bought with 6.25 MB of bits
(`ExpectedDamageOffers` 4 M, `ExpectedHealOffers` 1 M) and about 2% of parse wall. This capture is *not* the 10,015,348-line file the GC matrix above was
measured on — that one is no longer on disk — so its percentages are not comparable with the 47.3% damage dedup rate quoted there; it is a smaller
capture (3.36 M damage events against 4.80 M) and every number in this section belongs to it alone.

**The belief A killed: a shared record does not save a list slot.** The price of dropping a duplicate was being quoted as "record + list slot, ~104 B".
The store keeps an entry for every event whether or not the record behind it is shared, so the 40 B of list slot is a floor no cache can touch, and what
a deduped repeat actually reclaims is the object: measured 64 B. That is also why deleting the caches outright was never on the table — but it is worth
saying how narrow that was.

**The belief B killed: that the entries were the cheap half.** `FightManager`'s comment claimed "about a hundred MB of entries against the two hundred
the dropped records save". Priced properly and measured, an entry costs about as much as the object it protects — 50 B in a self-keyed dictionary, 36 B
in a `HashSet`, with 22.8 B of `Dictionary` internals measured per entry against 6.9 B of bucket array for a set — and the cache's *hit rate* decides
whether it pays. Of 1,609,080 distinct damage records in this capture, **1,348,947 (83.8%) are never restated**: their entries answer no lookup ever
again while the records stay in the store regardless. Ungated, all that machinery beat *no cache at all* by 16 MB. Declining to remember a value until it
repeats is where the memory actually was.

Why a Bloom filter can sit next to a cache that must not merge events: it is wrong in only one direction, and neither direction touches a record's
content. A false "seen" buys an entry nobody needed, which is exactly what the cache did before this class existed; a false "new" would cost one shared
instance for a repeat — while its bits stand it does not happen, which `RepeatFilterTest.NeverForgetsWhatItMarked` pins over 10k values. Nothing reads
a value out of the filter, and no answer here changes what a grid totals: sharing is invisible because the records are equal by value, and
`TryGet_NeverHandsOutADifferentValue` holds that line — whatever comes back is the caller's own instance or one equal to it.

Two decisions inside it, both about not growing. When a window of sightings runs out the filter **begins again instead of doubling**: memory is what it
exists to protect, and forgetting costs at most one shared instance per value that happens to straddle the reset. Clearing cached data clears the history
with it (`ClearCaches` → `RepeatStore.Clear`) rather than leaving stale bits that would make every sighting in the next session look like a repeat.
And the window is counted in *sightings*, not bits — ten bits and four probes reach the design's ~1-in-a-thousand false-positive rate exactly at the end
of it, which is where the reset is set; counting bits instead would run it deep past saturation, where it says "seen" to everything and the cache
silently degrades into the ungated one.

**The behavior change players can see**: sharing now starts on a value's *third* sighting, not its second. The first two occurrences of a heal are
distinct objects — one extra object per distinct record, which is what the 84% of never-restated values save many times over. The old test asserted
`AreSame` on the second line and was renamed to `Process_RepeatedHeal_SharesOneRecordInstanceFromTheThirdSighting` rather than quietly loosened.
The heal cache and its filter are static, so that test also became order-dependent in a way nothing else in the suite was: whichever class reached
`HealingLineParser` first decided whether a heal looked like a repeat. `LineParsersTest` now calls `ClearCaches()` in setup and cleanup; any new test
that asserts on shared instances has to do the same, or it passes only in its own position in the run order.

**Accepted wart, unfixed**: `HandleDamageProcessed` rewrites `record.Attacker` to `Labels.Unk` about a hundred lines *after* the record was offered to
the cache (the unknown-spell fix), so for those events the entry just taken can never be found by hash again, and every earlier event sharing the
instance reads back `Unk`. It is visible in the numbers above: the ungated runs hold 1,612,020 damage entries where this capture has 1,609,080 distinct
damage values, so about 2,900 entries are ones no lookup can reach — small here, and it grows with however many unknown-spell events a night carries.
It predates this change and behaves identically under it; un-tangling it means settling the record before offering it, which would change what earlier
unknown-spell rows display. Not a memory decision — whoever takes it up should decide it as a display one.

What did *not* change, deliberately: the name ids stay. Converting `HitRecord`'s `int` ids to pre-hashed values, or freezing records so they cannot be
rewritten after caching, measured larger than A+B together, but `StringCache.GetOrAdd` title-cases the spelling every view and export shows — a damage
row would keep its raw `foob's sword` where it used to read `Foob's Sword`. That is a rename of other people's data to save bytes, and it needs its own
decision. And the interning inside `GetCachedDamageRecord` is not part of the memory story at all: it is a spelling service that happens to live in the
miss path, which is why the lookup and the offer are two calls rather than one helper that would settle names for a value already held.

#### The eight bytes that were in every damage record for nothing

The cache decides how many record objects a night holds; the record's own fields decide what each one costs, and those had never been measured against
what the log can actually write. An object is 16 B of header plus its fields rounded up to a multiple of 8, so a field is worth either nothing or 8 B
depending on which side of a boundary the payload sits on — which is why this was measured field by field (`local/memprobe`) instead of added up:

| `DamageRecord` | size |
|---|---|
| as it stood: `Total`, `OverTotal`, mask, five name ids, `AttackerIsSpell` (35 B of payload) | **56 B** |
| `OverTotal` moved to `HealRecord` (31 B payload) | **48 B** |
| and the defender's owner deleted too (27 B payload) | **48 B** — free, bought by the first one |

Both are gone. `OverTotal` is the amount a line *asked* for, which is an over-heal: `HealingLineParser` was its only writer in the codebase, and `FctManager`
carried a comment saying damage records never carry it. It now lives on `HealRecord`, where the compiler enforces that. Dropping it from
`DamageRecord.Equals`/`GetHashCode` cannot merge two events that were distinct before, because the component being dropped was always 0 — of the two fields
removed, this is the one whose deletion is provably invisible to the dedup store.

The defender's owner was simply dead weight: written by the parser, title-cased in `GetCachedDamageRecord`, compared in `Equals`, and read by nothing — every
owner that gets displayed is an *attacker* owner (`RecordGroupCollections`' pet grouping, `DamageStatsBuilder`'s "SoAndSo +Pets", `HitLogViewer`'s own check).
Deleting it does merge records in one narrow case: the old key held a value derived from the defender's name *and* from whether `PlayerRegistry` had verified
that player yet, so the same pet defender could carry a null owner early in a session and a real one later, and those were two cache entries. They are one now,
which can only reduce what is retained, and the field nobody read is not in either record's display. **What stays is the parser's `CheckOwner(defender, out _)`
call**: it registers the pet with its player on the way out, so the side effect survives even though the answer no longer has anywhere to live — delete that
line and pet mapping quietly stops learning from damage taken.

The reason neither field could be moved a decade ago is `StatsUtil.UpdateStats(PlayerSubStats, HitRecord, …)`: one routine counting both kinds of event through
their common base, which is why the base had to hold a field only heals write. It is two routines now — `UpdateDamageStats` and `UpdateHealStats` — each
holding only what its own lines can produce. The damage switch kept every damage label and lost `Hot`/`Heal`, which is safe rather than tidy: the type of a
damage record comes from `CreateDamageRecord`'s callers, and that vocabulary is Ds, Bane, Melee, Dd, Dot, Proc, OtherDmg, Absorb, Block, Dodge, Miss,
Parry, Invulnerable and Riposte (`GetTypeFromSpell` only ever returns the type it was handed, `Bane` or `Proc`), so a heal label cannot reach it — do not put
those cases back. The heal half has no switch at all because `HealingLineParser` writes only `Heal` and `HoT`. And `LineModifiersParser.UpdateStats` takes
`(short modifiersMask, uint total, …)` rather than a record, since that pair is the whole of a hit its tallies depend on; the `Type != Miss` guard it used to
read off the record moved to its callers, where it belongs — a riposte line arrives as a miss, and whoever makes that call decides its attacker scores nothing.
Removing the record parameter is also what leaves `HitRecord` free to be small: nothing in the codebase takes a `HitRecord` any more.

Splitting them surfaced two things worth writing down rather than "fixing" on the way through. `MaxPotentialHit` for a heal has always been landed +
asked-for — 9409 landed of an 11000 ask makes it 20409, which counts the landed part twice — and it turns out nothing reads `MaxPotentialHit` at all: the
summary and breakdown tables print `Potential`, which is computed separately as `Total + Extra`. It is left exactly as it was and pinned that way in
`HitStatSplitTest`, because a number nobody sees is not worth a behaviour change on a memory commit; whoever wants it printed should decide what it means
first. The other is that the damage half's `BestSecTemp` accumulation runs for heals too — no heal caller passes `newFrame`, so no heal ever flushed a best
second, but the running total kept filling — and the heal routine keeps doing that, likewise pinned.

The one visible consequence is in `HitLogViewer`: its grid row accumulates events while grouping is on, and the "Over Healed" column read the inherited field,
so `HitLogRow` carries `OverTotal` of its own now and heals feed it. Damage rows show zero there exactly as they always did.

On this capture that is 8 B off each of ~2.38 M surviving damage records (~19 MB) and off each of the 4.52 M events the parser allocates (~36 MB never
allocated). `HealRecord` was unchanged at 48 B — it needs `OverTotal`, and the trimming was never going to even out the two record types, it was about not
paying for a field in the type that has none. That paragraph's "next step down" was taken: the label went into a byte and both records fell to 40 B, which is
the subsection below. Anything below *that* wants two-byte name ids, and that is the flat-array conversation in the section above, not a trimming job.

#### Sixteen words in one byte: what a record's label costs

A `HitRecord` said what kind of event it was with an `int` id into `StringCache` — four bytes to name one of sixteen words. Those sixteen are fourteen damage
labels (Melee, Direct Damage, DoT Tick, Proc, Bane Damage, Damage Shield, Other Damage, Absorb, Block, Dodge, Miss, Parry, Riposte, Invulnerable) and two heal
labels (Direct Heal, HoT Tick), and they are closed by construction rather than by hope: every `CreateDamageRecord` call site passes a `Labels` constant or a
variable assigned from one, `GetTypeFromSpell` returns the label it was handed or `Bane` or `Proc`, the miss branch maps its seven outcomes onto the last six,
and `HealingLineParser` picks `Heal` or `HoT` and nothing else. No line text reaches a record's label, so the word does not need an id space at all — it needs
an index into a table declared in code (`EQLogParser.Core/src/dao/model/HitLabel.cs`).

| | master | after the field trim | with the label in a byte |
|---|---|---|---|
| `HitRecord` (base) | 40 B | 32 B | 32 B |
| `DamageRecord` | 56 B | 48 B | **40 B** |
| `HealRecord` | 48 B | 48 B | **40 B** |
| `SpellCast`, `ReceivedSpell` | 48 B, 80 B | unchanged | unchanged — their payload never crossed a boundary |

`HealRecord` came down too, which the section above did not expect: with the label a byte, its payload sits at 23 B and lands inside the same step.

**A byte, not a short, and an enum only with the backing type written down.** Measured in `local/memprobe`, all at the same five ids plus the total, mask and
bool:

```
as shipped (27 B payload)           48.00 B
label id as a BYTE  (24)            40.00 B
label id as USHORT  (25)            48.00 B     the win, lost to one extra byte
enum : byte         (24)            40.00 B
enum, default int   (27)            48.00 B     an enum nobody gave a backing type
```

Sixteen values fit in a byte with 240 slots to spare, so the ceiling argument is not about the count — it is about *which* number is stored. A truncated
`StringCache` id would need the ceiling conversation (a long session interns hundreds of thousands of strings), and that is why `HitLabel` is its own table
rather than a narrowed id. `Labels` stays the single spelling source: the enum's words are the `Labels` constants, so `record.Type` hands back the same
interned literal it always did and every `record.Type == Labels.Melee` comparison — reference equality included — behaves as before.

Three rules keep the trade honest, all pinned in `HitLabelTest`:

- **`None` (0) reads back as null, not as a word.** Records that never set a label are not hypothetical, and while `Type` was an id they read null because
  `StringCache.GetName(0)` is null by design. A table that answered "Unknown" instead would be renaming events.
- **A word outside the table maps to `None` and is counted** (`HitLabels.Unmapped`), never guessed at. Folding `"direct damage"` into `Direct Damage` would
  move those events into a column of every view silently; matching is ordinal, so casing is a different word. The counter is the only thing that would ever
  notice, and a real session should leave it at zero.
- **The vocabulary is asserted at its size**, in the spirit of `EveryLeanWordIsThreeWordsNoMore`: sixteen plus `None`, each holding its expected `Labels`
  constant, no two sharing a word. A new label arrives with an enum member, a table pair, and the parser work that produces it — it does not get smuggled in
  as an extra line in an enum nobody re-reads.

The existing `DamageLineParserTest` turned out to be the real coverage for gaps: 54 assertions compare `record.Type` against `Labels` across eleven of these
words, so a word missing from the table reads back null *there*, in a test about parsing, rather than in some viewer nobody runs.

A correction worth keeping, because it was written down wrong here for a while: **`Reverse DS` is not one of the sixteen.** It appears in the damage parser as
the word given to an *attacker*, and no `CreateDamageRecord` call has ever passed it as a type. Anything that treated it as a label value was reserving space
for a case that cannot occur.

Parsing got cheaper on the way, which was not the point but is measurable: heal records used to run `StringCache.GetOrAdd(type)` per event — capitalise, look
up, mint under a lock if new — for one of two words the table already holds; damage records used to take a `ConcurrentDictionary` lookup per event. Both are
now one ordinal dictionary hit with no locking and nothing new to intern.

**What this is worth on the big local capture** (`local/eqlog_Kizant_xegony-09-03-26.txt`, 997 MB, counted by pass rather than driven through the pipeline:
4,524,557 damage events and 3,288,435 heal lines). Retained figures need the distinct-instance assumption from the section above (measured there at 52.7% of
damage events restated-or-unique ⇒ ~2.38 M surviving damage objects), so treat them as arithmetic on measured sizes, not as a heap reading:

| against `origin/master` | resident after the log is loaded | allocation never made |
|---|---|---|
| damage records, 56 B → 40 B | **−36 MB** (127 → 91 MB of record objects; −17% of the 210 MB damage store once its per-timestamp containers are counted) | 69 MB |
| heal records, 48 B → 40 B | −6 MB to −25 MB, depending on how much sharing the repeat store achieves (its measured rate on a smaller capture was ~3/4 of lines restated) | 25 MB |
| both changes, this commit alone | −18 MB of damage objects (and the 8 B on heals) | 34 MB damage + 25 MB heal |

The allocation column matters more than it looks: that is gen0 traffic the parser stops producing, and gen0 frequency is what the stall hunting under
"Instrumenting the UI thread" is chasing. Remaining headroom in the record itself is now the four-byte name ids — 24 B of a 40 B object — which is the
flat-array conversation above rather than another trim.

### Reading a report

One real line, every twenty seconds while an instrumented window is open, taken from the end of a 69 minute session:

```
UI perf 20 s | open fct+tmr:CooldownOverlay-413c | beat delay max 0 ms, stalls 3 worst 2297 ms | render:auto |
gc0 1 gc1 0 gc2 0 stopped 5 ms | heap 202 MB ws 554 MB alloc 0.6 MB/s paused 0.32% since start |
trig.timerTick n=220 avg 0.1 max 0.4 ms, trig.timerBar n=45 avg 0.1 max 0.1 ms, fct.feed n=1492 avg 0 max 0 ms,
ui.fightTable n=20 avg 0 max 0 ms, trig.logBatch×1, trig.logEntry×1, fct.queue=0, fct.drop=567, fct.hits=0, trig.timerBars=11
```

Wrapped here for the page; it is one line in the log, and the surface name carries a window hash. Everything up to `render:auto` is the watchdog: which
windows were open, how late the last beat's callback actually was, how many stalls the process has ever detected, and whether WPF fell back to software
rendering. The `gc …` clause is the window's own collector figures — one gen0 in twenty seconds, nothing stopped, the quiet case — and `paused` is the one lifetime
figure on the line. Quoted exactly as that build wrote it: it reads 0.32% where summing this file's own `stopped` column gives 35.48 s over 4,166 s of life,
which is why that figure is computed here now (0.85% for this moment) and why both it and the stall counters carry a `since start` label this transcript
predates. The tail is `PerfCounters.FormatWindow()`: per-span counts and times for the window, then the gauges — and `fct.hits=0` with `fct.feed n=1492` is
worth pausing on, because it says the overlay pumped 1,492 times in twenty seconds and had nothing alive to draw.

`UI STALL (open)` and `UI STALL closed` bracket one episode; the closed line's number is how late the beat ran, which is a lower bound on
how long the thread was unavailable. Read them in order:

1. `in progress …` — a span named there is the occupant, and the slow-pass warning for that same name (`slow UI pass meter.loadstats:
   812 ms (threshold 150 ms)`) will usually already be in the log, because any span over `PerfJournal.SlowPassMs` (150 ms: nine frames at
   60 Hz) complains on its own account, throttled to once per five seconds per name. A stall that names `nothing` is not a named span: it is
   something outside the instrumented paths — the framework, another window nobody timed, or native code.
2. `ui thread: blocked …` / `running …` — which of the two it was, since "nothing of ours ran" reads both ways. Blocked sends you looking
   for a wait (a lock, the render thread, a driver); running sends you looking for a pass nobody timed, in a trace.
3. `open …` — if the stall's window is not in that list, it is not the cause.
4. The heartbeat's cost table — worst spans first, so one 40 ms pass outranks nine hundred 0.2 ms ones, since the question is who held the
   thread and not who is chattiest. Each kind gets its own room on the line: with eight surfaces open the spans alone filled ten rows and
   every counter fell off, including the drop counters — the line has to carry what arrived and what was discarded during it, not just what
   ran.
5. `gc2`, `stopped N ms` and `alloc` in the same window — a gen2 collection in the window of a stall is memory pressure and wants different code;
   a stall with no collections is somebody's loop. And if a freeze is reported by neither the stall lines nor `beat delay`, look for
   `ui.worldstop` / `STOP-THE-WORLD` before believing the run was clean: those are the ones where nothing inside the process could watch — unless the line
   says `our own gc.tidy`, which means we stopped it ourselves and the counters were merely late.
6. `render:software` — a machine that fell back to software rendering changes what every other number on the page means.

The session that followed, run specifically to have everything open at once — overlay, meter, text overlay, trigger log and four timer
overlays, with the live log file opened *while* two simulated clients were still writing to it — cleared the rest of the list, so none of it
gets re-examined. Four timer overlays cost 31.5 passes a second averaging 0.08 ms and the trigger grid's refresh 0.4 a second averaging
0.04 ms: a few milliseconds of UI time per second summed. The concurrent load allocated at up to 925 MB/s for 38 s and never delayed a beat
by more than 31 ms, so loading costs memory rather than responsiveness — heap 0.3 GB to 2.4 GB and flat from then on, no leak with every
surface open. After that the longest beat delay in nine and a half minutes was 31 ms. Whatever the reported freezes are, they are not any
pass this code measures, which is why the probe above is the next instrument rather than another span.

### Instrumenting something new

Register a handle in a field initializer, wrap the work in `Begin`/`End` inside a `finally`, and use `PerfCounters.Run` where a
try/finally would be noise. Two rules: a pass left marked running by an exception keeps naming itself in every stall line afterwards, so
the `finally` is not optional (`AFaultedPassStopsNamingItself` holds the seam honest); and add the name to this section, because a stall
line that points at a span nobody can find in the docs is a dead end.

An event raised by a builder arrives on that builder's thread, so anything measured there must not read a property of a UI element before
handing work to the dispatcher. Pass the element over and read it inside the callback; the reference crosses threads safely, its properties do
not.

When the thing being measured is a pipeline rather than a pass — several steps whose split decides which one to fix — use `PerfBreakdown`
rather than registering half a dozen spans by hand: it names them as a set, keeps them sequential, prints the phase breakdown with the sizes,
and hands its line back to the caller instead of only logging it, because a rule nobody can assert is a rule that quietly changes.

### Taking the instruments back out

This much measuring was borrowed to answer specific questions, and none of it is what the application is for. When the questions stop arriving, delete it as one
pass rather than letting it age into scenery — a heartbeat nobody reads still costs a `GC.GetGCMemoryInfo()` every twenty seconds and still teaches a new
reader that this is how the codebase writes logs.

What goes, in the order that leaves the tree compiling:

1. The instrumented spans: every `PerfCounters.Register` in a field initialiser and its `Begin`/`End`/`Record`/`Note`/`Gauge` calls in frame paths —
   `trig.*`, `meter.*`, `chart.*`, `fct.*`, `audio.synth`, `ui.fightTable`. These are the hundreds of lines, and they are the reason the frame code reads
   harder than it does.
2. `EQLogParser.Core/src/perf/PerfCounters.cs`, `PerfGc.cs`, `PerfGap.cs`, `PerfBreakdown.cs`, and `EQLogParser/src/ui/perf/UiBeatMonitor.cs` plus its thread
   probe, with the tests beside them (`PerfCountersTest`, `PerfGcTest`, `PerfGapTest`, `PerfBreakdownTest`, `PerfJournalTest`, `UiBeatMonitorTest` and the Wpf
   beat/gap tests), the `PerfJournal.Enabled` gate and the `PerfReport` read in `App`. The wording assertions go with the sentences they pin — do not keep them as
   a museum.
3. What stays: **`GcTidyUp` is not an instrument, it is a feature** (it hands gigabytes back at three chosen moments), so `PerfJournal` stays alive for its
   one line — move that to the ordinary logger if deleting `PerfJournal` with the rest. Likewise `FctFramePacer` paces the display and `FctIngest`'s
   drop counters answer "did the overlay have room", which is a product decision, not a measurement.
4. What stays as knowledge: this section, the heap dump, and the two sentences worth remembering from the whole exercise — that a stop-the-world stops the
   watchdog so `beat delay` cannot see it, and that allocation during a parse (not rendering) is what stops this interface.


Everything below was measured against `local/eqlog_Kizant_xegony-09-03-26.txt`: 10,015,348 lines, two full raid nights plus some solo
killing, which is about what a heavy player loads in one sitting. Instance sizes came from filling an array with half a million records and
reading `GC.GetTotalAllocatedBytes`; the counts came from feeding the file through `DamageLineParser` and `HealingLineParser`.

| | events produced | distinct by value | repeats |
|---|---|---|---|
| damage | 4,799,101 | 2,530,156 | 47.3% |
| heal | 2,670,820 | 626,506 | 76.5% |

The whole file uses **1,002 distinct names and types**. That is the number that decides what a record should look like: four or five name
fields per event, drawn from a thousand values, do not want to be references.

**Repeats.** Damage already collapses onto one instance per distinct value in `FightManager.GetCachedDamageRecord`, which keeps 2.27M objects
off the heap for that file. Heals had nothing of the sort — three quarters of a night's heal lines restate a heal already seen and each one
reached the store as its own object until `HealingLineParser` grew the same cache. A heal is worth caching harder than a damage event because
it repeats more often, and `HealRecord` is now equal by value in the same way `DamageRecord` is; both compare the stored ids, so hashing one
costs four integer compares instead of six string walks.

**Names are ids.** `HitRecord`, `HealRecord` and `DamageRecord` store a `StringCache` id per name (four bytes) where they used to store a
reference (eight), which is most of the difference below; the text they stand for was already shared by interning, so the pointers were the
only thing the record owned.

| | before | now |
|---|---|---|
| `DamageRecord` | 88 B | 56 B |
| `HealRecord` | 72 B | 48 B |

Together with the heal cache that is roughly **240 MB of a ~500 MB record footprint** for the file above, and about 2.7M fewer live objects
for the collector to walk — which matters more than the bytes, since pause length tracks the graph it has to mark.

Two invariants hold this up and both are load-bearing:

- **ids are assigned ordinally.** `StringCache.GetId` matches exactly, so ids partition names precisely the way references did — `"spell"` and
  `"Spell"` stay two symbols. Whatever normalization a caller wanted before still happens where it happened (`GetOrAdd` title-cases damage
  names in `GetCachedDamageRecord`, and heals call it themselves). What that interning now decides is *spelling*: a record hands back the text
  that was stored, so grids, exports and the log viewers see what they saw before. It is no longer what makes repeats cheap to keep — the value
  lookup above does that — which means the damage cache could one day be judged on its own merits without renaming anything.
- **ids are never reused and never cleared.** `StringCache.Clear()` still drops the dedup dictionary, but the id tables outlive it: records
  already stored carry ids, and letting names go mid-session would silently rename them. The cost is bounded by however many distinct names and
  spells the process ever sees, which on two raid nights is four digits.
- **resolving a name takes no lock.** A read used to be a field load and is now an index into state the parser thread appends to, on the UI thread,
  inside loops over millions of records — so `StringCache` keeps names in fixed pages rather than one growing list. Growth allocates a fresh page and
  reveals it by publishing a longer directory in one write; nothing is ever moved, so a reader either sees a name or sees nothing, never a list
  half-grown underneath it. Putting a `List<string>` here would have worked on paper through the happens-before of whoever published the record and
  would have been the worst kind of thing to rely on: correct until some later caller stores a record somewhere unsynchronized. The parse of the file
  above ran the same in nine seconds either way, so the pages cost nothing to keep.

**What is still on this road.** Records being small is step one; the layout step is storing a fight's hits in flat arrays instead of a
`List<IAction>` of heap objects, which would take the damage side from ~140 MB to something like 60-80 MB and let the `_damageCache` dictionary
(order of 100 MB of entries at 2.5M keys) go away entirely, since a repeat stored inline costs bytes rather than an object. It is blocked on one
thing worth knowing before anyone starts: `StatsUtil.UpdateStats(PlayerSubStats, HitRecord, …)` and `LineModifiersParser.UpdateStats` are shared
by damage *and* heals, so a struct-per-hit representation either forks that stat logic in two or materializes records again on every summary
rebuild — and the summary rebuilds every few seconds during a fight. The two ways past it are to hoist that logic onto the fields themselves (an
interface over "a hit", with rows implementing it by index rather than by identity) or to give heals their own row shape and let each keep its own
copy of the rules. Either is a branch of its own; measure with `stopped` and `gc2` before believing any claim about it.
### TimeRange: merging copies, adding adopts

Two rules live in this class and only one of them was ever written down. **`Add(List<TimeSegment>)` adopts**: the segments handed to it become part of
the list, and the next overlapping `Add` welds them *in place* (`BeginTime`/`EndTime` are public setters). **`new TimeRange(segments)` copies.** The two
look alike and behave differently, so the safe rule is: anything merging a range it does not own uses `Add(TimeRange)`, added here for exactly that
reason - it copies one segment at a time.

The leak was not hypothetical. Computing a group's uptime rewrote the players it summarised: `DamageSummary` collected `stats.Ranges.TimeSegments`
*references* into a per-group cache, then merged that cache into a throwaway range and asked for a total - which welded span objects still owned by live
players. Reproduced in minutes with today's API before any fix was written (a test that has since gone: three spans for A, B sitting on A's weld point,
sum the group, and A's own spans had changed length).

**Shipped: a group derives its seconds from its members.** `StatsUtil.MergeMemberRanges(members)` unions each member's `Ranges` into a fresh range by
copying, and `BuildGroupedPlayers` calls it on every pass. That deletes the cache (`GroupEntry.TimeSegments`), the collection that filled it, and the one
external removal in the tree:

```csharp
oldGroup.TimeSegments.RemoveAll(t => player.Ranges.TimeSegments.Contains(t));   // gone
```

That line was a hand-patched cache update, needed only because moving a player between groups takes an incremental path that skips
`InitializeGroupTracking`. It depended on `List.Contains`, which for `TimeSegment` is **reference** equality - the class declares `Equals(TimeSegment)` but
implements neither `IEquatable<TimeSegment>` nor `Object.Equals`/`GetHashCode` - so it worked only while the exact objects survived in the player's list.
Removals from a segment list happen inside `Add` (`RemoveRange`), and one reachable-by-inspection path replaces a player's ranges with value copies
(`DamageStatsBuilder`: `stats.Ranges = new TimeRange(range.TimeSegments)`); if that ran between collecting and removing, `Contains` would match nothing and
the old group would keep 100% of the departed player's uptime. Whether it does run in that order was never traced, because it no longer matters: there is
no snapshot to invalidate. Guards: `EQLogParser.Test/src/util/MergedTimeRangesTest.cs` - union covers every member, overlapping members count once (which is
why a group cannot sum its members' `TotalSeconds`), order independence, rebuild-twice stability, and the aliasing guard that fails if `MergeMemberRanges`
or `Add(TimeRange)` is switched back to adoption. Verified to bite: reintroducing adoption fails 3 of the 11.

**The tick rule, stated.** `GetTotal()` treats silence as activity: a bridge span is inserted for every pair closer than `Offset = 6`, which is why asking
for a total mutates the list. Because `TimeSegment.Total` is `End - Begin + 1`, the unit is off by one from wall clock: gap 1 means *no* silence, so the
rule fills **up to 5 seconds of actual dead air, inclusive at 5** (measured: gap 6 → 21 s reported; gap 7 → 15 s reported). Chains compound without limit -
five 3-second spans with 4 s between each report 31 s from 15 real ones. Constant since the initial import, no comment on it; neighbouring conventions are
`SpellCountBuilder.DmgOffset = 5` and `StatsUtil.SpecialOffset/DeathOffset = 15`.

**Open at the time: folding that rule into `Add` so reads stop writing.** Prototyped in `local/timerange-lab/FastRange.cs` (`bridgeGap`) and measured against
the live class over **95,389 accepted adds**: totals differed 0 *and* the welded lists differed 0 - applying a "within 6 s" closure at insert reaches exactly
the list that read-time bridging reaches, and shape stops depending on who asked first. `GetTotal()` is idempotent today (ask1 = ask2 over ~160k asks), so
there is no compounding bug to preserve. Two things I got wrong on the way and fixed by measurement: my first fuzz reported a 14% mismatch, which was the
harness feeding the two structures different inputs (an "inverted" span with `begin == end` is a legal one-second point that `Add` accepts); and I flagged
the per-span consumers as a risk when they are provably unaffected - `SpellCountBuilder` queries `[begin-30, end+15]` and `StatsUtil` ±15, and two spans
within 6 s already have overlapping query windows (6 <= 45, 6 <= 30), so welding them cannot change what those loops ask for.

### TimeRange: the tick rule lives in Add

**Shipped.** `Add` welds a span that lands within `Offset = 6` of an existing run - the search starts at "first span whose `EndTime` reaches `begin - Offset`",
the overlap test carries `+ Offset`, and the rightward swallow does too. `GetTotal()` is now a sum over the spans: no bridge list, no `Add` called from a
getter, no rewrite of the thing it was asked about. The invariant is *runs in a range are always more than `Offset` apart*, and `Add` is the only door -
the `List` overload, both constructors and `Add(TimeRange)` all come through it.

Proven three ways rather than by review, in `local/timerange-lab` (`-- equivB`, not part of the build): **162,447 accepted adds** across four input shapes
(in order, out of order, single-second points, endpoints touching, inverted spans, nulls), with the frozen `OldTimeRange` read after every add, the live
class, and an independent model computed from the added spans themselves:

```
  totals disagreed with old      : 0            (and the same counter covers the independent model: all three always agree)
  live list != old's post-read   : 0            <- the shape the rule-at-insert produces is exactly the shape a bridging read produced
  invariant broken in live       : 0            <- sorted, disjoint, every pair more than a tick apart
  live list != old's UNREAD list : 120,708      (74.3% of steps - the intended change: silences are welded from the start)
```

The consequence nobody can see in those numbers is the good one. `StatsUtil.UpdateMinMaxTimes` buys "the last N seconds" by walking spans and spending their
lengths, and it was spending *raw* lengths while `TotalSeconds` reported *bridged* ones - the two definitions of a second disagreeing inside one filter.
Synthetic hour of bursts, asked for the last 600 seconds:

| quiet stretches | window the old walk returned | what the meter reported for it | after |
|---|---|---|---|
| up to 4 s | started at 2,849 | **753 s** for a 600 s filter | starts at 3,001, **601 s** |
| up to 8 s | started at 2,704 | **894 s** | 2,884, **714 s** |
| up to 11 s | started at 2,511 | **1,080 s** | 2,701, **890 s** |

So the "last N seconds"/"first N seconds" filters now hand back what they were asked for instead of up to 80% more, and a filtered select's `TotalSeconds`
agrees with the number in its own name. Cost moved the same way as the search did: 40 names x 800 fight spans build **25.4 → 2.6 ms**, five totals
**6.4 → 0.1 ms**, same answer; 200,000 casting ticks three seconds apart now sit in **one span** from the first insert instead of waiting for a read.

**What changed meaning** (the honest list, since this is the part a diff cannot show):
- `TimeSegments` is welded *always*, so membership tests change side: `TimeCheck(line, ...)` - which decides whether a clicked line falls in a player's
  activity - now says yes inside a ≤ 5 s silence whether or not anybody asked for a number first. Previously the answer depended on call order, which is the
  hidden bug in the question.
- Two spec tests pinned the old behaviour and were replaced, not deleted: `GetTotalBridgesTheSegmentListAsASideEffectOfBeingRead` became
  `AddingWithinATickWeldsTheRunAtInsert` + `ReadingTheTotalLeavesTheListAlone`, and the brute-force run model in
  `ManyOutOfOrderAddsProduceExactlyTheBruteForceRuns` now folds short silences because `Add` does. Those were the only 2 failures in 1,209 tests when the rule
  moved; both were assertions about mechanism. `AssertSortedAndDisjoint` gained the gap rule, so every sweep in that file proves the invariant for free, and
  `ExactlyATickOfSilenceWeldsAndOneSecondMoreDoesNot` pins the boundary (6 welds, 7 does not).
- The lab's older comparison `-- equivA` is **red by design** now: 310,086 whole-list disagreements over 1.92M ops, **0** in totals, every one of them a pair
  of runs ≤ 6 s apart welded early. `-- equivB` is the authoritative comparison from here on.
- `StatsUtil.FilterTimeRange` copies the spans it clips instead of handing the caller's own `TimeSegment` objects to `Add`, which welds what it is given -
  adoption was already a leak and the rule made it reach further. Guarded by `MergedTimeRangesTest.FilteringARangeNeverEditsTheSource`.
- Hand-built lists are now a shape the class cannot produce. `TimeSegments` is public, so a caller appending directly (three test fixtures did) skips the
  rule and gets a total that misses the silences their own data implies; all three go through `Add` now. Production code never bypassed it - the only
  `.TimeSegments.Add(` in the tree was already `TimeRange.Add`.

**Auditing it on a real log**: `StatsUtil` logs one Debug line per windowed build, which is where before/after runs can be compared without opening two
windows - `Stats window: asked min 0 max 300 -> 12345 .. 12645; 7 spans, 300.0 counted seconds (raw extent 412)`. On the old build the counted seconds exceed
the requested maximum; on this one they land on it. Everything else in the two builds should be letter-identical.

## Timer overlays: how a bar gets taken away

A countdown row is drawn because something put it in `TimerOverlayWindow._timerList`, and three things are supposed to take it out: the row's own scheduled
removal, a line matching "end early", and the overlay dropping rows whose model says `IsRemoved`. Every stuck-bar complaint is one of those failing, and each
had a hole. The rules that decide when a row may leave live in **`EQLogParser.Core/src/control/util/TimerLifecycle.cs`** — on plain numbers rather than on a
window, because every one was found by measuring an arithmetic result:

```
dotnet test EQLogParser.Test/EQLogParser.Test.csproj --filter TimerLifecycle     # cross-platform, no STA, no window
```

- **A duration cannot be trusted.** The length comes from the trigger config or a `TS` capture through `DateUtil.SimpleTimeToSeconds`, which answers in **uint**
  seconds; the only test on the whole path was `> 0`. A capture of `999999999` makes the removal delay `(int)(seconds * 1000)` **saturate** to `int.MaxValue`
  without throwing, so the task whose entire job is deleting the bar sleeps 24.8 days and the row sits at `0:00` for the session — keeping the render loop (which
  stops only on an empty list) alive with it. Further out, a typed `1e12` wraps `begin + TPS*seconds` into the **past**: nothing is drawn (`remaining` is guarded
  `>= 0`), so the row can never be offered for removal, and in "standard time" mode its `DurationTicks` is the divisor that flattens every other bar. Durations are
  clamped where they enter (`MaxDurationSeconds`, a day) and all the arithmetic saturates instead of wrapping (`EndTicks`, `TPS`, and `DelayMs`, which can never be
  negative — `Task.Delay` throws on that, in a task nobody observes).
- **A row arrives with no future.** Start is fire-and-forget while Stop waits on the overlay's render semaphore, so nothing anywhere keeps Add before Stop, and a
  Stop only removes what is already in the list. A short countdown whose removal task wakes while its own row is still queued loses that way, and so does the
  "restart timer" option under a burst: three lines matching one trigger, message two cancelling message one's row before that row's insert lands. Either way the
  insert lands a row past its end — which produces no model, so nothing can ever take it away, and whatever the bar last showed stays up. `AcceptsRow` refuses it
  at the door. `ThreeMessagesAtOnceNeverLeaveARowBehind` enumerates all 720 service orders of `A1 S1 A2 S2 A3 S3`: **630 stranded a row as shipped, none do now.**
- **A row has no length at all** — the shape that reads as *"a timer appeared at 0:00 and never left"* for a two-minute spell, which is not a race at all. The
  display cannot draw "nothing": `DateUtil.FormatTicks` answers `00:00` for zero *and* for every negative value (`ARowWithNoLengthNeverBecomesAForeverZeroBar`
  pins those strings). One wrong frame in the normal mode; in **show reset** mode permanent, because the cooldown/idle text *is* `FormatTime(DurationTicks)` with
  `IsRemoved` false by design. The display keeps that behaviour (a real cooldown placeholder has no time left to show) and a lengthless row never reaches an
  overlay — from `TriggerProcessor` it is not offered at all, though the trigger still fires, speaks, logs and repeats exactly as before, none of which needed a
  painted bar. What to tell a player reporting this: fill in the trigger's duration.
- **Idle rows only aged when the whole overlay went quiet.** The shipped rule at the foot of the render loop clears greyed cooldown rows once *every* timer has
  finished and an idle timeout is configured, which in a raid — where something is always counting down — is never, so they accumulated all night. They now age
  per row from when that row stopped being live (`RetainIdleRow`; the later of the countdown and its reset). With no idle timeout configured, `IdleNever` keeps
  the shipped "idle forever".
- **A fault in the render loop used to be permanent.** `StartRenderingAsync` could fault with `_isRendering` still `true`, and a loop is started only
  `if (!_isRendering)` — so every later timer joined a list nothing painted: the overlay froze on its last frame, which for a bar caught at its end reads `0:00`.
  The wrapper now clears the flag in a `finally`, so the next timer re-arms it. `StartTimerAsync`'s catch unwinds only the loop *it* armed; clearing one other
  rows are still using was itself a way to freeze an overlay.

The backstop is `ReapForgottenRows`, called under the render lock from the long tick: it drops live rows whose end plus `ReapGraceTicks` (2 s) has passed, which
is how a stop lost against a closing dispatcher, or routed to a window the trigger no longer uses, gets cleaned up in seconds instead of never. The grace is what
keeps the design intact — an owner that stops its own timers always wins, and the `0:00` frame still gets painted. **Zero reaping is the healthy expectation**; a
row that has to be reclaimed had no owner.

**Do not "simplify" these away.** `RetainRow` ignoring the reset phase looks wrong and is not: a row in the *live* list long past its end has no owner, and letting
the cooldown display vouch for it is how the leak stays. `DelayMs` returning 0 rather than -1 for an empty timer is not a rounding choice — `Task.Delay` throws on
negative, in a task nobody observes. And the clamp belongs at creation, not in the display: every consumer of these numbers has to be unable to overflow, not only
the one showing a bar. Keep the short tick in the `else` of the long-tick test in `RenderTimerLoopAsync`: the long tick runs when `_tickCounter` wraps to zero, so
anything that can skip the reset while cooldown rows are up stops the overlay rebuilding bars at all.

## Timeline rows: what a saved layout keeps, and how a row comes back

A timeline layout stores `SpellOrder` and nothing else about visibility: the list is exactly the rows that were showing,
in the order they were showing. The ✕ at the head of a row drops the spell out of `_keyOrder`, so before the rows
dropdown there were no bytes anywhere distinguishing *"you turned this off three fights ago"* from *"this fight never
contained it"* — and no way back short of rebuilding the layout. The fix is deliberately **not** a hidden list in the
file. The universe of rows is the fight itself (`_spellRanges`) filtered by the same `IsOffered` test `Display()` draws
with, so the dropdown can show what is switched off as unchecked rows without any new state: **absent from `SpellOrder`
means off, never lost.** Old layouts load untouched. `HiddenSpells` is gone with it — `SaveLayout` filled it from
`_selfOnly.Keys` (the self-only lookup, which is about *which* messages a spell has, not whether you want its row) and
`ApplyLayout` never read a byte of it; System.Text.Json ignores the stale member in files already on disk.

Two rules worth keeping:

- **`_rowsSeeded`, not `Count == 0`, decides whether the order list still needs seeding.** `Display()` used to read an
  empty list as "nobody initialised me" and refill it with every spell alphabetically. With a switch-board dropdown
  that is a bug rather than a convenience: Unselect All empties the list on purpose, and the next redraw would have
  resurrected all of it. An empty row list is a choice.
- **A row ticked back on lands at the bottom and gets dragged home.** Nothing remembers the slot a row held before its
  ✕ took it out; remembering would mean storing hidden rows *with positions*, plus a version marker for files written
  without them — the file change this whole design exists to avoid. `TimelineRows.Apply` keeps that rule out of the
  UserControl so it can be asserted (`EQLogParser.Wpf.Test/src/ui/chart/TimelineRowsTest.cs`; Windows-only assembly,
  so it builds everywhere and runs there).

`Select All` / `Unselect All` in this dropdown are momentary buttons rather than the latching pair from
`UiElementUtil.PreviewSelectAllComboBox`, because that helper reads backwards once anything has been ticked: a click on
a *Select All* whose box is empty routes to `Toggle("Unselect All", false)`, which unchecks every row and lights the
*Unselect All* latch. In the class filters nobody noticed because `SharedControls` starts everything checked — two
clicks on their Select All simply clears the list. Here both action boxes stay empty (`e.Handled` on the container's
preview, so the inner checkbox never sees the click) and the closed combo carries the state instead: **"12 of 40 Rows"**,
which is also the only hint a player gets that a layout — or an old ✕ — is holding rows back. `ChatViewer`'s channel
pair already keeps its own local version for the same reason; the shared helper still serves the class lists, which are
all-on by design, and is left alone.

## Choosing a file: one engine, and what it swallows

Three implementations of the same three jobs lived here until recently: the Windows API Code Pack
(`CommonOpenFileDialog`, also doing folders with `IsFolderPicker`), `Microsoft.Win32.OpenFileDialog` /
`SaveFileDialog`, and a `System.Windows.Forms.FolderBrowserDialog` for the NAG database folder. The reason for
three was nobody's. Add up every chooser in the app — 19 of them — and the whole feature set is a filter, a
suggested filename, a title, a default extension and a starting folder: no multi-select, no custom places, no
shell items, nothing that one engine can do and another cannot. What three engines did buy was three ways to
fail, because each validated `InitialDirectory` in its own manner and only some of them threw.

They are one file now: `EQLogParser/src/ui/util/FileDialogUtil.cs`, the only place a dialog gets constructed
(`PickFile`, `PickFolder`, `SaveFile`), on the dialogs that ship inside .NET — `OpenFileDialog`, `SaveFileDialog`,
`OpenFolderDialog`. The folder dialog arriving in .NET 8 is what made consolidation possible at all; while the
replacement was missing, "use the Code Pack" had a reason. The package went out with it: reference gone, both dlls
out of `sign.cmd` and the installer, `[InstallDelete]` entries added so an upgraded install takes the stale copies
with it too.

What every call keeps, and why:

- **A starting directory is only used while it exists.** `ResolveDirectory` takes a folder or a full file path and
  answers with the folder, or null for "Windows decides". Saved paths go stale — drive unmounted, EQ folder
  renamed, `Logs` moved into OneDrive — and a chooser pointed at a folder that isn't there answers with whatever
  exception that shell call felt like throwing.
- **Nothing escapes to the click handler.** Two attempts: the caller's folder, then no folder at all so Windows
  opens wherever it keeps that kind of dialog. Then null, which all 19 callers already treat as "nothing
  happened". That is not politeness. `App.xaml.cs:73` handles `DispatcherUnhandledException` and sets `Handled`,
  so a *managed* exception at Export was never what closed the window — which means "the app vanished" describes a
  native fault, somewhere no `catch` reaches. Dropping the interop wrapper we do not control is the strongest fix
  available for a crash we cannot reproduce; an injected third-party shell extension is the other candidate, and it
  names itself in Event Viewer → Windows Logs → Application → Application Error, faulting module.
- **A chooser that failed twice says so out loud.** Cancel and failure both arrive as null, which is precisely what
  a caller wants and precisely what a player cannot tell apart. So `Show` ends with a `MessageWindow` — inside its
  own try, because an error path that can throw is not an error path — and the log carries the control's name:
  *"the spell count import chooser failed from 'D:\Logs'"*. Before this, "clicked Export, nothing happened" was the
  entire user experience of a broken chooser.
- **Owner windows are real, and one of them can throw.** Several sites called `ShowDialog()` with nobody to own the
  dialog, which is how a picker ends up behind the window it belongs to and un-clickable — so every call passes an
  owner now. Handing `CommonDialog.ShowDialog(owner)` a null owner is fine (it falls back to the active window), but
  handing it a *window whose handle does not exist yet* throws `InvalidOperationException`: `WindowInteropHelper.Handle`
  is zero until that window has been shown. A click inside a visible window cannot hit this, so it stays theoretical —
  which is exactly why the second attempt drops the owner along with the folder rather than throwing the same way twice.

Where a chooser *starts* is deliberately unchanged for saves: every save call passes null and so keeps Windows'
remembered location, exactly as each of them behaved before. The log picker is the exception that was asked for —
this character's log folder, then the newest recent file whose folder still exists, then whatever Windows wants to
show — and `OpenLogFile` no longer rethrows on the way past it, which was the only bare `throw;` in the app.

### The sound path cell answers a click, and keeps its text selectable

`TextSoundEditor` shows a sound file picked out of the file system in a read-only text box, and that box used to be a
dead display: the only way back to the chooser was to move the options dropdown off "Browse for Sound File" and pick
it again, because re-picking the item already on show raises no `SelectionChanged` at all. Clicking the path opens the
chooser now — which is one mouse button doing two jobs, so `EQLogParser/src/ui/util/ClickNotDrag.cs` decides between
them:

- **The excursion latches.** A selection drag that wanders off and happens to come back over its own press point is
  still a drag. Comparing the press point against the release point called that a click and opened a dialog on top of
  the text being selected.
- **The tail of a double-click never arms**, so the word-select gesture does not stack a second chooser behind the
  first one.
- **A release with no armed press answers false**: focus landing in the cell cannot summon a dialog.
- **The release handler listens with `handledEventsToo`.** The text box owns this click — caret and selection — and
  marks it handled when it is done, so a plain `+=` on the bubbling event risks never running at all, which a player
  experiences as "clicking the path does nothing". Listening after the control keeps its half of the gesture and
  still gets the chooser.
- The tolerance is six device-independent units rather than `SystemParameters.MinimumHorizontalDragDistance`, because
  that dial is the shell's user-tweakable `SM_CXDRAG` and a test written against it changes results when somebody
  adjusts their mouse. `EQLogParser.Wpf.Test/src/ui/util/ClickNotDragTest.cs` holds each of these, and needs Windows to
  run like the rest of that assembly.

The box stays read-only but carries a visible caret and an inactive selection highlight, so the path can still be
selected by drag, by Shift and the arrows, and copied while the chooser has focus. Where the chooser *starts* follows
the rule above: the folder of the path on screen, or nothing at all when no path is showing — "pick a different one"
usually means another file in this folder. The dropdown itself is untouched: re-picking "Browse for Sound File" still
does nothing, and the path is the door.

## Derived damage summary from captured facts

The derivation's first product seam. Selecting rows in the derived fight list runs the *existing* damage summary over
facts the projection captured, so two engines can be read side by side over one log. `EQLogParser.Core/src/parsing/
derive/FightSummarySource.cs` holds it: `FightFactIndex` (fight → the ordinals of its damage facts), `SummaryFightFor`
(ordinal list → an ordinary `Fight` whose `DamageBlocks` hold ordinary `DamageRecord`s, one `ActionGroup` per run of
facts sharing a second — `FightManager.AddAction`'s own grouping rule), and `FightSummarySource.Build` (selected rows →
fights plus the `TimeRange` window `FightTable` would have built for the same selection). Nothing in the summary knows
the derivation exists; it is handed `Fight` objects.

The index is filled **during** the projection, through the `FactOwnershipHandler` sink, because the answer it needs —
which end of the fact the row sits on — only exists inside `FightProjection`, at the second the timeline decided what
each name was. The flag is called `towardOwner`: true when the row's own name was the defender, which is literally the
comparison `FightProjection` splits `DamageToOwner` from `DamageByOwner` by. It is *not* "the attacker was player-side":
an unclassified name hitting a known NPC aims at that NPC too, and legacy puts such a record in `DamageBlocks` as well
(`FightManager.IsValidAttack` accepts it when the name looks like a player). Keeping one expression for both means an
index filled from the sink cannot drift from the row's number — a test checks that instead of trusting a comment.

### Two ways to produce nothing at all, both found by tests

Neither failure prints anything, which is why they are written down.

- `lastDamage` seeded with `double.NaN` and updated by `if (time > lastDamage)` never leaves NaN: `time > NaN` is
  false. The fight then has no end, `TimeRange.Add` drops a segment whose bounds do not compare, and
  `DamageStatsBuilder` divides by an activity window of nothing. A derived selection that renders no numbers, with no
  exception anywhere to say so. Both bounds need their own `double.IsNaN` test.
- A materialized record's `SubType` may never be null. The fact table stores "the line carried no modifier text" as
  `NoSubtype` (-1) and plain melee is the ordinary case of that; but `StatsUtil.UpdateDamageStats` looks the subtype up
  in a `ConcurrentDictionary`, which throws on a null key — inside `DamageStatsBuilder`'s own catch, which logs and
  carries on. So a null there is an empty board, not a rougher one. `FightSummarySource.SubTypeOf` substitutes the type
  word: identical activity window, one breakdown row per kind instead of one per modifier message.

So a materialization test must not stop at "the records match the facts". Assert the fight's time bounds are real, and
run the result through the actual `DamageStatsBuilder` — that is where both traps surfaced.

### What the two boards actually say, measured (`mini-data/derive/mini-fight.txt`)

All six damage-filter settings on (AppSettings' defaults), one boss, both lists selecting the same fight:

| | legacy board | derived board |
|---|---|---|
| raid total | 604,857 | **722,926** (+19.5 %) |
| Rune, Ammeren, Triumph, Soell, Puksu, Kuma | 234065, 123456, 98636, 91356, 56666, 678 | identical, to the point |
| Sancus +Pets | — | 114,811 |
| Jazrakhan +Pets | — | 3,258 |

Every raider legacy managed to place reads exactly the same on the derived board, and the whole difference is two pets.
`PlayerRegistry` never learned them from this log: at the end of the run "Sancus`s pet" answers `IsPetOrPlayerOrMerc`
false, `IsPossiblePlayerName` false, `GetPlayerFromPet` null — so `FightManager.IsValidAttack` refused every one of their
records and their damage entered **no fight at all**, neither the boss's row nor a row of their own. The line itself says
whose pet it is; that is R5 evidence, stored as a flag on the fact and cut back out by `ClassificationRules.OwnerInName`
(one rule now shared by the rules pass, `FightDeriver`'s row `PetOwner`, and the materialized record's `AttackerOwner`).
`DamageStatsBuilder` folds a pet under the name in `AttackerOwner` when the registry has no mapping, which is where the
"+Pets" rows come from.

This difference is a rule difference, not arithmetic, and it must not be "fixed" into agreement: the derivation's whole
purpose is to decide at read time from everything the log said, rather than at parse time from what the registry
happened to know when the first line of a pair arrived. `FightSummarySourceTest` pins the shape instead — every legacy
raider equal, derived-only entries all `+Pets`, and the delta equal to the owner-in-line damage computed from the fact
table (not from either board).

A materialized record carries the modifier mask off its fact, which is what makes `DamageValidator`'s six exclusion
settings (assassinate, headshot, slay-undead, …) mean the same thing on both boards; see *The byte that filters live in*
below. `EQLogParser.Test/src/parsing/derive/FightSummarySourceTest.cs` holds all of the above; the UI side is one-way
(derived-list selection → damage board) so that clicking the other list still owns its own boards.

### The byte that filters live in, and the field it moved into

`DamageFact` carried a `ushort` after `Total` to keep `Time` eight-aligned — a padding slot holding a constant `0`, and
two more bytes after it went to `OverTotal`, "how much more was asked for than landed", which is a thing only a heal
line says. Damage never wrote it, the damage summary never read it, and it cost four bytes in every one of the ~13 M
damage facts on the 393 MB capture (~52 MB) so a derived record could carry a zero nobody consumed. `ModifiersMask` slid
into those bytes: the struct is still 32 B (`DamageFactSizeShouldNotGrowPastPadding`), and now `FightSummarySource`
can hand `DamageValidator` the mask it reads — before this, every derived total read **high** the moment one of the six
filters was switched off, because a mask of 0 excludes nothing.

That zero had a second consumer worth naming: `HitLogViewer` shows the mask as a column, and "(Riposte)" is part of why
a spell row reads the way it does. A fact that never stored it could not reproduce the row.

The rule the field enforces is on `DamageRecord`: **a record carries only what its own log lines can write**, which is
also why counting is split (`UpdateDamageStats` takes a `DamageRecord`, `UpdateHealStats` a `HealRecord`). `OverTotal`
lives on `HealRecord`. A heal's mask is captured the same way (`HealFact.ModMask`) for the day the healing board reads
from facts.

### Healing joins the capture, in its own table

Heals now flow into `HealFactTable` off `HealingLineParser.EventsHealProcessed` — deliberately *not* into the damage
table. Measured with a temporary probe over `EQLogParser.Test/data/derive/heal-fight.txt`: 5 of 9 events are heals, and
one array of the union (42 B: 36 for the shared fields plus the four damage-only bytes a heal can never write, and
always a zero) sat **57 % full of zeros** at capacity, **40 %** after trimming to what is shared. Split, each stream
pays only its own tail: 32 B and 40 B.

One array was not merely wasteful, it was wrong. `DamageFactsByFight` is a **contiguous ordinal run** per fight — a
consequence of the 64-bit spine being fight-major with time ascending inside it — so one array would force every heal
to carry a `FightId`. A raid heals before a pull exists and after the last boss dies, and those two populations are not
edge cases, they are the healing report: on the reference capture 16,947 of 582,785 events fall outside a fight,
*healing only*, because a heal does not create a fight and no `FightTimeline` transition happens on one. They would
either be dropped from the board or force the spine to grow an out-of-fight range while healing damage by the same
name stays inside it.

Three rules the split keeps honest, all pinned by `HealFactCaptureTest`:

- **One sequence across both tables.** The damage table owns the counter and hands it out (`NextSeq()`); a heal takes
  one. Each table's facts are appended under the caller's lock, so seq increases within each stream and the merged
  order is reconstructible — which is how heals get fight attribution later (read both streams in order, ask the same
  `FightTimeline`) without either table knowing about fights.
- **Names must resolve through the damage table's pool**, or the same raider gets two indices and the merge splits her
  in half. One test reads her name back out of *both* tables and demands they be equal.
- **The quiescence signal counts heals too.** Counting only damage would fire a derive during a healing-only stretch
  (a raid regrouping while everyone recovers) over facts still arriving, and "capturing… N" — the one number a user
  can check against the log — would be short by a third of the raid's output.

`HealFact.OverTotal` follows the damage rule and keeps both meanings of the log's parenthesised number apart: *with* a
paren it is the amount **after** over-heal was subtracted; *without* one it is 0, which means "the line said no more"
and never "zero was asked". A projection that summed it as extra healing would add nothing where the line said nothing.

### What falls out of the heal capture before derivation ever sees it, measured

The parser fires `EventsHealProcessed` only for lines it resolves to a named healer and target, so three shapes are
dropped upstream — these counts are from the 393 MB capture (`local/eqlog_Incogitable_xegony.txt`), against ~448,600
heal-action lines (2.9 % total):

| Line shape | Lines | Why |
|---|---:|---|
| `` `s pet healed itself … `` | 10,194 | the healer word is a pet name; extraction special-cases `` `s ward ``, not `` `s pet `` |
| `` `s pet has been healed … `` | 2,688 | same gap, on the receiving side of the sentence |
| `has been healed over time for …` | 120 | the amount offset expects `for <n>` right after "healed"; "over time" sits between |

What makes these a footnote instead of a bug is the composition: **all 10,194 active pet lines are a pet healing
itself** (measured, not assumed — 10,194 of 10,194), so no raider's output moves; and the passive shapes name no healer
at all, so the board could only have filed them under an unknown healer. The damage side of those same sentences is not
lost — `` X`s pet `` parses as attacker and as defender. What would flip this is a capture where pet lines heal
*raiders*, which is why the counts are written down rather than reasoned about; a fix should arrive with them
re-measured, and `HealFactCaptureTest` re-asserts each shape against the parser's actual behaviour.

## The NPC registry in `data/npcs.txt`

The file is 38,462 lines of names, one per line, and it is matched **case-blindly**: `EQDataStore` loads it into
`_allNpcs`, a `ConcurrentDictionary` built with `StringComparer.OrdinalIgnoreCase`, so `IsKnownNpc("A Shadowstone
Grabber")` answers for a row written in lowercase. The lowercase convention (117 rows still start capital) is tidiness,
not behaviour — but it means **dedupe has to be done case-insensitively**, or the same mob goes in twice.

What an entry buys: R6 (`R6-npcdb`) identity — **Medium** when the name contains a space, only **Weak** for a bare word,
which is why `Mite`, `Fade` and `Umber` stay overridable by line evidence — plus NPC-side fight edges through
`IsKnownNpc` in `FightDeriver`/`FightManager` and kill labelling in `EventViewer`.

Eighty-eight names came from the six captures (Sep 2022 → Sep 2026): every name no rule placed *and* absent from the
file case-insensitively, each then checked against `everquest.allakhazam.com/search.html?q=<name>` — the Mobs tab is a
`Name | Level | Type | Locations` table. A name was accepted only when a row's name equalled the searched name (under
both possessive spellings) and the type was **Monster, Undead, Animal, Raid Encounter** (including the compound
`Named|Raid Encounter`, which is how `Kratakel, Lord Misery` is filed — without that allowance the single largest name
in the set fell out) **or `a rare creature`**, the label they give rares. Refused on type: Quest NPC, Object, Banker.

A `[ … ]` inside an Allakhazam name is **their annotation, never part of an EQ name** — `Rufus Invictus [ Unbeatable
Force ]` says which raid instance the row belongs to, and no log line anywhere carries ` [` within a name. Comparing the
text before the first bracket is therefore legitimate, and it rescued exactly one name: `Rufus Invictus`, level 129 Raid
Encounter in *Elddar Forest: Raid Instance*, with 840 lines where it hits `Foob`, `Dextris Kanghammer` and players'
pets. It did not rescue `Gartik`: their row is `Commander Gartik`, a *22nd Anniversary: Blackburrow* ally, and all 664 of
the log's `Gartik` attacks land on `Aten Ha Ra`, a mob — an NPC **on our side**, which is the part worth remembering:

**The registry answers "is this name a player?" and nothing else — never hostility.** `an unknowing civilian`,
`a duressed civilian`, `A Rallosian recruit` and a raid boss sit in the same file, and registering a friendly NPC does not
make it a foe. Side comes from target-frame evidence and R15, not from `IsKnownNpc`; any rule that reads the registry as
"hostile" is wrong on its face.

What the batch says about our own rules, measured over those 88 names: **65 already arrive with the client's own
`Targeted (NPC):` line** and **0 ever appear under a `Targeted (Player):` line**, so not one of them was mislabelled in
these captures — the registry buys *provenance* (R6, plus fight edges in the live path) for names that would otherwise
rest on a shape guess whenever nobody targets them. Shape coverage: **40 are article-shaped** (what R14 reads), **9 are
bare words** (`Fade`, `Firethorn`, `Gwark`, `Lich`, `Magmath`, `Mite`, `NeuroKraken`, `Umber`, `Windshear`) — invisible to
every shape rule, and precisely the shape a player is free to choose, which is why R6 caps a spaceless name at Weak and
why the collision policy (a registry hit never outranks player evidence; see the `Terror` case in the identity design notes) has
to stay. **Three carry a comma title** (`Kratakel, Lord Misery`, `Keltakun, Last Word`, `Ogna, Artisan of War`) and one of
them is the biggest name in the set at 68,416 attack edges: no grammar rule can see that shape, so either a name list or
the client's own target line is the only road to it. **Twelve have no combat line at all** (civilians, shades,
`The Headsman`, `Defense Unit CDL`) — they were never going to reach a fight list, and "no rule placed it" meant *no
identity claim*, not *no evidence*.

Absence from Allakhazam proves nothing either: 43 candidates are simply missing from their index, some visibly worded
differently there (`a blazing emberfang` vs their `a fiery emberfang`, `a supercharged octodrone` vs `an overcharged
octodrone`). And short names that match only as the tail of a longer entry (`Boner` → `a mastruq bonereader`, `Trick`,
`Link`) turned out to be player-named pets and mercs, not log truncations — a substring is a hint to go look, never a
verdict.

One trap in the shipped data: **corpse rows are written with a backtick** (``commander b`drabits`s corpse``, 40 of them)
while the client writes corpses with an **apostrophe** — across the six captures, `'s corpse` appears in 6,748 lines and
`` `s corpse `` in 4 — and nothing normalises either form, so those rows can never match by exact name. Do not "fix" it
with a blanket replace: pets genuinely use the backtick (`` Kazcro`s pet ``). R13 takes the stripped base name for
corpse identity anyway, which is why nothing has noticed.

## The raid roster in `players.txt`: what Save() is allowed to throw away

`PlayerRegistry` keeps four things and saves only two of them, which is the whole background for this note:
`_defaultPlayerClass` (name → class, persisted), `_verifiedPlayers` (name → the **unix time** it was last
auto-confirmed, persisted), `_petMappings` (pet → owner pairs, persisted to `petmapping.txt`) and `_mercs`
(**memory only** — no timestamp, no file). Both persisted dictionaries are keyed per server
(`CacheDir/players.txt`, `CacheDir/petmapping.txt`), because a name that means "our raid" on one server is not the
same claim on another.

Auto-confirmation comes from fourteen call sites: five loot paths in `MiscLineParser` (roll, `receives N … from X's
corpse`, dragon scale / rune / currency splits), four ability-word paths in `LineModifiersParser` (`(Assassinate)`,
`(Headshot)`/`(Double Bow Shot)`, `(Slay Undead)`, twincast on a heal — the only ones that also carry a class),
two pet paths (`X's pet` seen attacking, and a petmap hit), `RegisterRaidPlayerFromWindow` (the `who` block: name +
level + class + guild), `TryGetConsumer` (the `Glug, glug… X takes a drink` actor, R17), and two merc paths. A name
hand-typed into the file has no timestamp at all and is kept for that reason.

**The bug this section exists for: every save used to delete the operator's own entries.** The window filter was

    if (!string.IsNullOrWhiteSpace(entry.Key) && (entry.Value > cutOff || !_defaultPlayerClass.ContainsKey(entry.Key)))

`_defaultPlayerClass` holds a class only where an ability word or the roster supplied one, so a typed name — no class,
no timestamp — failed both halves and vanished from the file at the next `Save()`, which runs on every window close and
on the ~5 minute auto-save while any other entry keeps it fresh. The file was therefore slowly eating exactly the rows
nobody but the operator could write: guild alts, a mainsurname-only entry. Persistence for a name that survives only
while something else re-confirms it is not persistence.

The filter is now `entry.Value > cutOff || IsManualEntry(entry.Key) || IsRejected(entry.Key)`, and three more rules
came with it, all of them about not spending operator data on inference:

- **A hand-typed name keeps its claim for the lifetime of the file.** `AddVerifiedPlayer` no longer stamps names that
  have no timestamp, so a typed entry never becomes "stale" and cannot be reaped; only evidence-dated entries age.
- **Removal writes a tombstone** (`!Name`) instead of letting the row disappear, and `Init` reads `!Name`, an empty
  value and a malformed timestamp as "rejection marker": the name stops being auto-confirmed AND stops claiming
  `Player` identity in `RegistrySeed`. A user who deletes a raid boss off their list gets what they asked for on the
  next derive rather than watching it come back a minute later.
- **A rejected owner loses ownership, not the mapping.** `petmapping.txt` stays intact because "Goruuk's pet" being
  somebody's pet is a different fact from Goruuk being on this server's roster — cascading into the pairs would throw
  away learned structure to express doubt about one name. The registry simply stops claiming the owner as a raider, and
  `RegistrySeed` refuses an ownership edge whose target was rejected; R5 still reads the line's own possessive text
  (`ClassificationRules.OwnerInName`) for the pet itself.

Round-trip coverage lives in `EQLogParser.Test/src/store/PlayerRegistryPersistenceTest.cs` — there was none: no test
constructed a registry with a `ConfigDir`, so every `Init`/`Save`/re-`Init` path (including that filter) was untested
while the WPF code called them on every window close. The class swaps `ConfigUtil.ConfigDir` to a temp directory, and
notes in passing that `CombatRecordLookup.IsValidClassName` is injected by `App.xaml.cs` and defaults to "no class name
is valid" headless — an assertion about a stored class needs the hook wired, and restored, because it is process state.

One cross-platform fix fell out of making those paths work: `CacheDir` concatenated `"\\players.txt"` (and
`"\\petmapping.txt"`), which on Linux produced one filename per server with a literal backslash inside it rather than
a file in the server's folder — so nothing in this repository's CI could ever have exercised the read-back path. Both
now go through `Path.Combine`, and the test asserts the files land *in* the server directory.

## The name census: what the Names window reads, and what an override costs

`ClassificationReport` (Core, `src/parsing/derive/`) is a snapshot of "what did this pipeline decide about every name
this capture mentioned", built for one purpose: letting a person audit classification instead of noticing one mistake
mid-fight. The fight grids' right-click stays (that is where mistakes get noticed) but both it and the window go through
`ClassificationCommands`, because before this there were two verbs writing two files — "Add player" went to
`players.txt`, "Set as Pet" to `mirror-overrides.txt`, at different strengths.

Four decisions, each because the obvious version was wrong:

- **The row set is the name pool, not "names that did something".** Interning happens when a name appears on ANY line,
  so `DamageFactTable.InternedNames` already answers "which names did this log mention" — including a boss that only
  ever got hit and a pet that only ever got healed (heal facts share that one pool: `HealFactTable` interns through the
  damage table, so index N is the same string in both streams). Filtering to attackers would hide the rows an auditor
  comes to look at.
- **Operator-only names are listed even with zero facts**: a rejection, a typed alt, and — the one that mattered — a
  verdict on a name this particular capture doesn't contain. Without it the window contradicts its own file and looks
  like it ignored the override; there is simply nothing to apply it to.
- **Totals are summed from the facts, never taken from a summary board.** The census answers "how busy was this name"
  for the whole capture; a board number carries the current fight selection into an audit list. Cost: one pass per
  stream writing into arrays indexed by name id (no hashing, no per-fact allocation), then ~6k timeless
  `IdentityWithSource` calls — tens of milliseconds on a 7.98 M fact capture, and nothing re-parsed or re-derived.
- **A disagreement is `players.txt` says raider AND the classifier says NPC. Absence is not a contradiction.** Unknown
  means "this capture has no opinion", which is what every guild alt who sat this log out reads, alongside the 12–42 %
  of facts whose names legacy never verified; flagging absence would paint the list red and bury the real rows. Pets and
  mercs are excluded because `players.txt` legitimately holds pet *owner* names, and a pet verdict costs its owner
  nothing. `AbsenceIsNotAContradiction` states it as a law over the whole census rather than pinning one row's kind, so
  it survives the rules getting better at placing names.

The as-of policy is "whatever the strongest claim says" (`IdentityWithSource` is timeless: strongest wins, ties go
later). A name whose kind genuinely changed mid-capture — charmed, un-petted, R13 resurrected — gets ONE row and no
history; its charm keys stay in `EntityTimeline`, and nicer treatment of those is deferred by decision, not oversight.

Tests are **cold** (registry emptied per test, no seed), which is why they assert the two kinds grammar and the shipped
database can settle from lines alone — `X`s pet` → Pet (R5), a name in `npcs.txt` → NPC (R6) — and expect raid members
to read Unknown. A census test that demanded Player verdicts from a cold run would only be testing the seed. Command
tests park `ConfigUtil.ConfigDir` in a temp folder, re-`Init` the store to prove a verdict reached disk, and leave both
singletons empty on the way out (`AGENTS`: no parallelization, this is process state).

## The sighting ledger: what this server's older logs concluded (2026-08-12)

Asked whether the classifier's data should be serialized and built up across log files. Splitting the question was the
whole answer, because two different things hide inside it. **Verdicts are already persisted** where a person said them:
`mirror-overrides.txt` (Manual strength), `players.txt` (membership + rejections), `petmapping.txt`, npcs.db — all read in
by R10/RegistrySeed on every open. What was NOT kept is the rules' own conclusions, and persisting those would create a
truth with no line evidence behind it while making the census's *why* column lie. Concretely: R7 builds sides out of what
the timeline already knows (`kinds[]` over every defender edge), so yesterday's conclusion arriving as input lets the
rules argue with their own memory until it looks corroborated — the same failure mode this file already documents for
players.txt (ClassificationRules.cs:330-337, ~45 min poisoned on names-as-"people"). So the ledger is a **display
fallback and nothing else**: `ClassificationReport.Build(..., priors)` borrows a verdict only where THIS log's rules said
nothing, and `FightProjection`/the board never see the file. Graduating it into the derive is possible but has to arrive
with derived-vs-legacy parity tests, not as a convenience.

Four properties make it safe, all pinned (`IdentityPriorStoreTest`, 9 tests):

- **Only what a later log might not answer again is recorded** (`IdentityPriorStore.WorthRemembering`), with the rule
  code stored beside it. The gate is an **allowlist of event-shaped rules** — `R1-`, `R2-`, `R3-`, `R4-`, `R5-called`,
  `R7-`, `R9-`, `R13-`, `R15-`, `R17-`, `R18-` — because the question is not "did a line say this" but "would next
  season's log be able to say it again". What stays out and why: `R6-npcdb` (npcs.txt ships with the program, so it
  answers for that name in every capture that mentions it — remembering it buys a row and no knowledge, and keeps
  contradicting a corrected database until the entry dies of age), `R14-shape`/`R16-comma` (the name's own spelling
  travels with it), `R5-owner` (the possessive is in the name; the durable claim belongs to petmapping.txt where the
  operator can edit it), `R0-local` (this session's character), `Manual`/`R10` (mirror-overrides.txt is that file),
  `RegistrySeed`/`You`, and `Prior` — this file reading itself is how a wrong name becomes permanently right.
  An allowlist because the failures are unequal: forgetting an event rule costs memory until someone adds it; admitting
  a permanent-input rule makes every log on earth file its built-in answers as experience, which no UI can show. A new
  rule has to ask (`TheRememberedVocabularyIsExactlyWhatItIs`). Measured on `mini-fight.txt`: **a cold capture writes an
  empty ledger**, because all four names that fixture places come from npcs.txt and pet grammar.
- **A restatement never downgrades a witnessed verdict.** A capture that can only say "it is in npcs.txt" leaves an
  existing `R7-graph` entry's reason and count alone (`ARestatementDoesNotDowngradeWhatALineWitnessed`) — the remembered
  reason stays the strongest thing any log actually saw, and no sighting is spent saying nothing new.
- **The file self-cleans on load.** Rows written before the gate existed (e.g. `Npc|R6-npcdb|…`) are not read back, and
  the load rewrites what is left rather than dropping them invisibly forever (`OldRowsThatRestOnTheDatabaseAreNotLoadedBack`).
  `Init` logs one INFO line — `Identity priors for <Server>: N remembered verdicts[, M rows refused as restatements…]` —
  because "did the memory load?" is otherwise unanswerable from a player's log file.
- **Agreement is idempotent per capture.** Derivation re-runs whenever a filter or override changes, so sighting time
  is the LOG's last event (not the clock) and the counter advances only on a strictly newer capture — five derives of one
  evening report one sighting, not five.
- **A changed verdict restarts the count**, so nothing advertises forty confirmations of a belief held for one.
- **It expires** against the newest entry (90 days) plus a 25k cap, keeping the most recently seen — using the newest
  entry rather than `DateTimeOffset.UtcNow` means replaying last season's backups does not nuke the ledger.

Census rows carry `IsPrior` with `Reason = "Prior:<code>"`, `PriorSightings`, `PriorSeenAtS`; an operator verdict beats a
prior, a line read in this capture beats it, and a rejected name borrows nothing (`ALedgerFillsSilenceAndNeverContradictsEvidence`,
`AnOperatorVerdictOutranksTheLedger`). Storage is `<ConfigDir>/<server>/identity-priors.txt` as
`Name=Kind|Reason|SeenAtS|Sightings`; `LoadProperties` splits on `=` and needs exactly two parts, so recorded reasons are
written with any `=` stripped, and a line that does not parse is dropped rather than repaired (`MalformedLinesAreDroppedNotRepaired`).

**Correction recorded honestly**: two commit messages (3d19b15a, a64042a3) describe a census count of "names with no claim
at all" (`UnresolvedInCapture`, `Row.IsUnresolved`, `ClassificationCommands.SetUnresolved`). That code is NOT in the tree —
`grep -rn Unresolved` returns nothing in either source or tests; it was lost when ClassificationReport.cs was rewritten from
a stale buffer. It is a ~20-line addition with three tests, deliberately not re-added silently here.

## The Names window, and why the flag column is the feature (2026-08-12)

`NamesTable` (View → **_Names**, docked beside the derived fight list) replaces the three hand-maintained panes. The
panes could show what somebody typed; they could not show what the classifier concluded, so a wrong verdict had no
surface to be noticed on — only a meter that looked odd. Its menu entries are gone (nothing in code referenced them);
the `ContentControl`s stay so an existing `EQLogParserLayout.xml` still loads.

Design points worth keeping:

- **Built on demand, off the UI thread.** A derive lands every few seconds while a log loads and a census nobody has
  open would be discarded each time, so `DeriveEngine.BuildNameCensus()` assembles the timeline exactly as a derive does
  (roster seed → rules → overrides last) and is called on open / Re-scan / a derive that lands while visible. Facts can
  arrive mid-walk: a census is display data, so one row shifting by one fact between passes is accepted rather than
  putting a lock on the capture path; a refused walk keeps the previous list and logs.
- **No second source of truth.** Every verb goes through `ClassificationCommands` into the same per-server files (verdicts,
  roster, rejections) plus the ledger; nothing is stored in the window.
- **The Notes column carries the action, not the kind.** "your verdict" / "no claim (you took it back)" / "older logs on
  this server, x2" / "roster says player, rules say NPC" / "not in this log". Each sentence answers *should I act*, which
  the Kind and Why columns cannot: `Prior:R6-npcdb` in Why must not read as if this capture produced it.

Also added to the census: `TotalNames`, `UnresolvedInCapture`, `Row.IsUnresolved` (see the correction note above — written
for real this time). Four new WPF-assembly tests (`EQLogParser.Wpf.Test/src/ui/common/NamesTableTest.cs`) pin the flag
sentences; they **compile but cannot run on Linux**, so treat them as unbuilt evidence until a Windows run.

## The one calculation a scope asks (DerivedTotals)

The requirement came from how the damage meter is used: if I watch a log, kill ten mobs inside my reset window, then
open the fight list and select the fights from that session, the two numbers must be **identical**. Legacy gets that
for free — `FightManager._overlayFights` holds the *same* `Fight` objects the list displays, and both surfaces print a
builder-produced `CombinedStats`. The derived engine keeps the property by making it structural instead: monitoring stores
(lines → facts → rows, nothing else), and every surface is a **reader** that asks one function about the rows it shows.

`DerivedTotals.For(rows, index, facts, heals)` = `FightSummarySource.Build` (facts aimed at each row's own name) +
`HealSummarySource.Materialize` (windowed by the rows' span, because a heal opens no fight) → a **fresh**
`DamageStatsBuilder` instance → its `StatsGenerationEvent`. `DeriveEngine.BuildScopeStats(rows)` adds the grid's
hidden-pet rule and is what the overlay will call.

The load-bearing part is the private builder. `DamageStatsBuilder.Instance` is what an open summary, the charts and
copy-to-clipboard all read as "last built", so a meter refreshing at 1 Hz through the singleton would repaint the
operator's summary with the meter's window — the two numbers this exists to unify would diverge *by construction*. One
object plus dictionaries per scope replaces millions of always-on per-line accumulations, so the cost lands wherever it
should. (`ARefreshLeavesTheBoardsLastAnswerAlone` primes the singleton with a different scope and asserts the scope run
left it alone.)

Two measured things, both from writing the tests rather than reading code:

- **The raid row's own `Hits` comes back 0** out of the damage builder while each player row carries its counts. So
  anything wanting a raid-wide hit count sums player rows — which is what the meter's list already does; asserted where
  it bit, so nobody "fixes" the scope by reading a field that was never filled.
- **Scopes add.** `For(A ∪ B)` totals exactly `For(A) + For(B)` for disjoint row sets, because `FightProjection` files
  each fact into exactly one row. That is what turns "the session so far" into "select-all in the list" without any
  reconciling code — and it is a tripwire: if a fact ever lands on two rows this reads high, if onto none, low.

Not built yet (deliberately, in this order): the overlay's session rule feeding `BuildScopeStats` instead of
`GetOverlayFights`, and its refresh cadence. And the precondition the next section measures: some captured facts
reach no row at all, so "select these fights" and "everything in the window" are not automatically the same set.

## What a session cannot see: facts that reach no row (census)

Pointing the damage meter at derived rows makes the meter's total and the list's select-all one function. That is only
honest if "the union of the rows" ≈ "everything in the window", so `UnrowedFactsTest` puts a spy in front of
`FightProjection`'s ownership sink (which ordinals did ANY row claim?) and subtracts. Note this is one level outside
`UnroutedFactCount`: *unrouted* facts reached a row and neither board wanted them; *unrowed* facts never reached one.

CI pins the definition on two synthetic cases (raid-only facts = the residue, counted not vanished; an ordinary pull =
zero gap, so `DamageFactCount + TankingFactCount + UnroutedFactCount` accounts for the whole capture). The real numbers
come from `EQLP_DERIVE_UNROWED=<log>[;<log>]`, and they found a hole worth more than the tool:

| capture | facts | unrowed | share | damage in the gap | one name owns |
|---|---|---|---|---|---|
| Kizant 2024-03-01 | 5,906,606 | 48,284 | 0.82 % | 5,838,413,554 | **`Darkside`, 5,644,042,553 = 96.7 %** |
| Kizant 2022-09-18 | 6,042,978 | 46,288 | 0.77 % | 4,327,198,414 | same shape (one pet, every boss) |
| Incogitable 2026 | 1,891,875 | 2,120 | 0.11 % | 181,332,891 | no single owner; mob-vs-mob self-DoT + raid-on-"pet" |

`Darkside` prints as **`Pet / RegistrySeed`, our pet at the time: True**, hitting `Commander Zoraxmen`,
`Hand of the King`, `Zelnithak`, `Aten Ha Ra` — the raid's own registered pet swinging at bosses, and it lands in no
fight row at all. Mechanism, in `FightProjection`'s gate cascade: side-ness comes from the *line's* evidence, so a pet
with no possessive in the text (`Darkside`, custom-named) reads mob-side, and the `Npc vs Npc` branch closes with
`if (!IsFlipped(timeline, atkName, t)) continue;` — "two mobs on each other is not a raid fight". The charm flip is the
only exemption; **ownership known from the seed/registry is not consulted there**, so the fact is dropped before it can
be filed. The second, smaller shape is its mirror image in the friendly-fire branch: `Incogitable -> A candlefolk flame
worshipper` (90.5 M) and `A candlefolk flame worshipper` itself (`Pet / RegistrySeed`, but `our pet at the time: False`)
— the raid's swings on a name the seed calls somebody's pet, whose ownership interval does not answer `IsOurPetAt` at
that second, get dropped as allied fire.

Why this matters for the meter (and for the +19–20 % derived-vs-legacy gap): legacy folds a mapped pet under its owner,
so its board *contains* this damage, while every derived board — fight list, summary, and the meter once it reads rows —
omits it. "Meter ≡ list" would hold; both would be quietly wrong together, which is the failure mode sharing one
calculation cannot protect against.

A fix is a gate change, not a counter change, and it wants its own pass: at `Npc vs Npc` (and at the friendly-fire
branch), consult ownership (`EntityTimeline.IsOurPetAt` / `PlayerRegistry` pet map) rather than only the charm flip;
key such a fact on the **mob** (the defender here, since our pet is the attacker), credit it as damage aimed at that
row's anchor, and carry `AttackerOwner = OwnerOf(name, t)` so the board folds it under the raider with `+Pets` exactly
as a possessive-named pet does. Boss-vs-boss noise stays out — the exemption is ownership, not "attacker unknown". Then
re-measure: this census, `RealLogBoardsTest`'s parity bars (Incogitable 4,473/4,473 field-matched, 2024
691/691), and the charm tests, because flipping which side a pet is on touches `CharmedOwned`/`RaidPet` labeling.

## Correction: the pet hole was not the side gate — it was a name looked up in the spell DB

The section above named a mechanism read off `FightProjection`'s branch cascade (`Npc vs Npc` → `continue`). That
hypothesis was wrong, and printing five of the offending facts killed it: every one reads `Darkside` **kind Pet, ourPet
True** attacking a defender whose kind is `Unknown`, which routes to the mob's row under the branch order as written. So
the diagnostic prints `IdentityAt`/`IsCharmedAt`/`IsOurPetAt` for BOTH names at the fact's own second, and the real skip
was two lines earlier:

```csharp
if (ClassificationRules.IsSelfTargetDamageSpell(atkName)) continue;   // spell feedback
```

That question is asked of the **string**, before anything asks whether the string is a combatant. `Darkside` is also a
self-target damaging spell in the DB, so every one of that pet's 44,553 facts was deleted from the projection before a
row could open — a raider's whole evening missing from the fight list, every board, and (once it reads rows) the damage
meter, silently and identically on all three, which is exactly why "meter ≡ list" would never have caught it.

Fix: compute sides first and drop feedback only when the attacker isn't already one of ours at that second
(`atkSide != Side.Player && IsSelfTargetDamageSpell(atkName)`). Unknown names — the case the guard exists for, `"You
have taken N damage from X."` leaving a spell in the attacker field — still open nothing, pinned by
`ANameThatIsOnlyASpellStillOpensNoFight`. Measured on the same three captures: Kizant 2024-03-01 goes **48,284 unrowed
facts / 5,838,413,554 damage → 3,731 / 194,371,001** (0.82 % → **0.06 %**), and the damage-side count rises by exactly
the pet's 44,553 facts — all of it recovered, nothing else moved.

`ARaidSideNameThatIsAlsoASelfTargetSpellStillGetsItsRows` pins what the meter needs, using a spell name from the DB as
the combatant: its hits reach the boss's row, the board's raid total includes them, no caster row appears under the
spell's name, and the damage arrives **on the owner's line** — `AttackerOwner` comes from `_charmers?.OwnerOf(...)` (the
timeline the index carries, `FightFactIndex(timeline)`), which is what `DamageStatsBuilder` folds by to print
`Bithika +Pets`. Two fixture traps learned here: the ownership interval must cover the facts' seconds (an interval of
`0..100` against facts at `t=1001` silently means "no owner at that time", and the board grows a row named after the
pet instead of folding), and the CI spell-DB fixture asserts `Inconclusive` rather than passing vacuously when the DB
lacks the name.

What is left in the residue is now 0.06-0.11 % and has one shape: raid-side attackers hitting a defender that reads
player-side while being neither charm-flipped nor answered by `IsOurPetAt` at that second — friendly fire as the gate
sees it (`Incogitable -> A candlefolk flame worshipper` 90.5 M; `A candlefolk flame worshipper` itself is
`Pet / RegistrySeed` with `ourPet False`). That is an interval/ownership-consistency question in the seed, not a gate
mistake, and it is the next thing to measure rather than widen the exemption for. Also still owed: re-running
`RealLogBoardsTest`'s parity bars now that derived sees this damage — the +19-20 % read as derived-running-high
should move toward legacy on the captures where a seed pet shared a spell name.

## Re-review of the legacy-replacement workstream (requirements vs what shipped)

Reviewed against what the operator actually asked for across this workstream, with the evidence that answers each. Two
things were found wrong by measuring after claiming — both corrected below rather than quietly — and one requirement is
deliberately not built yet because its safe shape needs a Windows run.

**1. "Load a log, watch the fight list fill, and it must not get slower."** The engine is an append-only capture plus a
quiescent derive; nothing recalculates on the parse path. Re-measured during this review: `eqlog_Incogitable_xegony.txt`
(344 MB) ingest **20.8 s**, classify+project **822 ms** for 4,174 rows, materialize 4,078 rows in 404 ms; 2024 capture
ingest 45.9 s. `8ec9cc93` kept the last of the eager arithmetic off the hot path by making every board a reader.

**2. "The overlay and the fight list must be the same numbers, exactly."** `DerivedTotals.For(rows, index, facts, heals)`
is now the only calculation, `DeriveEngine.BuildScopeStats(rows)` the one wrapper (it applies the grid's hidden-pet rule
so a scope and a click see the same population). Pinned structurally: `ScopesAddUpToTheSameTotalAsTheirUnion`
(`For(A ∪ B) == For(A) + For(B)`, the property that makes "the session so far" identical to "select-all"),
`TenPullsInOneWindowTotalTheSameAsTheRowsOneByOne` (the operator's stated scenario), and
`ARefreshLeavesTheBoardsLastAnswerAlone` (a 1 Hz meter refresh runs on its own `DamageStatsBuilder`, so an open summary
is never repainted with the meter's window). **Not done:** the overlay still reads `FightManager.GetOverlayFights()`; that
UI swap is mechanical now but needs to be watched on Windows, and it must ride the existing `EnableCombatMirror` gate.

**3. "Pet damage counts for the player, shown as `X +Pets`, in the meter and in the summary of the same fights."**
Pinned end to end by `ARaidSideNameThatIsAlsoASelfTargetSpellStillGetsItsRows`: hits reach the mob's row, the raid total
includes them, no phantom caster named after a spell appears, and they arrive on the owner's line via `AttackerOwner`
(the `FightFactIndex(timeline)` → `OwnerOf` seam `DamageStatsBuilder` folds by). The parity print confirms this is
what real boards do: derived-only rows on Incogitable are literally `Virul +Pets`, `Amengi +Pets`, …, while legacy keeps
those pets as their own rows or drops them.

**4. "Monitoring must not calculate DPS just because a meter could be open."** Monitoring appends facts and derives rows;
`DerivedTotals` runs only when a surface asks, on the asking surface's own builder instance (§"The one calculation a scope
asks"). Legacy still accumulates per line inside `BattleRow`; deleting that is late in the map, but nothing new pays it.

**5. "Keep the operator's knowledge; don't launder the model's guesses into it."** Rosters stay inputs forever;
`identity-priors.txt` (`972f8cfa`) carries cross-log warmth as a *display fallback only*, and classifier verdicts are
never persisted — every row still answers "why" from this log's lines. The Names window (`3b92e096`) replaced the three
hand-maintained panes as a surface.

**6. "Don't break what works."** `EQLogParser.Test` **1,463 pass / 5 opt-in skipped**, app + Core + both test assemblies
at **0 warnings**. Parity bars re-read after the gate fix: Incogitable unchanged (healing 373 healers exact with zero
mismatched (person, column) pairs; raid damage legacy 578,233,842,799 → derived 581,343,395,690, +0.54 %; 22 of 374
shared people move on Total), and — the sign that the dropped-fact hole closed rather than shifted things — **derived row
count now equals legacy's exactly (4,471 / 4,471; it was 4,312)**. On the 2024 capture: healing 104 healers exact,
derived raid damage **+0.12 %** vs legacy over a 5.9 M-fact evening. Arithmetic from the same two measurements, flagged
as arithmetic: without the fix that capture's derived board would read ≈0.5 % *low* (the pet's 5,644,042,553 was simply
missing), so the correction moved it from silently-under to slightly-over, in the same direction as every other known
difference (legacy dropping records whose attacker it cannot place).

**Two claims I had to take back.** (a) The first write-up named `FightProjection`'s `Npc vs Npc` gate as the mechanism
behind the missing pet damage; printing `IdentityAt`/`IsCharmedAt`/`IsOurPetAt` for both names at the fact's own second
showed the facts routed normally and the deletion happened in the spell-name feedback skip, which asked a question of a
string instead of of a combatant. The diagnostic stays in the census for exactly this reason. (b) An earlier roadmap said
the three boards still needed legacy fights; measurement showed they were already reading materialized derived rows, which
is why only one event (`EventsClearedActiveData`, `180ff710`) had to move before `FightManager` could go.

**Outstanding, in the order that makes them safe:**
- **Windowed scope** (the operator's "I can reset the meter at any time", so its window is often a *slice* of a row).
  Needs `FightSummarySource` materialization to accept `[from,to]` — and the trap to respect is its per-row `Fight`
  cache (`_summaries`, keyed by `DerivedFight`): a sliced `Fight` must never be served to an unwindowed selection, or a
  cleared meter would silently lower a summary. So: a separate non-cached windowed path, cost measured against a session's
  (small) ordinal runs, not slipped into the cached one.
- **Overlay UI swap** onto `BuildScopeStats` behind `EnableCombatMirror`, keeping the timeout rule inside overlay code,
  then removing `GetOverlayFights`/`HasOverlayFights`/`EventsNewOverlayFight`. Watch on Windows: this is the first change
  in the workstream whose behavior `dotnet test` on Linux cannot observe.
- **The 0.06–0.11 % residue**: raid-side swings onto a name the seed calls a pet whose ownership interval does not answer
  `IsOurPetAt` at that second. Belongs to seed interval consistency; do not widen a gate exemption to hide it.
- Remaining legacy readers stay in `docs/legacy-replacement-map.md` order: overlay → `EventViewer`'s `IsLifetimeNpc` →
  `LineChart`'s `FightTimeout` constant → `FightTable` → parse-time stat accumulation (`HitRecord`/`RecordsStore` last,
  until the line viewers read fact tables). Roster files never go away.

## A meter reset is a window, not a different set of rows

The requirement's loose end was "I can clear/reset the damage meter at any time", so its numbers are often a *slice* of
a fight row rather than the row. Read the original before designing anything, and it turned out to be a small, legible
rule set (`DamageOverlayStatsBuilder.ComputeOverlayDamageStats` + `DamageMeterConfigState.DamageResetMode`, saved as
`OverlayDamageMode`): the overlay accumulates **its own** per-player totals with an activity `TimeRange` each, and

- `timeout = mode == 0 ? FightManager.FightTimeout : mode` — "on kill" versus "N seconds of quiet then zero";
- the whole board prints only while the newest accumulated second is within `timeout` of now (`diff >= 0` guards the
  autumn clock-change bug), and a row prints only while its own last second is within `FightManager.MaxTimeout`;
- DPS is damage divided by **accumulated active seconds** (`TimeRange.GetTotal()`), never by wall clock since the fight.

So three things, and only one of them belongs to the derivation: the accumulation and the expiry are display policy and stay
in the overlay; what the derivation had to supply was *the arithmetic of a slice*. `DerivedTotals.For(rows, index, facts, heals,
fromT, toT)` is that argument (`DeriveEngine.BuildScopeStats(rows, fromT, toT)` is its wrapper): same rows, only facts
whose seconds lie inside the window, and — because the two materialization loops filter at the top and compute their
bounds and activity segments from what survives — damage, hit counts and activity seconds all belong to the window
together. Slicing is exact: a fact is in or out, so `For(rows,a,b) + For(rows,b,c) == For(rows,a,c)`
(`AdjacentSlicesOfTheSameRowsAddUpToTheWhole`: 800 + 950 = the whole 1,750).

**The hazard that decided the shape:** `FightFactIndex._summaries` caches one `Fight` per `DerivedFight`, so a sliced
`Fight` stored under the same key would let a meter that was just zeroed serve its truncated numbers to the next
unwindowed click on that row — both surfaces still agreeing perfectly, one of them wrong. Windowed materialization
therefore goes through `SummaryFightInWindow`, which reuses the whole builder but never touches the cache; the 3-argument
`FightSummarySource.Build` delegates to the windowed one unbounded, so the click path keeps its cached materialization
and a reset meter cannot influence it at all. Pinned from both directions by `ASlicedRowIsNeverCachedAsTheRow`.

**Two expectations I wrote and the log corrected** (both are now the assertions, because they are the properties that
matter and I had them backwards):

- *A window with nothing in it is not a zero.* It is "this row is not in this scope", so `BuildFight` returns null and
  `Build` counts it aside. Handing back an empty `Fight` would widen `AllRanges` — the DPS clock — with seconds nobody
  fought in, and drag heals from rows that contributed no numbers onto the heal board.
- *A window does not stretch a clock, and the clock is spans rather than wall clock.* Clamping only pulls a row's
  `BeginTime` forward to a reset that landed mid-fight (`T0+41` → damage 250, the 700 was before), which is what makes
  the meter's DPS honest; and because healing is windowed by the selection's **own spans**, a heal between two pulls
  belongs to neither, exactly as legacy's `HealingStatsBuilder` behaves. My first test put a heal in that gap at `t=5`
  and expected it inside a window ending at `t=39.5` — measured 0, and the measurement was right.

Suite **1,467 pass** (5 opt-in), solution and `EQLogParser.Wpf.Test` at **0 warnings**. The unwindowed path is
unchanged by construction (delegation + a null case that cannot arise when unbounded), so the real-log parity bars
measured earlier still stand; they get re-run with the overlay swap, which is the next step and needs a Windows run.

## The meter reads the derived engine now, and only its source moved

`OverlayDamageFromMirror` (settings.txt, off by default) points `DamageOverlayWindow`'s once-a-second build at
`DerivedTotals.ForOverlay(rows, index, facts, heals, fromT, toT)` — damage and tanking halves, each on its own builder
instance, returned as the `DamageOverlayStats` container the overlay already paints. What did NOT move: the meter's
policy. `_mirrorWindowT` is stamped at open/reset (`ResetOverlayFights` moves it too), and the expiry test is still
legacy's — quiet for longer than `mode == 0 ? FightManager.FightTimeout : mode` and the board zeroes with the window
reopening here. That split is the whole point of the seam: *which seconds* is the surface's business, *what they add up
to* is the derivation's, and it is why no "current fight" concept had to be invented inside the capture.

Two decisions worth keeping: a scope with nothing in it **holds the previous board** rather than painting zero (a derive
runs when the log goes quiet, so an in-progress fight can be seconds from landing, and a blank board would read as
"nobody is doing damage"); and an exception on this path blanks the board and logs it (first at once, then every 30th tick with a count),
because — as the operator put it — a fallback to code we intend to delete hides errors: a derived board that quietly became
a legacy board looks exactly like a correct one. `OverlayDamageFromMirror` therefore means "derived numbers or nothing". `ForOverlay` is pinned by `TheMeterAndASelectionOfTheSameRowsPrintTheSameNumber` (the meter's column equals a select-all of
the same rows per player and in total; its tank half counts the 120 + 90 the mobs landed; an empty window is no scope; a
sliced meter equals a sliced list) and `TheMeterBuildsOnItsOwnBuilders` (a refresh leaves the shared damage builder's answer
alone and never announces on the shared tank board). What still needs watching on Windows is not the arithmetic but the
wiring: that the timer's window advances, that expiry zeroes the board, and whether a real pull's numbers look like the pulls.

## One row per life: what the engagement gap splits, and what a duration column owes (2026-10)

Reported on `local/eqlog_Kizant_xegony.txt` (the pull is `Waxwork Abolishion`, with an h): the old fight list showed the
boss twice — 46 s, then 163 s after the adds died — while the derived list showed it once for "5 minutes", one entry
short of the encounter the raid actually fought. Both lists were looking at the same facts; they disagreed about what a
silence means.

```
LEGACY   18:34:08 → 18:34:53   46s   5.74B / 12,773 hits      "Time Alive: 46s"
LEGACY   18:37:08 → 18:39:50  163s  25.50B / 57,651 hits      "Time Alive: 163s"
LEGACY   18:39:52 → 18:40:16   25s   corpse DoT on raiders, no damage dealt

BEFORE   18:34:08 → 18:39:50  342s  31.70B / 85,831 hits      Dur = "5 minutes"   dmgwin = 0
AFTER    18:34:08 → 18:34:53   45s   5.81B / 15,190 hits      Dur = "00:45"       end = Gap
AFTER    18:37:08 → 18:39:50  162s  25.89B / 70,641 hits      Dur = "02:42"       end = Slain
```

**The split gap is legacy's expiry: 30 seconds, one number.** `FightProjection.EngagementGapS` was 300, chosen so a
continuous brawl would never be cut into invented boundaries. What it actually did was swallow encounters: a boss that
fades for 135 s while its adds are beaten dies in the same row as its own respawn. Legacy expires a name at
`FightManager.FightTimeout` (30 s), or 60 s for a fight that had never landed boss-directed damage — and only one of
those two numbers is kept here. A row that has not hurt anybody for half a minute is over either way, and a threshold
that depends on which side the first hit went makes a row's boundaries a function of damage direction rather than of
what happened. (Nothing in the six captures has ever been observed needing the 60 s one.)

The quiet cost of the merged row was the DPS clock, not the row count. Legacy totals a selection through `TimeRange.Add`,
which drops silences of 6 s and over, so its three rows summed to **324 s** of span where one merged row charged **343 s**
for the same click — the fade welded into "fight duration". A gap rule that swallows an encounter is not being
conservative about brawls; it is inventing one.

**Event→row matching keeps a wider window (`EventTailWindowS` = 300).** Those used to be one constant, and they are not
one question. Splitting rows asks "did this fight stop", which a silence answers. Matching asks "is this slain line (or
charm sighting) *about* this row", and the acts are performed by different people on different clocks — the raid's last
swing and a mesmerist's charm on the same mob need not be within half a minute of each other. Tightening the tail with
the split would have left rows reading "still going" long after their mob became the raid's pet (12 such rows on
Incogitable). `ACharmSightingLongAfterTheLastSwingStillClosesTheRow` holds the two apart; if someone re-merges them,
that test fails rather than a fight list.

**Each row keeps two time windows, written by the same comparison as its damage split.** `BeginDamageTime/LastDamageTime`
were stamped at row creation and never advanced afterwards, so every projected row reported a zero-length damage window
equal to its own birth second — and `Sectionizer` walks `LastDamageTime` to place the non-tanking divider list, so the
lie had a consumer. They are now maintained like `FightManager` maintains them (`Begin…` on the first fact in that
direction, `Last…` on every one), split by direction using the same `aimedAtAnchor` comparison that separates
`DamageToOwner` from `DamageByOwner` and files `FightFactIndex`, so no two seams can disagree about which way a fact
pointed. `NaN` is load-bearing: it means "this never happened", and a row nobody hit must not report a damage time of
zero. That in turn exposed `Sectionizer`: `Math.Max(x, NaN)` is NaN, and one such row silenced every later "Fight N"
divider in the list — an empty window is now skipped rather than folded in. (Legacy could never step on this: its
`DamageHits` counts only hits that landed on the NPC, so every row its non-tanking walk considered had a damage time by
construction; a projected row counts both directions.)

**The duration column prints seconds.** `DateUtil.FormatTicks(…, HMSCompact)` → `00:46`, `02:42`, `01:02:04`. It used to
run through `FormatGeneralTime`, the fuzzy words formatter, which returns an **empty string** under a minute (so the
first life of this boss showed no duration at all) and collapses 112 s and 162 s into "1 minute"/"2 minutes". The legacy
grid has no duration column whatsoever — its seconds lived in the row tooltip, `Time Alive: 46s` — so this is the
derivation's own number, and it might as well be exact. The inactivity divider keeps the words formatter on purpose: that
text is legacy's ("Inactivity > 5 minutes") and matching it is the point.

Cost of the tighter split, measured on the same capture: rows **237 → 289**, and the stale-death rule below then
deleted **19** of those as fabrications, leaving **270** (visible after hiding pet rows: **267**) against legacy's
**262** for 1,929,949 damage facts. The remainder runs a few rows long because the derived list also keys names
legacy's registry-gated rollup never placed.

Pinned by `ABossThatFadesWhileItsAddsDie_GetsOneRowPerLife`, `AQuarterMinuteOfQuietIsStillTheSameFight`,
`ARowRemembersItsDamageTimeApartFromItsTankingTime`, `ARowNobodyHit_HasNoDamageTimeRatherThanAFakeOne`,
`ACharmSightingLongAfterTheLastSwingStillClosesTheRow` (all `EQLogParser.Test`) and `DerivedFightRowsTest`
(`EQLogParser.Wpf.Test`, Windows-only). `OurPetTest`'s fixture had to be re-timed: its pet swing was written a full
minute after the raid's, which the old gap folded into one row and the new one correctly calls two encounters.

## A death closes only a row that was alive when it happened (2026-10)

`A corrupted egg`, same capture, 18:52:58. The grid listed the name twice with the same beginning: one row living until
18:53:02 and the next line showing 0 seconds. Legacy lists that window once (18:52:58 → 18:53:02, 6,316,561 damage),
and it is right — two rows for a name means two mobs, and no mob was alive in both.

The slain queue is keyed by **name**, but one name carries many mobs in a night. This one is slain at 18:52:40, :45,
:46, :52, :56, :56, then 18:53:02 — the raid is already swinging at the next egg when the previous corpse's line
arrives. The projection had inherited one rule from legacy ("a death ends the row once a strictly later timestamp
arrives", because the killing blow and its `was slain` line share a second-resolution stamp) and nothing checked
whether the death was inside the row it was being applied to. So when a death sat in the queue that **predated** the
row created after it, that brand-new row died at its own first second — `18:52:58 .. 18:52:58`, holding one hit
(2,573,017) while the row above it lost it (3,743,544 instead of 6,316,561). Nineteen such rows on this capture.

**The rule:** a queued death may close a row only if it landed inside that row (`dt >= row.BeginTime`). An older death
describes an earlier holder of the name — one whose facts already went to an earlier row, or to none at all — so it is
dropped and the next entry in the queue is asked. The same guard applies to the end-of-stream pass that marks trailing
deaths, and the consequence is stated rather than hidden: a row with no death inside it gets **no** death marker. It says
"still open" because the log gave no evidence that its mob died during it; inventing one from a line about a different
mob is what produced the row that looked like a fight of zero seconds.

Verified on the real pull (projection vs legacy, `A corrupted egg`, 18:52:20-18:53:40): six rows each, identical begin,
end and damage — 35,026,061 / 207,505,806 / 36,588,567 / 211,571,124 / 37,914,248 / 6,316,561. Note the legitimate
zero-span row in that list (18:52:46, 36.5M damage in one second): a mob hit and killed inside the same second is real,
and looks identical to the fabrication on the span column alone — which is why the pinned tests assert *who died inside
which row* rather than "no zero-length rows". Pinned by `ADeathOlderThanARowCannotCloseIt` (with the assertion that the
next holder stays unmarked) and `ANameReusedBySuccessiveMobs_ClosesOneRowPerDeath`.

**Do not go hunting zero-span rows afterwards.** 43 remain on this capture, and they are what the log says: a name with
exactly one timestamp in it — `A strengthening foodstuff` (one hit, then silence), `Wickflame Wax Snare`, a `petrified vile
egg` nudged once before the raid moved on. Legacy prints the same shape (`A corrupted egg` at 18:52:46 is 0 seconds and
36.5M damage in both lists). The defect was never a short row; it was **two rows sharing one beginning**, which claimed a
second mob existed when one was still alive.

## A fight's duration counts its seconds inclusively (2026-10)

The fight list's duration column first shipped as `EndTime - BeginTime`, which is the span between two timestamps and
not the number this product calls "how long it lasted". The convention lives in `TimeSegment.Total`:

```csharp
public double Total => EndTime - BeginTime + 1;   // EQLogParser.Utils/src/TimeRange.cs
```

That expression is not cosmetic — it is the **denominator of every DPS number on the damage board** (`TimeRange
GetTotal()` → `TotalSeconds` → `Dps = Total / TotalSeconds`), and it is what `FightManager` writes into the legacy
tooltip (`var ttl = fight.LastTime - fight.BeginTime + 1;` → `Time Alive: 46s`), which `FightSummarySource` reproduces
word for word for a derived row. Subtract instead of count and the grid argues with the surface one click away from it,
worst on the rows that are already hard to read: `A corrupted egg` at 18:52:46 took 36,588,567 damage with both bounds
inside one second, so an exclusive column printed `00:00` above a summary saying "Time Alive: 1s" and dividing by one
second. A zero-length duration is not a short fight — it is an infinity-shaped hole in any rate built on it.

**The rule:** `DerivedFight.DurationSeconds` = `Math.Max(1, LastTime - BeginTime + 1)`, or `0` when the row has no
bounds at all (`BeginTime`/`LastTime` start at ±infinity and `TimeSpan.FromSeconds` refuses a NaN). The `+1` lives on
the row, not in the formatter, so any other surface that needs "how long" takes the same arithmetic;
`DerivedFightRows` only formats it. Waxwork Abolishion's first life (18:34:08 .. 18:34:53) reads `00:46`, the same 46 the
legacy tooltip says for that row.

Two things this does **not** touch. The grid's `Inactivity > mm:ss` divider is a gap *between* rows, nobody fought in
it, and stays an exclusive difference. And `FightSummarySource`' tooltip keeps its own copy of FightManager's
expression rather than calling `DurationSeconds`: it is reproducing the text of a legacy `Fight`, whose bounds can be
clipped to the requested window (`summary.BeginTime < fromT`), so the two numbers are not always the same number.

Pinned by `DerivedFightTest` — `AFightInsideASingleSecondLivedOneSecond`,
`SecondsAreCountedInclusivelyLikeTheBoardCountsThem` (asserts the row's seconds equal
`new TimeSegment(begin, end).Total`, so the two conventions cannot drift apart quietly),
`TheDurationCellPrintsTheTooltipsNumber` (the same format expression the grid uses, asserted where it runs on Linux) and
`ARowWithoutBoundsSaysNothing`; `DerivedFightRowsTest` in the Windows assembly covers the row end to end.

## How often derivation re-runs (2026-10)

Symptom reported from live use: a derived damage meter had to be coaxed open with **Re-derive**, and then sat showing
the same numbers for the whole encounter, moving only when Re-derive was clicked again. The fight list had the same
disease and the summaries inherited it — they all read one `DerivedSnapshot`, and no new snapshot was arriving.

The trigger was quiescence alone:

```csharp
if (count == _lastTickCount && count != _lastDerivedCount) RederiveAsync();   // two equal ticks, 1 s apart
else _lastTickCount = count;
```

That is the right question for a **file being loaded** ("have you stopped yet?") and the wrong one for a **live log**:
during a raid a line arrives every fraction of a second, so two consecutive equal ticks never happen, and the engine
faithively declined to derive for the entire fight. Quiescence is a completion detector; it was being used as a refresh
clock.

**What one pass costs,** measured on the largest capture on file (`eqlog_Kizant_xegony.txt`, 2,931,939 facts including
heals), timing exactly what `RederiveAsync` does inside the gate — roster seed, `ClassificationRules.Apply`,
`FightProjection.Build`, `Sectionizer.StampGroupIds`: **660 ms / 653 ms / 610 ms**, of which classification is
200-250 ms. Refreshing while data is dirty is therefore cheap enough that not doing it was a bug rather than a saving.
A pass does park ingest (`CombatCapture.DeriveQuiescent` is a `lock (_gate)`), so the cadence has to pay for that too.

**The rule now** lives in `DeriveCadence` (Core, decided apart from the session because a `DispatcherTimer` is not
a test harness) and keeps both triggers:

| situation | decision |
|---|---|
| count held still between two ticks | derive — end of load, still the fast path |
| nothing new since the last pass | never — an idle log must cost zero |
| growth ≥ 25,000 facts in a tick | **not yet** — that is a file being read; quiescence catches its end |
| small growth (a live tail) | derive once `sinceLastPass ≥ clamp(4 × lastPassSeconds, 3 s, 15 s)` |

The bulk-load guard is what keeps the fix from slowing the one moment throughput matters: reading a file through the
pipeline runs at roughly **170,000 facts/second**, tailing live at tens, so the threshold separates them by three
orders of magnitude rather than by guesswork. The throttle scales with measured cost — the 650 ms pass above lands on
the 3 s floor (≈20% of wall time parked at the gate while a raid is fighting, which buys a meter that moves); a 10 s
pass would be spaced to the ceiling instead of running back to back.

**The ceiling has a reason.** `BuildMirrorUpdate` applies the meter's own expiry to whatever it has: quiet for longer
than `_currentDamageMode` (or `FightManager.FightTimeout` = 30 s in the default mode 0) and the board zeroes, because
that is how a meter is supposed to behave after a pull. It measures quietness against *the last fact the snapshot knows
about*, so a cadence allowed to let a snapshot age past 30 s would blank the board on a raid that was fighting the whole
time — the refresh rule and the expiry rule arguing, with the expiry winning every time. 15 s is "inside 30 s with room
for two ticks", and `TheCadenceStaysInsideTheMetersOwnQuietRule` refuses a change that lets them cross.

**Opening a derived meter also asks for a pass directly** (`DamageOverlayWindow`, live branch). The board reads a
snapshot rather than the live pipeline, so without that nudge the first thing a user sees is an empty window until the
cadence happens to fire — precisely the "I had to click Re-derive" complaint, in a milder form.

Pinned by `DeriveCadenceTest`: quiet still fires, idle costs nothing, a tailing count refreshes without silence and
not before the floor, the first pass does not wait for silence, a bulk load is left alone until it stops, the interval
scales with measured cost (floor/middle/ceiling/NaN), and the ceiling stays under `FightManager.FightTimeout`. The
session's feeding of the rule and the meter's open-nudge sit in the WPF assembly, so they build here and run on Windows.

## A derive pass that starts where the last one stopped (2026-10)

"How often derivation re-runs" above settled *when* a pass runs; this is about what a pass costs, because the two
together decide how live the meter feels. The old meter never recomputed anything — `FightManager.ProcessRecord`
increments totals as each line parses, so its numbers are ~0 s stale and only the repaint is throttled. A derived board
computes rows from facts, and computing them every refresh is what buys rows that re-key themselves as evidence arrives,
charm windows, pets folded under owners. The honest goal was therefore not to match legacy's per-line freshness but to
stop paying for work that had already been done.

Two plumbing wins went first (commit "Mirror: paint on the pass, and poll faster than the refresh rate"), because they
were pure latency rather than throughput: a derive finishing between two of the overlay's 1 s polls used to wait for the
next poll to be *seen*, and the session asked the cadence once a second, so every threshold could fire up to a whole
second late. The overlay now queues a repaint when `Derived` fires, and the session polls at 250 ms with the thresholds
themselves rewritten from tick counts into seconds and facts/**second** (`DeriveCadence`) — rewritten, not just
re-scaled, or a finer timer would have silently shortened the quiet window and the bulk-load guard.

Then the projection itself. Measured on `eqlog_Incogitable_xegony.txt` (1,891,875 damage facts + 420,115 heals, 4,644
rows) by `DeriveIncrementBenchmarkTest`:

| pass | projection | classification (rule replay) | total |
|---|---|---|---|
| full rebuild | 433 ms | 299 ms | **732 ms** |
| continued, +1,891 new facts (0.1 %) | **0 ms** | 268 ms | 268 ms |
| continued, +18,918 new facts (1.0 %) | **5 ms** | 265 ms | 270 ms |
| quiet tick, nothing arrived | **0 ms** | 275 ms | 275 ms |
| re-arrived burst of 94,593 (5 %) | 490 ms *(rebuilt)* | 273 ms | 763 ms |

A live raid tail offers a few hundred to a few thousand facts between refreshes, so the useful line is the second and
third: **the fold went from 433 ms to single-digit milliseconds**, and the pass now costs what classification costs.

**Why carrying state is sound rather than merely fast.** The fold is a forward-only sweep, and everything it must not
forget lives in `ProjectionState`, which the cache owns: the open row per name (a fight spanning the pass boundary), the
last *closed* row per name (`CharmPetRows` needs the pet→encounter chain, and a pet's facts begin after that row ends),
the completed rows, and the per-name death queues — name-keyed because one name carries many mobs, which is the same
reason a death must be applied to a row only if it falls inside it. The damage index travels with the rows it was filled
beside: its ordinal lists describe that walk (`FactOwnershipHandler` is called from inside the projection), so a carried
list with a fresh index would point at ordinals from a different walk, and both are rebuilt together or neither is.

Two gates decide whether a pass may continue:

1. **The watermark has to still name the same facts.** Not "same length" — a table swapped out at the same count would
   satisfy that. `ProjectionState.Covers` checks the boundary facts (`ACoincidentallyEqualTableIsNotAContinuation`).
2. **The classification stamp has to match the one the carried rows were folded under.** A moved verdict means the rows
   are wrong, so this buys a full rebuild — it is not a fallback that hides anything, and it is why the meter never shows
   a row from a reading the classifier has since abandoned.

**The stamp is where the other quarter-second was hiding.** Its first version hashed every name and entry by walking both
stores: ~250 ms per pass on this capture, *more than the fold it was guarding*, which made continuing pointless (the
first numbers out of the benchmark read "continued in 263 ms" with a 0 ms projection). It is now summed in at insert time:
each accepted insertion adds a term over (store, name, kind, strength, T0, T1, source, owner) and `StateStamp()` reads an
accumulator. Three properties matter, all pinned by `EntityTimelineDigestTest`:

- **Replaying evidence must not move it.** The rule book runs from scratch on every pass; the timeline's mutators already
  drop re-assertions of what is recorded, so a night whose evidence has not changed stamps identically. This is the whole
  reason a quiet tick is free — and the way it fails silently: a timestamp in a source string, or dedupe removed
  upstream, and every pass rebuilds while everything still looks correct.
- **Content must move it, not just volume.** Same number of verdicts with different verdicts still differs; an insertion
  *count* would read "unchanged" and carry rows across a reclassification
  (`TheSameNumberOfVerdictsCanBeDifferentVerdicts`).
- **Order must not move it.** The term is *added* (commutative) rather than chained, because `RegistrySeed` walks
  `PlayerRegistry`, whose enumeration order is unspecified and shifts as the registry grows — a chained digest would
  rebuild on a reordering that changes no verdict. Added rather than XOR'd because one tuple can legitimately be recorded
  in both stores, where an XOR pair would cancel to nothing.

**The law this leans on:** classification is append-deterministic — replaying the rules over a longer prefix of the same
fact stream reproduces every earlier insertion, so new verdicts only ever add terms. Anything that breaks that (a rule
that *revises* an earlier conclusion in place, or a removal/revision API on `EntityTimeline`) has to move the digest
itself; nothing else would notice, and the failure is a stale row on screen with plausible numbers. The reflection guard
in `EntityTimelineDigestTest` refuses a third store appearing without this being told for the same reason.

**Where the remaining ~270 ms goes.** `RegistrySeed.Apply` measures **0-1 ms**; the whole rest is `ClassificationRules.Apply`
running its rules over every fact and heal again. Split by rule (same capture, steady state):

| stage | ms |
|---|---|
| R9 charm windows | 83 |
| R18 healed-pet intervals | 67 |
| R15 healed-by-raid-side | 54 |
| R7 graph inference | 36 |
| line evidence (R1-R4) | 15-25 |
| R5 ownership flag sweep | 15 |
| npc database, name shape, comma title | ~0 each |

Each of the top three is an aggregate over the whole night — charm lines, heal casters per name — so caching it means
giving that rule its own watermark and its own persistent accumulator, fed only with what arrived since, plus a second
gate for the cases where classification inputs move without any new facts (a roster edit, an override, an npcs.txt
change). That is a change to the rules AGENTS pins hardest, and note the asymmetry that makes it a poor trade at first
blush: when those rules *do* find something new, the projection rebuilds anyway and the pass costs 730 ms. What is saved
is the ~250 ms of work whose result we cannot know without doing it. Not in here; the tables above are the argument for
doing it if a 3 s refresh floor still reads as slow, and the honest framing for the user is "1 s instead of 3 s, at the
cost of six rules that each need their own incremental proof".

**What "rebuilt" means on the 5 % line.** The benchmark manufactures arrivals by rewriting existing hits one hour on, so
ninety thousand duplicates genuinely re-write the edge counts and a verdict really does change — rebuilding is the
correct answer, which is why that row is reported rather than demanded. What every share must satisfy is that the list
ends up identical to a rebuild of the same facts.

Correctness is asserted at split points rather than argued (`FightProjectionIncrementTest`): a damage-only pass continues
where the last stopped; a death arriving in a later pass still splits the row one pass would; a row published dead and hit
again matches one full pass; a gap landing exactly on the boundary matches; a charm arriving later closes the row it
closed in one pass; the index travels with its rows; and `GrowingAParsedLogOnePassAtATimeEndsAtTheSameFightList` replays
each fixture log tick by tick against the single-pass list.

### The classification half joins the carry (2026-11)

The projection's 0–5 ms continued passes were being paced by a 378 ms from-zero classification replay every full tick —
R9 charm windows, R15 heal breadth, R7's graph and friends re-walking 1.9 M facts to re-reach verdicts they had already
reached. The carry (`ClassificationState`) deliberately does **not** carry the timeline: every pass still builds a fresh
`EntityTimeline`, and each gated rule (R5, R9, R15, R7, R18) carries only its own aggregates — stream cursors, edge and
candidate tables, charm event tables, and a registered list of the claims it ever asserted — replaying them onto the fresh
store in stage order. That preserves the rulebook's visibility law exactly: no stage ever sees a verdict the from-zero
replay would have hidden from it (a carried timeline would have leaked R7/R18 claims into earlier stages, e.g. R9's
break signal). The gate per rule is its own **boundary `StateStamp()`** captured at stage entry: unchanged means no
upstream verdict moved, so the rule's inputs cannot have changed either and the cursors resume; moved means discard the
aggregates *and the cursors* and walk from zero. R7's first cut reset the aggregate but not the cursor and rebuilt the
graph from only the newest edges; R5 swept with a persistent `claimed` set but registered nothing for replay, so carried
passes silently lost every `R5-owner` verdict — and `Odin` came back as `Player/R15-healed`, which is what a laundered
verdict source looks like: same kind, different reason, different owner. Both shipped caught only by diffing every
interned name's verdict+source between carried and from-zero passes over the real capture — that diff is now the gated
law (`CarriedPassMatchesAFullReplayOnRealLog`), with fixtures for the late roster join (a healer verified mid-log must
re-count every heal it ever cast: the cursor may never outrun a verdict) and the process-health reset.

Measured on Incogitable (1,891,875 damage facts, 420,115 heals): from-zero classification **378 ms**, carried quiet pass
**2 ms**, first (session-warming) pass ~398 ms; a live-tail full tick is now ~2 ms of rules + whatever the projection
gate allows. `RegistrySeed` deliberately seeds at strength 8 — below R15/R18's Strong healer gate — so a registry name
alone never launders other verdicts, and tests must reach for an evidence line (`EvJoinedRaid`), not `AddVerifiedPlayer`.

## A mob's victim is never noise: how the tank board lost raiders (2026-10)

Asked whether legacy can be removed yet, the honest answer needed more than the fight-row count, so the boards bar was
run over every local capture, 2022 to 2026. The first pass turned up a class of missing people on the tanking board, and
one rule explains all of it.

**The mechanism.** `FightProjection` routes a fact by comparing the row's anchor against the attacker and the defender, and
the raid-side test is exclusion: `IdentityAt(name) is not Npc and not Pet`. So "no rule has placed this name" reads as one
of ours. But a *branch before* that routing used the same word differently — a fact whose attacker is an NPC and whose
defender nobody had placed fell into the mob-on-mob drop, filed with `creditAttacker = false` so it landed on the mob's row
as its own output and was announced to nobody. Both halves of that branch were wrong for a raid member taking a mob's hits:

```
a crazed flesh horror hits Worthless for 900 (damage)
```

`Worthless` spends 322 swings on `A cunning scrykin` and `A flesh horror` (so she fights what beats her), the log gives her
no possessive, no spell, no chat line, and `R18-healed-pet` only begins her identity when a heal names her. On Incogitable
the bucket holds 1,003 facts / 14.06 M and its top names are `Batvar`, `Worthless`, `Rector of the Spire`, `Boner`,
`Morris`. **Ddread** was the sharpest case because she IS in the roster: her registry verification replays from mid-log
evidence, so 80 of her 85 incoming facts sit before her own identity begins and the derived board reported 12,275 of the
181,670 legacy shows.

**The fix is one branch, ordered before the drop.** An NPC hitting an unplaced defender is `FactTarget.RaidSide` on the
mob's row; keying stays with the attacker so the fact cannot be credited to the victim as output. Only a defender that
reads `Npc` is mob-on-mob noise now. `IsRaidVictimAt` gained nothing — it already admitted these facts, and could not be
consulted from the branch that was discarding them.

| capture | tanking people rows (legacy / derived) | damage taken by people: legacy → derived | Δ |
|---|---|---|---|
| Incogitable | 211 / **166 → 212** | 1,863,292,368 → 1,864,915,479 | −0.67 % → **+0.09 %** |
| 9-18-22 | 74 / 74 (all shared) | 6,736,482,353 → 6,774,441,624 | +0.56 % |
| 8-20-23 | 70 / 70 | 3,438,088,181 → 3,444,666,513 | +0.19 % |
| 01-06-24 | 203 / 203 | 5,809,058,810 → 5,811,757,032 | +0.05 % |
| 09-08-24 | 65 / 66 (+1 derived) | 1,839,352,269 → 1,835,492,983 | −0.21 % |
| Kizant (2024) | 44 / 44 | 3,185,117,348 → 3,183,920,870 | −0.04 % |
| 09-20-25 | 66 / 66 | 4,616,279,198 → 4,615,391,397 | −0.02 % |
| 09-03-26 | 61 / 61 | 8,262,731,939 → 8,260,802,583 | −0.02 % |

Only the Incogitable row has a measured "before": it came from running the same test on the same capture before the branch
changed. The other seven were never run with this question asked, and the attempt to manufacture their before-numbers is
itself worth recording — see the trap below.

**Two census lines in the harness were lying, and one bar was comparing the wrong thing.**

- `facts the walk drops (mob on unplaced …)` printed a bucket the code no longer drops. The census now enumerates the
  refusals the way the walk does (`self damage`, `spell feedback`, `friendly fire`, `mob on mob`) and adds one line for what
  the new branch ADMITS, labelled as such — that line is the useful number across captures, because it prices how much of
  "damage our people received" rests on exclusion rather than on evidence. It is a multi-server-raid thing: Incogitable
  admits **915 facts / 13.68 M** whose defender reads Unknown at the end plus **88 / 0.379 M** that turned out to be a listed
  player, while the Kizant logs admit 231 (01-06-24), 24 (2022), 5 (09-08-24) and nothing on the other four — yet even those
  handfuls are entire people's rows, which is why the population count moved by 46 on Incogitable and not by 46 million.
- The damage bar read `derived.Keys.Count >= legacy.Keys.Count * 0.9` and failed on eqlog_Kizant_xegony-09-08-24.txt for
  reasons that are not loss at all: legacy groups by `record.Attacker` whatever that name is, so npcs.txt puts NPCs on its
  damage board (`Elmara Emberclaw`, `Dhakka Nogg`, both `Npc:R6-npcdb`, ~31 M dealt TO the raid), and a person is listed as
  `X +Pets` whenever the builder knows her summons — `DamageStatsBuilder` folds an owner's own hits into that aggregate and
  demotes her plain row below top level (`stats.IsTopLevel = false`), so the person row does not survive into the top-level
  board. The derived side knows far more owners because `AttackerOwner` comes from the line's possessive word, so on
  eqlog_Kizant_xegony.txt **all eight** of legacy's person rows arrive as `X +Pets`: 118 B of damage "missing from people"
  that is sitting one row name away. The bar is now about people: `PersonOf` cuts the ` +Pets` suffix, and no raid-side
  person legacy lists may be absent from the derived board under either name — 0 absent on all eight captures. (Nothing in
  the product changes either way for a user: `StatsFormatter` prints `X +Pets` only when `PlayerParseShowPetLabel` is on.)

**The measurement trap: a "before" number made by deleting a line.** To price the fix I first replaced the new branch's
body with `continue`, which does not restore HEAD — the old code did *not* discard those facts, it filed them on the mob's
row with `creditAttacker = false`. The bogus run reported derived tanking at 29.7 M / 51 rows against HEAD's 1.85 B / 166, a
forty-fold difference that had nothing to do with anything real and would have become a triumphant paragraph in this file.
A before/after pair has to be made by running the previous code (stash the file, or keep the log of the run you did before
touching it), not by writing an approximation of it and trusting the shape.

**What is left.** The tank board now agrees with legacy on population almost everywhere: the residue is one name on
Incogitable (`Blizzak`) and one derived-only name on two captures, plus per-column deltas of a few hits on shared people
(`Inconsistent` −3 hits / −125 k). Damage remains "renamed more than lost" for the reason already documented (pet folding),
and the raid total sits inside +0.6 % everywhere. Legacy's removal is still gated on the surfaces that read `FightManager`
rather than on parity: the overlay's show/reset path, the main grid's `ComputeStats`, and the line viewers (damage/heal/tank
tables, `NpcStatsViewer`, `Timeline`), which are a separate workstream because the fact tables capture none of what they show.

## A fight that is still going: porting the meter's show/reset rule (2026-10)

The damage meter's *numbers* moved to the derived engine first (`OverlayDamageFromMirror`, see "The meter reads the derived engine now"),
and that left the meter in the worst intermediate state a surface can be in: it painted derived numbers while the three
questions that decide whether you ever see them were still answered by `FightManager` —

1. **open on launch**: `OpenDamageOverlayIfEnabled` opened a window only `if (FightManager.Instance.HasOverlayFights())`;
2. **close for real when hidden**: hiding the board called `HasOverlayFights()` again to decide whether to keep an invisible
   window or close it;
3. **open yourself on a pull**: `EventsNewOverlayFight` fired when FightManager created a fight object at a first hit, and
   MainWindow opened a meter for a user who had none.

None of those concepts exist in the capture. A derived row is not an object handed out at the moment a hit lands; it is a
life reconstructed from facts on a pass. So the missing piece was not plumbing but a rule, written down in
`EQLogParser.Core/src/parsing/derive/LiveFights.cs`.

### Which clock, and why the measurement matters more than the argument

`LiveFights` reads the **capture's own newest event** as "now", not wall time. The argument is easy (a load at ~170k facts/s
runs minutes behind the wall); the measurement is what kills the alternative. Census over three captures, damage facts only
(`EQLP_DERIVE_LIVE=<log> dotnet test --filter LiveFights_RealLog`):

| capture | rows | first→last damage fact inside the FILE | time with a fight live | dead stretches >30 s | announcements / rows with traffic |
|---|---|---|---|---|---|
| `eqlog_Kizant_xegony.txt` (2026) | 270 | 1,184,488 s = **329 h** | 105.3 min | 33 (longest 17,199 min) | **269 / 269** |
| `eqlog_Incogitable_xegony.txt` | 4,644 | 53,262,821 s = **616 days** | 3,874.6 min | 1,251 (longest 221,543 min) | **4,542 / 4,519** (23 restarts) |
| `eqlog_Kizant_xegony-09-20-25.txt` | 378 | 14,180 s = 3.0 h | 143.9 min | 39 (longest 11.6 min) | **377 / 377** |

A player never rotates the log file: one "capture" spans 616 days of wall clock and 1,251 stretches where nothing was hit
for half a minute. Any rule phrased against the wall clock is therefore a rule about when the raid logged out — it would say
"a fight is going on" across a fortnight of an untouched file, and go quiet during a dense three-hour farm (`09-20-25`, where
80 % of the file has a fight live in it). Keyed to the capture's newest event instead, "live" means *at the end of what we
have*, which is the only thing a meter can act on: for a tailed live log that coincides with the wall clock; for a finished
file it means the last moments, which is where a reader who reopened an old log wants to know whether there is anything to
see. At the newest event of these captures the rule answered 1 live row (Kizant: the log ends mid-pull), 0 (Incogitable) and
0 (09-20-25) — both zeros are honest: those files end after a kill, and `Dead` rows are not live however recent their last
hit.

### One announcement per life, which is what the auto-open rule needed

`DeriveEngine.NewFightObserved` fires once per derive that opens a fight which the previous pass did not report live —
4,542 times over Incogitable's 4,519 traffic-carrying rows, 269 over 269 on Kizant: **~1.00 per life**. That is the number
that decides whether MainWindow may open a window on it: if a pull announced every derive (a pass every few seconds inside a
three-minute fight) it would be re-opening a meter the user closed seconds ago, and if it announced once a night the rule
would be decorative. The 23 exceptions are real, not noise: **rows split on silence in ANY traffic while `LiveFights` reads
only the two direction windows**, so a row can stay alive (heals and chat keep its span open) while its damage and tanking
windows both go quiet — longest such pause measured 121 s — and its resumption is announced again. A raid that pauses a pull
and resumes deserves to get its meter back; the alternative (remembering every name ever seen) would never re-open for that
pull at all.

### The two visible behaviour changes, stated plainly

- **Launch.** Legacy opened an enabled meter if the log contained any fight with damage *ever*. Derived opens it only if the
  capture's last moments hold a live row; otherwise `NewFightObserved` opens it on the next pull. That is a real difference,
  chosen because "there was a fight somewhere in this 616-day file" is not information about now.
- **Hiding.** A hidden derived meter closes for real when nothing is live and comes back on the next announcement, instead of
  lingering invisibly for the rest of the process.
- **Reset.** `FullResetClick` resets each engine the way that engine keeps a board: legacy discards its builder and
  FightManager's overlay set (those hold running totals), derived moves its window start (`_mirrorWindowT = -1`) because a
  derived board holds nothing at all.

### The seam, and why it is one file

`MirrorMeter` (app) is now the only reader of `OverlayDamageFromMirror`, and the only place that answers "which engine is the
meter reading". Before this the key was read in two components and the second still asked `FightManager`, which is how a
half-ported surface looks finished. It holds no policy: `TimeoutFor(mode)` maps the `OverlayDamageMode` dial (0 = on kill →
the 30 s engagement gap, otherwise N seconds) so the board's expiry and the live question cannot drift into two different
numbers. `LiveFights.GapS` is that same `FightProjection.EngagementGapS`, pinned as *behaviour* in `LiveFightsTest` (live at
29 s, not at 31 s) because comparing two constant spellings of one number passes even when somebody edits it to 300.

`NewFightObserved` is **static** on `DeriveEngine` (like `ActiveChanged`) because its reader outlives a capture: the meter
must auto-open for the next log too. It is unsubscribed with the legacy event's old call sites folded into
`SubscribeOverlayFights()`/`UnsubscribeOverlayFights()`, so a window cannot be kept alive by a handler nobody remembers to
detach — and it is raised in its own `try` after `Derived`, because an auto-open throwing must not make the *derive* look
broken (the outer catch disables auto-derive; that decision belongs to the derive itself, not a subscriber).

**No fallback, again.** "No session" answers `false`, and `HasLiveFight` never consults FightManager on the derived path: a
meter that quietly started trusting legacy visibility rules looks exactly like a correct one.

Re-measure with `EQLP_DERIVE_LIVE=local/<log>.txt dotnet test --filter LiveFights_RealLog --logger "console;verbosity=detailed"`.
What is left on this path is not the wiring but the default: flip `OverlayDamageFromMirror` on and delete
`DamageOverlayStatsBuilder`, which is the legacy tally this bypasses.

### One dial for both surfaces (2026-10, burn-in)

`OverlayDamageFromMirror` is retired. The list's `EnableCombatMirror` is now the ONLY dial: `MirrorMeter.Enabled`
reads `AppSettings.IsCombatMirrorEnabled` live (no static capture), and the overlay window reads it live at every
decision site instead of capturing one at construction. The motivation was the exact failure mode this file keeps
flagging: two words let a meter paint derived numbers while its open/close/reset rules still answered to legacy
state, and a mid-run toggle of the list icon left an open overlay on the other engine until restart — the same
half-ported surface that looks finished. One flip now moves the list AND the meter together, and the MainWindow
toggle re-homes `SubscribeOverlayFights()` for whichever engine is on (a legacy-mode app no longer sits dead to the
auto-open announcement until the next log open).

The word is scaffolding for the side-by-side burn-in only: when the legacy engine is deleted it goes with it, and
the fight window simply docks and toggles like the old one — no settings entry. Nothing on the OFF branch may do
anything beyond "legacy runs exactly as before", or the deletion commit grows a behaviour migration inside it.

### A taunt belongs to the life that was open when it landed (2026-10)

The spell/taunt census over `eqlog_Incogitable_xegony.txt` found the one real bug the new seam could hide: derived
taunt totals ran ~300× legacy (`Useless|An echo`: 16 → **4,608**), while the log itself held **4,364** of Useless's
"has captured" lines in total. Cause: taunts are a name-keyed stream walked whole at materialization (the damage and
tank passes cannot have this bug — they walk the row's own ordinals), and an unbounded selection routes with
`fromT = -∞`, so **every** row named `An echo` received **every** taunt that ever named one, and a name with forty
lives multiplies by forty. The routing rule is now what legacy's `GetFight(npc)` always meant: the ONE open row the
name has at the fact's second — implemented as the fact naming the row AND sitting inside the row's own lifetime
(`BeginTime..LastTime`), intersected with any slice window. Same-name rows never overlap in time, so the clamp is
exact.

What that costs, by design: a taunt whose second falls in no row's lifetime is a known loss on the derived board —
a life with no facts is not a life. Legacy's total is lower for a reason that is not expiry at all: `HandleNewTaunt`
is `GetFight(npc) ?? Create(...)`, and while the DAMAGE path registers its new fight (`UpdateIfNewFightMap`), the taunt
path does not — `Create` only allocates a local that is immediately dropped. Every taunt arriving with no ACTIVE row for
the name (a tank's "has captured" routinely precedes the first hit line; anything >30 s past last activity likewise) is
appended to an object nothing can ever read: no registration, no events, invisible by construction, which is how it was
never noticed. Measured after the fix, same capture: taunts legacy **4,307** → derived **5,465** against ~6,083 raw
taunt lines; derived counts each parsed taunt once against the life that was actually being taunted, and legacy's
orphan path silently dropped ~1,800 of the capture's taunts (6,083 − 4,307) from every board — the old table simply
never looked wrong. The mechanism is pinned through the real parser by `FightManagerTest.ATauntWithNoActiveRowIsLostFromEveryBoard`
(and its damage-less 60 s sibling): if the orphan path ever gains a registration call, that test fails and these
numbers get re-measured. Spells: 515.078 B → 515.248 B (+0.03 %), 37 of 3,195 shared pairs moved at all. The census
prints grand totals precisely so a multiplication like this reads as a total out of line rather than a hundred small
per-pair moves (`SpellTaunt_RealLog_Census`). Pinned by `TauntsLandOnTheirNpcRowWithTheOutcomeWords`, which now also
carries the out-of-lifetime taunt that belongs to nobody.

### The X closes a meter; it does not reset one (2026-09-29)

Reported from use: the reset button behaves, but "the X is just supposed to close the window and it would open back up next time damage comes in
and it would continue where it left off if it were within the configured time range". Both halves were broken in the port, and the interesting part is that
the previous section of these notes argued for the broken behaviour.

**Reopen.** `FightManager.UpdateIfNewFightMap` raises `EventsNewOverlayFight` inside `if (fight.DamageHits > 0)`, *outside* the `TryAdd` branch: the event goes out on
every damage line of a fight that has ever done damage. Legacy's X therefore lasted until the next line — milliseconds. My port announced one row per life, reasoning that
"an event firing every derive would reopen a meter the user closed seconds ago". That reasoning mistook the player's intent for a bug: closing a board during a pull is
"get it out of the way", and `Disable Meter` exists for "keep it shut". The trigger is now `LiveFights.HasFreshDamage` — newer row activity than the last announcement, with
something still live — raised per derive. The rate question was measured rather than assumed: over the covered seconds of a night the reopen rule fires **~421 times at the 15 s
cadence and ~2,107 at the 3 s ceiling on Kizant (105 covered minutes), ~15,498 / ~77,492 on Incogitable (3,875 minutes)**, against **1,929,949** and **1,891,875** damage facts —
each of which fired legacy's event. Two to four orders of magnitude cheaper, and while a window is open every one of them is `_damageOverlay == null` returning false. The liveness
half is not decoration: without it the last hit of a kill would pop a board over a corpse.

**Continue where it left off.** Legacy got this without trying — its accumulation lives in statics (`DamageOverlayWindow._stats`/`_statsBuilder`) and in FightManager's fights, so
a reopened window kept painting what it had not thrown away. A derived board holds nothing between ticks: it is recomputed from the facts inside `[windowT, now]`, so the only thing
that can carry is `windowT`, and that field was an instance member — reborn at "now" with every reopen, which quietly deleted the seconds already spent on the pull from the numbers
while the raid kept going. It is static now, and the decision is a rule (`LiveFights.WindowStartFor`): the stored start survives unless it is absent (first tick after a clear) or the
capture has been quiet past the meter's own dial, in which case the board zeroes and starts here. Nothing runs while no window exists, so an X does not age the start either — a reopen
inside the range gets the whole pull, a reopen after it gets one fresh board, and in both cases it is `OverlayDamageMode` deciding rather than a special case in the window. And the `now` that rule measures against is **wall time, on purpose** - the one
meter rule that must see the real world: the dial (mode 0 = the engagement gap, else N seconds) is a promise in *real* seconds about how long a quiet board stays up, and a lag
between the fight and the file - the client buffering writes, a parse stall, a late flush - freezes the capture clock (no facts arrive, so its "now" does not move), which would hold
the board past whatever the user chose. `DateTime.Now` keeps counting through the lag; the capture clock cannot express it at all, because a static file contains no time. The
split is therefore deliberate and is not drift to "fix": *is there a fight to show* runs on capture time (`LiveFights` - a wall key there is a rule about when the raid logged out,
and the 329 h / 616 day file spans are the measurement), *how long does a quiet board stay up* runs on wall time. The consequence on an old capture ending mid-fight: the board
blanks after the dial and stays blank - which is what the original did (both engines compared against `DateTime.Now`) and what a live meter should do; boards and summaries are
what old logs are for. A first pass of this port had keyed the expiry to the capture's newest event for consistency with the open decision; it was reverted on measurement of the
lag case, because consistency cost the user's dial its real-seconds meaning.

Two things in the code and the notes disagreed with themselves while I wrote this, which is worth keeping as a lesson: my earlier "starts per life" census (269/269, 4,542/4,519) was
a correct measurement of *pull starts* that I had labelled "announcements" and built a product rule on. The census stays — it is the shape of a night, and 23 restarts with a 121 s pause
inside a row is the gap rule working on a real capture — but the announcement rule moved off it. The test that asserted "not per second of fighting" is gone; `TheAnnouncementMeansNewerDamage_NotANewFight`
pins the new one, including the dead-row and stale-activity refusals, and `AClosedMeterReopensOntoTheSecondsItLeft` pins the four combinations of stored start, quiet length and dial.

```
EQLP_DERIVE_LIVE=local/eqlog_Kizant_xegony.txt dotnet test EQLogParser.Test/EQLogParser.Test.csproj \
  --filter LiveFights_RealLog --logger "console;verbosity=detailed"     # prints [live] coverage, starts, restarts, reopen ceiling
```

## How long a meter update takes, measured end to end (2026-09-29)

Asked: can the meter update faster? Before changing anything the seconds were attributed, because "the meter is slow" has at least five
candidate causes here and they cost completely different things to fix.

**The chain, live raid, with measurements.** A line is written by EQ → `LogReader`'s tail loop drains it (**up to 200 ms**, `Task.Delay(200)`;
its `FileSystemWatcher` listens for Deleted/Renamed only, so nothing wakes this loop early) → captured into the fact tables (microseconds, under
`CombatCapture._gate`) → the session's pump asks every **250 ms** whether to derive → `DeriveCadence` answers with
`clamp(4 × lastPassSeconds, 3 s, 15 s)` → the pass runs **under the same `_gate`**, so it stops capture while it runs → `Derived` fires → the overlay
rebuilds its board and paints.

So the typical felt delay is **~3.3–3.6 s**, and it is almost entirely the **3 s cadence floor**. Two more numbers decide what can be done about it:

| what | Kizant (1.93 M damage facts) | Incogitable (1.89 M) |
|---|---|---|
| continued pass, projection only (+0.1 % of facts) | **0 ms** | **0 ms** |
| same pass including classification | **186 ms** | **261 ms** |
| full rebuild (classify + project) | 616 ms | 725 ms |
| meter board over a 30 min window / 2 h window | — | **5 ms / 10–21 ms** |
| meter board over every row in the file (1.7–1.8 M outcomes) | **1.6 s** | **3.6 s** |

Three things fall out of that table.

1. **Classification is the whole pass.** Projection is already incremental and costs nothing on a live increment; the rule book re-runs every pass
   whether or not anything was learned (R9 charm windows 83 ms, R18 healed-pet intervals 67, R15 heal breadth 54, R7 graph 36, line evidence 15–25,
   R5 ownership sweep 15). `4 × lastPassSeconds` therefore measures ~0.8 s and the floor does the actual governing.
2. **A pass parks ingest**, because capture and derive share `CombatCapture._gate`. At a 1 s cadence a 260 ms pass holds that gate ~26 % of the time,
   and the thread waiting on it is the parse thread. This — not CPU — is why the floor cannot simply be lowered.
3. **Repainting is cheap for the windows players actually run** (single digits to ~20 ms), and expensive only for a window that accumulated thousands of
   rows, i.e. a meter that has not expired all night. Any refresh rule that gets fast must stay cheap relative to *that*, or a dense farm night turns every
   refresh into a second of work.

**The options, in the order I would take them.**

- **(a) A cheap lane for the meter.** Refresh rows (projection only, 0–5 ms) at ~500 ms and keep classification on today's cadence. Latency drops to
  ~0.6–0.8 s, the gate stalls ~5 ms per refresh instead of ~250 ms, and the cost is staleness with a bounded size: identity verdicts (pet folding, charm)
  catch up at the next full pass rather than on the same one, which is what already happens today whenever a pass is skipped. Needs the carry's state-stamp
  gate to be asked per lane, so a stale-row rebuild still triggers exactly when a verdict moved.
- **(b) Tiered classification.** Run the cheap rules every pass and the measured-expensive ones (R9/R15/R18/R7) on a slower tick or when their own evidence
  count moves. Same one cadence, pass cost down to tens of ms, floor then becomes honest at ~1 s without the gate math hurting.
- **(c) Trim the fixed overheads.** Tail delay 200 → 50–100 ms, pump 250 → 100 ms: about 0.3 s for nearly no risk, and it helps every derived surface, not just the meter.
- **(d) Just lower `FloorSeconds`.** One constant, but it buys latency by parking ingest — I would not do this before (a) or (b) makes passes cheap.

Probe kept as `EQLogParser.Test/src/parsing/derive/MeterBoardCostRealLogTest.cs` (gated, skipped without the variable):
```
EQLP_DERIVE_COST=local/eqlog_Incogitable_xegony.txt dotnet test EQLogParser.Test/EQLogParser.Test.csproj \
  --filter MeterBoardCost --logger "console;verbosity=detailed"
```

### What was built from that list: the cheap lane, and 125 ms of overhead (2026-09-29)

Took **(c) then (a)** from the options above. Both are in, and the numbers below are arithmetic over the measurements in that table — they
are a prediction about *perceived* delay, which only a live raid can confirm.

**The cadence answers two questions now, not one.** `DeriveCadence.Decide(...) → DeriveKind { None, ProjectionOnly, Full }` replaces
`ShouldDerive`. `Full` classifies and projects (what the UI's Re-derive and every log opening ask for); `ProjectionOnly` folds the facts that
arrived since over **the `EntityTimeline` instance the last full pass produced** and is paced by
`FastFloorSeconds = 0.5`, while `Full` keeps exactly the cadence it always had — `clamp(4 × lastFullPassSeconds, 3 s, 15 s)`.

Reusing the timeline *instance* is what makes the lane cheap twice over: no rule book runs, and `EntityTimeline.StateStamp()` comes back
unchanged, which is precisely the answer `FightProjectionCache` needs to keep folding from its watermark instead of re-walking the night. The
stamp gate did not need to learn about lanes — carrying the object is what makes it answer correctly.

Order inside `Decide`, because each check is a different hazard:
1. **nothing new since the last pass of either lane → `None`** (idle costs nothing);
2. **count held still for `QuietSeconds` → `Full`**, deliberately the expensive lane even though a cheap one would be faster: the end of a load is
   when the rules finally have the whole capture in front of them, which is when they learn what a cheap refresh cannot (a pet whose owner spoke once
   at minute forty);
3. **bulk rate → `None`** — *both* lanes park. The cheap one is nearly free to us and still holds the gate the loader needs;
4. **full clock due → `Full`**, judged on `_sinceFullPass`, never on "since any pass";
5. otherwise **cheap clock due → `ProjectionOnly`**.

Point 4 is the one the session's bookkeeping can get wrong: if a cheap pass restarted the stopwatch that paces classification, a busy tail would run
forever on the verdicts it happened to have at minute one and pets/charms would quietly stop folding. `ACheapPassNeverPushesTheExpensiveOneAway` pins
the rule; `DeriveEngine` keeps `_sinceAnyPass` and `_sinceFullPass` separately and restarts only the latter when a pass classified.

**Fixed overheads**: the session's pump 250 → **100 ms**, and nothing else. That is not tunable by event in either direction — the cadence reads
durations, which `TheRuleDoesNotDependOnHowOftenItIsAsked` holds — so it buys promptness only, and it is what keeps a 0.5 s floor from landing with a
quarter-second of jitter on top.

`LogReader`'s tail delay was cut to **75 ms** during this work and **put back to 200**. It cannot be replaced by an event either (that loop's watcher
ignores `Changed`, and EQ's own write buffering coalesces those notifications, so a wake-up would need this same poll behind it), and it is real latency:
a line waits half the delay on average. What changed is what that 125 ms buys. Before the cheap lane it was part of a ~3.4 s wait that had no other term
willing to move; with folding on a 0.5 s floor, a line drained 125 ms later usually lands while the next pass is *still* not due, so most of it overlaps a
wait we were going to pay anyway. Polling thirteen times a second for hundredths of a second is not a trade worth making against the file EQ has open.

**What a refresh costs now, per second of live raid**: ~2 cheap passes at 0–5 ms projection + row-building, plus one full pass per ≥3 s at
186–261 ms → roughly **7–12 % of the ingest gate** against **6–9 %** before, in exchange for numbers moving in **~0.8–1.2 s** instead of ~3.4 s (the worst case carries the 200 ms tail delay above; a live raid's steady traffic mostly hides it behind the floor's phase).
The unknown term in that sum is `DerivedFightRows.Build`, which runs on every lane and formats four columns per row over *every row the capture ever
produced* (4,644 on one capture); it lives in the app assembly, so its number is `EQLogParser.Wpf.Test/src/control/util/DerivedSnapshotCostTest.cs`
(Windows-only, 5,000 synthetic rows bound at 60 ms). If that ever climbs toward tens of milliseconds, widen the cheap lane by row count before
touching either floor.

**Two tests worth naming.** `TwoCheapPassesOverOneSetOfVerdictsMatchASingleFold` (increment suite) grows a capture in three folds under one timeline
and compares against a single fold over the same swings: staleness in this lane is allowed to mean "a verdict the next full pass has not reached", never
"facts that went missing" — and it also asserts the carry survives consecutive cheap passes, because a cheap lane that rebuilds every 500 ms would be
slower than no cheap lane at all. The cadence suite now asserts *which* pass is due rather than "some pass": `IsFalse` on a live tail used to hide the
fact that half a second later it would have been a perfectly good refresh.

One consequence worth stating because somebody will try to "simplify" it away: an expensive pass writes two Info lines ("derive starting", "derive done");
a cheap pass writes the same two at **Debug**. Two passes a second of Info logging would push the raid itself out of the file this app writes, which is the
file anyone debugging a report is reading.


### Re-measured after a season of identity rules (2026-10)

With the rule count up from 14 to 20 and the casting-signature families at fifteen, the same Incogitable
benchmark reads **classify 309 ms + project 435 ms = 744 ms** for a full pass (recorded 732 ms before the
season began), continued projection still **0-5 ms**, quiet-tick projection **0 ms**. The whole batch of
rules added since - pet spells, the class-ambiguous gate, frenzy verb classes, ten new spell families -
costs tens of ms on the classify sweep, which runs only at the full cadence (~1 s even on a live tail).
It is never per line: the family gate evaluates once per *distinct (caster, spell) claim* inside R4, and
the capture path gained no check from any of it. The classify term remains dominated by the same five full
sweeps (R9/R18/R15/R7/R6) as when they were itemized; a rule that outgrows them is the trigger to make
sweeps incremental, and the gated benchmark is what shows that - not speculation.

## Handlers that XAML fires early (2026-10)

Two startup crashes in `MirrorFightTable`, one build apart, taught the same lesson twice — and the codebase already knew it.

**Crash 1** (`f2438c88` shipped the HP checkbox): `NullReferenceException` at `ShowHpChanged`, inside
`MirrorFightTable.InitializeComponent`, out through `MainWindow..ctor` → `CreateAppError`, every launch. XAML sets
`IsChecked="True"` on a CheckBox whose `Checked/Unchecked` point at that handler, and WPF raises the event *as it applies the
property* — mid-parse, when no named field of the control exists yet. The handler read `mirrorShowHp.IsChecked` off the null field.

**The first fix was wrong in a way worth remembering.** It guarded `if (mirrorShowHp is null) return;` — testing the *sender*.

**Crash 2** (same method, one deeper): NRE at `mirrorDamageColumn.IsHidden`, meaning the handler ran **past** the sender guard —
`mirrorShowHp` was wired while `mirrorDamageColumn` (declared ~35 lines later in the markup, inside the grid) was not. So
pre-load firings are not one event at one instant: whatever the exact mechanism (the checkbox carries a custom
`Template="{StaticResource CustomCheckBoxTemplate}"`, and template materialization is a known way for a toggle to push its value
back during parse), **multiple synthetic firings land at different points of the parse, each seeing a different half-wired pane.**
A sender-null guard can only ever catch the subset where that one field happens to be null.

**The convention every legacy table already follows**: guard on the *pane's readiness* — `dataGrid?.View != null`
(FightTable, TankingSummary, DamageBreakdown with its literal `// check if call is during initialization`, HitLogViewer's
`dataGrid is { View: not null }`). This is why that idiom looks redundant until it saves you. `SfDataGrid.View` materializes only
when something assigns `ItemsSource`, and the constructor does that *before* syncing the checkboxes from saved settings:

- during `InitializeComponent`: `mirrorGrid` (or its View) is null → every synthetic firing is swallowed, whatever field state it sees;
- in the constructor's dial sync and after: View exists → real events, including `IsChecked = ConfigUtil.IfSet(...)`, run fully.

Both mirror handlers now open with `if (mirrorGrid?.View is null) return;`. The rule generalizes to any pane: **a handler attached
to a property XAML itself sets must test the one object that cannot exist until the pane is fully loaded** — not the sender, not
"some field", but the readiness sentinel the rest of the app uses.

**Test coverage, and its limits.** `MirrorFightTableStartupTest` (Wpf.Test) pins three things: plain construction (MainWindow's own
path), "a saved-OFF dial survives XAML's IsChecked=True" (the silent corruption the synthetic toggle would cause), and — the half
that keeps this fix honest — "a real post-load uncheck still hides the column and saves the dial", because a guard too eager to
swallow passes every crash test while breaking the feature. These compile anywhere but only run on Windows; **constructing themed
panes in tests needs the three app-level StaticResource keys** (`CustomCheckBoxTemplate`, `TemplateToolTip`, `EQIconStyle` live in
App.xaml), which the headless host doesn't have — the test stubs them into a bare `Application.Current`. The stubs pin the
construction contract, not theme fidelity; and a startup crash class that only a full app launch reproduces is a standing argument
for launching the built app before shipping UI code.

## The derive that survives its own rules (2026-10)

**Why: a user asked, twice, "when would anyone ever press Re-derive?" — and the honest answer was once.** The button
existed because a failed pass latched auto-derive OFF for the life of the session ("stop the loop, leave Re-derive
available"). Enumerating the triggers said otherwise: quiescence fires a full pass at load end; the cadence keeps a live
tail fresh; an identity override calls `RederiveAsync` itself (`MirrorFightTable.xaml.cs:393`); opening the derived meter
forces a full pass (`DamageOverlayWindow.xaml.cs:226`). A press in healthy life recomputes what is already on screen.
The button's entire job was the crash corner — furniture advertising our own bugs. And the latch itself predates the
two-lane cadence: when derives only ran at load-end, stopping the loop cost a pass or two; under a 0.5-3 s refresh it
froze every derived surface for the rest of the night over one hiccup, silently except for one log line.

**What replaced it, coarse to fine.** (1) A classification STAGE that throws is a bug in one check: `RunStage` swallows
it, reports the full exception on `ClassificationOutcome.FailedRules` (Core owns no logger — the session writes the player's
log), and retires *only that stage* after five consecutive failures; one clean run clears the streak, `ResetRuleHealth`
(a new `DeriveEngine`) hands the next capture — different data, possibly nothing like the poison — the full rule book.
Soundness of partial classification: rules only ADD evidence, and every consumer already handles names nothing has
placed (every log starts that way), so the degradation direction is "less known", never "wrong side"; a retired stage
changes the timeline's content digest, and the carry gate re-folds rather than mixing rule books across passes.
(2) A session-level throw retries on `DeriveCadence.RetryDelayS` — 1 s, doubling, capped at 60 s, zeroed by any
completed pass; a transient fault (locked file, AV scan) vanishes inside a second, deterministic poison becomes a slow
repeating stack in eqlogparser.log, which *is* the diagnosis. (3) `IdentityPriorStore.Record` — a write for the NEXT
log's benefit — cannot fail a pass at all: wrapped, logged, boards untouched. The Re-derive button and its handler are
deleted; the fight table's failure status says "Derive failed (retrying automatically)". Tests: the guard laws (streak
arithmetic, retire-self-only, reset-on-new-session, never-silent) and the ladder's shape.

## The EMU corpus joins the parity battery (2026-10)

**Why: every real-log measurement to date was one log format.** The reorganized corpus puts live captures under
`local/logs/live/` and EMU-server captures under `local/logs/emu/`; the latter parse differently — Heroes Forge
`(Owner: X)` attacker lines, old-EMU criticals (`scores a critical hit! (9110)` pairing with the next hit line),
absorbed-damage shapes — behind the app's `EnableEmuParsing` setting, which only `DamageLineParser` reads. The capture
ingest rides that same parser, so this was the first time the capture/classification/projection stack ever saw those
grammars. `PipelineHarness` now honours `EQLP_EMU=1` (sets `AppSettings.IsEmuParsingEnabled` per run, restores it —
process-global flag, live logs misparse with it left on).

**Sweep, `Boards_RealLog_PerRaiderParity`, all PASS:**

| capture | facts (dmg/heal) | legacy→derived rows | raid damage | population law |
|---|---|---|---|---|
| Kugon (721 B old-EMU duel) | 7 / 0 | 1 → 1 | **34,073 = 34,073 exact** | 1/1 |
| Silresa (0.6 MB) | 4,472 / 13 | 40 → 40 | **833,145 = 833,145 exact** | 5/5, healers 2/2 strict |
| Catenza (2022, 1 MB) | 11,155 / 0 | 4 → 4 | −3.7 % (bare pet `Luna` → owner `+Pets`) | 23/23, absent 0 |
| Bulron (148 MB) | 707,860 / 25,455 | 13,505 → 13,503 | +0.02 % | 35→36, absent 0, healers 33/33 strict |
| Ikky (424 MB) | 1,498,088 / 22,703 | 24,958 → 24,955 | +0.07 % | 51→52, absent 0 |
| Roper (790 MB) | 4,356,144 / 33,805 | **44,729 → 44,728** | +1.7 % | 18→22, absent 0 |

The known findings all held on a foreign grammar: nobody legacy lists is ever absent (`absentFromDerived=0` six for
six), differences are the recorded pet-folding renames, and healing parity stays strict where heals exist (a melee-only
capture legitimately shows nothing — the boards test's "never empty" law was healed into "empty means BOTH doors
silent", because `eqlog_Kugon_thornblade.txt` is nine lines of duel with no heal anywhere). Live regression alongside:
Incogitable at its new path reproduces the recorded numbers byte-exact (578,233,842,799 → 581,343,395,690, 373/373
healers, zero heal diffs), and its fight rows now count 4,471 = legacy's 4,471 exactly, where the first recorded run
said 4,312 — the row-per-life split work closing that gap. The emu tanking line (Roper: legacy 35.5 M all-comers vs
21.4 M people, derived 21.4 M) is the same "legacy's board counts NPCs" census as live logs.
