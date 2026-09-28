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
