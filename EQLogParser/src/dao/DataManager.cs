using Caching;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  class DataManager
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    public static DataManager Instance = new DataManager();
    public event EventHandler<PetMapping> EventsUpdatePetMapping;
    public event EventHandler<string> EventsNewVerifiedPet;
    public event EventHandler<string> EventsNewVerifiedPlayer;
    public event EventHandler<string> EventsRemovedNonPlayer;
    public event EventHandler<NonPlayer> EventsNewNonPlayer;
    public event EventHandler<bool> EventsClearedActiveData;

    public string PlayerName { get; set; }

    private static string CONFIG_DIR;
    private static string PETMAP_FILE;
    private static string SETTINGS_FILE;
    private bool PetMappingUpdated = false;
    private bool SettingsUpdated = false;

    private List<TimedAction> AllDamageRecords = new List<TimedAction>();
    private List<TimedAction> AllHealRecords = new List<TimedAction>();
    private List<TimedAction> AllSpellCasts = new List<TimedAction>();
    private List<TimedAction> AllReceivedSpells = new List<TimedAction>();
    private List<TimedAction> AllResists = new List<TimedAction>();
    private List<TimedAction> PlayerDeaths = new List<TimedAction>();

    private Dictionary<string, byte> AllUniqueSpellCasts = new Dictionary<string, byte>();
    private LRUCache<SpellData> AllUniqueSpellsLRU = new LRUCache<SpellData>(2000, 500, false);
    private Dictionary<string, List<SpellData>> PosessiveLandsOnOthers = new Dictionary<string, List<SpellData>>();
    private Dictionary<string, List<SpellData>> NonPosessiveLandsOnOthers = new Dictionary<string, List<SpellData>>();
    private Dictionary<string, List<SpellData>> LandsOnYou = new Dictionary<string, List<SpellData>>();
    private Dictionary<SpellClasses, string> ClassNames = new Dictionary<SpellClasses, string>();
    private Dictionary<string, SpellData> SpellsDB = new Dictionary<string, SpellData>();
    private Dictionary<string, SpellData> SpellsNameDB = new Dictionary<string, SpellData>();
    private Dictionary<string, SpellData> SpellsAbbrvDB = new Dictionary<string, SpellData>();
    private Dictionary<string, SpellClasses> SpellsToClass = new Dictionary<string, SpellClasses>();

    private ConcurrentDictionary<string, string> ApplicationSettings = new ConcurrentDictionary<string, string>();
    private ConcurrentDictionary<string, NonPlayer> ActiveNonPlayerMap = new ConcurrentDictionary<string, NonPlayer>();
    private ConcurrentDictionary<string, string> PlayerReplacement = new ConcurrentDictionary<string, string>();
    private ConcurrentDictionary<string, byte> GameGeneratedPets = new ConcurrentDictionary<string, byte>();
    private ConcurrentDictionary<string, byte> LifetimeNonPlayerMap = new ConcurrentDictionary<string, byte>();
    private ConcurrentDictionary<string, string> PetToPlayerMap = new ConcurrentDictionary<string, string>();
    private ConcurrentDictionary<string, SpellClassCounter> PlayerToClass = new ConcurrentDictionary<string, SpellClassCounter>();
    private ConcurrentDictionary<string, byte> DefinitelyNotAPlayer = new ConcurrentDictionary<string, byte>();
    private ConcurrentDictionary<string, long> ProbablyNotAPlayer = new ConcurrentDictionary<string, long>();
    private ConcurrentDictionary<string, byte> UnVerifiedPetOrPlayer = new ConcurrentDictionary<string, byte>();
    private ConcurrentDictionary<string, byte> VerifiedPets = new ConcurrentDictionary<string, byte>();
    private ConcurrentDictionary<string, byte> VerifiedPlayers = new ConcurrentDictionary<string, byte>();

    private DataManager()
    {
      // static data first
      VerifiedPlayers["himself"] = 1;
      VerifiedPlayers["herself"] = 1;
      VerifiedPlayers["itself"] = 1;
      VerifiedPlayers["you"] = 1;
      VerifiedPlayers["YOU"] = 1;
      VerifiedPlayers["You"] = 1;
      VerifiedPlayers["your"] = 1;
      VerifiedPlayers["Your"] = 1;
      VerifiedPlayers["YOUR"] = 1;
      ClassNames[SpellClasses.WAR] = string.Intern("Warrior");
      ClassNames[SpellClasses.CLR] = string.Intern("Cleric");
      ClassNames[SpellClasses.PAL] = string.Intern("Paladin");
      ClassNames[SpellClasses.RNG] = string.Intern("Ranger");
      ClassNames[SpellClasses.SHD] = string.Intern("Shadow Knight");
      ClassNames[SpellClasses.DRU] = string.Intern("Druid");
      ClassNames[SpellClasses.MNK] = string.Intern("Monk");
      ClassNames[SpellClasses.BRD] = string.Intern("Bard");
      ClassNames[SpellClasses.ROG] = string.Intern("Rogue");
      ClassNames[SpellClasses.SHM] = string.Intern("Shaman");
      ClassNames[SpellClasses.NEC] = string.Intern("Necromancer");
      ClassNames[SpellClasses.WIZ] = string.Intern("Wizard");
      ClassNames[SpellClasses.MAG] = string.Intern("Magician");
      ClassNames[SpellClasses.ENC] = string.Intern("Enchanter");
      ClassNames[SpellClasses.BST] = string.Intern("Beastlord");
      ClassNames[SpellClasses.BER] = string.Intern("Berserker");

      // General Settings
      try
      {
        // needs to be during class initialization for some reason
        CONFIG_DIR = Environment.ExpandEnvironmentVariables(@"%AppData%\EQLogParser\config");
        PETMAP_FILE = CONFIG_DIR + @"\petmapping.txt";
        SETTINGS_FILE = CONFIG_DIR + @"\settings.txt";

        // create config dir if it doesn't exist
        System.IO.Directory.CreateDirectory(CONFIG_DIR);

        if (System.IO.File.Exists(SETTINGS_FILE))
        {
          string[] lines = System.IO.File.ReadAllLines(SETTINGS_FILE);
          foreach (string line in lines)
          {
            string[] parts = line.Split('=');
            if (parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
            {
              ApplicationSettings[parts[0]] = parts[1];
            }
          }
        }
      }
      catch (Exception ex)
      {
        LOG.Error(ex);
      }

      // Populated generated pets
      try
      {
        if (System.IO.File.Exists(@"data\petnames.txt"))
        {
          string[] lines = System.IO.File.ReadAllLines(@"data\petnames.txt");
          lines.ToList().ForEach(line => GameGeneratedPets[line.TrimEnd()] = 1);
        }
      }
      catch (Exception ex)
      {
        LOG.Error(ex);
      }

      try
      {
        if (System.IO.File.Exists(@"data\spells.txt"))
        {
          DictionaryListHelper<string, SpellData> helper = new DictionaryListHelper<string, SpellData>();
          string[] lines = System.IO.File.ReadAllLines(@"data\spells.txt");

          foreach (string line in lines)
          {
            string[] data = line.Split('^');
            int beneficial;
            int.TryParse(data[2], out beneficial);
            byte target;
            byte.TryParse(data[3], out target);
            short classMask;
            short.TryParse(data[4], out classMask);
            SpellData spellData = new SpellData()
            {
              ID = string.Intern(data[0]),
              Spell = string.Intern(data[1]),
              SpellAbbrv = Helpers.AbbreviateSpellName(data[1]),
              Beneficial = beneficial != 0,
              Target = target,
              ClassMask = classMask,
              LandsOnYou = string.Intern(data[5]),
              LandsOnOther = string.Intern(data[6]),
              Damaging = byte.Parse(data[7]) == 1,
              IsProc = byte.Parse(data[8]) == 1
            };

            SpellsDB[spellData.ID] = spellData;
            SpellsNameDB[spellData.Spell] = spellData;
            SpellsAbbrvDB[spellData.SpellAbbrv] = spellData;

            if (spellData.LandsOnOther.StartsWith("'s "))
            {
              helper.AddToList(PosessiveLandsOnOthers, spellData.LandsOnOther.Substring(3), spellData);
            }
            else if (spellData.LandsOnOther.Length > 1)
            {
              helper.AddToList(NonPosessiveLandsOnOthers, spellData.LandsOnOther.Substring(1), spellData);
            }

            if (spellData.LandsOnYou != "" && spellData.LandsOnOther != "") // just do stuff in common
            {
              helper.AddToList(LandsOnYou, spellData.LandsOnYou, spellData);
            }
          }
        }
      }
      catch (Exception ex)
      {
        LOG.Error(ex);
      }

      Dictionary<string, byte> keepOut = new Dictionary<string, byte>();
      var classEnums = Enum.GetValues(typeof(SpellClasses)).Cast<SpellClasses>().ToList();
      foreach (var spell in SpellsDB.Values)
      {
        // exact match meaning class-only spell
        if (classEnums.Contains((SpellClasses)spell.ClassMask))
        {
          // these need to be unique and keep track if a conflict is found
          if (SpellsToClass.ContainsKey(spell.Spell))
          {
            SpellsToClass.Remove(spell.Spell);
            keepOut[spell.Spell] = 1;
          }
          else if (!keepOut.ContainsKey(spell.Spell))
          {
            SpellsToClass[spell.Spell] = (SpellClasses) spell.ClassMask;
          }
        }
      }
    }

    public void Clear()
    {
      ActiveNonPlayerMap.Clear();
      LifetimeNonPlayerMap.Clear();
      ProbablyNotAPlayer.Clear();
      UnVerifiedPetOrPlayer.Clear();
      AllSpellCasts.Clear();
      AllUniqueSpellCasts.Clear();
      AllUniqueSpellsLRU.Clear();
      AllReceivedSpells.Clear();
      AllDamageRecords.Clear();
      AllHealRecords.Clear();
      PlayerDeaths.Clear();
      EventsClearedActiveData(this, true);
    }

    public void LoadState()
    {
      lock(this)
      {
        // Pet settings
        try
        {
          UpdateVerifiedPlayers(Labels.UNASSIGNED_PET_OWNER);

          if (System.IO.File.Exists(PETMAP_FILE))
          {
            string[] lines = System.IO.File.ReadAllLines(PETMAP_FILE);
            foreach (string line in lines)
            {
              string[] parts = line.Split('=');
              if (parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
              {
                UpdatePetToPlayer(parts[0], parts[1]);
                UpdateVerifiedPlayers(parts[1]);
                UpdateVerifiedPets(parts[0]);
              }
            }
          }
        }
        catch (Exception ex)
        {
          LOG.Error(ex);
        }

        // dont count initial load
        PetMappingUpdated = false;
      }
    }

    public void SaveState()
    {
      lock(this)
      {
        SaveConfiguration(PETMAP_FILE, PetMappingUpdated, PetToPlayerMap);
        PetMappingUpdated = false;

        SaveConfiguration(SETTINGS_FILE, SettingsUpdated, ApplicationSettings);
        SettingsUpdated = false;
      }
    }

    public string GetApplicationSetting(string key)
    {
      string setting;
      ApplicationSettings.TryGetValue(key, out setting);
      return setting;
    }

    public void SetApplicationSetting(string key, string value)
    {
      if (value == null)
      {
        string setting;
        if (ApplicationSettings.TryRemove(key, out setting))
        {
          SettingsUpdated = true;
        }
      }
      else
      {
        string existing;
        if (ApplicationSettings.TryGetValue(key, out existing))
        {
          if (existing != value)
          {
            ApplicationSettings[key] = value;
            SettingsUpdated = true;
          }
        }
        else
        {
          ApplicationSettings[key] = value;
          SettingsUpdated = true;
        }
      }
    }

    public void AddNonPlayerMapBreak(string text)
    {
      NonPlayer divider = new NonPlayer() { GroupID = -1, BeginTimeString = NonPlayer.BREAK_TIME, Name = string.Intern(text) };
      EventsNewNonPlayer(this, divider);
    }

    public void AddPlayerDeath(string player, string npc, double dateTime)
    {
      PlayerDeaths.Add(new PlayerDeath() { Player = string.Intern(player), Npc = string.Intern(npc), BeginTime = dateTime });
    }

    public void AddDamageRecord(DamageRecord record)
    {
      // ReplacePlayer is done in the line parser already
      AllDamageRecords.Add(record);
    }

    public void AddResistRecord(ResistRecord record)
    {
      // Resists are only seen by current player
      AllResists.Add(record);
    }

    public void AddHealRecord(HealRecord record)
    {
      bool replaced;
      record.Healer = ReplacePlayer(record.Healer, out replaced);
      record.Healed = ReplacePlayer(record.Healed, out replaced);
      AllHealRecords.Add(record);

      // can use heals to determine additional players
      bool foundHealer = CheckNameForPlayer(record.Healer);
      bool foundHealed = CheckNameForPlayer(record.Healed) || CheckNameForPet(record.Healed);

      if (!foundHealer && foundHealed && Helpers.IsPossiblePlayerName(record.Healer, record.Healer.Length))
      {
        UpdateVerifiedPlayers(record.Healer);
      }
    }

    public void AddSpellCast(SpellCast cast)
    {
      bool replaced;
      cast.Caster = ReplacePlayer(cast.Caster, out replaced);

      lock (AllSpellCasts)
      {
        AllSpellCasts.Add(cast);

        string abbrv = Helpers.AbbreviateSpellName(cast.Spell);
        if (abbrv != null)
        {
          AllUniqueSpellCasts[abbrv] = 1;
        }
      }

      UpdatePlayerClassFromSpell(cast);
    }

    public void AddReceivedSpell(ReceivedSpell received)
    {
      bool replaced;
      received.Receiver = ReplacePlayer(received.Receiver, out replaced);

      lock (AllReceivedSpells)
      {
        AllReceivedSpells.Add(received);
      }
    }

    public void SetPlayerName(string name)
    {
      PlayerReplacement["you"] = name;
      PlayerReplacement["You"] = name;
      PlayerReplacement["YOU"] = name;
      PlayerReplacement["your"] = name;
      PlayerReplacement["Your"] = name;
      PlayerReplacement["YOUR"] = name;
      PlayerReplacement["himself"] = name;
      PlayerReplacement["herself"] = name;
      PlayerReplacement["itself"] = name;
      PlayerReplacement["Himself"] = name;
      PlayerReplacement["Herself"] = name;
      PlayerReplacement["Itself"] = name;
      UpdateVerifiedPlayers(name);
      PlayerName = name;
    }

    public string ReplacePlayer(string name, out bool replaced)
    {
      replaced = false;
      string result = name;

      string found;
      if (PlayerReplacement.TryGetValue(name, out found))
      {
        replaced = true;
        result = found;
      }

      return string.Intern(result);
    }

    public bool CheckNameForPet(string name)
    {
      bool isPet = VerifiedPets.ContainsKey(name);

      if (!isPet && GameGeneratedPets.ContainsKey(name))
      {
        UpdateVerifiedPets(name);
        isPet = true;

        if (!PetToPlayerMap.ContainsKey(name))
        {
          UpdatePetToPlayer(name, Labels.UNASSIGNED_PET_OWNER);
        }
      }

      return isPet;
    }

    public bool CheckNameForPlayer(string name)
    {
      return VerifiedPlayers.ContainsKey(name);
    }

    public List<TimedAction> GetCastsDuring(double beginTime, double endTime)
    {
      return SearchActions(AllSpellCasts, beginTime, endTime);
    }

    public List<TimedAction> GetDamageDuring(double beginTime, double endTime)
    {
      return SearchActions(AllDamageRecords, beginTime, endTime);
    }

    public List<TimedAction> GetHealsDuring(double beginTime, double endTime)
    {
      return SearchActions(AllHealRecords, beginTime, endTime);
    }

    public List<TimedAction> GetResistsDuring(double beginTime, double endTime)
    {
      return SearchActions(AllResists, beginTime, endTime);
    }

    public List<TimedAction> GetReceivedSpellsDuring(double beginTime, double endTime)
    {
      return SearchActions(AllReceivedSpells, beginTime, endTime);
    }

    public List<TimedAction> GetPlayerDeathsDuring(double beginTime, double endTime)
    {
      return SearchActions(PlayerDeaths, beginTime, endTime);
    }

    public SpellData GetSpellByAbbrv(string abbrv)
    {
      SpellData result = null;
      if (abbrv != "" && abbrv != Labels.UNKNOWN_SPELL && SpellsAbbrvDB.ContainsKey(abbrv))
      {
        result = SpellsAbbrvDB[abbrv];
      }
      return result;
    }

    public SpellData GetSpellByName(string name)
    {
      SpellData result = null;
      if (name != "" && name != Labels.UNKNOWN_SPELL && SpellsNameDB.ContainsKey(name))
      {
        result = SpellsNameDB[name];
      }
      return result;
    }

    public NonPlayer GetNonPlayer(string name)
    {
      NonPlayer npc = null;
      ActiveNonPlayerMap.TryGetValue(name, out npc);
      return npc;
    }

    public string GetClassName(SpellClasses type)
    {
      string name = "";
      if (ClassNames.ContainsKey(type))
      {
        name = ClassNames[type];
      }
      return name;
    }

    public string GetPlayerClass(string name)
    {
      string className = "";
      SpellClassCounter counter;
      if (PlayerToClass.TryGetValue(name, out counter))
      {
        className = ClassNames[counter.CurrentClass];
      }
      return className;
    }

    public string GetPlayerFromPet(string pet)
    {
      string player = null;
      PetToPlayerMap.TryGetValue(pet, out player);
      return player;
    }

    public SpellData GetNonPosessiveLandsOnOther(string value, out List<SpellData> output)
    {
      SpellData result = null;
      if (NonPosessiveLandsOnOthers.TryGetValue(value, out output))
      {
        result = FindByLandsOn(value, output);
      }
      return result;
    }

    public SpellData GetPosessiveLandsOnOther(string value, out List<SpellData> output)
    {
      SpellData result = null;
      if (PosessiveLandsOnOthers.TryGetValue(value, out output))
      {
        result = FindByLandsOn(value, output);
      }
      return result;
    }

    public SpellData GetLandsOnYou(string value, out List<SpellData> output)
    {
      SpellData result = null;
      if (LandsOnYou.TryGetValue(value, out output))
      {
        result = FindByLandsOn(value, output);
      }
      return result;
    }

    private SpellData FindByLandsOn(string value, List<SpellData> output)
    {
      SpellData result = null;
      if (output.Count == 1)
      {
        result = output[0];
      }
      else if (output.Count > 1)
      {
        if (!AllUniqueSpellsLRU.TryGet(value, out result))
        {
          result = output.Find(spellData => AllUniqueSpellCasts.ContainsKey(spellData.SpellAbbrv));
          if (result == null)
          {
            // one more thing, if all the abbrviations look the same then we know the spell
            // even if the version is wrong. grab the last one
            var distinct = output.Select(spellData => spellData.SpellAbbrv).Distinct().ToList();
            if (distinct.Count == 1)
            {
              result = output.Last();
            }
          }
          AllUniqueSpellsLRU.AddReplace(value, result);
        }
      }
      return result;
    }

    public bool IsProbablyNotAPlayer(string name)
    {
      bool probably = DefinitelyNotAPlayer.ContainsKey(name);

      if (!probably && !VerifiedPlayers.ContainsKey(name) && !VerifiedPets.ContainsKey(name) && !GameGeneratedPets.ContainsKey(name)
        && ProbablyNotAPlayer.ContainsKey(name))
      {
        probably = ProbablyNotAPlayer[name] >= 5;
      }

      return probably;
    }

    public bool RemoveActiveNonPlayer(string name)
    {
      NonPlayer npc;
      return ActiveNonPlayerMap.TryRemove(name, out npc);
    }

    public void UpdateIfNewNonPlayerMap(string name, NonPlayer npc)
    {
      if (!LifetimeNonPlayerMap.ContainsKey(name))
      {
        LifetimeNonPlayerMap[name] = 1;
      }

      if (!ActiveNonPlayerMap.ContainsKey(name))
      {
        ActiveNonPlayerMap[name] = npc;
        EventsNewNonPlayer(this, npc);
      }
    }

    public void UpdatePlayerClassFromSpell(SpellCast cast)
    {
      SpellClasses theClass;
      if (SpellsToClass.TryGetValue(cast.Spell, out theClass))
      {
        SpellClassCounter counter;
        if (!PlayerToClass.TryGetValue(cast.Caster, out counter))
        {
          PlayerToClass.TryAdd(cast.Caster, new SpellClassCounter() { ClassCounts = new ConcurrentDictionary<SpellClasses, int>() });
          counter = PlayerToClass[cast.Caster];
        }

        if (!counter.ClassCounts.ContainsKey(theClass))
        {
          counter.ClassCounts.TryAdd(theClass, 0);
        }

        lock (counter)
        {
          int value = ++counter.ClassCounts[theClass];
          if (value > counter.CurrentMax)
          {
            counter.CurrentMax = value;
            counter.CurrentClass = theClass;
          }
        }
      }
    }

    public void UpdatePetToPlayer(string pet, string player)
    {
      if (!PetToPlayerMap.ContainsKey(pet) || PetToPlayerMap[pet] != player)
      {
        PetToPlayerMap[pet] = player;
        EventsUpdatePetMapping(this, new PetMapping() { Pet = pet, Owner = player });
        PetMappingUpdated = true;
      }
    }

    public void UpdateDefinitelyNotAPlayer(string name)
    {
      DefinitelyNotAPlayer[name] = 1;
    }

    public bool UpdateProbablyNotAPlayer(string name)
    {
      bool updated = false;
      if (!VerifiedPlayers.ContainsKey(name) && !VerifiedPets.ContainsKey(name) && !GameGeneratedPets.ContainsKey(name) &&
        !UnVerifiedPetOrPlayer.ContainsKey(name))
      {
        if (!DefinitelyNotAPlayer.ContainsKey(name) && Helpers.IsPossiblePlayerName(name))
        {
          long value = 0;
          if (ProbablyNotAPlayer.ContainsKey(name))
          {
            value = ProbablyNotAPlayer[name];
          }

          ProbablyNotAPlayer[name] = ++value;
        }
        else
        {
          DefinitelyNotAPlayer[name] = 1;
        }

        updated = true;
      }
      return updated;
    }

    public void UpdateUnVerifiedPetOrPlayer(string name)
    {
      // avoid checking to remove unless needed
      if (!IsProbablyNotAPlayer(name))
      {
        UnVerifiedPetOrPlayer[name] = 1;
        CheckNolongerNPC(name);
      }
    }

    public void UpdateVerifiedPets(string name)
    {
      if (VerifiedPets.TryAdd(name, 1))
      {
        EventsNewVerifiedPet(this, name);
        CheckNolongerNPC(name);
      }
    }

    public void UpdateVerifiedPlayers(string name)
    {
      if (VerifiedPlayers.TryAdd(name, 1))
      {
        EventsNewVerifiedPlayer(this, name);
        CheckNolongerNPC(name);
      }
    }

    private void SaveConfiguration(string fileName, bool updated, ConcurrentDictionary<string, string> dict)
    {
      lock (this)
      {
        try
        {
          if (updated)
          {
            List<string> lines = new List<string>();
            foreach (var keypair in dict)
            {
              if (keypair.Value != Labels.UNASSIGNED_PET_OWNER)
              {
                lines.Add(keypair.Key + "=" + keypair.Value);
              }
            }

            System.IO.File.WriteAllLines(fileName, lines);
          }
        }
        catch (Exception ex)
        {
          LOG.Error(ex);
        }
      }
    }

    private void CheckNolongerNPC(string name)
    {
      // remove from NPC map if it exists
      CheckNonPlayerMap(name);

      // remove from ProbablyNotAPlayer if it exists
      long value;
      ProbablyNotAPlayer.TryRemove(name, out value);

      byte bvalue;
      UnVerifiedPetOrPlayer.TryRemove(name, out bvalue);
    }

    private void CheckNonPlayerMap(string name)
    {
      NonPlayer npc;
      byte bnpc;
      bool removed = ActiveNonPlayerMap.TryRemove(name, out npc);
      removed = LifetimeNonPlayerMap.TryRemove(name, out bnpc) || removed;

      if (removed)
      {
        EventsRemovedNonPlayer(this, name);
      }
    }

    private List<TimedAction> SearchActions(List<TimedAction> allActions, double beginTime, double endTime)
    {
      TimedAction startAction = new TimedAction() { BeginTime = beginTime - 1 };
      TimedAction endAction = new TimedAction() { BeginTime = endTime + 1 };
      TimedActionComparer comparer = new TimedActionComparer();

      int startIndex = allActions.BinarySearch(startAction, comparer);
      if (startIndex < 0)
      {
        startIndex = Math.Abs(startIndex) - 1;
      }

      if (allActions.Count > startIndex && allActions[startIndex].BeginTime == startAction.BeginTime)
      {
        var result = allActions.FindIndex(startIndex, action => action.BeginTime > startAction.BeginTime);
        startIndex = (result > -1) ? result : startIndex;
      }

      int endIndex = allActions.BinarySearch(endAction, comparer);
      if (endIndex < 0)
      {
        endIndex = Math.Abs(endIndex) - 1;
      }

      int last = endIndex - startIndex;
      return last > 0 ? allActions.GetRange(startIndex, last) : new List<TimedAction>();
    }

    private class SpellClassCounter
    {
      public int CurrentMax { get; set; }
      public SpellClasses CurrentClass { get; set; }
      public ConcurrentDictionary<SpellClasses, int> ClassCounts { get; set; }
    }

    private class TimedActionComparer : IComparer<TimedAction>
    {
      public int Compare(TimedAction x, TimedAction y)
      {
        return x.BeginTime.CompareTo(y.BeginTime);
      }
    }
  }
}