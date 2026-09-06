# NAG Floating Combat Text — Reference Notes

Reverse-engineered notes on the FCT implementation in [NAG](https://github.com/guildantix/eq-parse) v0.2.16
(`local/nag-source-v0.2.16/`, Electron + Angular). Kept here as a design reference for our own FCT feature.

## Pipeline architecture

```
combat log file ──▶ combat-watcher.js (separate process)
                        │  FileMonitor streams new lines
                        ▼
                    CombatParser.addCombatEvent(line)  →  FctModel?
                        │  ipcRenderer.send('overlay:send:fct-component', fcts[])   [batched]
                        ▼
                    main (window-manager.js:483) ──▶ renderer.js 'renderer:receive:fct-component'
                        ▼
                    renderFct2(model)  →  DOM in borderless overlay window
```

- `src/electron/threads/combat-watcher.js` — watches the character log file; each new line goes through
  `parseLogEntries()`, FCT models are batched per read-chunk and sent via IPC (main relays to renderer).
- `src/electron/utilities/combat-parser.js` (~3k lines) — regex-based log parser; also maintains
  encounter/DPS stats. A `TakpCombatParser` variant exists for other game logs.
- `src/electron/threads/renderer.js → renderFct2()` — the current FCT renderer. An older standalone FCT
  window (`overlay-fct.js/.html`, marked "TODO: Delete this overlay") and an in-renderer v1 path
  (`renderFct`) still exist for legacy style presets.

## Log parsing → FctModel

Named-capture regexes in `combat-parser.js` (~lines 187–213) classify lines:

| Regex | Matches |
| --- | --- |
| `MeleeDamage` | "You **hit/backstab/bash/claw/rend/strike/sweep** *mob* for N points of damage." — one big verb alternation; optional `(Critical! ...)` modifier group |
| `MeleeMiss` / `ActiveDefense` | dodged/parried/riposte/block/miss |
| `DirectDamage` | "*actor* hit *target* for N points of *fire/cold/physical* damage by *spell*" |
| `YourDoT` / `OtherDoT` / `SelfDot` | "has taken N damage from your *spell*" |
| `Healing` | "*actor* healed *target* (over time for) N (M) hit points by *spell*" — captures over-heal (`healActual` vs `healLiteral`) |
| plus Thorns, deaths, charms, defensive-up lines | |

`_getFctModel()` (`combat-parser.js:2010`) converts a log record into an `FctModel`:

- Sets **combat-type booleans** based on who acted/was hit: `myHits`, `otherHitsOnMe`, `mySpellHits`,
  `otherSpellHitsOnMe`, `myHealing`, `otherHealingOnMe` (spells split from melee; healing split by direction).
- Evaded hits set `avoidType` (dodge/parry/riposte/block/miss) and leave `amount = 0`; models with
  `amount <= 0` are **dropped** — misses never produce FCT text, they only feed stats.
- `action` = skill name ("Sweep", "Fireball"), `damageType` = resist type, `overHealing`.
- **Modifiers** parsed from the parenthetical into a list: `critical`, `crippling_blow`, `flurry`, `lucky`,
  `twincast`, `riposte`, `strikethrough`, `wild_rampage`, `rampage`, `assassinate`, `headshot`,
  `double_bow_shot`, `deadly_strike`, `finishing_blow`; defaults to `normal` if none.

`FctModel` (`data/models/fct.js`) is a flat DTO: actor/target, amount, action, modifiers, combat types,
timestamp, characterId, plus render-side state (dom, intervalId, pos, accumulationPeriod).

## User config: FctCombatGroup (`data/models/overlay-window.js:301`)

Each group = one "column of numbers" on screen. Per group the user configures:

- **Which events** — the 7 combat-type checkboxes + modifier checkboxes, both compiled to **bitmasks**
  (`_combatTypesFlags`, `_combatModifiersFlags`; 15 modifier slots).
- **Value/source styles** — two `StylePropertiesModel`s (`data/models/core.js`): font family/size/weight/
  color/transparency, padding, inline/block, justify, plus **border and glow both implemented as
  multi-directional `text-shadow` stacks** (5-point outline + 5-point blur).
- **Starting position** — bitmask `HitStartPositionTypes` (left=1, right=2, bottom=4, top=8, random=16) with
  mutual-exclusion rules enforced in the editor UI (can't be top+bottom etc.; random excludes everything else).
- **`accumulateHits`** and/or **`ignoreHits`** + threshold (`percent` of max-hit or absolute value).
- **`combatAnimations`** — checkboxes: blowout, fountain, scroll, fadeIn, fadeOut, grow, shrink.

Groups are matched **in list order** (user can reorder); defaults ship in `data/predefined-fct-groups/*.json`
(my-hits, my-critical-hits, other-hits, healing, ...), and configs are shareable via
`{NAG:fct/<id>}` quick-share codes.

## Matching + smart thresholds (`renderer.js`)

- `getRenderGroup(model)`: a group matches when `(model.combatTypesFlags & group.flags) ===
  model.combatTypesFlags` (group's types must be a **superset**) and its modifiers match; exact modifier
  match wins immediately, partial matches are kept as fallback.
- **Rolling median per (character, group)**: up to 1000 recent hits stored sorted, persisted via IPC
  (`combatGroupHits`/`combatGroupMedian`, debounced save). `maxHit = 2 × median`.
  - `ignoreHits`: discard hits below threshold (% of max or absolute) — "only show big hits".
  - `accumulateHits`: hits **below** the threshold don't get their own popup; they're folded into the most
    recent live component with identical flags. The count-up animates over **750 ms at 25 ms ticks**
    (`accumulatHits`), and the source label appends the skill name (truncated to ~37 chars + `...`).
- Display values use `toShorthandString`: `<1000` raw, `1,234` comma, `12.5k`, `123k`, `1.5m`.

## Rendering & animation (`renderFct2`, `renderer.html`)

DOM per hit — three nested layers so independent CSS animations can compose:

```html
<div class="fct-values">              ← primary animation (blowout/fountain)
  <div class="fct-sub-animation">     ← horizontal drift (fountain)
    <div class="fct-text-layer">      ← grow/shrink
      <span class="fctText">1,234</span>
      <span class="fctSource">(Sweep)</span>
```

Each FCT overlay contains 5 absolutely-positioned flex columns:
`.fct-content.top-left/.top-right/.bottom-left/.bottom-right/.random`. Top groups `insertBefore(firstChild)`;
bottom groups append (column, justify-end → new hits push upward).

Keyframes (`renderer.html` ~line 464):

- **blowout** — scale 1.5→2 in 1%, hold to 50%, shrink to 0.1; 4 s ease-out (variant adds fade at the end).
- **fountain** — randomized duration (1 s ±25%); primary keyframe rises `--random-y` (100–150 px, sign
  flipped for top-start groups) with cubic-bezier ease-out, then falls a little extra (`--y-direction` ∓40 px)
  in an arc; secondary keyframe drifts `--random-x` (±100–150 px). Per-hit CSS custom properties set inline.
- **fadeIn** 500 ms; **fadeOut** defaults to 7 s, or matches the longest active animation, keeping opacity 1
  until 77% before fading; timing fn flips (ease-out vs ease-in) depending on which duration applies.
- **grow/shrink** run on the inner text layer only; shrink is delayed so it happens at the very end (or spans
  the full fountain duration).

Composition logic (mirrored in the editor's live preview,
`floating-combat-text.component.ts:applyGroupAnimations`): blowout/fountain/scroll are mutually exclusive
primaries; fadeOut + fadeIn appended after; grow/shrink last. Removal is a single
`setTimeout(totalAnimationDuration)`.

**Random/critical grid**: the overlay is pre-divided into cells sized from an estimated hit footprint
(`qw = fontSize/2·4.6875·2.5`, `qh = fontSize/2·3.125·2`). `getRandomLocation()` load-balances by preferring
columns with ≤ average occupancy, marks a cell occupied, and frees it at **66.7%** of the animation (spot is
reusable while the old one still fades). If every cell is full, the new hit is **merged into the most recent
critical's value** (count-up).

## Notable quirks / things to keep in mind

- FCT only fires for lines where the actor *or* target is a tracked character (`_fctCharacters`), so you get
  "my damage" and "damage on me" but nothing between other NPCs.
- The legacy standalone `overlay-fct.js` renderer staggers crits (500 ms ±50% random delay, 4 s display) and
  splits healing into a separate `.healing-content-area`; the new path instead relies on per-group
  position/animation choices.
- Overheal is captured but not yet differentiated by direction (explicit TODO: "Healing — incoming excludes
  overheal while outgoing shows overheal").
- TODOs exist for combined-hit bump animations and DPS integration; legacy duplication (`renderFct` vs
  `renderFct2`, `overlay-fct.js`) is intentionally left in for migration.
- No canvas/WebGL — all DOM + CSS keyframes, cheap enough for their use case (a few popups per second) and
  makes per-element randomization via CSS variables trivial.

## Reusable ideas for EQLP

1. Bitmask group-matching model with ordered fallbacks.
2. Median-based accumulate/ignore threshold.
3. Three-layer element composition for stacking independent animations.
4. Cell-grid with occupancy balancing + merge-into-last-crit fallback.

## EQLP design direction (in progress)

Decisions made while planning the EQLP implementation, recorded so they are not quietly reversed.

### Record-driven feed — no direct log interaction

FCT consumes parsed **records**, never raw log lines:

- **Damage** — consume the existing live `DamageLineParser.EventsDamageProcessed` (`DamageRecord`
carries type, subType/skill, amount, `ModifiersMask`, attacker/defender owners). Same pattern as
  `FightManager` already uses. `DamageRecord` has no durable store; the event *is* its live interface.
- **Healing** — `HealRecord` is built in `HealingLineParser` but only written to `RecordsStore`, with no
  live event. Add a symmetric `EventsHealProcessed` right next to the existing
  `RecordsStore.Instance.Add(record, ...)` call. (Polling `GetHealsDuring` was considered and rejected:
  timer granularity for zero benefit when the record is already in hand at write time.)
- **Live-only gating** — both parser paths also fire during historical replay when a log is opened.
  `LogReader` already tags each line: the initial load loop emits non-monitor lines, the live tail loop
  emits monitor lines. That flag is threaded `LogReaderItem.IsMonitor → LineData.IsMonitor →` the
  processed events (`DamageProcessedEvent.IsMonitor`, `HealProcessedEvent.IsMonitor`), and `FctManager`
  simply drops non-monitor records. No session state, no timestamp heuristics.
- Group matching, median/accumulate and ignore thresholds port from NAG (see above).

### Rendering — no Storyboards, no new dependency (initially)

- **Rejected:** `Storyboard`/`DoubleAnimation` clocks — one running clock per animated element means
  dispatcher + GC pressure at FCT hit rates. Rejected: per-hit `TextBlock` + `DropShadowEffect` — element
  churn on spawn/teardown plus per-element GPU effect rasterization.
- **Adopted:** a single custom `FrameworkElement` (see `EQLogParser/src/ui/control/FctSimCanvas.cs`,
  started as the simulation renderer). `CompositionTarget.Rendering` drives one per-frame update pass;
  `OnRender` does `DrawText` for every active hit; each hit is plain data with a **cached `FormattedText`**
  that is only re-laid-out when its displayed value changes (count-ups). Outline/glow use NAG's exact
  text-shadow trick in vector form (NAG's 5-point outline, crit-only diagonal glow approximation — its
  real glow is a wide radial blur the browser rasterizes for free) instead of GPU effects. Zero idle
  cost: the render loop unsubscribes when no hits are active.
- **SkiaSharp backend (user-approved, implemented):** `EQLogParser/src/ui/control/FctSkiaCanvas.cs`
  renders all hits into one CPU-raster `SKSurface` per frame and blits it as a single image.
  Outline collapses NAG's 5 shadows into a two-pass `StrokeAndFill` + fill; glow is a true Gaussian
  blur (`SKMaskFilter.CreateBlur`) baked once per unique crit value into a ref-counted halo sprite.
  Both backends implement `IFctCanvas` (feed + lifecycle) with the tuning counters split off into
  `IFctDiagnostics`, so production code targets one interface and the winner of the A/B test is the only thing
  that ships. `TryAccumulate` left the interface once folding became shared ingest policy (`FctIngest`): a
  renderer should not be asked to decide whether a hit deserves its own number.
- **Package pinning:** core `SkiaSharp` only, pinned to **3.119.2** — the last line whose core still
  targets plain net8 (3.119.4 dropped it; 4.x changed the text API and its assets target net9/net10).
  `SkiaSharp.Views.WPF` was initially referenced for one `ToWriteableBitmap()` extension and has been
  dropped: every one of its dependency groups pulls OpenTK — below a Win10-19041 project platform it
  resolves through the net4x build (NU1701), and at ≥19041 its net8 asset demands the OpenTK 4.3 family,
  which conflicts with KokoroSharp's OpenTK 5.0.0-pre.13 (NU1608, cascading into XAML markup-compile
  failures). The CPU-surface → WPF blit is now inlined in `FctSkiaCanvas` (`EnsureSurface` + `Blit`: pinned
  `ReadPixels` of Bgra8888/premultiplied into WPF's native `Bgra32`, no conversion). The destination
  `WriteableBitmap` and its pinned copy buffer are allocated per resize rather than per frame, and painting is
  capped near 60 Hz by `FctFramePacer` (whole ticks, not an elapsed-time threshold) because
  `CompositionTarget.Rendering` fires at display refresh — see
  docs/DesignNotes.md → Floating Combat Text.
- **Rejected outright:** WebView2 (could port NAG's CSS keyframes unchanged, but adds a browser runtime
  for text popups) and Direct2D via Vortice/SharpDX (we would hand-roll DirectWrite layout; the scale does
  not justify it).

### A/B verdict (measured, ×10 raid-scale simulation)

- **Vector (`FctSimCanvas`): under 30 fps** and visibly falling behind — even after eliminating render
  layers, matching NAG's shadow stack and switching to aliased rasterization, the per-hit display-list
  path plateaus. Confirms the ceiling is WPF's vector pipeline, not the machine.
- **SkiaSharp (`FctSkiaCanvas`): ~100 fps** and visually much better — true Gaussian crit glow, no
  perceptible lag at ×10. The single-image blit holds steady because per-frame cost scales with C++
  draw ops (~5/hit), not WPF display-list elements.
- **Decision:** the SkiaSharp backend is the production renderer. `FctSimCanvas` stays in the tree only
  as the A/B reference behind the second Tools entry; retire it once FCT ships.

### Phasing

1. **Performance simulation** (done, verdict above): `Tools → FCT Simulation (Vector, Test)` and
   `Tools → FCT Simulation (Skia, Test)` each run a fixed-seed 60-second, raid-scale (~7,500 records at
   ×10 rate, bursts to ~200/s) simulation on one backend and report fps / active hits / frame ms /
   draw ops per second in the header. Same record stream, same lane/stack/fold logic, two renderers.
   **Winner: SkiaSharp.**
1b. **Layout v1** (user-specified, implemented in both canvases): hits spawn in the bottom third and
   float upward with a sideways arc that grows as they rise; incoming lanes (damage taken red,
   healing received green) arc within the left half of center, outgoing lanes (damage dealt yellow,
   crits orange, heals dealt green) within the right half. Crits keep the blowout + glow but stay on
   the half of their source lane. Display time starts at 3.5 s — half NAG's 7 s default, because at raid hit rates
   7 s of overlapping numbers is unreadable and a lane should be clear between pulls — and is compressed per lane by
   predicted congestion (`FctLifeController`: lifetime = slack/live-rate, 1 s floor; crits fixed).
1c. **Layout v2 — direction went vertical** (current default): my hits rise out of the top band and hits on me sink
   out of the bottom, both travelling *away* from a protected strip across the middle; x now means *what* (damage
   toward the middle of a band, healing out wide, crits and labels centered) instead of *who*, and colour carries no
   direction at all (nothing is blue, labels are hueless, amber only for `Invulnerable`/`Absorb`). The left/right split
   that shipped first has since been **removed**: two region schemes meant every style added afterwards had to be told
   about the other one, and the cell grid could not lay out non-overlapping columns inside a half. Rationale:
   `docs/DesignNotes.md` → "One region scheme, and why direction went vertical".
   Deliberate NAG deviations: no fixed flex columns (free float + arc
   instead), and **no crit cell grid** (decided): crit emphasis is larger size + orange + glow/blowout,
   drawn last so crits cover non-crits (two-pass draw order in both canvases) — that was judged enough.
   Rise distance/lifetime is EQ-style (long float) rather than NAG's 1 s fountain.
   NAG offers animation as a bag of independent checkboxes (`combatAnimations`: blowout, fountain, scroll, fadeIn,
   fadeOut, grow, shrink — with `fountainSubDrift`, `blowoutSubZoomOut`, and direction variants per style); ours is
   **one** `FctMotionStyle` per hit — hold / fountain / pulse / spray — with fade always on and crit blowout always
   layered on top. Mutually exclusive choreographies were already mutually exclusive in NAG, and the combinable ones
   (grow plus shrink, drift plus scroll) produced numbers whose motion told contradictory stories. Rationale:
   `docs/DesignNotes.md` → "Four motion styles, and the one thing none of them may change".
   Party-wide healing (other chars' heals) is not fed yet; it lands with group config.
2. Record plumbing (done): `EventsHealProcessed`, per-line `IsMonitor` gate, `FctManager` (records →
   lane-matched hit batches; smart group matching comes with the config phase).
3. Group config: editor UI, styles, starting positions, animation choices, JSON persistence in the config
   dir, import/export. Not built: the overlay runs on fixed per-lane styles from `FctStyle`.
4. Smart features: **folding is implemented, and it counts instead of summing** (`FctIngest`) — a hit folds into the live
   number already showing that exact amount for that exact ability, which draws as `2,040 ×2`; periodic DoT/HoT ticks are the
   stream it exists for, healing never folds, and the lane cap folds and evicts before it drops (and counts what it loses).
   NAG's median-based accumulate threshold is **not** implemented and was deliberately removed once folding stopped adding
   amounts up: it decided when it was acceptable to inflate somebody else's number. Deviation from NAG stands: nothing is
   *ignored* — NAG can drop hits below its threshold, here they either appear or are counted. Per-character enable waits on group config.
   (Crit cell grid evaluated and rejected — see layout v1 above.)

### Product pass (v1): what changed once it fed real records

Recorded so these are not mistaken for NAG ports or quietly reversed; full rationale in docs/DesignNotes.md →
Floating Combat Text.

- **Feed:** `FctManager` publishes a `ConcurrentQueue` drained once per painted frame rather than raising one
  event per record, drops commands older than 500 ms, and is gated by `Enabled` so an overlay nobody opened costs
  the parse loop nothing. `Create()`/`Dispose()` pair with the window's life.
- **Model:** one `FctLane` enum in Core (`FctSimLane` deleted); `Source` carries a bare ability/verb name —
  wrapping it in parentheses belongs to the renderer, and doing it in both places drew `((Fireball))`.
- **Zero-damage labels:** commands carry `ValueText`, and `FctMotion.RefreshText` guarantees a label is never
  replaced by its formatted `Value` (which is 0 — Dodge was drawing as "0").
- **Shared policy:** layout, motion, lifetime and folding extracted out of the two canvases into `FctIngest`,
  `FctLayout`, `FctMotion`, `FctStyle`; a backend keeps only substrate resources and its draw loop.
- **Usable in game:** click-through lock (`WS_EX_TRANSPARENT`/`WS_EX_NOACTIVATE`) unlocked from the Tools menu, geometry
  and lock/enabled persisted through `ConfigUtil` plus region scheme and motion style through `FctOverlaySettings`
  (`FctOverlayLayout`, `FctOverlayMotion`), hidden overlay stops its canvas and gates the feed,
  and a protected center band keeps numbers off the target/cast area.
- **Still NAG-shaped:** lane colors and sizes, two-pass outline, crit blowout + Gaussian halo, count-up folding,
  adaptive lifetime.
