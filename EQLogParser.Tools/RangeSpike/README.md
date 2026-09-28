# RangeSpike — does a byte-range sharded parse actually work?

A measurement spike, outside `EQLogParser.sln` so it cannot ship by accident. It answers two questions that
decide whether parallel parsing is worth designing:

1. **How fast is a shard that reads only its own slice?** (`plan` + `parse`)
2. **Does merging shards reproduce the sequential capture?** (`merge` + `diff` + `boards`)

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet build EQLogParser.Tools/RangeSpike/RangeSpike.csproj -c Release
BIN=EQLogParser.Tools/RangeSpike/bin/Release/net10.0/RangeSpike.dll
F=$PWD/local/eqlog_Incogitable_xegony.txt
SEED=$PWD/local/EQLPData/config/xegony

dotnet $BIN plan  --file $F --parts 8                       # newline-aligned byte cut points
dotnet $BIN parse --file $F --seed $SEED --out seq.spool --cwd $PWD/EQLogParser
dotnet $BIN parse --file $F --from A --to B --seed $SEED --out s0.spool --cwd $PWD/EQLogParser   # xN concurrently
dotnet $BIN merge --dir shards --out merged.spool           # remap name pools, renumber the shared sequence
dotnet $BIN diff    --ignore-seq --a seq.spool --b merged.spool   # row-for-row, content only
dotnet $BIN boards  --a seq.spool --b merged.spool          # per-name meter totals: what a player would see
dotnet $BIN flagcheck --a seq.spool                         # is a side flag a fact about the line at all?
```

## Speed (8 physical cores, one NVMe file, wall clock including 8 process starts + spool writes)

| capture | sequential parse | sequential wall | shards | slowest shard | parallel wall | speedup |
|---|---|---|---|---|---|---|
| Incogitable 344 MB / 3.57 M lines | 13,448 ms | 14.74 s | 4 | 4,950 ms | 6.53 s | **2.3×** |
| Incogitable | — | 14.96 s (unseeded) | 8 | 3,298 ms | 5.02 s | **3.0×** |
| Kizant-09-03-26 998 MB / 10.0 M lines | 24,726 ms | 26.19 s | 8 | 5,308 ms | 7.09 s | **3.7×** |

Merge is cheap: 8 shards of 7.98 M facts merged in **0.70 s**. Peak RSS per worker ~500 MB (vs 1,675 MB for
the single pass), so 8 workers need ~4 GB — worth knowing on a laptop with the game open.

## Correctness: what breaks at a boundary

`boards` compares the numbers a player would notice — damage and healing **per name**:

| capture | shards | names | board rows differing | total delta |
|---|---|---|---|---|
| Incogitable 344 MB | 4, seeded | 1,176 | **0 damage, 0 heal** | **exactly 0** |
| Kizant 998 MB | 8, seeded | 243 | 2 (`Matrim +140,015`, ``Nniki`s pet -1,320``) | 138,695 of 1.658 T = **0.000008 %** |

Row-for-row (`diff`) the picture is more informative:

- **Unseeded workers** differ on 500,714 damage rows (26 %) and 168,404 heals (40 %). Every difference is
  `FlagAttkPlayerSide`/`FlagDefPlayerSide` — the worker started without knowing who the raiders were.
- **Seeded workers** (given the operator's own `players.txt` + `petmapping.txt`, which production loads at
  file open anyway) differ on 19,323 damage rows (**1.0 %**) and 16,829 heals (**4.0 %**) — still flags only.
  Names, types, amounts, spell/modifier names and the totals themselves: identical. Deaths: 0 differences.
  Taunts: 0 differences.

## The finding that matters more than sharding

`flagcheck` on a **single sequential capture**: 32 attacker names (of 999) carry *both* values of
`FlagAttkPlayerSide` across their own facts — `Betebeatz` reads side=0 on 9,512 facts and side=1 on 5,342 —
and 28 healer names flip on 52,302 heal facts. So those two bits are not a fact about the line; they are a
timestamped snapshot of what `PlayerRegistry` happened to believe at that moment.

Consequences:

- **No shard-boundary strategy can reproduce them**, exact or otherwise: they are not determined by the text
  a shard was given. Stop trying to make them reproducible and stop stamping time-dependent verdicts into
  immutable facts. Answer "is this name player-side" in the deriver, per name over the whole capture (the
  rules already do per-name inference — R7/R14/R15/R18), and a sharded capture becomes reproducible by
  construction *and* today's per-name inconsistency disappears as the same change.
- Line-derived bits are unaffected: `OwnerInLine`/`AttackerIsSpell` are off the text, and `ModMask` was
  already captured for exactly this reason (AGENTS: "the byte that filters live in").

## The two real boundary leaks (both fixable)

1. **Identity timing.** A worker with an empty registry re-verifies raiders a previous shard already knew:
   +104 identity events on Incogitable, 56 → 300 on Kizant. Each spends a number from the shared sequence, so
   every later fact shifts by a constant (`Seq` drift; `diff` without `--ignore-seq` shows it as 1.4 M
   "mismatches" that are pure renumbering). Merge fixes it: renumber the merged stream (implemented) and
   treat identity knowledge as an idempotent set rather than a per-index row.
2. **Delayed-crit pairing.** `DamageLineParser`'s `_previousAction`/`_lastCrit`/`_delayCritRecord` are process
   state; a shard's first lines have nothing to pair with, which is where Kizant's `Matrim +140,015` came
   from. Standard fix: give each worker a few hundred lines of **overlap** before its start offset and discard
   facts that begin earlier — cost ~0.1 % of bytes read.

## What this says about the design

Sharding is viable and worth ~3-4× on the parse stage, which today is 85 % of load time. The prerequisites are
small and each is independently desirable: (a) pass the operator seed explicitly instead of via process
globals, (b) stop stamping registry *opinions* into facts, (c) overlap reads to cover parser state. The
ugly part is not the parser — it is that `PlayerRegistry`, `ConfigUtil.PlayerName` and every line parser's
statics are process-global, which is why this had to be a separate executable to measure honestly.
