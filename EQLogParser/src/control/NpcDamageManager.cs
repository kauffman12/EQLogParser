using System;
using System.Collections.Generic;
using System.Globalization;

namespace EQLogParser
{
  class NpcDamageManager
  {
    internal double LastFightProcessTime = double.NaN;
    private int CurrentNpcID = 0;
    private static readonly Dictionary<string, bool> RecentSpellCache = new Dictionary<string, bool>();
    private static readonly Dictionary<string, bool> ValidCombo = new Dictionary<string, bool>();
    private const int RECENTSPELLTIME = 300;

    public NpcDamageManager() => DamageLineParser.EventsDamageProcessed += HandleDamageProcessed;

    ~NpcDamageManager() => DamageLineParser.EventsDamageProcessed -= HandleDamageProcessed;

    internal void ResetTime() => LastFightProcessTime = double.NaN;

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
      var isAttackerPlayer = PlayerManager.Instance.IsPetOrPlayer(processed.Record.Attacker) || processed.Record.Attacker == Labels.RS;
      if (isAttackerPlayer && (processed.Record.Type == Labels.DD || processed.Record.Type == Labels.DOT || processed.Record.Type == Labels.PROC))
      {
        RecentSpellCache[processed.Record.SubType] = true;
      }

      string comboKey = processed.Record.Attacker + "=" + processed.Record.Defender;
      if (ValidCombo.TryGetValue(comboKey, out bool defender) || IsValidAttack(processed.Record, isAttackerPlayer, out defender))
      {
        ValidCombo[comboKey] = defender;
        bool isNonTankingFight = false;
        string origTimeString = processed.OrigTimeString.Substring(4, 15);

        // fix for unknown spells having a good name to work from
        if (processed.Record.AttackerIsSpell && defender)
        {
          processed.Record.Attacker = Labels.UNK;
        }

        Fight fight = Get(processed.Record, processed.BeginTime, origTimeString, defender);

        if (defender)
        {
          Helpers.AddAction(fight.DamageBlocks, processed.Record, processed.BeginTime);
          AddPlayerTime(fight, processed.Record, processed.Record.Attacker, processed.BeginTime);
          fight.BeginDamageTime = double.IsNaN(fight.BeginDamageTime) ? processed.BeginTime : fight.BeginDamageTime;
          fight.LastDamageTime = processed.BeginTime;

          if (StatsUtil.IsHitType(processed.Record.Type))
          {
            fight.DamageHits++;
            fight.Total += processed.Record.Total;
            isNonTankingFight = fight.DamageHits == 1;

            var attacker = processed.Record.AttackerOwner ?? processed.Record.Attacker;
            if (fight.PlayerTotals.TryGetValue(attacker, out FightTotalDamage total))
            {
              total.Damage += (processed.Record.Type == Labels.BANE) ? 0 : processed.Record.Total;
              total.DamageWithBane += processed.Record.Total;
              total.Name = processed.Record.Attacker;
              total.PetOwner = processed.Record.AttackerOwner;
              total.UpdateTime = processed.BeginTime;
            }
            else
            {
              fight.PlayerTotals[attacker] = new FightTotalDamage
              {
                Damage = (processed.Record.Type == Labels.BANE) ? 0 : processed.Record.Total,
                DamageWithBane = processed.Record.Total,
                Name = processed.Record.Attacker,
                PetOwner = processed.Record.AttackerOwner,
                UpdateTime = processed.BeginTime,
                BeginTime = processed.BeginTime
              };
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
        fight.TooltipText = string.Format(CultureInfo.CurrentCulture, "#Hits To Players: {0}, #Hits From Players: {1}, Time Alive: {2}s", fight.TankHits, fight.DamageHits, ttl);

        DataManager.Instance.UpdateIfNewFightMap(fight.CorrectMapKey, fight, isNonTankingFight);
      }
    }

    private Fight Get(DamageRecord record, double currentTime, string origTimeString, bool defender)
    {
      string npc = defender ? record.Defender : record.Attacker;

      Fight fight = DataManager.Instance.GetFight(npc);
      if (fight == null)
      {
        fight = Create(npc, currentTime, origTimeString);
      }

      return fight;
    }

    private Fight Create(string defender, double currentTime, string origTimeString)
    {
      return new Fight()
      {
        Name = string.Intern(defender),
        BeginTimeString = string.Intern(origTimeString),
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
        var isDefenderPlayer = PlayerManager.Instance.IsPetOrPlayer(record.Defender);
        var isAttackerNpc = (!isAttackerPlayer && DataManager.Instance.IsKnownNpc(record.Attacker)) || (record.AttackerIsSpell && !isAttackerPlayerSpell);
        var isDefenderNpc = (!isDefenderPlayer && DataManager.Instance.IsKnownNpc(record.Defender)) || isAttackerPlayerSpell;

        if (isDefenderNpc && !isAttackerNpc)
        {
          valid = isAttackerPlayer || PlayerManager.Instance.IsPossiblePlayerName(record.Attacker);
          npcDefender = true;
        }
        else if (!isDefenderNpc && isAttackerNpc)
        {
          valid = isDefenderPlayer || PlayerManager.Instance.IsPossiblePlayerName(record.Defender);
          npcDefender = false;
        }
        else if (!isDefenderNpc && !isAttackerNpc)
        {
          if (isDefenderPlayer || isAttackerPlayer)
          {
            valid = isDefenderPlayer != isAttackerPlayer;
            if (!valid)
            {
              if (PlayerManager.Instance.IsCharmPet(record.Attacker))
              {
                valid = true;
                npcDefender = false;
              }
              else if (PlayerManager.Instance.IsCharmPet(record.Defender))
              {
                valid = true;
                npcDefender = true;
              }
            }
            else
            {
              npcDefender = !isDefenderPlayer;
            }
          }
          else
          {
            npcDefender = PlayerManager.Instance.IsPossiblePlayerName(record.Attacker) || !PlayerManager.Instance.IsPossiblePlayerName(record.Defender);
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
