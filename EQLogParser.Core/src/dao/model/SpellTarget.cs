namespace EQLogParser
{
  internal enum SpellTarget
  {
    Los = 1, Casterae = 2, Castergroup = 3, Casterpb = 4, Singletarget = 5, Self = 6, Targetae = 8, Pet = 14,
    Casterpbplayers = 36, Pet2 = 38, Nearbyplayersae = 40, Targetgroup = 41, Directionae = 42, Targetringae = 45,

    /*
     * Remote healing — the caster's target's group, so it lands on names nowhere near the caster (`Remote Healing Flood`,
     * `Scorn`). 732 rows in spells.txt carry it and it behaved like Castergroup in every capture measured: with the caster
     * gated to somebody our side already proved, its heals landed on raid members and on nothing provably hostile.
     * The codes spells.txt uses that this enum does NOT name are listed (with counts) in SpellTargetCodeCoverageTest, so a
     * new one fails a test instead of reading as "unknown shape" forever.
     */
    Remotehealing = 46
  }
}
