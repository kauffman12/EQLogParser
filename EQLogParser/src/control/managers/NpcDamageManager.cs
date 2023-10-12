using System;
using System.Collections.Generic;

namespace EQLogParser
{
  class NpcDamageManager
  {
    internal double LastFightProcessTime = double.NaN;
    private int CurrentNpcId = 1;
    private static readonly Dictionary<string, bool> RecentSpellCache = new();
    private static readonly Dictionary<string, bool> ValidCombo = new();
    private const int RecentSpellTime = 300;

    public NpcDamageManager()
    {
      DamageLineParser.EventsDamageProcessed += HandleDamageProcessed;
      DamageLineParser.EventsNewTaunt += HandleNewTaunt;
    }

    internal void Reset()
    {
      LastFightProcessTime = double.NaN;
      CurrentNpcId = 1;
      RecentSpellCache.Clear();
      ValidCombo.Clear();
    }

    private void HandleNewTaunt(object sender, TauntEvent e)
    {
      var fight = DataManager.Instance.GetFight(e.Record.Npc) ?? Create(e.Record.Npc, e.BeginTime);
      Helpers.AddAction(fight.TauntBlocks, e.Record, e.BeginTime);
    }

    private void HandleDamageProcessed(object sender, DamageProcessedEvent processed)
    {
      if (LastFightProcessTime != processed.BeginTime)
      {
        DataManager.Instance.CheckExpireFights(processed.BeginTime);
        ValidCombo.Clear();

        if (processed.BeginTime - LastFightProcessTime > RecentSpellTime)
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

      var comboKey = processed.Record.Attacker + "=" + processed.Record.Defender;
      if (ValidCombo.TryGetValue(comboKey, out var defender) || IsValidAttack(processed.Record, isAttackerPlayer, out defender))
      {
        ValidCombo[comboKey] = defender;
        var isNonTankingFight = false;

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

        var fight = Get(processed.Record, processed.BeginTime, defender);

        if (defender)
        {
          Helpers.AddAction(fight.DamageBlocks, processed.Record, processed.BeginTime);
          AddPlayerTime(fight.DamageSegments, fight.DamageSubSegments, processed.Record, processed.Record.Attacker, processed.BeginTime);
          fight.BeginDamageTime = double.IsNaN(fight.BeginDamageTime) ? processed.BeginTime : fight.BeginDamageTime;
          fight.LastDamageTime = processed.BeginTime;

          if (StatsUtil.IsHitType(processed.Record.Type))
          {
            fight.DamageHits++;
            fight.DamageTotal += processed.Record.Total;
            isNonTankingFight = fight.DamageHits == 1;

            var attacker = processed.Record.AttackerOwner ?? processed.Record.Attacker;
            var validator = new DamageValidator();
            if (fight.PlayerDamageTotals.TryGetValue(attacker, out var total))
            {
              total.Damage += validator.IsValid(processed.Record) ? processed.Record.Total : 0;
              total.PetOwner ??= processed.Record.AttackerOwner;
              total.UpdateTime = processed.BeginTime;
            }
            else
            {
              fight.PlayerDamageTotals[attacker] = new FightTotalDamage
              {
                Damage = validator.IsValid(processed.Record) ? processed.Record.Total : 0,
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
          }
        }
        else
        {
          Helpers.AddAction(fight.TankingBlocks, processed.Record, processed.BeginTime);
          AddPlayerTime(fight.TankSegments, fight.TankSubSegments, processed.Record, processed.Record.Defender, processed.BeginTime);
          fight.BeginTankingTime = double.IsNaN(fight.BeginTankingTime) ? processed.BeginTime : fight.BeginTankingTime;
          fight.LastTankingTime = processed.BeginTime;

          fight.TankHits++;
          fight.TankTotal += processed.Record.Total;

          if (fight.PlayerTankTotals.TryGetValue(processed.Record.Defender, out var total))
          {
            total.Damage += processed.Record.Total;
            total.UpdateTime = processed.BeginTime;
          }
          else
          {
            fight.PlayerTankTotals[processed.Record.Defender] = new FightTotalDamage
            {
              Damage = processed.Record.Total,
              UpdateTime = processed.BeginTime,
              BeginTime = processed.BeginTime
            };
          }
        }

        fight.LastTime = processed.BeginTime;
        LastFightProcessTime = processed.BeginTime;
        DataManager.Instance.UpdateIfNewFightMap(fight.CorrectMapKey, fight, isNonTankingFight);
      }
    }

    private Fight Get(DamageRecord record, double currentTime, bool defender)
    {
      var npc = defender ? record.Defender : record.Attacker;
      return DataManager.Instance.GetFight(npc) ?? Create(npc, currentTime);
    }

    private Fight Create(string defender, double currentTime)
    {
      var timeString = DateUtil.FormatSimpleDate(currentTime);
      return new Fight
      {
        Name = string.Intern(defender),
        BeginTimeString = string.Intern(timeString),
        BeginTime = currentTime,
        LastTime = currentTime,
        Id = CurrentNpcId++,
        CorrectMapKey = string.Intern(defender)
      };
    }

    private static void AddPlayerTime(Dictionary<string, TimeSegment> segments, Dictionary<string, Dictionary<string, TimeSegment>> subSegments,
      DamageRecord record, string player, double time)
    {
      StatsUtil.UpdateTimeSegments(segments, subSegments, StatsUtil.CreateRecordKey(record.Type, record.SubType), player, time);
    }

    private static bool IsValidAttack(DamageRecord record, bool isAttackerPlayer, out bool npcDefender)
    {
      npcDefender = false;

      if (IsSelfAttack(record))
      {
        return false;
      }

      var isAttackerPlayerSpell = IsAttackerPlayerSpell(record, isAttackerPlayer);
      isAttackerPlayer = isAttackerPlayer || isAttackerPlayerSpell;
      var isDefenderPlayer = PlayerManager.Instance.IsPetOrPlayerOrMerc(record.Defender);
      var isAttackerNpc = IsAttackerNpc(record, isAttackerPlayerSpell, isAttackerPlayer);
      var isDefenderNpc = IsDefenderNpc(record, isAttackerPlayerSpell, isDefenderPlayer);

      if (isDefenderNpc)
      {
        if (!isAttackerNpc)
        {
          npcDefender = true;
          return isAttackerPlayer || PlayerManager.IsPossiblePlayerName(record.Attacker);
        }
        else if (DataManager.Instance.GetFight(record.Defender) != null && DataManager.Instance.GetFight(record.Attacker) == null)
        {
          npcDefender = true;
          return true;
        }
      }
      else
      {
        if (isAttackerNpc)
        {
          return isDefenderPlayer || PlayerManager.IsPossiblePlayerName(record.Defender);
        }
        else if (isDefenderPlayer != isAttackerPlayer)
        {
          npcDefender = !isDefenderPlayer;
          return true;
        }
      }

      return false;
    }

    private static bool IsSelfAttack(DamageRecord record)
    {
      return record.Attacker.Equals(record.Defender, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAttackerPlayerSpell(DamageRecord record, bool isAttackerPlayer)
    {
      return record.AttackerIsSpell && RecentSpellCache.ContainsKey(record.Attacker);
    }

    private static bool IsAttackerNpc(DamageRecord record, bool isAttackerPlayerSpell, bool isAttackerPlayer)
    {
      return (!isAttackerPlayer && DataManager.Instance.IsKnownNpc(record.Attacker)) || (record.AttackerIsSpell && !isAttackerPlayerSpell);
    }

    private static bool IsDefenderNpc(DamageRecord record, bool isAttackerPlayerSpell, bool isDefenderPlayer)
    {
      return (!isDefenderPlayer && DataManager.Instance.IsKnownNpc(record.Defender)) || isAttackerPlayerSpell;
    }
  }
}
