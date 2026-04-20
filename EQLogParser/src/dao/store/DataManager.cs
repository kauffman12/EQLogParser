using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Media;

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
    public const string NoNpcs = "No NPCs Selected";
    public const string PetPlayerOption = "Players +Pets";
    public const string PlayerOption = "Players";
    public const string PetOption = "Pets";
    public const string RaidOption = "Raid";
    public const string RaidTotals = "Totals";
    public const string Riposte = "Riposte";
    public const string AllOption = "Uncategorized";
    public const string ByGroupOption = "Group View";
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

  internal interface IDataManager
  {
    SpellData GetDamagingSpellByName(string name);
    bool IsOldSpell(string name);
    string AbbreviateSpellName(string spell);
    SpellData GetSpellByAbbrv(string abbrv);
  }

  internal class DataManager : IDataManager
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    private static DataManager _instance;
    internal static DataManager Instance
    {
      get => _instance ??= new();
      set => _instance = value;
    }
 

    private static readonly SpellAbbrvComparer AbbrvComparer = new();
    private readonly HashSet<SpellData> _allSpellData = [];
    private readonly List<string> _adpsKeys = ["#DoTCritRate", "#NukeCritRate"];
    private readonly object _adpsLock = new object();
    private readonly Dictionary<string, Dictionary<string, uint>> _adpsValues = [];
    private readonly Dictionary<string, Dictionary<string, uint>> _adpsActive = [];
    internal readonly Dictionary<string, HashSet<SpellData>> _adpsLandsOn = [];
    private readonly Dictionary<string, HashSet<SpellData>> _adpsWearOff = [];
    private readonly Dictionary<string, bool> _oldSpellNamesDb = [];
    private readonly SpellTreeNode _landsOnOtherTree = new();
    private readonly SpellTreeNode _landsOnYouTree = new();
    private readonly SpellTreeNode _wearOffTree = new();

    // definitely used in single thread
    private readonly Dictionary<string, string> _titleToClass = [];

   private readonly ConcurrentDictionary<string, byte> _allNpcs = new();
    private readonly ConcurrentDictionary<string, SpellData> _spellsAbbrvDb = new();
    private readonly ConcurrentDictionary<string, string> _spellsToClass = new();
    private readonly ConcurrentDictionary<string, string> _spellAbbrvCache = new();
    private readonly ConcurrentDictionary<string, List<SpellData>> _spellsNameDb = new();
    private readonly ConcurrentDictionary<string, SpellData> _unknownSpellDb = new();
    private readonly ConcurrentDictionary<string, SolidColorBrush> _classBrushes = new();
    private readonly ConcurrentDictionary<SpellClass, string> _classNames = new();
    private readonly ConcurrentDictionary<string, SpellClass> _classesByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _sortedClassList = [];
    private readonly int _classListCount;

    // rank abbreviation
    private readonly HashSet<string> RankWords;
    private readonly Regex RomanRegex = new(@"^M{0,3}(CM|CD|D?C{0,3})(XC|XL|L?X{0,3})(IX|IV|V?I{0,3})$", RegexOptions.IgnoreCase | RegexOptions.Compiled
);

    internal DataManager()
    {
      var spellList = new List<SpellData>();

      RankWords = new(StringComparer.OrdinalIgnoreCase)
      {
        "Azia", "Beza", "Caza", "Third", "Fifth", "Octave"
      };

      // populate ClassNames from SpellClass enum and resource table
      var wpfAvailable = Type.GetType("System.Windows.Media.SolidColorBrush, PresentationCore") != null;
      foreach (var item in Enum.GetValues<SpellClass>())
      {
        if (Enum.GetName(item)?.ToUpperInvariant() is string { } resourceName)
        {
          var name = Resource.ResourceManager.GetString(resourceName, CultureInfo.InvariantCulture);
          if (!string.IsNullOrEmpty(name))
          {
            _classNames[item] = string.Intern(name);
            _classesByName[name] = item;
          }

          var color = Resource.ResourceManager.GetString($"{resourceName}_COLOR", CultureInfo.InvariantCulture);
          if (!string.IsNullOrEmpty(color) && wpfAvailable)
          {
            try
            {
              _classBrushes[name] = UiUtil.GetBrush(color);
            }
            catch (FormatException ex)
            {
              Log.Error($"Failed to parse color for class {item}: {ex.Message}");
            }
          }
        }
      }

      _sortedClassList.AddRange(_classNames.Values);
      _sortedClassList.Sort();
      _classListCount = _sortedClassList.Count;

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
      foreach (var line in ConfigUtil.ReadList(@"data\procs.txt").Where(line => line.Length > 0 && line[0] != '#'))
      {
        procCache[line] = true;
        procCache[$"New {line}"] = true;
      }

      foreach (ref var line in CollectionsMarshal.AsSpan(ConfigUtil.ReadList(@"data\spells.txt")))
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
            else if (string.Compare(_spellsAbbrvDb[spellData.NameAbbrv].Name, spellData.Name, StringComparison.OrdinalIgnoreCase) < 0)
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
      }

      var keepOut = new Dictionary<string, byte>();

      var itemSpellsCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
      foreach (var line in ConfigUtil.ReadList(@"data\itemspells.txt").Where(line => line.Length > 0 && line[0] != '#'))
      {
        itemSpellsCache[string.Intern(line)] = true;
      }

      foreach (ref var spell in CollectionsMarshal.AsSpan(spellList))
      {
        _allSpellData.Add(spell);

        // ignore spells tied to items for building spell class cache
        if (itemSpellsCache.ContainsKey(spell.Name)) continue;

        // exact class match
        if (spell.Level < 255 && _classNames.TryGetValue((SpellClass)spell.ClassMask, out var theClassName))
        {
          // Obviously illusions are bad to look for
          // Call of Fire is Ranger only and self target but VT clickie lets warriors use it
          if (!spell.NameAbbrv.Contains("Illusion", StringComparison.OrdinalIgnoreCase) &&
            !spell.NameAbbrv.Contains("Mount", StringComparison.OrdinalIgnoreCase) &&
            !spell.NameAbbrv.EndsWith(" gate", StringComparison.OrdinalIgnoreCase) &&
            !spell.NameAbbrv.Contains(" Synergy", StringComparison.OrdinalIgnoreCase) &&
            !spell.NameAbbrv.Contains("Call of Fire", StringComparison.OrdinalIgnoreCase) &&
            !spell.NameAbbrv.Contains("Pet Heal", StringComparison.OrdinalIgnoreCase) &&
            !(spell.ClassMask == (int)SpellClass.Clr && spell.NameAbbrv.Contains("Effect")) &&
            !(spell.ClassMask == (int)SpellClass.Brd && spell.Level >= 250))
          {
            // these need to be unique and keep track if a conflict is found
            if (_spellsToClass.ContainsKey(spell.Name))
            {
              _spellsToClass.TryRemove(spell.Name, out _);
              keepOut[spell.Name] = 1;
            }
            else if (!keepOut.ContainsKey(spell.Name))
            {
              _spellsToClass[spell.Name] = theClassName;
            }
          }
        }
        else
        {
          // these need to be unique and keep track if a conflict is found
          if (_spellsToClass.ContainsKey(spell.Name))
          {
            _spellsToClass.TryRemove(spell.Name, out _);
            keepOut[spell.Name] = 1;
          }
        }
      }

      // load NPCs
      foreach (ref var line in CollectionsMarshal.AsSpan(ConfigUtil.ReadList(@"data\npcs.txt")))
      {
        if (line?.Trim() is string trimmed && trimmed.Length > 0)
        {
          _allNpcs[string.Intern(trimmed)] = 1;
        }
      }

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
    public bool IsOldSpell(string name) => !string.IsNullOrEmpty(name) && _oldSpellNamesDb.ContainsKey(name);
    internal bool IsPlayerSpell(string name) => GetSpellByName(name)?.ClassMask > 0;
  
    internal string GetClassFromTitle(string title) => _titleToClass.GetValueOrDefault(title);
    internal List<string> GetClassList() => [.. _sortedClassList];
    internal int GetClassListCount() => _classListCount;
    internal bool IsValidClassName(string className) => !string.IsNullOrEmpty(className) && _classesByName.ContainsKey(className);

    public string AbbreviateSpellName(string spell)
    {
      if (_spellAbbrvCache.TryGetValue(spell, out var cached))
        return cached;

      // Split once into tokens
      var parts = spell.Split(' ', StringSplitOptions.RemoveEmptyEntries);
      var count = parts.Length;

      // --- Handle "<name> Rk. I|II|III|..."
      if (count >= 3 &&
          parts[count - 2].Equals("Rk.", StringComparison.OrdinalIgnoreCase) &&
          IsRoman(parts[count - 1]))
      {
        count -= 2;
      }

      // --- Strip other trailing rank indicators
      while (count > 0)
      {
        var last = parts[count - 1];

        if (RankWords.Contains(last))
        {
          count--;
          continue;
        }

        if (IsRoman(last))
        {
          count--;
          continue;
        }

        if (int.TryParse(last, out _))
        {
          count--;
          continue;
        }

        break; // reached base name
      }

      // If nothing removed → return original
      if (count == parts.Length)
      {
        _spellAbbrvCache[spell] = spell;
        return string.Intern(spell);
      }

      // Rebuild abbreviated name
      var result = string.Join(" ", parts, 0, count);

      _spellAbbrvCache[spell] = result;
      return string.Intern(result);

      bool IsRoman(string s) => RomanRegex.IsMatch(s);
    }


    internal SpellData AddUnknownSpell(string spellName)
    {
      if (!_unknownSpellDb.TryGetValue(spellName, out var result))
      {
        // unknown spell
        var spellData = new SpellData
        {
          Id = string.Intern(spellName),
          Name = string.Intern(spellName),
          NameAbbrv = string.Intern(AbbreviateSpellName(spellName)),
          IsUnknown = true
        };
        _unknownSpellDb[spellName] = spellData;
        result = spellData;
      }
      return result;
    }

   internal SolidColorBrush GetClassBrush(string className)
    {
      if (!string.IsNullOrEmpty(className) && _classBrushes.TryGetValue(className, out var brush))
      {
        return brush;
      }
      return UiUtil.DefaultBrush;
    }

    internal SpellClass? GetClassEnum(string className)
    {
      if (!string.IsNullOrEmpty(className) && _classesByName.TryGetValue(className, out var theClass))
      {
        return theClass;
      }
      return 0;
    }

    internal string GetSpellClass(string name)
    {
      if (!string.IsNullOrEmpty(name) && _spellsToClass.TryGetValue(name, out var result))
      {
        return result;
      }
      return null;
    }

    public SpellData GetSpellByAbbrv(string abbrv)
    {
      if (!string.IsNullOrEmpty(abbrv) && abbrv != Labels.Unassigned && _spellsAbbrvDb.TryGetValue(abbrv, out var value))
      {
        return value;
      }

      return null;
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

    public SpellData GetDamagingSpellByName(string name)
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

      internal SpellTreeResult GetLandsOnOther(string[] split, out string player)
    {
      player = null;
      var found = SearchSpellPath(_landsOnOtherTree, split);

      if (found.SpellData.Count > 0 && found.DataIndex > -1)
      {
        player = string.Join(" ", [.. split], 0, found.DataIndex + 1);
        if (player.EndsWith("'s", StringComparison.OrdinalIgnoreCase))
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
            lock (_adpsLock)
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

          lock (_adpsLock)
          {
            foreach (var key in CollectionsMarshal.AsSpan(_adpsKeys))
            {
              var dict = _adpsValues[key];
              if (dict.ContainsKey(spellData.NameAbbrv))
              {
                var activeDict = _adpsActive[key];
                var msg = string.IsNullOrEmpty(spellData.LandsOnYou) ? spellData.NameAbbrv : spellData.LandsOnYou;
                var foundKey = activeDict.Keys.FirstOrDefault(k => k.Contains(msg, StringComparison.OrdinalIgnoreCase));
                if (foundKey != null)
                {
                  activeDict.Remove(foundKey);
                  updated = true;
                }
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

          var level = byte.Parse(data[2], CultureInfo.InvariantCulture);

          spellData = new SpellData
          {
            Id = string.Intern(data[0]),
            Name = string.Intern(data[1]),
            NameAbbrv = string.Intern(AbbreviateSpellName(data[1])),
            Level = level,
            Duration = (ushort)duration,
            IsBeneficial = beneficial != 0,
            Target = target,
            MaxHits = ushort.Parse(data[5], CultureInfo.InvariantCulture),
            ClassMask = classMask,
            Damaging = short.Parse(data[8], CultureInfo.InvariantCulture),
            //CombatSkill = uint.Parse(data[9], CultureInfo.InvariantCulture),
            Resist = (SpellResist)int.Parse(data[10], CultureInfo.InvariantCulture),
            SongWindow = data[11] == "1" || data[11] == "-1",
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
      lock (_adpsLock)
      {
        var dot = (uint)_adpsActive[_adpsKeys[0]].Sum(kv => kv.Value);
        var nuke = (uint)_adpsActive[_adpsKeys[1]].Sum(kv => kv.Value);
        FightManager.Instance.SetCritRateMods(dot, nuke);
      }
    }

    private static SpellData FindPreviousCast(string player, IEnumerable<SpellData> output, bool isAdps = false)
    {
      SpellData[] filtered = null;
      foreach (var (_, cast) in RecordManager.Instance.GetSpellsLast(8))
      {
        if (!cast.Interrupted)
        {
          filtered ??= [.. output.Where(value => !isAdps || value.Adps > 0)];
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
  internal void Clear()
    {
      foreach (var spellData in _allSpellData)
      {
        spellData.SeenRecently = false;
      }
      _unknownSpellDb.Clear();
      FightManager.Instance.Clear();
    }

   internal void UpdateAdps(SpellData spellData)
    {
      lock (_adpsLock)
      {
        foreach (var key in CollectionsMarshal.AsSpan(_adpsKeys))
        {
          if (_adpsValues[key].TryGetValue(spellData.NameAbbrv, out var value))
          {
            var msg = string.IsNullOrEmpty(spellData.LandsOnYou) ? spellData.Name : spellData.LandsOnYou;
            FightManager.Instance.UpdateAdps(key, msg, value);
          }
        }
      }
    }

 

     internal static bool ResolveSpellAmbiguity(ReceivedSpell spell, double currentTime, out SpellData replaced)
    {
      replaced = null;

      var className = PlayerManager.Instance.GetPlayerClass(spell.Receiver, currentTime);
      var spellClass = (int)Instance.GetClassEnum(className);
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

      return replaced != null;
    }

    /// <summary>
    /// Searches for a spell path in the provided spell tree.
    /// </summary>
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
      if (ReferenceEquals(a, b))
      {
        return 0;
      }

      if (a is null)
      {
        return 1;
      }

      if (b is null)
      {
        return -1;
      }

      var result = b.Duration.CompareTo(a.Duration);

      if (result == 0)
      {
        var aHasId = int.TryParse(a.Id, out var aInt);
        var bHasId = int.TryParse(b.Id, out var bInt);

        if (aHasId && bHasId)
        {
          result = bInt.CompareTo(aInt);
        }
        else if (aHasId && !bHasId)
        {
          result = -1;
        }
        else if (!aHasId && bHasId)
        {
          result = 1;
        }
        else
        {
          result = string.Compare(a.Id, b.Id, StringComparison.Ordinal);
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