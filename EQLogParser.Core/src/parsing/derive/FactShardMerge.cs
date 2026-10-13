using System;
using System.Collections.Generic;

namespace EQLogParser
{
  /*
   * One ingest worker's output: the two tables a lane's capture filled while parsing one byte range.
   * A shard is finished work — its capture has stopped, its rows will never move — and it exists only
   * long enough to be merged into the session's real tables and thrown away.
   */
  internal sealed class FactShard(DamageFactTable facts, HealFactTable heals)
  {
    internal DamageFactTable Facts { get; } = facts ?? throw new ArgumentNullException(nameof(facts));
    internal HealFactTable Heals { get; } = heals;
  }

  /*
   * Merging shards into the one pair of tables the engine reads — the seam that turns "N parallel
   * captures" back into "one capture", and the reason a sharded load can hold up a byte-equality
   * standard against the sequential read. Merging is NOT concatenation; two things must be rebuilt
   * deliberately, both of which the RangeSpike tool proved out over full captures first
   * (docs/DesignNotes → the sharded-load chapter):
   *
   *  - The string pools. Each shard interned into its own indices: a raider is name 7 in one shard and
   *    402 in the other. Every index of every row is remapped through the TARGET table's own
   *    interning (names ignore case, subtypes and heal spells are ordinal, aux ignores case — the same
   *    comparers DamageFactTable/HealFactTable use at append time; a merge with the wrong comparer
   *    would be the "one entity, two ids" bug rebuilt at a new seam). Pool order = shard order, so a
   *    single-shard merge is the identity.
   *
   *  - The sequence. One counter numbers damage, heals, deaths, taunts, identity events and evidence
   *    in consumer order in the sequential capture — that shared clock is how the projection pairs a
   *    self-heal with the damage it answered without either table knowing about fights. Each shard ran
   *    its own 1..n, so the merge interleaves a shard's six queues by their local sequence and hands
   *    every emitted row a fresh number. Within one shard that restores exactly the original line
   *    order; across shards, ranges concatenated in byte order give exactly the file's order. Ties at
   *    the same second inside ONE shard never reorder; across shards a same-second tie may land on the
   *    other side of its neighbour, which is the documented (and measured: totals exact, one delayed
   *    crit pair at a range edge) cost of splitting a stream mid-sentence.
   *
   * The return value is the last sequence used; the session's real capture resumes from it
   * (CombatCapture's startSequence), because the monitor phase must keep numbering events ABOVE the
   * merged history or heal-to-damage pairing starts reading 10-million-event-old rows.
   */
  internal static class FactShardMerge
  {
    // The six queues of a shard, in the order the merge's pick-min scans them.
    private const int Queues = 6;

    internal static int MergeInto(DamageFactTable facts, HealFactTable heals, IReadOnlyList<FactShard> shards)
    {
      var seq = 0;
      foreach (var shard in shards)
      {
        MergeOne(facts, heals, shard, ref seq);
      }

      return seq;
    }

    private static void MergeOne(DamageFactTable facts, HealFactTable heals, FactShard shard, ref int seq)
    {
      var src = shard.Facts;
      var srcHeals = shard.Heals;

      /*
       * Remap tables, built by walking each SOURCE pool and interning its stored string into the target.
       * Pool order is preserved, so every index a source row carries has exactly one entry here.
       */
      var names = src.InternedNames;
      var nameMap = new int[names.Count];
      for (var i = 0; i < names.Count; i++)
      {
        nameMap[i] = facts.InternName(names[i]);
      }

      var subMap = new int[src.SubtypeCount];
      for (var i = 0; i < subMap.Length; i++)
      {
        subMap[i] = facts.InternSubtype(src.SubtypeOf((ushort)i));
      }

      var auxMap = new int[src.AuxCount];
      for (var i = 0; i < auxMap.Length; i++)
      {
        auxMap[i] = facts.InternAux(src.AuxOf((short)i));
      }

      int[] spellMap = null;
      if (heals is not null && srcHeals is not null)
      {
        spellMap = new int[srcHeals.SpellCount];
        for (var i = 0; i < spellMap.Length; i++)
        {
          spellMap[i] = heals.InternSpell(srcHeals.SpellOf((ushort)i));
        }
      }

      var heads = new int[Queues];
      var damageCount = src.FactCount;
      var healCount = heals is not null && srcHeals is not null ? srcHeals.HealCount : 0;
      var deathCount = src.DeathCount;
      var tauntCount = src.TauntCount;
      var identityCount = src.IdentityEventCount;
      var evidenceCount = src.Evidence.Length;

      // K-way merge of the shard's six queues by their LOCAL shared sequence (spike-proven shape).
      // Local seqs are dense and unique within a shard, so a plain pick-min over six cursors is exact.
      while (true)
      {
        var best = -1;
        var bestSeq = int.MaxValue;
        for (var q = 0; q < Queues; q++)
        {
          if (heads[q] < CountOf(q, damageCount, healCount, deathCount, tauntCount, identityCount, evidenceCount))
          {
            var at = SeqOf(q, src, srcHeals, heads[q]);
            if (at < bestSeq)
            {
              best = q;
              bestSeq = at;
            }
          }
        }

        if (best < 0)
        {
          return;
        }

        // int like CombatCapture._sequence, not wider: the sequential capture is the ceiling on event
        // count, and the merge must hit exactly the same ceiling, not a different one.
        var n = ++seq;
        switch (best)
        {
          case 0:
          {
            ref readonly var f = ref src.RefAt(heads[0]++);
            facts.AddFact(new DamageFact(n, f.TimeS, Map(nameMap, f.AtkIdx), Map(nameMap, f.DefIdx),
              f.Total, f.TypeId, f.Flags, f.ModMask,
              f.SubIdx == DamageFactTable.NoSubtype ? DamageFactTable.NoSubtype : (ushort)subMap[f.SubIdx]));
            break;
          }

          case 1:
          {
            ref readonly var f = ref srcHeals.RefAt(heads[1]++);
            heals.AddHeal(new HealFact(n, f.TimeS, Map(nameMap, f.HealerIdx), Map(nameMap, f.HealedIdx),
              f.Total, f.OverTotal, f.TypeId, f.Flags, f.ModMask,
              f.SubIdx == HealFact.NoSpell ? HealFact.NoSpell : (ushort)spellMap[f.SubIdx]));
            break;
          }

          case 2:
          {
            var deaths = src.Deaths;
            ref readonly var f = ref deaths[heads[2]++];
            facts.AddDeath(new DeathFact(n, f.TimeS, Map(nameMap, f.KilledIdx), Map(nameMap, f.KillerIdx)));
            break;
          }

          case 3:
          {
            var taunts = src.Taunts;
            ref readonly var f = ref taunts[heads[3]++];
            facts.AddTaunt(new TauntFact(n, f.TimeS, Map(nameMap, f.NpcIdx)));
            break;
          }

          case 4:
          {
            var identities = src.IdentityEvents;
            ref readonly var f = ref identities[heads[4]++];
            facts.AddIdentity(new IdentityEvent(n, f.TimeS, Map(nameMap, f.NameIdx), f.Kind));
            break;
          }

          default:
          {
            var evidence = src.Evidence;
            ref readonly var f = ref evidence[heads[5]++];
            facts.AddEvidence(new EvidenceFact(n, f.TimeS, Map(nameMap, f.NameIdx), f.Kind, Map(auxMap, f.AuxIdx)));
            break;
          }
        }
      }
    }

    /*
     * A name index of -1 is a real value, not a bug: DeathFact stores (short)-1 when the log line names
     * no killer at all. Passing it through the map would be an off-by-one into someone else's raider.
     */
    private static short Map(int[] map, short idx) => idx < 0 ? (short)-1 : (short)map[idx];

    private static int CountOf(int queue, int damage, int heal, int death, int taunt, int identity, int evidence) =>
      queue switch
      {
        0 => damage,
        1 => heal,
        2 => death,
        3 => taunt,
        4 => identity,
        _ => evidence,
      };

    private static int SeqOf(int queue, DamageFactTable facts, HealFactTable heals, int head) =>
      queue switch
      {
        0 => facts.RefAt(head).Seq,
        1 => heals.RefAt(head).Seq,
        2 => facts.Deaths[head].Seq,
        3 => facts.Taunts[head].Seq,
        4 => facts.IdentityEvents[head].Seq,
        _ => facts.Evidence[head].Seq,
      };
  }
}
