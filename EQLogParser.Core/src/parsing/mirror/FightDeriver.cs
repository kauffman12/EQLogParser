namespace EQLogParser.Mirror
{
  // Re-derives fights from the fact table — the re-derivable replacement for
  // FightManager.HandleDamageProcessed. The decision logic (direction per record, fight creation,
  // expiry, slain flush) is a line-for-line replay of the current pipeline with two deliberate
  // swaps:
  //   * PlayerRegistry.Instance.IsPetOrPlayerOrMerc(name) -> timeline.IdentityAt(name, t)
  //     (Phase 1 seeds this from verification events; Phase 2 rules make it retroactive)
  //   * string-keyed per-second combo cache / spell cache -> index/struct keyed (no allocs)
  // Everything else — EQDataStore.IsKnownNpc, PlayerRegistry.IsPossiblePlayerName, the expiry and
  // slain-queue state machines — uses the same calls the current pipeline makes, so an identical
  // fact stream with identical identity answers must produce identical fights.
  internal static class FightDeriver
  {
    private const int MaxTimeout = FightManager.MaxTimeout;     // 60 s — hard fight expiry
    private const int FightTimeout = FightManager.FightTimeout; // 30 s — expiry once boss-directed actions exist
    private const int RecentSpellTime = 300;                    // recent-spell cache horizon

    internal sealed class DeriveContext
    {
      public EntityTimeline Timeline;
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

    // facts and deaths must be in consumer order (they are: appended by the mirror as events fire).
    public static List<DerivedFight> Derive(IFactTable facts, EntityTimeline timeline)
    {
      var ctx = new DeriveContext { Timeline = timeline, Facts = facts };
      var span = facts.Facts;
      var deaths = facts.Deaths;
      var deathCursor = 0;

      for (var i = 0; i < span.Length; i++)
      {
        // merge the death stream in exact line order (Seq is assigned by the mirror at append time)
        while (deathCursor < deaths.Length && deaths[deathCursor].Seq < span[i].Seq)
        {
          HandleDeath(deaths[deathCursor], ctx);
          deathCursor++;
        }

        HandleDamageFact(span[i], i, ctx);
      }

      while (deathCursor < deaths.Length)
      {
        HandleDeath(deaths[deathCursor], ctx);
        deathCursor++;
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

      var isAttackerPlayer = IsPlayerSide(ctx.Timeline, atkName, t) || atkName == Labels.Rs;
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

      // the AttackerIsSpell re-target fix (the current pipeline mutates record.Attacker to "Unknown");
      // applies to this record only — the combo cache keeps the pre-fix decision
      var isUnkRewrite = false;
      if (fact.AttackerIsSpell && defender)
      {
        defender = !IsPlayerSide(ctx.Timeline, defName, t);
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

      var t = (double)fact.TimeS;
      var isAttackerPlayerSpell = fact.AttackerIsSpell && ctx.RecentSpells.Contains(atkName);
      isAttackerPlayer = isAttackerPlayer || isAttackerPlayerSpell;
      var isDefenderPlayer = IsPlayerSide(ctx.Timeline, defName, t);
      var isAttackerNpc = (!isAttackerPlayer && EQDataStore.Instance.IsKnownNpc(atkName)) || (fact.AttackerIsSpell && !isAttackerPlayerSpell);
      var isDefenderNpc = (!isDefenderPlayer && EQDataStore.Instance.IsKnownNpc(defName)) || isAttackerPlayerSpell;

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

    private static bool IsPlayerSide(EntityTimeline timeline, string name, double t) =>
      timeline.IdentityAt(name, t) is IdentityKind.Player or IdentityKind.Merc;

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
