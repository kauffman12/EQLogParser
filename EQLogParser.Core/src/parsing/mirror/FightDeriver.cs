namespace EQLogParser.Mirror
{
  // Re-derives fights from the fact table — the re-derivable replacement for
  // FightManager.HandleDamageProcessed. The decision logic (direction per record, fight creation,
  // expiry, slain flush) is a line-for-line replay of the current pipeline with two deliberate
  // swaps:
  //   * PlayerRegistry.Instance.IsPetOrPlayerOrMerc(name) at ingest time -> the fact's own
  //     AttackerPlayerSide/DefenderPlayerSide flags: the mirror captured exactly that call's
  //     answer on the same thread at the same instant (a fact, not a judgment — verified
  //     mid-log names read player-side only from their evidence time onward, like the live
  //     registry did). Phase 2 rules will layer retroactive reclassification over this.
  //   * string-keyed per-second combo cache / spell cache -> index/struct keyed (no allocs)
  // Everything else — EQDataStore.IsKnownNpc, PlayerRegistry.IsPossiblePlayerName, the expiry and
  // slain-queue state machines — uses the same calls the current pipeline makes, so an identical
  // fact stream must produce identical fights.
  //
  // EntityTimeline / identity rules are not consulted here yet: Phase 2 wires them in as a
  // retroactive overlay over the captured registry verdicts.
  internal static class FightDeriver
  {
    private const int MaxTimeout = FightManager.MaxTimeout;     // 60 s — hard fight expiry
    private const int FightTimeout = FightManager.FightTimeout; // 30 s — expiry once boss-directed actions exist
    private const int RecentSpellTime = 300;                    // recent-spell cache horizon

    internal sealed class DeriveContext
    {
      public IFactTable Facts;
      public Dictionary<short, DerivedFight> ActiveFights = [];
      public List<DerivedFight> Fights = [];
      // combo direction decided earlier in the same second: null value = not yet decided
      // (the current pipeline's _validCombo is cleared on every timestamp change)
      public Dictionary<(short Atk, short Def), bool?> ComboCache = new(4_096);
      public HashSet<string> RecentSpells = [];   // interned spell names cast by the player side
      public List<short> SlainQueue = [];
      public double SlainTime = double.NaN;
      public double LastProcessTime = double.NaN;
      public int NonTankingSeq;
    }

    // all four streams are in consumer order (Seq is a single counter assigned by the mirror at
    // append time); a replay must apply them in exactly that order, so each step takes the lowest
    // Seq among the stream heads.
    public static List<DerivedFight> Derive(IFactTable facts)
    {
      var ctx = new DeriveContext { Facts = facts };
      var damages = facts.Facts;
      var deaths = facts.Deaths;
      var identities = facts.IdentityEvents;
      var taunts = facts.Taunts;
      var d = 0;
      var x = 0;
      var id = 0;
      var t2 = 0;

      while (d < damages.Length || x < deaths.Length || id < identities.Length || t2 < taunts.Length)
      {
        // Seqs are unique (one shared counter), so exactly one stream head owns the minimum.
        var minSeq = int.MaxValue;
        if (d < damages.Length) minSeq = Math.Min(minSeq, damages[d].Seq);
        if (x < deaths.Length) minSeq = Math.Min(minSeq, deaths[x].Seq);
        if (id < identities.Length) minSeq = Math.Min(minSeq, identities[id].Seq);
        if (t2 < taunts.Length) minSeq = Math.Min(minSeq, taunts[t2].Seq);

        if (d < damages.Length && damages[d].Seq == minSeq)
        {
          HandleDamageFact(in damages[d], d, ctx);
          d++;
        }
        else if (x < deaths.Length && deaths[x].Seq == minSeq)
        {
          HandleDeath(deaths[x], ctx);
          x++;
        }
        else if (id < identities.Length && identities[id].Seq == minSeq)
        {
          HandleIdentity(identities[id], ctx);
          id++;
        }
        else
        {
          HandleTaunt(taunts[t2], ctx);
          t2++;
        }
      }

      // end-of-log flush — in the app the next line's CheckSlainQueue does this; a finished fact
      // stream has no next line, so the final state is what that check would produce.
      FlushSlainQueue(ctx);
      return ctx.Fights;
    }

    // mirrors UpdateSlain (consumer-thread order: flush pending queue first, then enqueue)
    private static void HandleDeath(DeathFact death, DeriveContext ctx)
    {
      if (!double.IsNaN(ctx.SlainTime) && death.TimeS > ctx.SlainTime)
      {
        FlushSlainQueue(ctx);
      }

      // UpdateSlain capitalizes the slain name before the active-fight lookup; the fight keys are
      // exact strings, so resolve the capitalized form against the (final) name table.
      var slainName = TextUtils.CapitalizeFirst(ctx.Facts.NameOf(death.KilledIdx));
      var slainKey = ctx.Facts.InternName(slainName);

      if (!ctx.SlainQueue.Contains(slainKey) && ctx.ActiveFights.ContainsKey(slainKey))
      {
        ctx.SlainQueue.Add(slainKey);
        ctx.SlainTime = death.TimeS;
      }
    }

    private static void FlushSlainQueue(DeriveContext ctx)
    {
      if (ctx.SlainQueue.Count == 0) return;
      foreach (var key in ctx.SlainQueue)
      {
        if (ctx.ActiveFights.Remove(key, out var fight)) fight.Dead = true;
      }
      ctx.SlainQueue.Clear();
      ctx.SlainTime = double.NaN;
    }

    // mirrors FightManager's EventsNewVerifiedPet hook: RemoveFight drops the matching active
    // fight WITHOUT setting Dead (the object survives in AllFights, dead=false) — the only way a
    // current-pipeline fight closes without damage, expiry, or a slain line. The other kinds have
    // no fight-side effect in today's pipeline; Phase 2 rules will consume them.
    private static void HandleIdentity(in IdentityEvent identity, DeriveContext ctx)
    {
      if (identity.Kind == IdentityEvent.VerifiedPet)
      {
        ctx.ActiveFights.Remove(identity.NameIdx);
      }
    }

    // Taunts have no replayed state effect: FightManager.HandleNewTaunt does GetFight(npc) ??
    // Create(npc, t), but Create only constructs the object — it never inserts into
    // _activeFights and never fires EventsNewFight, so for a name without an active fight the
    // result is an orphan that absorbs one TauntBlock and dies. For an active fight the block is
    // appended to its TauntBlocks, which feed no compared field and do not arm the expiry gate
    // (DamageBlocks only). The fact is kept in the stream as evidence for Phase 2 rules.
    private static void HandleTaunt(in TauntFact taunt, DeriveContext ctx)
    {
      // intentionally stateless — see comment above
      _ = taunt;
      _ = ctx;
    }

    private static void HandleDamageFact(in DamageFact fact, int factIndex, DeriveContext ctx)
    {
      var t = (double)fact.TimeS;
      var atkName = ctx.Facts.NameOf(fact.AtkIdx);
      var defName = ctx.Facts.NameOf(fact.DefIdx);

      // the app calls CheckSlainQueue on every damage line before the event fires (Process:1003)
      if (!double.IsNaN(ctx.SlainTime) && t > ctx.SlainTime)
      {
        FlushSlainQueue(ctx);
      }

      // ---- per-second bookkeeping (mirrors the top of HandleDamageProcessed, runs on EVERY
      // record including ones dropped below — the original re-checks whenever the stored
      // LastFightProcessTime differs, and that only advances on kept records) ----
      if (!ctx.LastProcessTime.Equals(t))
      {
        CheckExpireFights(ctx, t);
        ctx.ComboCache.Clear();
        if (t - ctx.LastProcessTime > RecentSpellTime) ctx.RecentSpells.Clear();
      }

      // mirrors FightManager: registry verdict at ingest time (the fact's flag) or the "Rs" label
      var isAttackerPlayer = fact.AttackerPlayerSide || atkName == Labels.Rs;
      if (isAttackerPlayer && (fact.TypeId == LabelTypes.Dd || fact.TypeId == LabelTypes.Dot || fact.TypeId == LabelTypes.Proc) &&
        fact.SubIdx != DamageFactTable.NoSubtype)
      {
        ctx.RecentSpells.Add(ctx.Facts.SubtypeOf(fact.SubIdx));
      }

      var comboKey = (fact.AtkIdx, fact.DefIdx);
      bool defender;
      if (ctx.ComboCache.TryGetValue(comboKey, out var cachedDirection) && cachedDirection.HasValue)
      {
        defender = cachedDirection.Value; // decided earlier this second — the original _validCombo hit
      }
      else if (!IsValidAttack(fact, atkName, defName, ctx, isAttackerPlayer, out var npcDefender))
      {
        return; // record dropped (self-attack / player-on-player) — LastProcessTime does not advance
      }
      else
      {
        defender = npcDefender;
        ctx.ComboCache[comboKey] = defender; // stored PRE-fix, exactly like the original
      }

      // the AttackerIsSpell re-target fix (the current pipeline mutates record.Attacker to
      // "Unknown"): it re-queries the registry live — same thread/instant, so the fact's captured
      // verdict is the identical answer. Applies to this record only; the combo cache keeps the
      // pre-fix decision.
      var isUnkRewrite = false;
      if (fact.AttackerIsSpell && defender)
      {
        defender = !fact.DefenderPlayerSide;
        if (defender) isUnkRewrite = true;
      }

      var key = defender ? fact.DefIdx : fact.AtkIdx;
      if (!ctx.ActiveFights.TryGetValue(key, out var fight))
      {
        fight = new DerivedFight
        {
          Name = ctx.Facts.NameOf(key),
          Id = ctx.Fights.Count + 1,
          BeginTime = t,
          LastTime = t,
          FactStart = factIndex
        };
        ctx.ActiveFights[key] = fight;
        ctx.Fights.Add(fight);
      }

      if (defender)
      {
        fight.HasDamageActions = true;
        if (LabelTypes.IsHit(fact.TypeId))
        {
          var wasFirstHit = fight.DamageHits == 0;
          fight.DamageHits++;
          fight.DamageTotal += fact.Total;
          if (wasFirstHit) fight.NonTankingOrder = ctx.NonTankingSeq++; // EventsNewNonTankingFight fires on first hit

          // mirrors Fight.PlayerDamageTotals: every boss-directed hit lands here, keyed by
          // owner ?? attacker (classification filtering happens at roll-up, D3)
          var owner = OwnerOf(fact, atkName);
          var aggKey = owner ?? (isUnkRewrite ? Labels.Unk : atkName);
          if (!fight.PlayerRollup.TryGetValue(aggKey, out var agg))
          {
            agg = new NameAgg { PetOwner = isUnkRewrite ? null : owner };
            fight.PlayerRollup[aggKey] = agg;
          }
          agg.Add(fact.Total, true, owner, t);
        }
        fight.BeginDamageTime = double.IsNaN(fight.BeginDamageTime) ? t : fight.BeginDamageTime;
        fight.LastDamageTime = t;
      }
      else
      {
        fight.HasTankingActions = true;
        fight.TankHits++;
        fight.TankTotal += fact.Total;

        // mirrors Fight.PlayerTankTotals: every tanking-directed record lands here, keyed by defender
        if (!fight.TankRollup.TryGetValue(defName, out var tankAgg))
        {
          tankAgg = new NameAgg();
          fight.TankRollup[defName] = tankAgg;
        }
        tankAgg.Add(fact.Total, true, null, t);

        fight.BeginTankingTime = double.IsNaN(fight.BeginTankingTime) ? t : fight.BeginTankingTime;
        fight.LastTankingTime = t;
      }

      fight.LastTime = t;
      if (!ctx.LastProcessTime.Equals(t)) ctx.LastProcessTime = t;
    }

    // Line-for-line port of FightManager.IsValidAttack (same branch structure, same return
    // semantics: false drops the record entirely).
    private static bool IsValidAttack(in DamageFact fact, string atkName, string defName, DeriveContext ctx, bool isAttackerPlayer, out bool npcDefender)
    {
      npcDefender = false;

      if (string.Equals(atkName, defName, StringComparison.OrdinalIgnoreCase)) return false; // IsSelfAttack

      // NOTE: the original reads RecentSpellCache (written with SubType keys) with
      // ContainsKey(record.Attacker) — reproduced exactly; it only hits when an attacker's name
      // was previously recorded as a player spell's SubType.
      var isAttackerPlayerSpell = fact.AttackerIsSpell && ctx.RecentSpells.Contains(atkName);
      isAttackerPlayer = isAttackerPlayer || isAttackerPlayerSpell;
      var isDefenderPlayer = fact.DefenderPlayerSide;
      var isAttackerNpc = (!isAttackerPlayer && EQDataStore.Instance.IsKnownNpc(atkName)) || (fact.AttackerIsSpell && !isAttackerPlayerSpell);
      // the `|| isAttackerPlayer` term is original code (a player's target counts as NPC territory)
      var isDefenderNpc = (!isDefenderPlayer && EQDataStore.Instance.IsKnownNpc(defName)) || isAttackerPlayerSpell || isAttackerPlayer;

      if (isAttackerPlayer && isDefenderPlayer) return false;

      if (isDefenderNpc)
      {
        if (!isAttackerNpc)
        {
          npcDefender = true;
          return isAttackerPlayer || PlayerRegistry.IsPossiblePlayerName(atkName);
        }

        if (ctx.ActiveFights.ContainsKey(fact.DefIdx) && !ctx.ActiveFights.ContainsKey(fact.AtkIdx))
        {
          npcDefender = true;
          return true;
        }
      }
      else
      {
        if (isAttackerNpc)
        {
          return isDefenderPlayer || PlayerRegistry.IsPossiblePlayerName(defName);
        }

        if (isDefenderPlayer) return true;

        if (isAttackerPlayer)
        {
          npcDefender = true;
          return true;
        }

        if (!PlayerRegistry.IsPossiblePlayerName(defName))
        {
          npcDefender = true;
          return true;
        }

        if (!PlayerRegistry.IsPossiblePlayerName(atkName)) return true;

        npcDefender = true;
      }

      return true;
    }

    // Phase 1 line-derived ownership (R5 input already stored as a flag on the fact): "X`s pet" /
    // "X`s warder" attacker names carry their owner in the name. Registry-gated owner lookups
    // (the parser's CheckOwner) are intentionally not reproduced — that gate is part of what the
    // rules replace in Phase 2.
    private static string OwnerOf(in DamageFact fact, string attacker)
    {
      if (!fact.OwnerInLine) return null;
      const string petSuffix = "`s pet";
      const string warderSuffix = "`s warder";
      if (attacker.EndsWith(petSuffix, StringComparison.Ordinal))
      {
        return attacker[..^petSuffix.Length];
      }
      if (attacker.EndsWith(warderSuffix, StringComparison.Ordinal))
      {
        return attacker[..^warderSuffix.Length];
      }
      return null;
    }

    private static void CheckExpireFights(DeriveContext ctx, double currentTime)
    {
      var toRemove = new List<short>();
      foreach (var kv in ctx.ActiveFights)
      {
        var diff = currentTime - kv.Value.LastTime;
        if (diff > MaxTimeout || (diff > FightTimeout && kv.Value.HasDamageActions))
        {
          toRemove.Add(kv.Key);
        }
      }
      foreach (var key in toRemove)
      {
        if (ctx.ActiveFights.Remove(key, out var fight)) fight.Dead = true;
      }
    }
  }
}
