# Legacy replacement map (working doc, 2026-08-12)

Goal: delete the per-line fight/stat pipeline (`FightManager`, `Fight`, `BattleRow`, parse-time `StatsUtil` accumulation)
and run everything off the combat mirror. This file is the measured inventory — what still reads legacy, the exact seam
each one needs, and the order deletions become safe.

## Already off legacy (measured, not assumed)

- **Fight list** → `MirrorFightTable` + `FightProjection`/`MirrorFightRows` (`mirrorFightWindow`, docked when
  `EnableCombatMirror` is on).
- **All three boards** — damage, healing, tanking. A click on a derived row hands the real builders ordinary objects:
  `MirrorSummaryFights` materializes `Fight.DamageBlocks` **and** `TankingBlocks`/segments; `MirrorSummaryHeals` fills
  `GenerateStatsOptions.Heals` because `HealingStatsBuilder` never looks at a `Fight` at all. Same builder, same grid, so
  a disagreement is a classification difference rather than two UIs disagreeing.
- **Charts** ride the builders' `EventsUpdateDataPoint`, so whatever board was last computed drives them — no legacy read.
- **Name identity surface** → `NamesTable` (View → Names) + `ClassificationReport`/`ClassificationCommands`, replacing the
  Verified Players / Verified Pets / Pet Owners panes as surfaces (their menu entries removed, panes kept only so an old
  `EQLogParserLayout.xml` still loads).
- **Cross-log memory** → `identity-priors.txt` (`IdentityPriorStore`), display fallback only.

## What still reads legacy, with the seam each needs

| file | legacy touch | replacement |
|---|---|---|
`HealingSummary`, `TankSummary`/`TankingSummary`, `DamageSummary`, `DamageChart`, `HealingChart`, `TankChart`, `ColumnChart` | **only** `FightManager.Instance.EventsClearedActiveData` — a "new log, blank your grid" signal; their data comes from the builders | one mirror event: `MirrorSession.CaptureCleared`. Six files, one line each. Cheapest win on the board and it removes the shared coupling that makes the other rows look bigger than they are |
`EventViewer` | `FightManager.Instance.IsLifetimeNpc(name)` | ask the timeline (`EntityTimeline` kind + reason), same question `NamesTable` shows |
|`DamageOverlayWindow` + `MainWindow` | `HasOverlayFights`, `ResetOverlayFights`, `EventsNewOverlayFight` | the mirror has no notion of "the fight happening right now": rows are built from a quiescent snapshot. Needs a live/current-fight concept (open row whose last fact is recent) before the overlay can move — **the main real work left** |
|`FightTable.xaml.cs` | `Clear`, `EventsClearedActiveData`, `EventsNewFight`, `EventsNewNonTankingFight` | delete with `mirrorFightWindow` as the only list; keep legacy docked behind the setting until the overlay moves, because today the overlay and this table are the same story told twice |
|parse-time accumulation (`StatsUtil.UpdateDamageStats/UpdateHealStats`, `RecordsStore`) | feeds `BattleRow`/per-line stats the boards no longer need once all consumers are mirror-side | stop calling it once no consumer reads those rows; keep `HitRecord` construction (the fact tables and the line viewers need it) |

## Order of deletion (each step independently shippable, parity as the exit test)

1. **`CaptureCleared` event** on the mirror; re-point the six boards/charts at it. Zero behaviour change, deletes the
   widest coupling.
2. **`IsLifetimeNpc` → timeline** in `EventViewer`.
3. **Live/current fight in the mirror** (`DerivedFight` span whose last fact is within N seconds + a cheap per-derive
   "current" stamp) → overlay fights, then `FightTable`'s new-fight events.
4. **Delete `FightTable`** and the legacy list dock (setting goes away; `MirrorFightWindow` becomes the fight list).
5. **Turn off parse-time stat accumulation**, then `BattleRow`, then `FightManager`'s display bookkeeping. Keep
   `RecordsStore`/`HitRecord` until the line viewers (`HitLogViewer`, `EventViewer`) are re-pointed at the fact tables.
6. Roster files stay as *inputs* forever by design (`players.txt` = membership, `mirror-overrides.txt` = verdicts,
   `identity-priors.txt` = memory) — deleting legacy is about the fight/stat pipeline, not about those files.

## Risks to keep in view

- **Parity is the oracle while both lists exist.** `MirrorComparison` and the mirror-vs-legacy tests measure the gap; on
  `mini-fight.txt` the derived board reads +19.5 % over legacy with two pets whose owner the registry never learned
  folded under their raiders. That difference is intentional and must not be "fixed" by making the mirror agree — delete
  the oracle only when nobody still needs to explain the gap.
- **Heal→fight attribution does not exist** (deliberately: `HealFactTable` shares only the name pool and sequence). The
  healing board doesn't need it; a *per-fight* healing column, or heals in the overlay, will. Plan it as its own change.
- **Priors must stay out of the derive** until parity tests can absorb the feedback loop (docs/DesignNotes.md →
  "The sighting ledger"). If they enter, `MirrorRuleExtensionsTest` and every parity number must be re-measured.
- The FCT/stat globals (`FctScale.*`, `FctLayout.*`) and `[assembly: DoNotParallelize]` obligations are unchanged by any
  of this; new mirror-touching tests inherit them.

## What is clickable today (Windows pass, in this order)

Everything below runs on mirror data already; the live damage overlay does not (step 3), so skip it until then.
Enable the mirror first: View / toolbar **EnableCombatMirror** (`EnableCombatMirror` in settings.txt) docks the derived
fight list beside the legacy table. Then load a log and let it finish ("capturing…" -> derive done).

1. **Does the mirror cost me anything while loading?** Watch the status line and scroll the legacy tables during a big
   load with the mirror on vs off. Expected: no visible difference; the derive fires once the log goes quiet
   (`Combat mirror derive done: N fights, M ms`), measured 0.8 s project + 0.4 s materialize on a 344 MB capture.
2. **One fight, two lists.** In the legacy Fight table select one mob's row -> Damage Summary; then find the same mob in
   the mirror list and click it. Expected: raid damage equal for that fight, hits equal per player, tanking tab populated
   from the same facts. Known intentional differences: pets appear folded into their owner as `Name +Pets` (legacy shows
   the pet as its own row or drops it), and derived totals may read slightly *high* where legacy dropped records whose
   attacker it could not place.
3. **Many fights, one number.** Select 10-20 consecutive rows in the mirror list (the grid settles one announcement per
   drag) and compare against selecting the equivalent range in the legacy table. Expected: same damage to within the pet
   question above; healing tab equal, since both window healing by the selection's own span.
4. **+Pets specifically.** Find a raider with a pet (petmapping.txt or an own-pet line in the log). Expected on the derived
   board: one row `Name +Pets` whose damage includes the pet; the raid total unchanged by the folding (it moves, not grows).
5. **Charmed mobs.** On a capture with `X has been charmed.` (Incogitable): the mob's own rows are off the list, the
   encounter row reads `dead, charmed`, and selecting it still includes the hidden pet rows' damage — the list is shorter
   but no board is smaller. Selecting nothing/deselecting clears all three boards rather than leaving stale numbers.
6. **Names window** (View -> Names): every name in the capture, what it was called (Player/Npc/Pet/Merc/Unknown) and which
   rule said so; operator edits in players.txt/npcs.txt/petmapping.txt take effect on the next derive and show their
   provenance instead of the rule's.
7. **Derived damage meter** (opt-in): add `OverlayDamageFromMirror` to settings.txt and restart. Same overlay, same
   settings (`OverlayDamageMode` still decides when a quiet board zeroes: 0 = on kill, else N seconds), but its numbers
   come from the mirrored facts inside `[reset, now]` instead of the overlay's own running totals. What to look for:
   the same fight measured by both paths should agree; if it does not, that is a bug, because both halves are built by
   `MirrorStats` — the same code a mirror-list select-all calls. Two honest differences in feel: it updates per derive
   (the mirror derives when the log goes quiet) rather than per parsed line, so during a live pull it can trail the
   legacy meter by a few seconds; and if the mirror has nothing for the window it holds the last board instead of
   flickering to zero (the expiry rule eventually zeroes that too, so a stale board cannot outlive the timeout).
   **There is no fallback to legacy on this path.** If the derived build throws, the board goes empty and the log says so
   (first failure at once, then every 30th tick with a running count); if nothing is being mirrored, same thing plus a
   one-time warning. A derived board that quietly became a legacy board would be indistinguishable from a correct one.
   Off = byte-identical to before.

Report anything where a *derived* board disagrees with itself (click A+B, then click A and B separately — those must add
up exactly), because that is a bug rather than a known legacy difference.
