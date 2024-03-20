using System;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  internal class NpcDamageManager
  {
    internal double LastFightProcessTime = double.NaN;
    private int _currentNpcId = 1;
    private static readonly Dictionary<string, bool> RecentSpellCache = [];
    private readonly Dictionary<string, bool> _validCombo = [];
    private readonly ObjectCache<DamageRecord> _damageCache = new();
    private const int RecentSpellTime = 300;

    public NpcDamageManager()
    {
      DamageLineParser.EventsDamageProcessed += HandleDamageProcessed;
      DamageLineParser.EventsNewTaunt += HandleNewTaunt;
    }

    internal void Reset()
    {
      LastFightProcessTime = double.NaN;
      RecentSpellCache.Clear();
      _currentNpcId = 1;
      _damageCache.Clear();
      _validCombo.Clear();
    }

    private void HandleNewTaunt(TauntEvent e)
    {
      var fight = DataManager.Instance.GetFight(e.Record.Npc) ?? Create(e.Record.Npc, e.BeginTime);
      AddAction(fight.TauntBlocks, e.Record, e.BeginTime);
    }

    private void HandleDamageProcessed(DamageProcessedEvent processed)
    {
      var beginTime = processed.BeginTime;
      var record = _damageCache.Add(processed.Record);

      if (!LastFightProcessTime.Equals(beginTime))
      {
        DataManager.Instance.CheckExpireFights(beginTime);
        _validCombo.Clear();

        if (beginTime - LastFightProcessTime > RecentSpellTime)
        {
          RecentSpellCache.Clear();
        }
      }

      // cache recent player spells to help determine who the caster was
      var isAttackerPlayer = PlayerManager.Instance.IsPetOrPlayerOrMerc(record.Attacker) || record.Attacker == Labels.Rs;
      if (isAttackerPlayer && record.Type is Labels.Dd or Labels.Dot or Labels.Proc)
      {
        RecentSpellCache[record.SubType] = true;
      }

      var comboKey = record.Attacker + "=" + record.Defender;
      if (_validCombo.TryGetValue(comboKey, out var defender) || IsValidAttack(record, isAttackerPlayer, out defender))
      {
        _validCombo[comboKey] = defender;
        var isNonTankingFight = false;

        // fix for unknown spells having a good name to work from
        if (record.AttackerIsSpell && defender)
        {
          // check if it's really a player being hit
          defender = !PlayerManager.Instance.IsPetOrPlayerOrMerc(record.Defender);

          if (defender)
          {
            record.Attacker = Labels.Unk;
          }
        }

        var fight = Get(record, beginTime, defender);

        if (defender)
        {
          AddAction(fight.DamageBlocks, record, beginTime);
          AddPlayerTime(fight.DamageSegments, fight.DamageSubSegments, record, record.Attacker, beginTime);
          fight.BeginDamageTime = double.IsNaN(fight.BeginDamageTime) ? beginTime : fight.BeginDamageTime;
          fight.LastDamageTime = beginTime;

          if (StatsUtil.IsHitType(record.Type))
          {
            fight.DamageHits++;
            fight.DamageTotal += record.Total;
            isNonTankingFight = fight.DamageHits == 1;

            var attacker = record.AttackerOwner ?? record.Attacker;
            var validator = new DamageValidator();
            if (fight.PlayerDamageTotals.TryGetValue(attacker, out var total))
            {
              total.Damage += validator.IsValid(record) ? record.Total : 0;
              total.PetOwner ??= record.AttackerOwner;
              total.UpdateTime = beginTime;
            }
            else
            {
              fight.PlayerDamageTotals[attacker] = new FightTotalDamage
              {
                Damage = validator.IsValid(record) ? record.Total : 0,
                PetOwner = record.AttackerOwner,
                UpdateTime = beginTime,
                BeginTime = beginTime
              };
            }

            SpellDamageStats stats = null;
            var spellKey = record.Attacker + "++" + record.SubType;
            switch (record.Type)
            {
              case Labels.Dd:
                {
                  if (!fight.DdDamage.TryGetValue(spellKey, out stats))
                  {
                    stats = new SpellDamageStats { Caster = record.Attacker, Spell = record.SubType };
                    fight.DdDamage[spellKey] = stats;
                  }

                  break;
                }
              case Labels.Dot:
                {
                  if (!fight.DoTDamage.TryGetValue(spellKey, out stats))
                  {
                    stats = new SpellDamageStats { Caster = record.Attacker, Spell = record.SubType };
                    fight.DoTDamage[spellKey] = stats;
                  }

                  break;
                }
              case Labels.Proc:
                {
                  if (!fight.ProcDamage.TryGetValue(spellKey, out stats))
                  {
                    stats = new SpellDamageStats { Caster = record.Attacker, Spell = record.SubType };
                    fight.ProcDamage[spellKey] = stats;
                  }

                  break;
                }
            }

            if (stats != null)
            {
              stats.Count += 1;
              stats.Max = Math.Max(record.Total, stats.Max);
              stats.Total += record.Total;
            }
          }
        }
        else
        {
          AddAction(fight.TankingBlocks, record, beginTime);
          AddPlayerTime(fight.TankSegments, fight.TankSubSegments, record, record.Defender, beginTime);
          fight.BeginTankingTime = double.IsNaN(fight.BeginTankingTime) ? beginTime : fight.BeginTankingTime;
          fight.LastTankingTime = beginTime;
          fight.TankHits++;
          fight.TankTotal += record.Total;

          if (fight.PlayerTankTotals.TryGetValue(record.Defender, out var total))
          {
            total.Damage += record.Total;
            total.UpdateTime = beginTime;
          }
          else
          {
            fight.PlayerTankTotals[record.Defender] = new FightTotalDamage
            {
              Damage = record.Total,
              UpdateTime = beginTime,
              BeginTime = beginTime
            };
          }
        }

        fight.LastTime = beginTime;
        LastFightProcessTime = beginTime;
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
        Id = _currentNpcId++,
        CorrectMapKey = string.Intern(defender)
      };
    }

    private static void AddAction(List<ActionGroup> blockList, IAction action, double beginTime)
    {
      if (blockList.LastOrDefault() is { } last && last.BeginTime.Equals(beginTime))
      {
        last.Actions.Add(action);
      }
      else
      {
        var newSegment = new ActionGroup { BeginTime = beginTime };
        newSegment.Actions.Add(action);
        blockList.Add(newSegment);
      }
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

      var isAttackerPlayerSpell = IsAttackerPlayerSpell(record);
      isAttackerPlayer = isAttackerPlayer || isAttackerPlayerSpell;
      var isDefenderPlayer = PlayerManager.Instance.IsPetOrPlayerOrMerc(record.Defender);
      var isAttackerNpc = IsAttackerNpc(record, isAttackerPlayerSpell, isAttackerPlayer);
      var isDefenderNpc = IsDefenderNpc(record, isAttackerPlayerSpell, isDefenderPlayer) || isAttackerPlayer;

      if (isAttackerPlayer && isDefenderPlayer)
      {
        return false;
      }

      if (isDefenderNpc)
      {
        if (!isAttackerNpc)
        {
          npcDefender = true;
          return isAttackerPlayer || PlayerManager.IsPossiblePlayerName(record.Attacker);
        }

        if (DataManager.Instance.GetFight(record.Defender) != null && DataManager.Instance.GetFight(record.Attacker) == null)
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

        if (isDefenderPlayer)
        {
          npcDefender = false;
          return true;
        }
      }

      return true;
    }

    private static bool IsSelfAttack(DamageRecord record)
    {
      return record.Attacker.Equals(record.Defender, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAttackerPlayerSpell(DamageRecord record)
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
