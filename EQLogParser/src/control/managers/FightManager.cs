using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  internal interface IFightManager
  {
    void RemoveActiveFight(string name);
    void ClearActiveAdps();
    Fight GetFight(string name);
    void CheckExpireFights(double currentTime);
    void UpdateIfNewFightMap(string name, Fight fight, bool isNonTankingFight);
    bool IsLifetimeNpc(string name);
  }

  internal class FightManager : IFightManager
  {
    private static FightManager _instance;
    internal static FightManager Instance
    {
      get => _instance ??= new();
      set => _instance = value;
    }

    internal event EventHandler<string> EventsRemovedFight;
    internal event EventHandler<Fight> EventsNewFight;
    internal event EventHandler<Fight> EventsNewNonTankingFight;
    internal event EventHandler<Fight> EventsUpdateFight;
    internal event EventHandler<Fight> EventsNewOverlayFight;
    internal event Action<bool> EventsClearedActiveData;

    internal const int MaxTimeout = 60;
    internal const int FightTimeout = 30;
    internal uint MyNukeCritRateMod { get; private set; }
    internal uint MyDoTCritRateMod { get; private set; }

    // overlay / active / lifetime fight state
    private readonly ConcurrentDictionary<long, Fight> _overlayFights = new();
    private readonly ConcurrentDictionary<string, Fight> _activeFights = new();
    private readonly ConcurrentDictionary<string, byte> _lifetimeFights = new();

    // ADPS / crit rate state
    private readonly List<string> _adpsKeys = ["#DoTCritRate", "#NukeCritRate"];
    private readonly object _adpsLock = new();
    private readonly Dictionary<string, Dictionary<string, uint>> _adpsActive = [];

    // NPC damage processing state (merged from NpcDamageManager)
    internal double LastFightProcessTime = double.NaN;
    private int _currentNpcId = 1;
    private static readonly Dictionary<string, bool> RecentSpellCache = [];
    private readonly Dictionary<string, bool> _validCombo = [];
    private readonly SimpleObjectCache<DamageRecord> _damageCache = new();
    private const int RecentSpellTime = 300;
    private readonly List<Defender> _defenders = [];

    internal FightManager()
    {
      _adpsKeys.ForEach(adpsKey => _adpsActive[adpsKey] = []);

      PlayerManager.Instance.EventsNewVerifiedPlayer += (_, name) => RemoveFight(name);
      PlayerManager.Instance.EventsNewVerifiedPet += (_, name) => RemoveFight(name);

      DamageLineParser.EventsDamageProcessed += HandleDamageProcessed;
      DamageLineParser.EventsNewTaunt += HandleNewTaunt;
    }

    internal void Reset()
    {
      LastFightProcessTime = double.NaN;
      RecentSpellCache.Clear();
      _currentNpcId = 1;
      _validCombo.Clear();
      _damageCache.Clear();
      _defenders.Clear();
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
          if (!MainWindow.IsDamageOverlayOpen)
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
        EventsNewFight?.Invoke(this, fight);
      }
      else
      {
        EventsUpdateFight?.Invoke(this, fight);
      }

      // basically an Add use case for only showing Fights with player damage
      if (isNonTankingFight)
      {
        EventsNewNonTankingFight?.Invoke(this, fight);
      }

      if (fight.DamageHits > 0)
      {
        _overlayFights[fight.Id] = fight;

        // don't bother if not configured (lazy optimization)
        if (ConfigUtil.IfSet("IsDamageOverlayEnabled"))
        {
          EventsNewOverlayFight?.Invoke(this, fight);
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

    internal void Clear()
    {
      _activeFights.Clear();
      _lifetimeFights.Clear();
      _overlayFights.Clear();
      ClearActiveAdps();
      EventsClearedActiveData?.Invoke(true);
    }

    public void ClearActiveAdps()
    {
      lock (_adpsLock)
      {
        _adpsKeys.ForEach(key => _adpsActive[key].Clear());
        MyDoTCritRateMod = 0;
        MyNukeCritRateMod = 0;
      }
    }

    internal Dictionary<long, Fight> GetOverlayFights() => _overlayFights.ToDictionary(i => i.Key, i => i.Value);
    internal void RemoveOverlayFight(long id) => _overlayFights.Remove(id, out _);
    internal bool HasOverlayFights() => !_overlayFights.IsEmpty;

    internal void ZoneChanged()
    {
      var updated = false;
      lock (_adpsLock)
      {
        foreach (var active in _adpsActive)
        {
          foreach (var landsOn in active.Value.Keys.ToArray())
          {
            if (DataManager.Instance._adpsLandsOn.TryGetValue(landsOn, out var value))
            {
              // Need this check since Glyph may be present and there's no
              // lands on data for it as it's a special cast
              if (value.Any(spellData => spellData.SongWindow))
              {
                _adpsActive[active.Key].Remove(landsOn);
                updated = true;
              }
            }
          }
        }
      }

      if (updated)
      {
        RecalculateAdpsInternal();
      }
    }

    internal void UpdateAdps(string key, string msg, uint value)
    {
      lock (_adpsLock)
      {
        _adpsActive[key][msg] = value;
        RecalculateAdpsInternal();
      }
    }

    private void RecalculateAdpsInternal()
    {
      lock (_adpsLock)
      {
        MyDoTCritRateMod = (uint)_adpsActive[_adpsKeys[0]].Sum(kv => kv.Value);
        MyNukeCritRateMod = (uint)_adpsActive[_adpsKeys[1]].Sum(kv => kv.Value);
      }
    }

    internal void RecalculateAdps()
    {
      RecalculateAdpsInternal();
    }

    internal void SetCritRateMods(uint dot, uint nuke)
    {
      MyDoTCritRateMod = dot;
      MyNukeCritRateMod = nuke;
    }

    public bool IsLifetimeNpc(string name) => !string.IsNullOrEmpty(name) && _lifetimeFights.ContainsKey(name);

    internal void RemoveFight(string name)
    {
      if (!string.IsNullOrEmpty(name))
      {
        var removed = _activeFights.TryRemove(name, out _);
        removed = _lifetimeFights.TryRemove(name, out _) || removed;

        if (removed)
        {
          EventsRemovedFight?.Invoke(this, name);
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

    private void TestProcessed(DamageProcessedEvent processed)
    {
      Defender found = null;
      var oldest = processed.BeginTime - FightTimeout;
      for (var i = _defenders.Count - 1; i >= 0; i--)
      {
        if (oldest > _defenders[i].BeginTime)
        {
          break;
        }

        if (_defenders[i].Name == processed.Record.Defender)
        {
          found = _defenders[i];
          found.BeginTime = processed.BeginTime;
          break;
        }
      }

      if (found == null)
      {
        found = new Defender
        {
          Name = processed.Record.Defender,
          BeginTime = processed.BeginTime
        };

        _defenders.Add(found);
      }

      found.Records.Add(processed.Record);
    }

    private void HandleDamageProcessed(DamageProcessedEvent processed)
    {
      //TestProcessed(processed);
      var beginTime = processed.BeginTime;
      var record = _damageCache.Add(processed.Record);

      if (!LastFightProcessTime.Equals(beginTime))
      {
        CheckExpireFights(beginTime);
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

    private bool IsValidAttack(DamageRecord record, bool isAttackerPlayer, out bool npcDefender)
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
          return isDefenderPlayer || PlayerManager.IsPossiblePlayerName(record.Defender);
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

        if (!PlayerManager.IsPossiblePlayerName(record.Defender))
        {
          npcDefender = true;
          return true;
        }

        if (!PlayerManager.IsPossiblePlayerName(record.Attacker))
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

    private bool IsAttackerNpc(DamageRecord record, bool isAttackerPlayerSpell, bool isAttackerPlayer)
    {
      return (!isAttackerPlayer && DataManager.Instance.IsKnownNpc(record.Attacker)) || (record.AttackerIsSpell && !isAttackerPlayerSpell);
    }

    private bool IsDefenderNpc(DamageRecord record, bool isAttackerPlayerSpell, bool isDefenderPlayer)
    {
      return (!isDefenderPlayer && DataManager.Instance.IsKnownNpc(record.Defender)) || isAttackerPlayerSpell;
    }
  }
}
