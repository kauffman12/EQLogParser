using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace EQLogParser
{
  internal enum SpellClass
  {
    WAR = 1, CLR = 2, PAL = 4, RNG = 8, SHD = 16, DRU = 32, MNK = 64, BRD = 128, ROG = 256,
    SHM = 512, NEC = 1024, WIZ = 2048, MAG = 4096, ENC = 8192, BST = 16384, BER = 32768
  }

  internal enum SpellTarget
  {
    LOS = 1, CASTERAE = 2, CASTERGROUP = 3, CASTERPB = 4, SINGLETARGET = 5, SELF = 6, TARGETAE = 8, PET = 14,
    CASTERPBPLAYERS = 36, PET2 = 38, NEARBYPLAYERSAE = 40, TARGETGROUP = 41, DIRECTIONAE = 42, TARGETRINGAE = 45
  }

  internal enum SpellResist
  {
    UNDEFINED = -1, UNRESISTABLE = 0, MAGIC, FIRE, COLD, POISON, DISEASE, LOWEST, AVERAGE, PHYSICAL, CORRUPTION
  }

  internal static class Labels
  {
    public const string ABSORB = "Absorb";
    public const string DD = "Direct Damage";
    public const string DOT = "DoT Tick";
    public const string DS = "Damage Shield";
    public const string RS = "Reverse DS";
    public const string BANE = "Bane Damage";
    public const string OTHER_DMG = "Other Damage";
    public const string PROC = "Proc";
    public const string HOT = "HoT Tick";
    public const string HEAL = "Direct Heal";
    public const string MELEE = "Melee";
    public const string SELF_HEAL = "Melee Heal";
    public const string NO_DATA = "No Data Available";
    public const string PET_PLAYER_OPTION = "Players +Pets";
    public const string PLAYER_OPTION = "Players";
    public const string PET_OPTION = "Pets";
    public const string RAID_OPTION = "Raid";
    public const string RAID_TOTALS = "Totals";
    public const string RIPOSTE = "Riposte";
    public const string ALL_OPTION = "Uncategorized";
    public const string UNASSIGNED = "Unknown Pet Owner";
    public const string UNK = "Unknown";
    public const string UNK_SPELL = "Unknown Spell";
    public const string RECEIVED_HEAL_PARSE = "Received Healing";
    public const string HEAL_PARSE = "Healing";
    public const string TANK_PARSE = "Tanking";
    public const string TOP_HEAL_PARSE = "Top Heals";
    public const string DAMAGE_PARSE = "Damage";
    public const string MISS = "Miss";
    public const string DODGE = "Dodge";
    public const string PARRY = "Parry";
    public const string BLOCK = "Block";
    public const string INVULNERABLE = "Invulnerable";
  }

  class DataManager
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

    internal static DataManager Instance = new();
    internal event EventHandler<string> EventsRemovedFight;
    internal event EventHandler<Fight> EventsNewFight;
    internal event EventHandler<Fight> EventsNewNonTankingFight;
    internal event EventHandler<RandomRecord> EventsNewRandomRecord;
    internal event EventHandler<Fight> EventsUpdateFight;
    internal event EventHandler<bool> EventsClearedActiveData;
    internal event EventHandler<Fight> EventsNewOverlayFight;

    internal const int MAX_TIMEOUT = 60;
    internal const int FIGHT_IMEOUT = 30;
    internal const double BUFFS_OFFSET = 90;
    internal uint MyNukeCritRateMod { get; private set; }
    internal uint MyDoTCritRateMod { get; private set; }

    private static readonly SpellAbbrvComparer AbbrvComparer = new();
    private static readonly TimedActionComparer TAComparer = new();
    private static readonly object LockObject = new();

    private readonly List<ActionGroup> AllMiscBlocks = new();
    private readonly List<ActionGroup> AllDeathBlocks = new();
    private readonly List<ActionGroup> AllHealBlocks = new();
    private readonly List<ActionGroup> AllSpellCastBlocks = new();
    private readonly List<ActionGroup> AllReceivedSpellBlocks = new();
    private readonly List<ActionGroup> AllResistBlocks = new();
    private readonly List<ActionGroup> AllRandomBlocks = new();
    private readonly List<ActionGroup> AllLootBlocks = new();
    private readonly List<TimedAction> AllSpecialActions = new();
    private readonly List<LootRecord> AssignedLoot = new();

    private readonly List<string> AdpsKeys = new() { "#DoTCritRate", "#NukeCritRate" };
    private readonly Dictionary<string, Dictionary<string, uint>> AdpsActive = new();
    private readonly Dictionary<string, Dictionary<string, uint>> AdpsValues = new();
    private readonly Dictionary<string, HashSet<SpellData>> AdpsLandsOn = new();
    private readonly Dictionary<string, HashSet<SpellData>> AdpsWearOff = new();
    private readonly Dictionary<string, bool> OldSpellNamesDb = new();
    private readonly Dictionary<string, Dictionary<SpellResist, ResistCount>> NpcResistStats = new();
    private readonly Dictionary<string, TotalCount> NpcTotalSpellCounts = new();
    private readonly SpellTreeNode LandsOnOtherTree = new();
    private readonly SpellTreeNode LandsOnYouTree = new();
    private readonly SpellTreeNode WearOffTree = new();

    // defnitely used in single thread
    private readonly Dictionary<string, string> TitleToClass = new();

    // locking was causing a problem for OverlayFights? I don't know
    private readonly Dictionary<long, Fight> OverlayFights = new();
    private readonly ConcurrentDictionary<string, byte> AllNpcs = new();
    private readonly ConcurrentDictionary<string, SpellData> SpellsAbbrvDb = new();
    private readonly ConcurrentDictionary<string, SpellClass> SpellsToClass = new();
    private readonly ConcurrentDictionary<string, Fight> ActiveFights = new();
    private readonly ConcurrentDictionary<string, byte> LifetimeFights = new();
    private readonly ConcurrentDictionary<string, string> SpellAbbrvCache = new();
    private readonly ConcurrentDictionary<string, string> RanksCache = new();
    private readonly ConcurrentDictionary<string, List<SpellData>> SpellsNameDb = new();


    private int LastSpellIndex = -1;

    private DataManager()
    {
      var spellList = new List<SpellData>();

      // build ranks cache
      Enumerable.Range(1, 9).ToList().ForEach(r => RanksCache[r.ToString(CultureInfo.CurrentCulture)] = "");
      Enumerable.Range(1, 200).ToList().ForEach(r => RanksCache[TextUtils.IntToRoman(r)] = "");
      RanksCache["Third"] = "Root";
      RanksCache["Fifth"] = "Root";
      RanksCache["Octave"] = "Root";

      // Player title mapping for /who queries
      ConfigUtil.ReadList(@"data\titles.txt").ForEach(line =>
      {
        var split = line.Split('=');
        if (split.Length == 2)
        {
          TitleToClass[split[0]] = split[0];
          foreach (var title in split[1].Split(','))
          {
            TitleToClass[title + " (" + split[0] + ")"] = split[0];
          }
        }
      });

      // Old Spell cache (EQEMU)
      ConfigUtil.ReadList(@"data\oldspells.txt").ForEach(line => OldSpellNamesDb[line] = true);

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

            if (SpellsNameDb.TryGetValue(spellData.Name, out var spellDataList))
            {
              spellDataList.Add(spellData);
            }
            else
            {
              SpellsNameDb[spellData.Name] = new() { spellData };
            }

            if (!SpellsAbbrvDb.ContainsKey(spellData.NameAbbrv))
            {
              SpellsAbbrvDb[spellData.NameAbbrv] = spellData;
            }
            else if (string.Compare(SpellsAbbrvDb[spellData.NameAbbrv].Name, spellData.Name, true, CultureInfo.CurrentCulture) < 0)
            {
              // try to keep the newest version
              SpellsAbbrvDb[spellData.NameAbbrv] = spellData;
            }

            // restricted received spells to only ADPS related
            if (!string.IsNullOrEmpty(spellData.LandsOnOther) && (spellData.Adps > 0 || spellData.IsBeneficial))
            {
              BuildSpellPath(spellData.LandsOnOther.Trim().Split(' ').ToList(), LandsOnOtherTree, spellData);
            }

            if (!string.IsNullOrEmpty(spellData.LandsOnYou) && (spellData.Adps > 0 || spellData.IsBeneficial))
            {
              BuildSpellPath(spellData.LandsOnYou.Trim().Split(' ').ToList(), LandsOnYouTree, spellData);
            }

            if (!string.IsNullOrEmpty(spellData.WearOff) && (spellData.Adps > 0 || spellData.IsBeneficial))
            {
              BuildSpellPath(spellData.WearOff.Trim().Split(' ').ToList(), WearOffTree, spellData);
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
        // exact match meaning class-only spell that are of certain target types
        var tgt = (SpellTarget)spell.Target;
        if (spell.Level <= 254 && spell.Proc == 0 && (tgt == SpellTarget.SELF || tgt == SpellTarget.SINGLETARGET || tgt == SpellTarget.LOS || spell.Rank > 1) &&
          classEnums.Contains((SpellClass)spell.ClassMask))
        {
          // Obviously illusions are bad to look for
          // Call of Fire is Ranger only and self target but VT clickie lets warriors use it
          if (spell.Name.IndexOf("Illusion", StringComparison.OrdinalIgnoreCase) == -1 &&
          !spell.Name.EndsWith(" gate", StringComparison.OrdinalIgnoreCase) &&
          spell.Name.IndexOf(" Synergy", StringComparison.OrdinalIgnoreCase) == -1 &&
          spell.Name.IndexOf("Call of Fire", StringComparison.OrdinalIgnoreCase) == -1)
          {
            // these need to be unique and keep track if a conflict is found
            if (SpellsToClass.ContainsKey(spell.Name))
            {
              SpellsToClass.TryRemove(spell.Name, out var _);
              keepOut[spell.Name] = 1;
            }
            else if (!keepOut.ContainsKey(spell.Name))
            {
              SpellsToClass[spell.Name] = (SpellClass)spell.ClassMask;
            }
          }
        }
      });

      // load NPCs
      ConfigUtil.ReadList(@"data\npcs.txt").ForEach(line => AllNpcs[line.Trim()] = 1);

      // Load Adps
      AdpsKeys.ForEach(adpsKey => AdpsActive[adpsKey] = new Dictionary<string, uint>());
      AdpsKeys.ForEach(adpsKey => AdpsValues[adpsKey] = new Dictionary<string, uint>());

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
                    AdpsValues[key][spellData.NameAbbrv] = rate;

                    if (!AdpsWearOff.TryGetValue(spellData.WearOff, out _))
                    {
                      AdpsWearOff[spellData.WearOff] = new HashSet<SpellData>();
                    }

                    AdpsWearOff[spellData.WearOff].Add(spellData);

                    if (!AdpsLandsOn.TryGetValue(spellData.LandsOnYou, out _))
                    {
                      AdpsLandsOn[spellData.LandsOnYou] = new HashSet<SpellData>();
                    }

                    AdpsLandsOn[spellData.LandsOnYou].Add(spellData);
                  }
                }
              }
            }
          }
          else if (AdpsKeys.Contains(trimmed))
          {
            key = trimmed;
          }
        }
      }

      PlayerManager.Instance.EventsNewTakenPetOrPlayerAction += (sender, name) => RemoveFight(name);
      PlayerManager.Instance.EventsNewVerifiedPlayer += (sender, name) => RemoveFight(name);
      PlayerManager.Instance.EventsNewVerifiedPet += (sender, name) => RemoveFight(name);

      SpellData GetAdpsByName(string name)
      {
        SpellData spellData = null;

        if (!SpellsAbbrvDb.TryGetValue(name, out spellData))
        {
          if (SpellsNameDb.TryGetValue(name, out var spellList))
          {
            spellData = spellList.Find(item => item.Adps > 0);
          }
        }

        return spellData;
      }
    }

    internal void AddDeathRecord(DeathRecord record, double beginTime) => Helpers.AddAction(AllDeathBlocks, record, beginTime);
    internal void AddMiscRecord(IAction action, double beginTime) => Helpers.AddAction(AllMiscBlocks, action, beginTime);
    internal void AddReceivedSpell(ReceivedSpell received, double beginTime) => Helpers.AddAction(AllReceivedSpellBlocks, received, beginTime);
    internal List<ActionGroup> GetAllLoot() => AllLootBlocks.ToList();
    internal List<ActionGroup> GetAllRandoms() => AllRandomBlocks.ToList();
    internal List<ActionGroup> GetCastsDuring(double beginTime, double endTime) => SearchActions(AllSpellCastBlocks, beginTime, endTime);
    internal List<ActionGroup> GetDeathsDuring(double beginTime, double endTime) => SearchActions(AllDeathBlocks, beginTime, endTime);
    internal List<ActionGroup> GetHealsDuring(double beginTime, double endTime) => SearchActions(AllHealBlocks, beginTime, endTime);
    internal List<ActionGroup> GetMiscDuring(double beginTime, double endTime) => SearchActions(AllMiscBlocks, beginTime, endTime);
    internal List<ActionGroup> GetResistsDuring(double beginTime, double endTime) => SearchActions(AllResistBlocks, beginTime, endTime);
    internal List<ActionGroup> GetReceivedSpellsDuring(double beginTime, double endTime) => SearchActions(AllReceivedSpellBlocks, beginTime, endTime);
    internal bool IsKnownNpc(string npc) => !string.IsNullOrEmpty(npc) && AllNpcs.ContainsKey(npc.ToLower(CultureInfo.CurrentCulture));
    internal bool IsOldSpell(string name) => OldSpellNamesDb.ContainsKey(name);
    internal bool IsPlayerSpell(string name) => GetSpellByName(name)?.ClassMask > 0;
    internal bool IsLifetimeNpc(string name) => LifetimeFights.ContainsKey(name);

    internal string AbbreviateSpellName(string spell)
    {
      if (!SpellAbbrvCache.TryGetValue(spell, out var result))
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

          if (RanksCache.TryGetValue(lastWord, out var root))
          {
            result = spell[..index];
            if (!string.IsNullOrEmpty(root))
            {
              result += " " + root;
            }
          }
        }

        SpellAbbrvCache[spell] = result;
      }

      return string.Intern(result);
    }

    internal void AddLootRecord(LootRecord record, double beginTime)
    {
      Helpers.AddAction(AllLootBlocks, record, beginTime);

      if (!record.IsCurrency && record.Quantity > 0 && AssignedLoot.Count > 0)
      {
        var found = AssignedLoot.FindLastIndex(item => item.Player == record.Player && item.Item == record.Item);
        if (found > -1)
        {
          AssignedLoot.RemoveAt(found);

          foreach (var block in AllLootBlocks.OrderByDescending(block => block.BeginTime))
          {
            found = block.Actions.FindLastIndex(item => item is LootRecord loot && loot.Player == record.Player && loot.Item == record.Item && loot.Quantity == 0);
            if (found > -1)
            {
              lock (block.Actions)
              {
                block.Actions.RemoveAt(found);
              }
            }
          }
        }
      }
      else if (!record.IsCurrency && record.Quantity == 0)
      {
        AssignedLoot.Add(record);
      }
    }

    internal void AddRandomRecord(RandomRecord record, double beginTime)
    {
      Helpers.AddAction(AllRandomBlocks, record, beginTime);
      EventsNewRandomRecord?.Invoke(this, record);
    }

    internal void AddResistRecord(ResistRecord record, double beginTime)
    {
      Helpers.AddAction(AllResistBlocks, record, beginTime);

      if (SpellsNameDb.TryGetValue(record.Spell, out var spellList))
      {
        if (spellList.Find(item => !item.IsBeneficial) is { } spellData)
        {
          UpdateNpcSpellResistStats(record.Defender, spellData.Resist, true);
        }
      }
    }

    internal void CheckExpireFights(double currentTime)
    {
      foreach (ref var fight in ActiveFights.Values.ToArray().AsSpan())
      {
        var diff = currentTime - fight.LastTime;
        if (diff > MAX_TIMEOUT || (diff > FIGHT_IMEOUT && fight.DamageBlocks.Count > 0))
        {
          RemoveActiveFight(fight.CorrectMapKey);
          RemoveOverlayFight(fight.Id);
        }
      }
    }

    internal SpellData GetSpellByAbbrv(string abbrv)
    {
      if (!string.IsNullOrEmpty(abbrv) && abbrv != Labels.UNASSIGNED && SpellsAbbrvDb.TryGetValue(abbrv, out var value))
      {
        return value;
      }

      return null;
    }

    internal Dictionary<string, Dictionary<SpellResist, ResistCount>> GetNpcResistStats()
    {
      lock (NpcResistStats)
      {
        return NpcResistStats.ToDictionary(entry => entry.Key, entry => entry.Value);
      }
    }

    internal Dictionary<string, TotalCount> GetNpcTotalSpellCounts()
    {
      lock (NpcTotalSpellCounts)
      {
        return NpcTotalSpellCounts.ToDictionary(entry => entry.Key, entry => entry.Value);
      }
    }

    internal Fight GetFight(string name)
    {
      Fight result = null;
      if (!string.IsNullOrEmpty(name))
      {
        ActiveFights.TryGetValue(name, out result);
      }
      return result;
    }

    internal SpellData GetDamagingSpellByName(string name)
    {
      SpellData spellData = null;

      if (!string.IsNullOrEmpty(name) && name != Labels.UNK_SPELL && SpellsNameDb.TryGetValue(name, out var spellList))
      {
        spellData = spellList.Find(item => item.Damaging > 0);
      }

      return spellData;
    }

    internal SpellData GetHealingSpellByName(string name)
    {
      SpellData spellData = null;

      if (!string.IsNullOrEmpty(name) && name != Labels.UNK_SPELL && SpellsNameDb.TryGetValue(name, out var spellList))
      {
        spellData = spellList.Find(item => item.Damaging < 0);
      }

      return spellData;
    }

    internal SpellData GetSpellByName(string name)
    {
      SpellData spellData = null;

      if (!string.IsNullOrEmpty(name) && name != Labels.UNK_SPELL && SpellsNameDb.TryGetValue(name, out var spellList))
      {
        if (spellList.Count <= 10)
        {
          foreach (ref var spell in spellList.ToArray().AsSpan())
          {
            if (spellData == null || (spellData.Level < spell.Level && spell.Level <= 250) || (spellData.Level > 250 && spell.Level <= 250))
            {
              spellData = spell;
            }
          }
        }
        else
        {
          spellData = spellList.Last();
        }
      }

      return spellData;
    }

    internal string GetClassFromTitle(string title)
    {
      if (TitleToClass.TryGetValue(title, out var value))
      {
        return value;
      }
      return null;
    }

    internal void AddSpecial(TimedAction action)
    {
      lock (AllSpecialActions)
      {
        AllSpecialActions.Add(action);
      }
    }

    internal void AddHealRecord(HealRecord record, double beginTime)
    {
      record.Healer = PlayerManager.Instance.ReplacePlayer(record.Healer, record.Healed);
      record.Healed = PlayerManager.Instance.ReplacePlayer(record.Healed, record.Healer);
      Helpers.AddAction(AllHealBlocks, record, beginTime);
    }

    internal void HandleSpellInterrupt(string player, string spell, double beginTime)
    {
      for (var i = AllSpellCastBlocks.Count - 1; i >= 0 && beginTime - AllSpellCastBlocks[i].BeginTime <= 5; i--)
      {
        var index = AllSpellCastBlocks[i].Actions.FindLastIndex(action => action is SpellCast sc && sc.Spell == spell && sc.Caster == player);
        if (index > -1 && AllSpellCastBlocks[i].Actions[index] is SpellCast cast)
        {
          cast.Interrupted = true;
          break;
        }
      }
    }

    internal void AddSpellCast(SpellCast cast, double beginTime, string specialKey = null)
    {
      if (SpellsNameDb.ContainsKey(cast.Spell))
      {
        Helpers.AddAction(AllSpellCastBlocks, cast, beginTime);
        LastSpellIndex = AllSpellCastBlocks.Count - 1;

        if (SpellsToClass.TryGetValue(cast.Spell, out var theClass))
        {
          PlayerManager.Instance.UpdatePlayerClassFromSpell(cast, theClass);
        }

        if (specialKey != null)
        {
          var updated = false;
          lock (LockObject)
          {
            foreach (ref var key in AdpsKeys.ToArray().AsSpan())
            {
              if (AdpsValues[key].TryGetValue(cast.SpellData.NameAbbrv, out var value))
              {
                var msg = string.IsNullOrEmpty(cast.SpellData.LandsOnYou) ? cast.SpellData.Name : cast.SpellData.LandsOnYou;
                AdpsActive[key][msg] = value;
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

    internal List<TimedAction> GetSpecials()
    {
      lock (AllSpecialActions)
      {
        return AllSpecialActions.OrderBy(special => special.BeginTime).ToList();
      }
    }

    internal SpellTreeResult GetLandsOnOther(string[] split, out string player)
    {
      player = null;
      var found = SearchSpellPath(LandsOnOtherTree, split);

      if (found.SpellData.Count > 0 && found.DataIndex > -1)
      {
        player = string.Join(" ", split.ToArray(), 0, found.DataIndex + 1);
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
      var found = SearchSpellPath(LandsOnYouTree, split);

      if (found.DataIndex == 0 && found.SpellData.Count > 0)
      {
        found.SpellData = FindByLandsOn(ConfigUtil.PlayerName, found.SpellData);

        // check Adps
        if (AdpsLandsOn.TryGetValue(found.SpellData[0].LandsOnYou, out var spellDataSet) && spellDataSet.Count > 0)
        {
          var spellData = spellDataSet.Count == 1 ? spellDataSet.First() : FindPreviousCast(ConfigUtil.PlayerName, spellDataSet.ToList(), true);

          // this only handles latest versions of spells so an older one may have given us the landsOn string and then it wasn't found
          // for some spells this makes sense because of the level requirements and it wouldn't do anything but thats not true for all of them
          // need to handle older spells and multiple rate values
          if (spellData != null)
          {
            var updated = false;
            lock (LockObject)
            {
              foreach (ref var key in AdpsKeys.ToArray().AsSpan())
              {
                if (AdpsValues[key].TryGetValue(spellData.NameAbbrv, out var value))
                {
                  AdpsActive[key][spellData.LandsOnYou] = value;
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
      var found = SearchSpellPath(WearOffTree, split);

      if (found.DataIndex == 0 && found.SpellData.Count > 0)
      {
        found.SpellData = FindByLandsOn(split[0], found.SpellData);

        // check Adps
        if (AdpsWearOff.TryGetValue(found.SpellData[0].WearOff, out var spellDataSet) && spellDataSet.Count > 0)
        {
          var spellData = spellDataSet.First();
          var updated = false;

          lock (LockObject)
          {
            foreach (ref var key in AdpsKeys.ToArray().AsSpan())
            {
              if (AdpsValues[key].TryGetValue(spellData.NameAbbrv, out _))
              {
                var msg = string.IsNullOrEmpty(spellData.LandsOnYou) ? spellData.Name : spellData.LandsOnYou;
                AdpsActive[key].Remove(msg);
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

    internal void UpdateNpcSpellReflectStats(string npc)
    {
      lock (NpcTotalSpellCounts)
      {
        if (!NpcTotalSpellCounts.TryGetValue(npc, out var value))
        {
          NpcTotalSpellCounts[npc] = new TotalCount { Reflected = 1 };
        }
        else
        {
          value.Reflected++;
        }
      }
    }

    internal void UpdateNpcSpellResistStats(string npc, SpellResist resist, bool resisted = false)
    {
      // NPC is always upper case after it is parsed
      lock (NpcResistStats)
      {
        if (!NpcResistStats.TryGetValue(npc, out var stats))
        {
          stats = new Dictionary<SpellResist, ResistCount>();
          NpcResistStats[npc] = stats;
        }

        if (!stats.TryGetValue(resist, out var count))
        {
          stats[resist] = resisted ? new ResistCount { Resisted = 1 } : new ResistCount { Landed = 1 };
        }
        else
        {
          if (resisted)
          {
            count.Resisted++;
          }
          else
          {
            count.Landed++;
          }
        }
      }

      lock (NpcTotalSpellCounts)
      {
        if (!NpcTotalSpellCounts.TryGetValue(npc, out var value))
        {
          NpcTotalSpellCounts[npc] = new TotalCount { Landed = 1 };
        }
        else
        {
          value.Landed++;
        }
      }
    }

    internal void ZoneChanged()
    {
      var updated = false;

      lock (LockObject)
      {
        foreach (var active in AdpsActive)
        {
          active.Value.Keys.ToList().ForEach(landsOn =>
          {
            if (AdpsLandsOn.TryGetValue(landsOn, out var value))
            {
              // Need this check since Glyph may be present and there's no
              // lands on data for it as it's a special cast
              if (value.Any(spellData => spellData.SongWindow))
              {
                AdpsActive[active.Key].Remove(landsOn);
                updated = true;
              }
            }
          });
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
          var duration = int.Parse(data[3], CultureInfo.CurrentCulture) * 6; // as seconds
          var beneficial = int.Parse(data[4], CultureInfo.CurrentCulture);
          var target = byte.Parse(data[6], CultureInfo.CurrentCulture);
          var classMask = ushort.Parse(data[7], CultureInfo.CurrentCulture);

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
            ID = string.Intern(data[0]),
            Name = string.Intern(data[1]),
            NameAbbrv = string.Intern(AbbreviateSpellName(data[1])),
            Level = byte.Parse(data[2], CultureInfo.CurrentCulture),
            Duration = (ushort)duration,
            IsBeneficial = beneficial != 0,
            Target = target,
            MaxHits = ushort.Parse(data[5], CultureInfo.CurrentCulture),
            ClassMask = classMask,
            Damaging = short.Parse(data[8], CultureInfo.CurrentCulture),
            //CombatSkill = uint.Parse(data[9], CultureInfo.CurrentCulture),
            Resist = (SpellResist)int.Parse(data[10], CultureInfo.CurrentCulture),
            SongWindow = data[11] == "1",
            Adps = byte.Parse(data[12], CultureInfo.CurrentCulture),
            Mgb = data[13] == "1",
            Rank = byte.Parse(data[14], CultureInfo.CurrentCulture),
            LandsOnYou = string.Intern(data[15]),
            LandsOnOther = string.Intern(data[16]),
            WearOff = string.Intern(data[17])
          };
        }
      }

      return spellData;
    }

    private void RecalculateAdps()
    {
      lock (LockObject)
      {
        MyDoTCritRateMod = (uint)AdpsActive[AdpsKeys[0]].Sum(kv => kv.Value);
        MyNukeCritRateMod = (uint)AdpsActive[AdpsKeys[1]].Sum(kv => kv.Value);
      }
    }

    private SpellData FindPreviousCast(string player, List<SpellData> output, bool isAdps = false)
    {
      if (LastSpellIndex > -1)
      {
        var outputSpan = output.ToArray().AsSpan();
        var endTime = AllSpellCastBlocks[LastSpellIndex].BeginTime - 5;
        for (var i = LastSpellIndex; i >= 0 && AllSpellCastBlocks[i].BeginTime >= endTime; i--)
        {
          for (var j = AllSpellCastBlocks[i].Actions.Count - 1; j >= 0; j--)
          {
            if (AllSpellCastBlocks[i].Actions[j] is SpellCast { Interrupted: false } cast)
            {
              foreach (var value in outputSpan)
              {
                if ((!isAdps || value.Adps > 0) && (value.Target != (int)SpellTarget.SELF || cast.Caster == player) &&
                  value.Name == cast.Spell)
                {
                  return value;
                }
              }
            }
          }
        }
      }

      return null;
    }

    private List<SpellData> FindByLandsOn(string player, List<SpellData> output)
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
          // one more thing, if all the abbrviations look the same then we know the spell
          // even if the version is wrong. grab the newest
          result = (output.Distinct(AbbrvComparer).Count() == 1) ? new List<SpellData> { output.First() } : output;
        }
        else
        {
          result = new List<SpellData> { foundSpellData };
        }
      }

      return result;
    }

    internal bool RemoveActiveFight(string name)
    {
      var removed = ActiveFights.TryRemove(name, out var fight);
      if (removed)
      {
        fight.Dead = true;
      }
      return removed;
    }

    internal void UpdateIfNewFightMap(string name, Fight fight, bool isNonTankingFight)
    {
      LifetimeFights[name] = 1;

      if (ActiveFights.TryAdd(name, fight))
      {
        ActiveFights[name] = fight;
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
        bool needEvent;

        lock (OverlayFights)
        {
          needEvent = OverlayFights.Count == 0;
          OverlayFights[fight.Id] = fight;
        }

        if (needEvent)
        {
          EventsNewOverlayFight?.Invoke(this, fight);
        }
      }
    }

    internal Dictionary<long, Fight> GetOverlayFights()
    {
      Dictionary<long, Fight> result;
      lock (OverlayFights)
      {
        result = OverlayFights.ToDictionary(i => i.Key, i => i.Value);
      }
      return result;
    }

    internal void RemoveOverlayFight(long id)
    {
      lock (OverlayFights)
      {
        OverlayFights.Remove(id, out _);
      }
    }

    internal bool HasOverlayFights()
    {
      bool result;
      lock (OverlayFights)
      {
        result = OverlayFights.Count > 0;
      }
      return result;
    }

    internal void ResetOverlayFights(bool active = false)
    {
      Span<Fight> span;

      lock (OverlayFights)
      {
        span = OverlayFights.Values.ToArray().AsSpan();
      }

      var groupId = (active && ActiveFights.Count > 0) ? ActiveFights.Values.First().GroupId : -1;

      // active is used after the log as been loaded. the overlay opening is displayed so that
      // FightTable has time to populate the GroupIds. if for some reason not enough time has
      // ellapsed then the IDs will still be 0 so ignore
      if (groupId == 0)
      {
        groupId = -1;
      }

      var removeList = new List<long>();

      foreach (ref var fight in span)
      {
        if (fight != null)
        {
          if (groupId == -1 || fight.GroupId != groupId)
          {
            fight.PlayerDamageTotals.Clear();
            fight.PlayerTankTotals.Clear();
            removeList.Add(fight.Id);
          }
        }
      }

      removeList.ForEach(RemoveOverlayFight);
    }

    internal void Clear()
    {
      lock (LockObject)
      {
        LastSpellIndex = -1;
        ActiveFights.Clear();
        LifetimeFights.Clear();
        OverlayFights.Clear();
        AllDeathBlocks.Clear();
        AllMiscBlocks.Clear();
        AllSpellCastBlocks.Clear();
        AllReceivedSpellBlocks.Clear();
        AllResistBlocks.Clear();
        AllHealBlocks.Clear();
        AllLootBlocks.Clear();
        AllRandomBlocks.Clear();
        AllSpecialActions.Clear();
        SpellAbbrvCache.Clear();
        AssignedLoot.Clear();
        ClearActiveAdps();
        EventsClearedActiveData?.Invoke(this, true);
      }

      lock (NpcTotalSpellCounts)
      {
        NpcTotalSpellCounts.Clear();
      }

      lock (NpcResistStats)
      {
        NpcResistStats.Clear();
      }
    }

    internal void ClearActiveAdps()
    {
      lock (LockObject)
      {
        AdpsKeys.ForEach(key => AdpsActive[key].Clear());
        MyDoTCritRateMod = 0;
        MyNukeCritRateMod = 0;
      }
    }

    internal static bool ResolveSpellAmbiguity(ReceivedSpell spell, out SpellData replaced)
    {
      replaced = null;

      if (spell.Ambiguity.Count < 30)
      {
        var spellClass = (int)PlayerManager.Instance.GetPlayerClassEnum(spell.Receiver);
        var subset = spell.Ambiguity.FindAll(test => test.Target == (int)SpellTarget.SELF && spellClass != 0 && (test.ClassMask & spellClass) == spellClass);
        var distinct = subset.Distinct(AbbrvComparer).ToList();
        replaced = distinct.Count == 1 ? distinct.First() : spell.Ambiguity.First();
      }

      return replaced != null;
    }

    private void RemoveFight(string name)
    {
      if (!string.IsNullOrEmpty(name))
      {
        var removed = ActiveFights.TryRemove(name, out _);
        removed = LifetimeFights.TryRemove(name, out _) || removed;

        if (removed)
        {
          EventsRemovedFight?.Invoke(this, name);
        }

        Span<Fight> overlayFights;

        lock (OverlayFights)
        {
          overlayFights = OverlayFights.Values.ToArray().AsSpan();
        }

        foreach (ref var fight in overlayFights)
        {
          if (fight.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
          {
            RemoveOverlayFight(fight.Id);
          }
        }
      }
    }

    private static List<ActionGroup> SearchActions(List<ActionGroup> allActions, double beginTime, double endTime)
    {
      var startBlock = new ActionGroup { BeginTime = beginTime };
      var endBlock = new ActionGroup { BeginTime = endTime + 1 };

      var startIndex = allActions.BinarySearch(startBlock, TAComparer);
      if (startIndex < 0)
      {
        startIndex = Math.Abs(startIndex) - 1;
      }

      var endIndex = allActions.BinarySearch(endBlock, TAComparer);
      if (endIndex < 0)
      {
        endIndex = Math.Abs(endIndex) - 1;
      }

      var last = endIndex - startIndex;
      return last > 0 ? allActions.GetRange(startIndex, last) : new List<ActionGroup>();
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

    private static void BuildSpellPath(List<string> data, SpellTreeNode node, SpellData spellData, int lastIndex = -1)
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

    static int DurationCompare(SpellData a, SpellData b)
    {
      var result = b.Duration.CompareTo(a.Duration);

      if (result == 0 && int.TryParse(a.ID, out var aInt) && int.TryParse(b.ID, out var bInt))
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

    private class TimedActionComparer : IComparer<TimedAction>
    {
      public int Compare(TimedAction x, TimedAction y) => (x != null && y != null) ? x.BeginTime.CompareTo(y.BeginTime) : 0;
    }

    public class ResistCount
    {
      internal uint Landed { get; set; }
      internal uint Resisted { get; set; }
    }

    public class TotalCount
    {
      internal uint Landed { get; set; }
      internal uint Reflected { get; set; }
    }

    public class SpellTreeNode
    {
      internal List<SpellData> SpellData { get; set; } = new();
      internal Dictionary<string, SpellTreeNode> Words { get; set; } = new();
    }

    public class SpellTreeResult
    {
      internal List<SpellData> SpellData { get; set; }
      internal int DataIndex { get; set; }
    }
  }
}