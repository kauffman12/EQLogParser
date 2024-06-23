﻿using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace EQLogParser
{
  internal enum SpellClass
  {
    War = 1, Clr = 2, Pal = 4, Rng = 8, Shd = 16, Dru = 32, Mnk = 64, Brd = 128, Rog = 256,
    Shm = 512, Nec = 1024, Wiz = 2048, Mag = 4096, Enc = 8192, Bst = 16384, Ber = 32768
  }

  internal enum SpellTarget
  {
    Los = 1, Casterae = 2, Castergroup = 3, Casterpb = 4, Singletarget = 5, Self = 6, Targetae = 8, Pet = 14,
    Casterpbplayers = 36, Pet2 = 38, Nearbyplayersae = 40, Targetgroup = 41, Directionae = 42, Targetringae = 45
  }

  internal enum SpellResist
  {
    Undefined = -2, Reflected = -1, Unresistable = 0, Magic, Fire, Cold, Poison, Disease, Lowest, Average, Physical, Corruption
  }

  internal static class Labels
  {
    public const string Absorb = "Absorb";
    public const string Dd = "Direct Damage";
    public const string Dot = "DoT Tick";
    public const string Ds = "Damage Shield";
    public const string Rs = "Reverse DS";
    public const string Bane = "Bane Damage";
    public const string OtherDmg = "Other Damage";
    public const string Proc = "Proc";
    public const string Hot = "HoT Tick";
    public const string Heal = "Direct Heal";
    public const string Melee = "Melee";
    public const string SelfHeal = "Melee Heal";
    public const string NoData = "No Data Available";
    public const string PetPlayerOption = "Players +Pets";
    public const string PlayerOption = "Players";
    public const string PetOption = "Pets";
    public const string RaidOption = "Raid";
    public const string RaidTotals = "Totals";
    public const string Riposte = "Riposte";
    public const string AllOption = "Uncategorized";
    public const string Unassigned = "Unknown Pet Owner";
    public const string Unk = "Unknown";
    public const string UnkSpell = "Unknown Spell";
    public const string ReceivedHealParse = "Received Healing";
    public const string HealParse = "Healing";
    public const string TankParse = "Tanking";
    public const string TopHealParse = "Top Heals";
    public const string DamageParse = "Damage";
    public const string Miss = "Miss";
    public const string Dodge = "Dodge";
    public const string Parry = "Parry";
    public const string Block = "Block";
    public const string Invulnerable = "Invulnerable";
  }

  internal class DataManager
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    internal static DataManager Instance = new();
    internal event EventHandler<string> EventsRemovedFight;
    internal event EventHandler<Fight> EventsNewFight;
    internal event EventHandler<Fight> EventsNewNonTankingFight;
    internal event EventHandler<Fight> EventsUpdateFight;
    internal event EventHandler<Fight> EventsNewOverlayFight;
    internal event Action<bool> EventsClearedActiveData;

    internal const int MaxTimeout = 60;
    internal const int FightTimeout = 30;
    internal const double BuffsOffset = 90;
    internal uint MyNukeCritRateMod { get; private set; }
    internal uint MyDoTCritRateMod { get; private set; }

    private static readonly SpellAbbrvComparer AbbrvComparer = new();
    private readonly List<string> _adpsKeys = ["#DoTCritRate", "#NukeCritRate"];
    private readonly HashSet<SpellData> _allSpellData = [];
    private readonly Dictionary<string, Dictionary<string, uint>> _adpsActive = [];
    private readonly Dictionary<string, Dictionary<string, uint>> _adpsValues = [];
    private readonly Dictionary<string, HashSet<SpellData>> _adpsLandsOn = [];
    private readonly Dictionary<string, HashSet<SpellData>> _adpsWearOff = [];
    private readonly Dictionary<string, bool> _oldSpellNamesDb = [];
    private readonly SpellTreeNode _landsOnOtherTree = new();
    private readonly SpellTreeNode _landsOnYouTree = new();
    private readonly SpellTreeNode _wearOffTree = new();

    // definitely used in single thread
    private readonly Dictionary<string, string> _titleToClass = [];

    // locking was causing a problem for OverlayFights? I don't know
    private readonly ConcurrentDictionary<long, Fight> _overlayFights = new();
    private readonly ConcurrentDictionary<string, byte> _allNpcs = new();
    private readonly ConcurrentDictionary<string, SpellData> _spellsAbbrvDb = new();
    private readonly ConcurrentDictionary<string, SpellClass> _spellsToClass = new();
    private readonly ConcurrentDictionary<string, Fight> _activeFights = new();
    private readonly ConcurrentDictionary<string, byte> _lifetimeFights = new();
    private readonly ConcurrentDictionary<string, string> _spellAbbrvCache = new();
    private readonly ConcurrentDictionary<string, string> _ranksCache = new();
    private readonly ConcurrentDictionary<string, List<SpellData>> _spellsNameDb = new();

    private DataManager()
    {
      var spellList = new List<SpellData>();

      // build ranks cache
      Enumerable.Range(1, 9).ToList().ForEach(r => _ranksCache[r.ToString(CultureInfo.CurrentCulture)] = "");
      Enumerable.Range(1, 200).ToList().ForEach(r => _ranksCache[TextUtils.IntToRoman(r)] = "");
      _ranksCache["Third"] = "Root";
      _ranksCache["Fifth"] = "Root";
      _ranksCache["Octave"] = "Root";

      // Player title mapping for /who queries
      ConfigUtil.ReadList(@"data\titles.txt").ForEach(line =>
      {
        var split = line.Split('=');
        if (split.Length == 2)
        {
          _titleToClass[split[0]] = split[0];
          foreach (var title in split[1].Split(','))
          {
            _titleToClass[title + " (" + split[0] + ")"] = split[0];
          }
        }
      });

      // Old Spell cache (EQEMU)
      ConfigUtil.ReadList(@"data\oldspells.txt").ForEach(line => _oldSpellNamesDb[line] = true);

      var procCache = new Dictionary<string, bool>();
      ConfigUtil.ReadList(@"data\procs.txt").Where(line => line.Length > 0 && line[0] != '#').ToList().ForEach(line => procCache[line] = true);

      ConfigUtil.ReadList(@"data\spells.txt").ForEach(line =>
      {
        try
        {
          var spellData = ParseCustomSpellData(line);
          if (spellData != null)
          {
            spellData.Proc = procCache.ContainsKey(spellData.Name) ? (byte)1 : (byte)0;
            spellList.Add(spellData);

            if (_spellsNameDb.TryGetValue(spellData.Name, out var spellDataList))
            {
              spellDataList.Add(spellData);
            }
            else
            {
              _spellsNameDb[spellData.Name] = [spellData];
            }

            if (_spellsAbbrvDb.TryAdd(spellData.NameAbbrv, spellData))
            {
            }
            else if (string.Compare(_spellsAbbrvDb[spellData.NameAbbrv].Name, spellData.Name, true, CultureInfo.InvariantCulture) < 0)
            {
              // try to keep the newest version
              _spellsAbbrvDb[spellData.NameAbbrv] = spellData;
            }

            // restricted received spells to only ADPS related
            if (!string.IsNullOrEmpty(spellData.LandsOnOther) && (spellData.Adps > 0 || spellData.IsBeneficial))
            {
              BuildSpellPath([.. spellData.LandsOnOther.Trim().Split(' ')], _landsOnOtherTree, spellData);
            }

            if (!string.IsNullOrEmpty(spellData.LandsOnYou) && (spellData.Adps > 0 || spellData.IsBeneficial))
            {
              BuildSpellPath([.. spellData.LandsOnYou.Trim().Split(' ')], _landsOnYouTree, spellData);
            }

            if (!string.IsNullOrEmpty(spellData.WearOff) && (spellData.Adps > 0 || spellData.IsBeneficial))
            {
              BuildSpellPath([.. spellData.WearOff.Trim().Split(' ')], _wearOffTree, spellData);
            }
          }
        }
        catch (OverflowException ex)
        {
          Log.Error("Error reading spell data", ex);
        }
      });

      var keepOut = new Dictionary<string, byte>();
      var classEnums = Enum.GetValues(typeof(SpellClass)).Cast<SpellClass>().ToList();

      spellList.ForEach(spell =>
      {
        _allSpellData.Add(spell);
        // exact match meaning class-only spell that are of certain target types
        var tgt = (SpellTarget)spell.Target;
        if (spell.Level <= 254 && spell.Proc == 0 && (tgt == SpellTarget.Self || tgt == SpellTarget.Singletarget || tgt == SpellTarget.Los || spell.Rank > 1) &&
          classEnums.Contains((SpellClass)spell.ClassMask))
        {
          // Obviously illusions are bad to look for
          // Call of Fire is Ranger only and self target but VT clickie lets warriors use it
          if (!spell.Name.Contains("Illusion", StringComparison.OrdinalIgnoreCase) &&
            !spell.Name.EndsWith(" gate", StringComparison.OrdinalIgnoreCase) &&
            !spell.Name.Contains(" Synergy", StringComparison.OrdinalIgnoreCase) &&
            !spell.Name.Contains("Call of Fire", StringComparison.OrdinalIgnoreCase))
          {
            // these need to be unique and keep track if a conflict is found
            if (_spellsToClass.ContainsKey(spell.Name))
            {
              _spellsToClass.TryRemove(spell.Name, out _);
              keepOut[spell.Name] = 1;
            }
            else if (!keepOut.ContainsKey(spell.Name))
            {
              _spellsToClass[spell.Name] = (SpellClass)spell.ClassMask;
            }
          }
        }
      });

      // load NPCs
      ConfigUtil.ReadList(@"data\npcs.txt").ForEach(line => _allNpcs[line.Trim()] = 1);

      // Load Adps
      _adpsKeys.ForEach(adpsKey => _adpsActive[adpsKey] = []);
      _adpsKeys.ForEach(adpsKey => _adpsValues[adpsKey] = []);

      string key = null;
      foreach (var line in ConfigUtil.ReadList(@"data\adpsMeter.txt"))
      {
        if (!string.IsNullOrEmpty(line) && line.Trim() is { Length: > 0 } trimmed)
        {
          if (trimmed[0] != '#' && !string.IsNullOrEmpty(key))
          {
            if (trimmed.Split('|') is { Length: > 0 } multiple)
            {
              foreach (var spellLine in multiple)
              {
                if (spellLine.Split('=') is { Length: 2 } list && uint.TryParse(list[1], out var rate))
                {
                  if (GetAdpsByName(list[0]) is { } spellData)
                  {
                    _adpsValues[key][spellData.NameAbbrv] = rate;

                    if (!_adpsWearOff.TryGetValue(spellData.WearOff, out _))
                    {
                      _adpsWearOff[spellData.WearOff] = [];
                    }

                    _adpsWearOff[spellData.WearOff].Add(spellData);

                    if (!_adpsLandsOn.TryGetValue(spellData.LandsOnYou, out _))
                    {
                      _adpsLandsOn[spellData.LandsOnYou] = [];
                    }

                    _adpsLandsOn[spellData.LandsOnYou].Add(spellData);
                  }
                }
              }
            }
          }
          else if (_adpsKeys.Contains(trimmed))
          {
            key = trimmed;
          }
        }
      }

      PlayerManager.Instance.EventsNewVerifiedPlayer += (_, name) => RemoveFight(name);
      PlayerManager.Instance.EventsNewVerifiedPet += (_, name) => RemoveFight(name);
      return;

      SpellData GetAdpsByName(string name)
      {
        if (!_spellsAbbrvDb.TryGetValue(name, out var spellData))
        {
          if (_spellsNameDb.TryGetValue(name, out var list))
          {
            return list.Find(item => item.Adps > 0);
          }
        }
        return spellData;
      }
    }

    internal bool IsKnownNpc(string npc) => !string.IsNullOrEmpty(npc) && _allNpcs.ContainsKey(npc.ToLower(CultureInfo.CurrentCulture));
    internal bool IsOldSpell(string name) => !string.IsNullOrEmpty(name) && _oldSpellNamesDb.ContainsKey(name);
    internal bool IsPlayerSpell(string name) => GetSpellByName(name)?.ClassMask > 0;
    internal bool IsLifetimeNpc(string name) => !string.IsNullOrEmpty(name) && _lifetimeFights.ContainsKey(name);
    internal Dictionary<long, Fight> GetOverlayFights() => _overlayFights.ToDictionary(i => i.Key, i => i.Value);
    internal void RemoveOverlayFight(long id) => _overlayFights.Remove(id, out _);
    internal bool HasOverlayFights() => _overlayFights.Count > 0;
    internal string GetClassFromTitle(string title) => _titleToClass.GetValueOrDefault(title);

    internal string AbbreviateSpellName(string spell)
    {
      if (!_spellAbbrvCache.TryGetValue(spell, out var result))
      {
        result = spell;
        int index;
        if ((index = spell.IndexOf(" Rk. ", StringComparison.Ordinal)) > -1)
        {
          result = spell[..index];
        }
        else if ((index = spell.LastIndexOf(" ", StringComparison.Ordinal)) > -1)
        {
          var lastWord = spell[(index + 1)..];

          if (_ranksCache.TryGetValue(lastWord, out var root))
          {
            result = spell[..index];
            if (!string.IsNullOrEmpty(root))
            {
              result += " " + root;
            }
          }
        }

        _spellAbbrvCache[spell] = result;
      }

      return string.Intern(result);
    }

    internal void CheckExpireFights(double currentTime)
    {
      var removeActiveKeys = new List<string>();
      foreach (var fight in _activeFights.Values)
      {
        var diff = currentTime - fight.LastTime;
        if (diff > MaxTimeout || (diff > FightTimeout && fight.DamageBlocks.Count > 0))
        {
          removeActiveKeys.Add(fight.CorrectMapKey);
          RemoveOverlayFight(fight.Id);
        }
      }

      removeActiveKeys.ForEach(RemoveActiveFight);
    }

    internal SpellClass? GetSpellClass(string spell)
    {
      if (spell != null && _spellsToClass.TryGetValue(spell, out var result))
      {
        return result;
      }
      return null;
    }

    internal SpellData GetSpellByAbbrv(string abbrv)
    {
      if (!string.IsNullOrEmpty(abbrv) && abbrv != Labels.Unassigned && _spellsAbbrvDb.TryGetValue(abbrv, out var value))
      {
        return value;
      }

      return null;
    }

    internal Fight GetFight(string name)
    {
      Fight result = null;
      if (!string.IsNullOrEmpty(name))
      {
        _activeFights.TryGetValue(name, out result);
      }
      return result;
    }

    internal SpellData GetDetSpellByName(string name)
    {
      SpellData spellData = null;
      if (!string.IsNullOrEmpty(name) && name != Labels.UnkSpell && _spellsNameDb.TryGetValue(name, out var spellList))
      {
        spellData = spellList.Find(item => !item.IsBeneficial);
      }

      return spellData;
    }

    internal SpellData GetDamagingSpellByName(string name)
    {
      SpellData spellData = null;
      if (!string.IsNullOrEmpty(name) && name != Labels.UnkSpell && _spellsNameDb.TryGetValue(name, out var spellList))
      {
        spellData = spellList.Find(item => item.Damaging > 0);
      }

      return spellData;
    }

    internal SpellData GetHealingSpellByName(string name)
    {
      SpellData spellData = null;
      if (!string.IsNullOrEmpty(name) && name != Labels.UnkSpell && _spellsNameDb.TryGetValue(name, out var spellList))
      {
        spellData = spellList.Find(item => item.Damaging < 0);
      }

      return spellData;
    }

    internal SpellData GetSpellByName(string name)
    {
      SpellData spellData = null;

      if (!string.IsNullOrEmpty(name) && name != Labels.UnkSpell && _spellsNameDb.TryGetValue(name, out var spellList))
      {
        if (spellList.Count <= 10)
        {
          foreach (var spell in CollectionsMarshal.AsSpan(spellList))
          {
            if (spellData == null || (spellData.Level < spell.Level && spell.Level <= 250) || (spellData.Level > 250 && spell.Level <= 250))
            {
              spellData = spell;
            }
          }
        }
        else
        {
          spellData = spellList.LastOrDefault();
        }
      }

      return spellData;
    }

    internal void UpdateAdps(SpellData spellData)
    {
      var updated = false;
      lock (_adpsKeys)
      {
        foreach (var key in CollectionsMarshal.AsSpan(_adpsKeys))
        {
          if (_adpsValues[key].TryGetValue(spellData.NameAbbrv, out var value))
          {
            var msg = string.IsNullOrEmpty(spellData.LandsOnYou) ? spellData.Name : spellData.LandsOnYou;
            _adpsActive[key][msg] = value;
            updated = true;
          }
        }
      }

      if (updated)
      {
        RecalculateAdps();
      }
    }

    internal SpellTreeResult GetLandsOnOther(string[] split, out string player)
    {
      player = null;
      var found = SearchSpellPath(_landsOnOtherTree, split);

      if (found.SpellData.Count > 0 && found.DataIndex > -1)
      {
        player = string.Join(" ", [.. split], 0, found.DataIndex + 1);
        if (player.EndsWith("'s"))
        {
          // if string is only 2 then it must be invalid
          player = (player.Length > 2) ? player[..^2] : null;
        }

        found.SpellData = FindByLandsOn(player, found.SpellData);
      }

      return found;
    }

    internal SpellTreeResult GetLandsOnYou(string[] split)
    {
      var found = SearchSpellPath(_landsOnYouTree, split);

      if (found.DataIndex == 0 && found.SpellData.Count > 0)
      {
        found.SpellData = FindByLandsOn(ConfigUtil.PlayerName, found.SpellData);

        // check Adps
        if (_adpsLandsOn.TryGetValue(found.SpellData[0].LandsOnYou, out var spellDataSet) && spellDataSet.Count > 0)
        {
          var spellData = spellDataSet.Count == 1 ? spellDataSet.First() : FindPreviousCast(ConfigUtil.PlayerName, [.. spellDataSet], true);

          // this only handles latest versions of spells so an older one may have given us the landsOn string and then it wasn't found
          // for some spells this makes sense because of the level requirements and it wouldn't do anything but thats not true for all of them
          // need to handle older spells and multiple rate values
          if (spellData != null)
          {
            var updated = false;
            lock (_adpsKeys)
            {
              foreach (var key in CollectionsMarshal.AsSpan(_adpsKeys))
              {
                if (_adpsValues[key].TryGetValue(spellData.NameAbbrv, out var value))
                {
                  _adpsActive[key][spellData.LandsOnYou] = value;
                  updated = true;
                }
              }
            }

            if (updated)
            {
              RecalculateAdps();
            }
          }
        }
      }

      return found;
    }

    internal SpellTreeResult GetWearOff(string[] split)
    {
      var found = SearchSpellPath(_wearOffTree, split);

      if (found.DataIndex == 0 && found.SpellData.Count > 0)
      {
        found.SpellData = FindByLandsOn(split[0], found.SpellData);

        // check Adps
        if (_adpsWearOff.TryGetValue(found.SpellData[0].WearOff, out var spellDataSet) && spellDataSet.Count > 0)
        {
          var spellData = spellDataSet.First();
          var updated = false;

          lock (_adpsKeys)
          {
            foreach (var key in CollectionsMarshal.AsSpan(_adpsKeys))
            {
              if (_adpsValues[key].TryGetValue(spellData.NameAbbrv, out _))
              {
                var msg = string.IsNullOrEmpty(spellData.LandsOnYou) ? spellData.Name : spellData.LandsOnYou;
                _adpsActive[key].Remove(msg);
                updated = true;
              }
            }
          }

          if (updated)
          {
            RecalculateAdps();
          }
        }
      }

      return found;
    }

    internal void ZoneChanged()
    {
      var updated = false;
      lock (_adpsKeys)
      {
        foreach (var active in _adpsActive)
        {
          foreach (var landsOn in active.Value.Keys.ToArray())
          {
            if (_adpsLandsOn.TryGetValue(landsOn, out var value))
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
        RecalculateAdps();
      }
    }

    internal SpellData ParseCustomSpellData(string line)
    {
      SpellData spellData = null;
      if (!string.IsNullOrEmpty(line))
      {
        var data = line.Split('^');
        if (data.Length >= 11)
        {
          var duration = int.Parse(data[3], CultureInfo.InvariantCulture) * 6; // as seconds
          var beneficial = int.Parse(data[4], CultureInfo.InvariantCulture);
          var target = byte.Parse(data[6], CultureInfo.InvariantCulture);
          var classMask = ushort.Parse(data[7], CultureInfo.InvariantCulture);

          // deal with too big or too small values
          // all adps we care about is in the range of a few minutes
          if (duration > ushort.MaxValue)
          {
            duration = ushort.MaxValue;
          }
          else if (duration < 0)
          {
            duration = 0;
          }

          spellData = new SpellData
          {
            Id = string.Intern(data[0]),
            Name = string.Intern(data[1]),
            NameAbbrv = string.Intern(AbbreviateSpellName(data[1])),
            Level = byte.Parse(data[2], CultureInfo.InvariantCulture),
            Duration = (ushort)duration,
            IsBeneficial = beneficial != 0,
            Target = target,
            MaxHits = ushort.Parse(data[5], CultureInfo.InvariantCulture),
            ClassMask = classMask,
            Damaging = short.Parse(data[8], CultureInfo.InvariantCulture),
            //CombatSkill = uint.Parse(data[9], CultureInfo.InvariantCulture),
            Resist = (SpellResist)int.Parse(data[10], CultureInfo.InvariantCulture),
            SongWindow = data[11] == "1",
            Adps = byte.Parse(data[12], CultureInfo.InvariantCulture),
            Mgb = data[13] == "1",
            Rank = byte.Parse(data[14], CultureInfo.InvariantCulture),
            HasAmbiguity = data[15] == "1" || data[16] == "1",
            LandsOnYou = string.Intern(data[17]),
            LandsOnOther = string.Intern(data[18]),
            WearOff = string.Intern(data[19])
          };
        }
      }

      return spellData;
    }

    private void RecalculateAdps()
    {
      lock (_adpsKeys)
      {
        MyDoTCritRateMod = (uint)_adpsActive[_adpsKeys[0]].Sum(kv => kv.Value);
        MyNukeCritRateMod = (uint)_adpsActive[_adpsKeys[1]].Sum(kv => kv.Value);
      }
    }

    private static SpellData FindPreviousCast(string player, IEnumerable<SpellData> output, bool isAdps = false)
    {
      var filtered = output.Where(value => !isAdps || value.Adps > 0).ToArray();
      foreach (var (_, cast) in RecordManager.Instance.GetSpellsLast(8))
      {
        if (!cast.Interrupted)
        {
          foreach (var value in filtered)
          {
            if ((value.Target != (int)SpellTarget.Self || cast.Caster == player) && value.Name == cast.Spell)
            {
              return value;
            }
          }
        }
      }

      return null;
    }

    private static List<SpellData> FindByLandsOn(string player, List<SpellData> output)
    {
      List<SpellData> result = null;
      if (output.Count == 1)
      {
        result = output;
      }
      else if (output.Count > 1)
      {
        var foundSpellData = FindPreviousCast(player, output);
        if (foundSpellData == null)
        {
          // one more thing, if all the abbreviations look the same then we know the spell
          // even if the version is wrong. grab the newest
          result = (output.Distinct(AbbrvComparer).Count() == 1) ? [output.First()] : output;
        }
        else
        {
          result = [foundSpellData];
        }
      }

      return result;
    }

    internal void RemoveActiveFight(string name)
    {
      if (_activeFights.TryRemove(name, out var fight))
      {
        fight.Dead = true;
      }
    }

    internal void UpdateIfNewFightMap(string name, Fight fight, bool isNonTankingFight)
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

    internal void ResetOverlayFights(bool active = false)
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
        if (fight != null && (groupId == -1 || fight.GroupId != groupId))
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
      // clear used recently
      foreach (var spellData in _allSpellData)
      {
        spellData.SeenRecently = false;
      }

      _activeFights.Clear();
      _lifetimeFights.Clear();
      _overlayFights.Clear();
      ClearActiveAdps();
      EventsClearedActiveData?.Invoke(true);
    }

    internal void ClearActiveAdps()
    {
      lock (_adpsKeys)
      {
        _adpsKeys.ForEach(key => _adpsActive[key].Clear());
        MyDoTCritRateMod = 0;
        MyNukeCritRateMod = 0;
      }
    }

    internal static bool ResolveSpellAmbiguity(ReceivedSpell spell, out SpellData replaced)
    {
      replaced = null;

      if (spell.Ambiguity.Count < 50)
      {
        var spellClass = (int)PlayerManager.Instance.GetPlayerClassEnum(spell.Receiver);
        var subset = spell.Ambiguity.FindAll(test => test.Target == (int)SpellTarget.Self && spellClass != 0 && (test.ClassMask & spellClass) == spellClass);
        var distinct = subset.Distinct(AbbrvComparer).ToList();
        if (distinct.Count == 1)
        {
          replaced = distinct.First();
        }
        else
        {
          var recent = spell.Ambiguity.FirstOrDefault(spellData => spellData.SeenRecently);
          replaced = recent ?? spell.Ambiguity.First();
        }
      }

      return replaced != null;
    }

    private void RemoveFight(string name)
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

    public static SpellTreeResult SearchSpellPath(SpellTreeNode node, string[] split, int lastIndex = -1)
    {
      if (lastIndex == -1)
      {
        lastIndex = split.Length - 1;
      }

      if (node.Words.TryGetValue(split[lastIndex], out var child))
      {
        if (lastIndex > 0)
        {
          return SearchSpellPath(child, split, lastIndex - 1);
        }

        return new SpellTreeResult { SpellData = child.SpellData, DataIndex = lastIndex };
      }

      return new SpellTreeResult { SpellData = node.SpellData, DataIndex = lastIndex };
    }

    private static void BuildSpellPath(IReadOnlyList<string> data, SpellTreeNode node, SpellData spellData, int lastIndex = -1)
    {
      if (lastIndex == -1)
      {
        lastIndex = data.Count - 1;
      }

      if (data[lastIndex] == "'s")
      {
        node.SpellData.Add(spellData);
        node.SpellData.Sort(DurationCompare);
      }
      else
      {
        if (!node.Words.TryGetValue(data[lastIndex], out var child))
        {
          child = new SpellTreeNode();
          node.Words[data[lastIndex]] = child;
        }

        if (lastIndex == 0)
        {
          child.SpellData.Add(spellData);
          child.SpellData.Sort(DurationCompare);
        }
        else
        {
          BuildSpellPath(data, child, spellData, lastIndex - 1);
        }
      }
    }

    private static int DurationCompare(SpellData a, SpellData b)
    {
      var result = b.Duration.CompareTo(a.Duration);
      if (result == 0 && int.TryParse(a.Id, out var aInt) && int.TryParse(b.Id, out var bInt))
      {
        // Check if the durations are equal
        if (aInt != bInt)
        {
          result = aInt > bInt ? -1 : 1;
        }
      }
      return result;
    }


    private class SpellAbbrvComparer : IEqualityComparer<SpellData>
    {
      public bool Equals(SpellData x, SpellData y) => x?.NameAbbrv == y?.NameAbbrv;
      public int GetHashCode(SpellData obj) => obj.NameAbbrv.GetHashCode();
    }

    internal class SpellTreeNode
    {
      public List<SpellData> SpellData { get; set; } = [];
      public Dictionary<string, SpellTreeNode> Words { get; set; } = [];
    }

    internal class SpellTreeResult
    {
      public List<SpellData> SpellData { get; set; }
      public int DataIndex { get; set; }
    }
  }
}