# Combat Text Overlay: Design and Implementation Plan

## Recommendation

Build one preset first: **Classic**. Place incoming damage and received healing on the left, outgoing damage and healing on the right, and scroll both streams upward. Keep an empty area between them for the player and target. Use red for incoming damage, green for healing, and warm white/yellow for outgoing damage.

Launch with useful defaults and a preview, not a configuration wizard. Later add **Curved HUD** and **Sprinkler** as two complete presentation presets. Keep event routing and colors consistent across all three. Users should choose an overall appearance, not separately choose an arrangement, animation, and direction for every event.

These are proposed product defaults, not universal game conventions. The references below establish precedents for fixed scroll areas, event routing, animation choices, and filtering; exact dimensions, timing, and priority rules here are starting hypotheses to test.

## 1. Scope and constraints

The overlay receives combat information but does not know where the player, enemies, or allies appear on screen. The user positions its center near the usual center of combat. It must therefore present events in fixed screen areas rather than imply that a number belongs to a particular visible character.

Keep the center clear for every frame of an animation, including text width, crit enlargement, and icons. Do not attach text to an assumed target position. On multiple-target attacks, a right-side number means outgoing damage, not damage to whichever enemy happens to be under it.

Healing means health restored; HoT means healing over time. Incoming and outgoing describe the local player's relationship to the event. A self-heal is both mathematically, but should normally produce only one visual entry.

## 2. What combat text is useful for

Combat text provides immediate confirmation that an action connected, feedback about effectiveness, and the satisfaction of seeing significant hits. Non-numeric results such as immune, resist, reflect, interrupt, and dispel can be more actionable than routine damage totals. These benefits recur in player discussions, alongside strong complaints about visual clutter. [1–4]

It is not a replacement for a damage meter or combat log. Dense streams are difficult to read precisely, and cumulative performance belongs in a summary. Preserve complete events in the existing log/parser even when the overlay groups or suppresses their presentation.

The design goal is readable feedback during actual combat. Showing every recorded event is not a success criterion.

## 3. Conventions versus design choices

| Aspect | Established precedent | Decision for this overlay |
|---|---|---|
| Color | Green healing; red incoming damage; white/yellow outgoing damage are recognizable conventions, with variations | Keep these semantic colors; provide an accessible palette later |
| Location | MMO addons use separate fixed incoming/outgoing areas; some separate incoming healing too | Incoming left, outgoing right |
| Above/below | No universal incoming/outgoing mapping established by the reviewed sources | Reserve a small upper notification area for later |
| Motion | Vertical scrolling, arcs, angled motion, and other styles are documented | Straight upward first |
| Damage versus healing motion | No established requirement that they move in opposite directions | Use the same motion within an area |
| Critical events | Size/animation emphasis is a common presentation technique | Modest initial enlargement; retain semantic color |
| Periodic events | Repetition and AoE spam motivate grouping and filtering | Group repeated related ticks without losing totals |

MSBT documents independent areas and event styling. SCT explicitly documents vertical, horizontal, angled up/down, sprinkler, and curved/angled HUD animation styles. Neither establishes a universal direction for a particular event category. [5–7]

## 4. Version one: Classic

### Event routing

| Event | Area | Appearance and default behavior |
|---|---|---|
| Damage received | Left | Red; upward movement |
| Healing received | Left | Green with a plus sign; upward movement |
| Damage inflicted | Right | Warm white/yellow; upward movement |
| Healing performed on others | Right | Green with a plus sign; upward movement |
| Self-healing | Left only | Green; suppress duplicate outgoing presentation |
| HoT/DoT ticks | Corresponding incoming/outgoing area | Same category color; normal emphasis; eligible for grouping |
| Crit | Corresponding area | About 1.2× initial size, settling quickly; no special lane |
| Immune/resist/interrupt | Corresponding area, when available | Short explicit label; distinguish failure from successful interrupt |
| Pet events | Hidden initially | Later expose as a single opt-in category |

Do not infer an interrupt, effective healing, or periodic classification if the input cannot identify it reliably. Feature availability should follow parser capabilities. Full resists and partial resists should not receive the same failure label.

### Layout and animation starting values

Use device-independent units so scaling behaves predictably. These are internal defaults to tune in preview, not separate settings to expose at launch.

| Parameter | Proposed starting point |
|---|---|
| Clear center width | 240 units, measured between inner text boundaries |
| Each stream width | About 220 units |
| Stream height | About 180 units |
| Text size | 22 units with a dark outline |
| Lifetime | 1.5 seconds |
| Motion | Constant upward motion, from bottom to top of stream |
| Fade | Final 0.3 seconds |
| Crit emphasis | About 1.2× for the first 0.15 seconds, then normal |
| Spacing | At least one rendered line height plus padding |

Align left-stream entries against its inner right boundary and right-stream entries against its inner left boundary. Text grows away from the center. Clamp unusually long labels within the stream; do not let a large number or crit cross the protected gap.

Keep entries moving smoothly; avoid bouncing or repeated scale pulses. A new event should not visibly rearrange every existing entry.

### Small initial settings surface

Expose only enable/disable, text size, spacing between streams, and a **Position / Preview** action. Position mode lets users drag the overlay as one unit. Outside position mode, the overlay should pass clicks through to the game.

Preview should show incoming damage, healing, outgoing damage, a crit, and a busy burst. It should not require entering combat. Save placement automatically and offer Reset. Add the preset selector only when another preset actually exists.

Do not initially expose independent coordinates, speeds, directions, colors, fonts, or animation curves for each category. Keep useful advanced controls collapsed when they are eventually added.

## 5. Handling busy combat

Basic overload handling belongs in version one. Otherwise the default will look good in a quiet preview and fail in a raid.

Use a short, fixed aggregation window, initially around 200 ms, for rapid related events. Key grouping by direction, damage/healing category, ability when known, recipient, and periodic/direct classification. Keep misses and other result labels separate from numeric totals. Do not sum healing and damage into a net number.

For example, three related DoT ticks can display as `12.4k (3 hits)` rather than three overlapping entries. Do not merge different targets by default; deliberate AoE grouping can come later with an explicit target count. If grouping crits and normal hits, track both counts internally and avoid presenting the entire sum as a crit.

A fixed window must flush at its deadline even if more events arrive; continuously extending it can delay feedback indefinitely. Important result labels should bypass routine tick aggregation. A small batching delay is acceptable for routine spam, but should be evaluated during testing.

Bound active text and pending work. With 180 units of travel and 1.5-second lifetime, spacing implies only a handful of readable entries per second per stream. Under heavier load, merge or suppress lower-priority routine presentation rather than building seconds of delayed text. Never delay an interrupt or immunity behind a backlog of ticks. Track dropped visual entries during development so overload is observable.

Prefer effective healing if the parser supplies it reliably; hide zero-effective overhealing by default in that case. If only raw healing is known, show it without claiming it is effective healing.

## 6. A small preset catalog

The preset names below describe this product's intended behavior. They are inspired by existing addons, not claims that all addons implement these names identically.

| Preset | Arrangement and motion | Purpose |
|---|---|---|
| **Classic — default** | Left incoming, right outgoing; straight upward streams | Most predictable and easiest to read |
| **Curved HUD** | Same routing; entries rise along mirrored outward curves | Frames central action with slightly more movement |
| **Sprinkler** | Same routing; entries launch outward on short, bounded arcs | More energetic feedback and prominent hits |

Classic should be the only implementation required to launch. Add Curved HUD next and Sprinkler last. Keep size, position, filtering, and semantic colors when changing presets; replace only preset-owned motion and geometry. Show a short live preview for each choice.

For Curved HUD, keep the entire curved path outside the central exclusion area. For Sprinkler, incoming entries launch left and outgoing entries launch right. Use a small deterministic set of trajectories rather than unconstrained random scattering. Cap the footprint and number of simultaneous entries. Crits may pop more strongly but still obey the same bounds.

Do not add an independent arrangement selector alongside these presets. If users later request a vertically stacked layout or a dedicated healing area, add one complete preset after validating the need. Avoid a matrix of layout × direction × animation × category settings.

## 7. Features to add next, in order

### Phase 1 — reliable default

Deliver Classic, four core damage/healing categories, self-heal deduplication, simple crit emphasis, readable number formatting, preview/position mode, saved placement, and bounded grouping. Include available immune/resist results in the existing streams. Verify click-through behavior and display scaling.

Acceptance: the overlay is understandable without setup beyond placement; the center stays clear; sustained bursts remain current and bounded; healing and damage are never conflated.

### Phase 2 — reduce noise and improve accessibility

Add a small collapsed Details section: received healing on/off, outgoing healing on/off, periodic events on/off, pet events on/off, and optional minimum amount. Offer Quiet mode as one bundled filter choice rather than a new presentation preset. Quiet mode retains significant direct hits and actionable results while reducing routine ticks.

Add an accessible color palette and reduced-motion option. Reduced motion should use a stable short-lived stack/fade, with the same routing. Keep plus signs, position, and explicit labels so red/green color discrimination is not required.

Acceptance: a healer, tank, and damage dealer can each remove their main source of noise without a complex editor.

### Phase 3 — Curved HUD and dedicated notifications

Add the second visual preset and its preview. Introduce a compact notification area above the protected center for meaningful parser-supported events such as successful interrupts or important failures. Give notifications a short lifetime, deduplicate repeats, and bound their stack. Avoid showing the same notification in two places.

Acceptance: motion remains readable at the same load as Classic; alerts do not conceal the target or accumulate after a burst.

### Phase 4 — Sprinkler and richer event context

Add Sprinkler after load behavior is proven. Consider ability icons or short names, explicit AoE totals/target counts, and optional separate pet presentation. Make these additions selective: attaching a full ability and target name to every number can recreate the combat-log clutter the overlay is meant to reduce.

Defer a free-form area editor, custom curves, per-event fonts/colors, trigger scripting, and extensive profile management until actual usage demonstrates a need.

## 8. Implementation structure

Keep combat parsing, presentation policy, and drawing separate. Normalize incoming records into timestamp, direction, category, amount, ability/recipient identifiers when available, critical/periodic flags, and result type. Mark unknown fields explicitly.

Pass those records through self-heal deduplication, filters, bounded aggregation, priority handling, and formatting. The result should be a display entry containing text, semantic style, destination area, timestamp, and lifetime. A preset determines its path, not its combat meaning.

Use one animation clock to update active entries. For each entry, calculate normalized age from elapsed monotonic time, then derive position, opacity, and scale. Do not tie speed to the number of rendered frames. Reuse visual objects where practical and cap active objects.

For a WPF implementation, batch event delivery to the UI dispatcher instead of dispatching every log line independently. Keep UI access on the UI thread and animate transforms/opacity rather than rebuilding the whole layout each frame. These are architecture suggestions, not a requirement to port a WoW addon line for line.

Store a preset identifier, overlay position, scale, gap, and a small filter configuration. Version the configuration so future presets can be introduced without resetting user placement.

## 9. Focused validation

Replay representative event sequences: single-target attacks, fast DoTs, simultaneous incoming damage/healing, self-heals, AoE bursts, and multiple crits. Include long amounts and names, different window sizes and scaling, and a sustained overload period followed by silence.

Check that text never crosses the protected center, the last burst does not continue displaying stale events seconds later, self-heals display once, aggregation totals are correct, and results such as immune remain visible. Compare Classic and later presets using the same replay. Evaluate CPU use and allocation during the busy replay, not only an idle overlay.

## 10. Implementations and discussions to study

**MSBT: first reference for event organization.** Its documented model includes independent incoming/outgoing/notification areas, per-event styling, AoE merging, and spam controls. The linked GitHub repository is a community-maintained fork; it is a design/source reference rather than an endorsement of current game compatibility. [5]

**SCT: first reference for movement.** The source includes `sct_animation.lua`; its README describes eight animation styles and two independently configurable animation frames. Study how rendering is separated from event assignment. [6–7]

**xCT/xCT+: alternative area organization.** Original xCT documents four frames: incoming damage, incoming healing, notifications, and outgoing damage/healing. This is a useful precedent if a dedicated healing-area preset becomes necessary. [8–9]

Player discussions provide qualitative feedback, not controlled evidence of performance improvements. The exact defaults in this proposal should be validated against this overlay's event volume and available log data.

### Sources

1. [Game developer discussion: making good floating damage text](https://www.reddit.com/r/gamedev/comments/18t0eet/what_are_things_you_found_out_about_making_good/) — motion, font readability, crit emphasis, spacing, and fading.
2. [WoW discussion: reducing floating damage spam](https://www.reddit.com/r/wow/comments/1lm013l/the_game_is_so_much_more_playable_without_the/) — visual obstruction, periodic damage, and filtering.
3. [Sleek Combat Text discussion](https://www.reddit.com/r/WowUI/comments/1n6zuj3/addon_sleek_combat_text_update/) — hit satisfaction and actionable feedback such as immunities and target-hit confirmation.
4. [ESO scrolling combat text feedback](https://forums.elderscrollsonline.com/en/discussion/251746/scrolling-combat-text-feedback) — readability controls and tick/AoE grouping requests.
5. [MSBT description](https://www.curseforge.com/wow/addons/mik-scrolling-battle-text) and [community source fork](https://github.com/Placidina/MikScrollingBattleText) — event routing, styling, merging, filtering, and API documentation.
6. [SCT source and README](https://github.com/Thaodan/scrolling-combat-text) — animation types and independent frames.
7. [SCT animation implementation](https://github.com/Thaodan/scrolling-combat-text/blob/master/sct_animation.lua) — movement code reference.
8. [Original xCT description](https://www.wowinterface.com/downloads/info18053-xCT.html) — four-frame organization.
9. [xCT+ source](https://github.com/dandruff/xCT) — separate combat frames and spam reduction.
10. [Better Combat Text description](https://www.curseforge.com/wow/addons/better-combat-text) — explicit yellow outgoing, red incoming, and green healing color mapping.

Prepared September 5, 2026, from the sources reviewed in this conversation.
