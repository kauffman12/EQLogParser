# Classification migration review summary

## Overall assessment

**The architecture is moving in the right direction, but I would not consider the replacement complete or ready to merge yet.** I found reproducible correctness bugs, an unbounded memory-retention problem, and legacy identity decisions that still bypass—or constrain—the new classifier.

I compared `develop` (`f1efdcfc`) with `master` (`8628fc42`). The detailed review, including locations, reproductions, migration inventory, and recommendations, is in **[docs/ClassificationReview.md](docs/ClassificationReview.md)**. That is an ignored local document and will not exist in a fresh clone. No implementation files were changed during the review.

## Highest-priority findings

### 1. Meter refreshes leak stats builders

`DerivedTotals` creates new damage/tanking builders per calculation. Their constructors subscribe themselves to the static `CombatEvents.ActiveDataCleared` event and never unsubscribe.

**Reproduced:** ten overlay calculations left **20 additional subscribers after full GC**. Those instances retain calculation state and accumulate over a long session.

The arithmetic should remain shared, but temporary builders must not acquire permanent lifecycle subscriptions.

### 2. Incremental fights return stale cached summaries

The projection correctly retains and grows existing fight objects. However, `FightFactIndex` caches their materialized summaries without invalidating them when new facts arrive.

**Reproduced:** a fight grew from **100 to 300 damage**, while its summary still returned **100**—the exact same cached object.

This can make the fight list, selected summary, and meter disagree despite correct captured facts.

### 3. A cheap refresh can prevent the final classification pass

The cadence first checks whether anything changed since **either** kind of pass. A projection-only pass consumes that change without classifying it. If the log then becomes quiet, subsequent checks return “nothing to do,” even after full classification is overdue.

**Reproduced:** after a cheap pass consumes the last increment, the scheduler continues returning `None`.

Related issues:
- The completion watermark can include facts captured after the computed snapshot.
- Explicit rederive requests are dropped while another pass is running.

The engine needs separate **projected and classified watermarks**, plus a pending-full-pass flag.

### 4. Spell-effect classification drops real damage

R21 represents spell effects as NPCs. When an effect hits an NPC, projection treats it as mob-on-mob and drops it.

**Reproduced:** a 500-damage spell-effect hit produced no fight row and no damage.

On classified Incogitable data, **382 facts totaling 32,932,003 damage** encounter this condition. That figure measures the current routing problem, not a complete master-versus-branch board comparison.

“Spell effect,” “NPC,” and “hostile” cannot be interchangeable concepts.

### 5. Correct time-scoped ownership is lost during aggregation

The timeline and materialized records can correctly identify different owners of the same charmed-pet name. `DamageStatsBuilder` then collapses those records into one final owner per name.

**Reproduced:** 100 damage for Firstowner and 200 for Secondowner became:

```text
Secondowner +Pets = 300
```

Additionally, the live ownership lookup queries at positive infinity, which cannot match exclusive-ended ownership intervals.

Ownership must remain event-time-specific through the actual board calculation.

### 6. The Names pane runs another classifier over mutable live data

`BuildNameCensus` classifies outside the capture lock while names and facts can grow. Its fresh classification state does not isolate the rule book’s shared failure/retirement collections.

This is a code-inspection finding rather than a Windows race reproduction. Catching exceptions does not make those reads coherent.

The pane should consume a **versioned, published classification result**, not independently classify the same live capture.

## Other confirmed problems

The full review covers these separately:

- **Class edits can revert after restart:** the ledger saves the new class, but the old `players.txt` class wins during initialization.
- **Pet mappings are pruned incorrectly:** recording a rule verdict deleted both an undated mapping and a 100-day-old mapping, despite their different retention contract.
- **Incremental R21 differs from a fresh pass:** a cast token discarded before its name enters the entity pool is never reconsidered.
- **Weak legacy memory blocks stronger inference:** R7/R15 skip already-classified names, so strength-8 registry seeds can prevent competing evidence from being generated at all.
- **Identity predicates disagree:** for example, a live NPC verdict can still qualify through the old mercenary registry.
- **Memory operations have inconsistent semantics:** “sightings” counts growing passes rather than captures; clearing a claim in Names can remove unrelated membership/class/owner memory, while the Fight Table’s clear behaves differently.

## Did we preserve the good old checks?

**Mostly, but frequently through the old machinery rather than as properly weighted evidence.**

Good migrations include targeted-player observations, presence, `/who`, chat, drink/eat, loot, and possessive ownership rules.

Important remaining legacy paths include:

- Pet “My leader is…” speech.
- EMU explicit owner annotations.
- Companion-heal and Elemental Conversion ownership recognition.
- Several player-specific combat modifiers.
- Pet-only spell targets and generated pet names.
- Operator “Set as Player” actions in older summaries.

Some still write `PlayerRegistry`, then reach classification only as generic low-strength `RegistrySeed` evidence. Some consumers still query the registry directly.

**That does not meet your intended contract yet.** A reliable observation should retain its recognizer, timestamp, subject/owner pair, and appropriate confidence—not become indistinguishable from an old saved default.

I would not simply raise every registry seed’s strength. That would also elevate stale or historically mistaken defaults.

## Where I agree—and disagree—with the design

### Keep

- Capturing facts separately from conclusions.
- Replayable classification with provenance.
- Confidence tiers and strong-evidence gates.
- Time-bounded ownership and charm behavior.
- Incremental computation checked against fresh replay.
- Reusing existing stats arithmetic rather than building another calculator.

### Change

The most important conceptual correction is:

**Identity, allegiance, ownership, and class are separate questions.**

R15 currently labels many custom-named pets “Player” because it established raid-side behavior. That helps routing but undermines the goal of distinguishing players from pets.

Similarly:
- A spell effect is not an NPC.
- Historical roster membership is not direct observation.
- An inferred friendly actor is not necessarily a person.
- A manual correction should affect downstream inference, not merely overwrite the final displayed kind.

## Performance

The incremental engine itself shows substantial improvement:

| Incogitable measurement | Time |
|---|---:|
| Fresh classification + projection | ~910 ms |
| Continued projection, +1,891 facts | <1 ms |
| Continued projection, +18,918 facts | ~5 ms |
| Quiet carried classification | ~3 ms |

However, a separate Names census measured approximately **608 ms classification + 106 ms reporting**. Repeating that while the pane is visible duplicates work the engine already performed.

The builder leak is the immediate performance priority; after that, eliminate duplicate classification and coalesce asynchronous summary rebuilds.

## What I would do next

1. **Fix the demonstrated regressions first:** builder lifecycle, summary invalidation, cadence, spell routing, ownership aggregation.
2. **Publish one coherent classification snapshot** for Names, summaries, predicates, and selection tasks.
3. **Finish the evidence migration:** preserve each useful legacy recognizer explicitly before removing its registry writes.
4. **Complete persistence together:** independent assertion clocks, consistent clear commands, deterministic class precedence, then retire old readers/writers.
5. **Finish consumer migration and delete duplicate identity state.**

I would pause adding classification rules until these boundaries are stable.

### Validation

- Full solution rebuild: **0 warnings, 0 errors**.
- Non-WPF suite: **1,637 passed, 7 skipped**.
- Real-log incremental equality passed for live and THJ captures.
- One THJ benchmark assertion failed because its synthetic tail legitimately changed a verdict; investigation supports rebuilding in that case.
- Windows runtime tests were **not run**.

The finish line should be **one identity authority**, not necessarily deletion of the `PlayerRegistry` type—its grammar and time-scoped class responsibilities may still be useful.
