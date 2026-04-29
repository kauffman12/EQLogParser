using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace EQLogParser
{
  internal interface IFightManager
  {
    void RemoveActiveFight(string name);
    Fight GetFight(string name);
    void CheckExpireFights(double currentTime);
    void UpdateIfNewFightMap(string name, Fight fight, bool isNonTankingFight);
    bool IsLifetimeNpc(string name);
  }

  internal class FightManager : IFightManager, ILifecycle
  {
    internal event Action<string> EventsRemovedFight;
    internal event Action<Fight> EventsNewFight;
    internal event Action<Fight> EventsNewNonTankingFight;
    internal event Action<Fight> EventsUpdateFight;
    internal event Action<Fight> EventsNewOverlayFight;
    internal event Action<bool> EventsClearedActiveData;
    internal const int MaxTimeout = 60;
    internal const int FightTimeout = 30;

    // singleton with set for unit test
    internal static FightManager Instance { get; set; } = new();

    // overlay / active / lifetime fight state
    private readonly ConcurrentDictionary<long, Fight> _overlayFights = new();
    private readonly ConcurrentDictionary<string, Fight> _activeFights = new();
    private readonly ConcurrentDictionary<string, byte> _lifetimeFights = new();

    // NPC damage processing state (merged from NpcDamageManager)
    internal double LastFightProcessTime = double.NaN;
    private int _currentNpcId = 1;
    private static readonly ConcurrentDictionary<string, bool> RecentSpellCache = [];
    private readonly ConcurrentDictionary<string, bool> _validCombo = [];
    private readonly SimpleObjectCache<DamageRecord> _damageCache = new();
    private const int RecentSpellTime = 300;

    internal FightManager()
    {
      PlayerRegistry.Instance.EventsNewVerifiedPlayer += (name) => RemoveFight(name);
      PlayerRegistry.Instance.EventsNewVerifiedPet += (name) => RemoveFight(name);

      DamageLineParser.EventsDamageProcessed += HandleDamageProcessed;
      DamageLineParser.EventsNewTaunt += HandleNewTaunt;

      LifecycleManager.Register(this);
    }

    public void CheckExpireFights(double currentTime)
    {
      var removeActiveKeys = new List<string>();
      foreach (var kv in _activeFights)
      {
        var diff = currentTime - kv.Value.LastTime;
        if (diff > MaxTimeout || (diff > FightTimeout && kv.Value.DamageBlocks.Count > 0))
        {
          removeActiveKeys.Add(kv.Value.CorrectMapKey);

          // cleanup overlay data if overlay isn't actually open
          if (!AppSettings.IsDamageOverlayOpen)
          {
            RemoveOverlayFight(kv.Value.Id);
          }
        }
      }

      removeActiveKeys.ForEach(RemoveActiveFight);
    }

    public Fight GetFight(string name)
    {
      Fight result = null;
      if (!string.IsNullOrEmpty(name))
      {
        _activeFights.TryGetValue(name, out result);
        // don't think this happens but just in-case
        if (result?.Dead == true)
        {
          _activeFights.TryRemove(name, out _);
        }
      }
      return result;
    }

    public void RemoveActiveFight(string name)
    {
      if (_activeFights.TryRemove(name, out var fight))
      {
        fight.Dead = true;
      }
    }

    public void UpdateIfNewFightMap(string name, Fight fight, bool isNonTankingFight)
    {
      _lifetimeFights[name] = 1;

      if (_activeFights.TryAdd(name, fight))
      {
        _activeFights[name] = fight;
        EventsNewFight?.Invoke(fight);
      }
      else
      {
        EventsUpdateFight?.Invoke(fight);
      }

      // basically an Add use case for only showing Fights with player damage
      if (isNonTankingFight)
      {
        EventsNewNonTankingFight?.Invoke(fight);
      }

      if (fight.DamageHits > 0)
      {
        _overlayFights[fight.Id] = fight;

        // don't bother if not configured (lazy optimization)
        if (ConfigUtil.IfSet("IsDamageOverlayEnabled"))
        {
          EventsNewOverlayFight?.Invoke(fight);
        }
      }
    }

    internal void ResetOverlayFights(bool active = false, bool deadAlso = false)
    {
      var groupId = (active && !_activeFights.IsEmpty) ? _activeFights.Values.First().GroupId : -1;
      // active is used after the log as been loaded. the overlay opening is displayed so that
      // FightTable has time to populate the GroupIds. if for some reason not enough time has
      // elapsed then the IDs will still be 0 so ignore
      if (groupId == 0)
      {
        groupId = -1;
      }

      var removeList = new List<long>();
      foreach (var fight in _overlayFights.Values)
      {
        if (fight != null && (groupId == -1 || fight.GroupId != groupId || (deadAlso && fight.Dead)))
        {
          fight.PlayerDamageTotals.Clear();
          fight.PlayerTankTotals.Clear();
          removeList.Add(fight.Id);
        }
      }

      removeList.ForEach(RemoveOverlayFight);
    }

    public void Clear(bool serverChanged = true)
    {
      Volatile.Write(ref LastFightProcessTime, double.NaN);
      _currentNpcId = 1;
      _activeFights.Clear();
      _lifetimeFights.Clear();
      _overlayFights.Clear();
      _damageCache.Clear();
      _validCombo.Clear();
      RecentSpellCache.Clear();
      AdpsTracker.Instance.Clear();
      EventsClearedActiveData?.Invoke(serverChanged);
    }

    public void Shutdown() => Clear();

    internal Dictionary<long, Fight> GetOverlayFights() => _overlayFights.ToDictionary(i => i.Key, i => i.Value);
    internal void RemoveOverlayFight(long id) => _overlayFights.Remove(id, out _);
    internal bool HasOverlayFights() => !_overlayFights.IsEmpty;
    public bool IsLifetimeNpc(string name) => !string.IsNullOrEmpty(name) && _lifetimeFights.ContainsKey(name);

    internal void RemoveFight(string name)
    {
      if (!string.IsNullOrEmpty(name))
      {
        var removed = _activeFights.TryRemove(name, out _);
        removed = _lifetimeFights.TryRemove(name, out _) || removed;

        if (removed)
        {
          EventsRemovedFight?.Invoke(name);
        }

        var removeOverlayFights = new List<long>();
        foreach (var fight in _overlayFights.Values)
        {
          if (fight.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
          {
            removeOverlayFights.Add(fight.Id);
          }
        }

        removeOverlayFights.ForEach(RemoveOverlayFight);
      }
    }

    // ---- NPC Damage Processing (merged from NpcDamageManager) ----

    private void HandleNewTaunt(TauntEvent e)
    {
      var fight = GetFight(e.Record.Npc) ?? Create(e.Record.Npc, e.BeginTime);
      AddAction(fight.TauntBlocks, e.Record, e.BeginTime);
    }

    private void HandleDamageProcessed(DamageProcessedEvent processed)
    {
      //TestProcessed(processed);
      var beginTime = processed.BeginTime;
      var record = _damageCache.Add(processed.Record);

      if (!Volatile.Read(ref LastFightProcessTime).Equals(beginTime))
      {
        CheckExpireFights(beginTime);
        _validCombo.Clear();

        if (beginTime - Volatile.Read(ref LastFightProcessTime) > RecentSpellTime)
        {
          RecentSpellCache.Clear();
        }
      }

      // cache recent player spells to help determine who the caster was
      var isAttackerPlayer = PlayerRegistry.Instance.IsPetOrPlayerOrMerc(record.Attacker) || record.Attacker == Labels.Rs;
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
          defender = !PlayerRegistry.Instance.IsPetOrPlayerOrMerc(record.Defender);

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
        Volatile.Write(ref LastFightProcessTime, beginTime);
        // tooltip
        var ttl = fight.LastTime - fight.BeginTime + 1;
        fight.TooltipText = $"#Hits To Players: {fight.TankHits}, #Hits From Players: {fight.DamageHits}, Time Alive: {ttl}s";
        UpdateIfNewFightMap(fight.CorrectMapKey, fight, isNonTankingFight);
      }
    }

    private Fight Get(DamageRecord record, double currentTime, bool defender)
    {
      var npc = defender ? record.Defender : record.Attacker;
      return GetFight(npc) ?? Create(npc, currentTime);
    }

    private Fight Create(string defender, double currentTime)
    {
      var timeString = DateUtil.FormatDotNetDateSeconds(currentTime);
      return new Fight
      {
        Name = string.Intern(defender),
        BeginTimeString = string.Intern(timeString),
        BeginTime = currentTime,
        LastTime = currentTime,
        Id = Interlocked.Increment(ref _currentNpcId),
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

    private bool IsValidAttack(DamageRecord record, bool isAttackerPlayer, out bool npcDefender)
    {
      npcDefender = false;

      if (IsSelfAttack(record))
      {
        return false;
      }

      var isAttackerPlayerSpell = IsAttackerPlayerSpell(record);
      isAttackerPlayer = isAttackerPlayer || isAttackerPlayerSpell;
      var isDefenderPlayer = PlayerRegistry.Instance.IsPetOrPlayerOrMerc(record.Defender);
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
          return isAttackerPlayer || PlayerRegistry.IsPossiblePlayerName(record.Attacker);
        }

        if (GetFight(record.Defender) != null && GetFight(record.Attacker) == null)
        {
          npcDefender = true;
          return true;
        }
      }
      else
      {
        if (isAttackerNpc)
        {
          return isDefenderPlayer || PlayerRegistry.IsPossiblePlayerName(record.Defender);
        }

        if (isDefenderPlayer)
        {
          return true;
        }

        if (isAttackerPlayer)
        {
          npcDefender = true;
          return true;
        }

        if (!PlayerRegistry.IsPossiblePlayerName(record.Defender))
        {
          npcDefender = true;
          return true;
        }

        if (!PlayerRegistry.IsPossiblePlayerName(record.Attacker))
        {
          return true;
        }

        npcDefender = true;
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
      return (!isAttackerPlayer && EQDataStore.Instance.IsKnownNpc(record.Attacker)) || (record.AttackerIsSpell && !isAttackerPlayerSpell);
    }

    private static bool IsDefenderNpc(DamageRecord record, bool isAttackerPlayerSpell, bool isDefenderPlayer)
    {
      return (!isDefenderPlayer && EQDataStore.Instance.IsKnownNpc(record.Defender)) || isAttackerPlayerSpell;
    }
  }
}
