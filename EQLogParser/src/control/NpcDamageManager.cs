using System;
using System.Collections.Generic;

namespace EQLogParser
{
  class NpcDamageManager
  {
    internal double LastFightProcessTime = double.NaN;
    private int CurrentNpcID = 1;
    private static readonly Dictionary<string, bool> RecentSpellCache = new Dictionary<string, bool>();
    private static readonly Dictionary<string, bool> ValidCombo = new Dictionary<string, bool>();
    private const int RECENTSPELLTIME = 300;

    public NpcDamageManager()
    {
      DamageLineParser.EventsDamageProcessed += HandleDamageProcessed;
      DamageLineParser.EventsNewTaunt += HandleNewTaunt;
    }

    internal void Reset()
    {
      LastFightProcessTime = double.NaN;
      CurrentNpcID = 1;
      RecentSpellCache.Clear();
      ValidCombo.Clear();
    }

    private void HandleNewTaunt(object sender, TauntEvent e)
    {
      Fight fight = DataManager.Instance.GetFight(e.Record.Npc);

      if (fight == null)
      {
        fight = Create(e.Record.Npc, e.BeginTime);
      }

      Helpers.AddAction(fight.TauntBlocks, e.Record, e.BeginTime);
    }

    private void HandleDamageProcessed(object sender, DamageProcessedEvent processed)
    {
      if (LastFightProcessTime != processed.BeginTime)
      {
        DataManager.Instance.CheckExpireFights(processed.BeginTime);
        ValidCombo.Clear();

        if (processed.BeginTime - LastFightProcessTime > RECENTSPELLTIME)
        {
          RecentSpellCache.Clear();
        }
      }

      // cache recent player spells to help determine who the caster was
      var isAttackerPlayer = PlayerManager.Instance.IsPetOrPlayerOrMerc(processed.Record.Attacker) || processed.Record.Attacker == Labels.RS;
      if (isAttackerPlayer && (processed.Record.Type == Labels.DD || processed.Record.Type == Labels.DOT || processed.Record.Type == Labels.PROC))
      {
        RecentSpellCache[processed.Record.SubType] = true;
      }

      string comboKey = processed.Record.Attacker + "=" + processed.Record.Defender;
      if (ValidCombo.TryGetValue(comboKey, out bool defender) || IsValidAttack(processed.Record, isAttackerPlayer, out defender))
      {
        ValidCombo[comboKey] = defender;
        bool isNonTankingFight = false;

        // fix for unknown spells having a good name to work from
        if (processed.Record.AttackerIsSpell && defender)
        {
          // check if it's really a player being hit
          defender = !PlayerManager.Instance.IsPetOrPlayerOrMerc(processed.Record.Defender);

          if (defender)
          {
            processed.Record.Attacker = Labels.UNK;
          }
        }

        Fight fight = Get(processed.Record, processed.BeginTime, defender);

        if (defender)
        {
          Helpers.AddAction(fight.DamageBlocks, processed.Record, processed.BeginTime);
          AddPlayerTime(fight, processed.Record, processed.Record.Attacker, processed.BeginTime);
          fight.BeginDamageTime = double.IsNaN(fight.BeginDamageTime) ? processed.BeginTime : fight.BeginDamageTime;
          fight.LastDamageTime = processed.BeginTime;

          if (StatsUtil.IsHitType(processed.Record.Type))
          {
            fight.DamageHits++;
            fight.DamageTotal += processed.Record.Total;
            isNonTankingFight = fight.DamageHits == 1;

            var attacker = processed.Record.AttackerOwner ?? processed.Record.Attacker;
            var validator = new DamageValidator();
            if (fight.PlayerDamageTotals.TryGetValue(attacker, out FightTotalDamage total))
            {
              total.Damage += validator.IsValid(processed.Record) ? processed.Record.Total : 0;
              total.Name = processed.Record.Attacker;
              total.PetOwner = total.PetOwner ?? processed.Record.AttackerOwner;
              total.UpdateTime = processed.BeginTime;
            }
            else
            {
              fight.PlayerDamageTotals[attacker] = new FightTotalDamage
              {
                Damage = validator.IsValid(processed.Record) ? processed.Record.Total : 0,
                Name = processed.Record.Attacker,
                PetOwner = processed.Record.AttackerOwner,
                UpdateTime = processed.BeginTime,
                BeginTime = processed.BeginTime
              };
            }

            SpellDamageStats stats = null;
            var spellKey = processed.Record.Attacker + "++" + processed.Record.SubType;
            if (processed.Record.Type == Labels.DD)
            {
              if (!fight.DDDamage.TryGetValue(spellKey, out stats))
              {
                stats = new SpellDamageStats { Caster = processed.Record.Attacker, Spell = processed.Record.SubType };
                fight.DDDamage[spellKey] = stats;
              }
            }
            else if (processed.Record.Type == Labels.DOT)
            {
              if (!fight.DoTDamage.TryGetValue(spellKey, out stats))
              {
                stats = new SpellDamageStats { Caster = processed.Record.Attacker, Spell = processed.Record.SubType };
                fight.DoTDamage[spellKey] = stats;
              }
            }
            else if (processed.Record.Type == Labels.PROC)
            {
              if (!fight.ProcDamage.TryGetValue(spellKey, out stats))
              {
                stats = new SpellDamageStats { Caster = processed.Record.Attacker, Spell = processed.Record.SubType };
                fight.ProcDamage[spellKey] = stats;
              }
            }

            if (stats != null)
            {
              stats.Count += 1;
              stats.Max = Math.Max(processed.Record.Total, stats.Max);
              stats.Total += processed.Record.Total;
            }

            // only a pet can 'hit' with a Flurry since players only crush/slash/punch/pierce with main hand weapons
            if (processed.Record.AttackerOwner == null && processed.Record.Type == Labels.MELEE && processed.Record.SubType == "Hits" &&
              LineModifiersParser.IsFlurry(processed.Record.ModifiersMask))
            {
              PlayerManager.Instance.AddVerifiedPet(processed.Record.Attacker);
            }
          }
        }
        else
        {
          Helpers.AddAction(fight.TankingBlocks, processed.Record, processed.BeginTime);
          AddPlayerTime(fight, processed.Record, processed.Record.Defender, processed.BeginTime);
          fight.BeginTankingTime = double.IsNaN(fight.BeginTankingTime) ? processed.BeginTime : fight.BeginTankingTime;
          fight.LastTankingTime = processed.BeginTime;

          if (StatsUtil.IsHitType(processed.Record.Type))
          {
            fight.TankHits++;
          }
        }

        fight.LastTime = processed.BeginTime;
        LastFightProcessTime = processed.BeginTime;

        var ttl = fight.LastTime - fight.BeginTime + 1;
        fight.TooltipText = string.Format("#Hits To Players: {0}, #Hits From Players: {1}, Time Alive: {2}s", fight.TankHits, fight.DamageHits, ttl);

        DataManager.Instance.UpdateIfNewFightMap(fight.CorrectMapKey, fight, isNonTankingFight);
      }
    }

    private Fight Get(DamageRecord record, double currentTime, bool defender)
    {
      string npc = defender ? record.Defender : record.Attacker;

      Fight fight = DataManager.Instance.GetFight(npc);
      if (fight == null)
      {
        fight = Create(npc, currentTime);
      }

      return fight;
    }

    private Fight Create(string defender, double currentTime)
    {
      string timeString = DateUtil.FormatSimpleDate(currentTime);
      return new Fight
      {
        Name = string.Intern(defender),
        BeginTimeString = string.Intern(timeString),
        BeginTime = currentTime,
        LastTime = currentTime,
        Id = CurrentNpcID++,
        CorrectMapKey = string.Intern(defender)
      };
    }

    private static void AddPlayerTime(Fight fight, DamageRecord record, string player, double time)
    {
      var isInitialTanking = fight.DamageBlocks.Count == 0;
      var segments = isInitialTanking ? fight.InitialTankSegments : fight.DamageSegments;
      var subSegments = isInitialTanking ? fight.InitialTankSubSegments : fight.DamageSubSegments;
      StatsUtil.UpdateTimeSegments(segments, subSegments, Helpers.CreateRecordKey(record.Type, record.SubType), player, time);
    }

    private static bool IsValidAttack(DamageRecord record, bool isAttackerPlayer, out bool npcDefender)
    {
      bool valid = false;
      npcDefender = false;

      if (!record.Attacker.Equals(record.Defender, StringComparison.OrdinalIgnoreCase))
      {
        var isAttackerPlayerSpell = record.AttackerIsSpell && RecentSpellCache.ContainsKey(record.Attacker);
        isAttackerPlayer = isAttackerPlayer || isAttackerPlayerSpell;
        var isDefenderPlayer = PlayerManager.Instance.IsPetOrPlayerOrMerc(record.Defender);
        var isAttackerNpc = (!isAttackerPlayer && DataManager.Instance.IsKnownNpc(record.Attacker)) || (record.AttackerIsSpell && !isAttackerPlayerSpell);
        var isDefenderNpc = (!isDefenderPlayer && DataManager.Instance.IsKnownNpc(record.Defender)) || isAttackerPlayerSpell;

        if (isDefenderNpc && !isAttackerNpc)
        {
          valid = isAttackerPlayer || PlayerManager.IsPossiblePlayerName(record.Attacker);
          npcDefender = true;
        }
        else if (!isDefenderNpc && isAttackerNpc)
        {
          valid = isDefenderPlayer || PlayerManager.IsPossiblePlayerName(record.Defender);
          npcDefender = false;
        }
        else if (!isDefenderNpc && !isAttackerNpc)
        {
          if (isDefenderPlayer || isAttackerPlayer)
          {
            valid = isDefenderPlayer != isAttackerPlayer;
            if (valid)
            {
              npcDefender = !isDefenderPlayer;
            }
          }
          else
          {
            npcDefender = PlayerManager.IsPossiblePlayerName(record.Attacker) || !PlayerManager.IsPossiblePlayerName(record.Defender);
            valid = true;
          }
        }
        else if (isDefenderNpc && isAttackerNpc && DataManager.Instance.GetFight(record.Defender) != null
          && DataManager.Instance.GetFight(record.Attacker) == null)
        {
          valid = true;
          npcDefender = true;
        }
      }

      return valid;
    }
  }
}
