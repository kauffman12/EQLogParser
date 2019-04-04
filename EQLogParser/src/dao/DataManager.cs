using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;

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
    public string ServerName { get; set; }

    public static string ARCHIVE_DIR;
    private static string CONFIG_DIR;
    private static string PETMAP_FILE;
    private static string SETTINGS_FILE;
    private bool PetMappingUpdated = false;
    private bool SettingsUpdated = false;

    private List<ActionBlock> AllDamageBlocks = new List<ActionBlock>();
    private List<ActionBlock> AllHealBlocks = new List<ActionBlock>();
    private List<ActionBlock> AllSpellCastBlocks = new List<ActionBlock>();
    private List<ActionBlock> AllReceivedSpellBlocks = new List<ActionBlock>();
    private readonly List<ActionBlock> AllResists = new List<ActionBlock>();
    private List<ActionBlock> PlayerDeaths = new List<ActionBlock>();

    private Dictionary<string,SpellData> AllUniqueSpellsCache = new Dictionary<string,SpellData>();
    private Dictionary<string, List<SpellData>> PosessiveLandsOnOthers = new Dictionary<string, List<SpellData>>();
    private Dictionary<string, List<SpellData>> NonPosessiveLandsOnOthers = new Dictionary<string, List<SpellData>>();
    private Dictionary<string, List<SpellData>> LandsOnYou = new Dictionary<string, List<SpellData>>();
    private Dictionary<SpellClasses, string> ClassNames = new Dictionary<SpellClasses, string>();
    private Dictionary<string, SpellData> SpellsDB = new Dictionary<string, SpellData>();
    private Dictionary<string, SpellData> SpellsNameDB = new Dictionary<string, SpellData>();
    private Dictionary<string, SpellData> SpellsAbbrvDB = new Dictionary<string, SpellData>();
    private Dictionary<string, SpellClasses> SpellsToClass = new Dictionary<string, SpellClasses>();
    private Dictionary<string, byte> GameGeneratedPets = new Dictionary<string, byte>();
    private Dictionary<string, string> PlayerReplacement = new Dictionary<string, string>();

    private ConcurrentDictionary<string, string> ApplicationSettings = new ConcurrentDictionary<string, string>();
    private ConcurrentDictionary<string, NonPlayer> ActiveNonPlayerMap = new ConcurrentDictionary<string, NonPlayer>();
    private ConcurrentDictionary<string, byte> AllUniqueSpellCasts = new ConcurrentDictionary<string, byte>();
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
        ARCHIVE_DIR = Environment.ExpandEnvironmentVariables(@"%AppData%\EQLogParser\archive\");
        PETMAP_FILE = CONFIG_DIR + @"\petmapping.txt";
        SETTINGS_FILE = CONFIG_DIR + @"\settings.txt";

        // create config dir if it doesn't exist
        Directory.CreateDirectory(CONFIG_DIR);

        if (File.Exists(SETTINGS_FILE))
        {
          string[] lines = File.ReadAllLines(SETTINGS_FILE);
          foreach (string line in lines)
          {
            string[] parts = line.Split('=');
            if (parts != null && parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
            {
              ApplicationSettings[parts[0]] = parts[1];
            }
          }
        }
      }
      catch (IOException ex)
      {
        LOG.Error(ex);
      }
      catch (UnauthorizedAccessException uax)
      {
        LOG.Error(uax);
      }
      catch (SecurityException se)
      {
        LOG.Error(se);
      }

      // Populate generated pets
      try
      {
        if (File.Exists(@"data\petnames.txt"))
        {
          string[] lines = File.ReadAllLines(@"data\petnames.txt");
          lines.ToList().ForEach(line => GameGeneratedPets[line.TrimEnd()] = 1);
        }
      }
      catch (IOException ex)
      {
        LOG.Error(ex);
      }
      catch (UnauthorizedAccessException uax)
      {
        LOG.Error(uax);
      }
      catch (SecurityException se)
      {
        LOG.Error(se);
      }

      try
      {
        if (File.Exists(@"data\spells.txt"))
        {
          DictionaryListHelper<string, SpellData> helper = new DictionaryListHelper<string, SpellData>();
          string[] lines = System.IO.File.ReadAllLines(@"data\spells.txt");

          foreach (string line in lines)
          {
            string[] data = line.Split('^');
            int beneficial = int.Parse(data[2], CultureInfo.CurrentCulture);
            byte target = byte.Parse(data[3], CultureInfo.CurrentCulture);
            ushort classMask = ushort.Parse(data[4], CultureInfo.CurrentCulture);
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
              Damaging = byte.Parse(data[7], CultureInfo.CurrentCulture) == 1,
              IsProc = byte.Parse(data[8], CultureInfo.CurrentCulture) == 1
            };

            SpellsDB[spellData.ID] = spellData;
            SpellsNameDB[spellData.Spell] = spellData;
            SpellsAbbrvDB[spellData.SpellAbbrv] = spellData;

            if (spellData.LandsOnOther.StartsWith("'s ", StringComparison.Ordinal))
            {
              helper.AddToList(PosessiveLandsOnOthers, spellData.LandsOnOther.Substring(3), spellData);
            }
            else if (spellData.LandsOnOther.Length > 1)
            {
              helper.AddToList(NonPosessiveLandsOnOthers, spellData.LandsOnOther.Substring(1), spellData);
            }

            if (spellData.LandsOnYou.Length > 0 && spellData.LandsOnOther.Length > 0) // just do stuff in common
            {
              helper.AddToList(LandsOnYou, spellData.LandsOnYou, spellData);
            }
          }
        }
      }
      catch (IOException ex)
      {
        LOG.Error(ex);
      }
      catch (UnauthorizedAccessException uax)
      {
        LOG.Error(uax);
      }
      catch (SecurityException se)
      {
        LOG.Error(se);
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
      AllSpellCastBlocks.Clear();
      AllUniqueSpellCasts.Clear();
      AllUniqueSpellsCache.Clear();
      AllReceivedSpellBlocks.Clear();
      AllDamageBlocks.Clear();
      AllHealBlocks.Clear();
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
      ApplicationSettings.TryGetValue(key, out string setting);
      return setting;
    }

    public void SetApplicationSetting(string key, string value)
    {
      if (value == null)
      {
        if (ApplicationSettings.TryRemove(key, out string setting))
        {
          SettingsUpdated = true;
        }
      }
      else
      {
        if (ApplicationSettings.TryGetValue(key, out string existing))
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

    public void AddPlayerDeath(string player, string npc, double beginTime)
    {
      var newDeath = new PlayerDeath() { Player = string.Intern(player), Npc = string.Intern(npc) };
      AddAction(PlayerDeaths, newDeath, beginTime);
    }

    public void AddDamageRecord(DamageRecord record, bool isPlayerDamage, double beginTime)
    {
      if (isPlayerDamage)
      {
        // ReplacePlayer is done in the line parser already
        AddAction(AllDamageBlocks, record, beginTime);
      }
    }

    public void AddResistRecord(ResistRecord record, double beginTime)
    {
      // Resists are only seen by current player
      AddAction(AllResists, record, beginTime);
    }

    public void AddHealRecord(HealRecord record, double beginTime)
    {
      record.Healer = ReplacePlayer(record.Healer, out bool replaced);
      record.Healed = ReplacePlayer(record.Healed, out replaced);

      AddAction(AllHealBlocks, record, beginTime);

      // can use heals to determine additional players
      bool foundHealer = CheckNameForPlayer(record.Healer);
      bool foundHealed = CheckNameForPlayer(record.Healed) || CheckNameForPet(record.Healed);

      if (!foundHealer && foundHealed && Helpers.IsPossiblePlayerName(record.Healer, record.Healer.Length))
      {
        UpdateVerifiedPlayers(record.Healer);
      }
    }

    public void AddSpellCast(SpellCast cast, double beginTime)
    {
      cast.Caster = ReplacePlayer(cast.Caster, out bool replaced);
      AddAction(AllSpellCastBlocks, cast, beginTime);

      string abbrv = Helpers.AbbreviateSpellName(cast.Spell);
      if (abbrv != null)
      {
        AllUniqueSpellCasts[abbrv] = 1;
      }

      UpdatePlayerClassFromSpell(cast);
    }

    public void AddReceivedSpell(ReceivedSpell received, double beginTime)
    {
      received.Receiver = ReplacePlayer(received.Receiver, out bool replaced);
      AddAction(AllReceivedSpellBlocks, received, beginTime);
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

    public void SetServerName(string server)
    {
      ServerName = server;
    }

    public string ReplacePlayer(string name, out bool replaced)
    {
      replaced = false;
      string result = name;

      if (PlayerReplacement.TryGetValue(name, out string found))
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

    public List<ActionBlock> GetCastsDuring(double beginTime, double endTime)
    {
      return SearchActions(AllSpellCastBlocks, beginTime, endTime);
    }

    public List<ActionBlock> GetDamageDuring(double beginTime, double endTime)
    {
      return SearchActions(AllDamageBlocks, beginTime, endTime);
    }

    public List<ActionBlock> GetHealsDuring(double beginTime, double endTime)
    {
      return SearchActions(AllHealBlocks, beginTime, endTime);
    }

    public List<ActionBlock> GetResistsDuring(double beginTime, double endTime)
    {
      return SearchActions(AllResists, beginTime, endTime);
    }

    public List<ActionBlock> GetReceivedSpellsDuring(double beginTime, double endTime)
    {
      return SearchActions(AllReceivedSpellBlocks, beginTime, endTime);
    }

    public List<ActionBlock> GetPlayerDeathsDuring(double beginTime, double endTime)
    {
      return SearchActions(PlayerDeaths, beginTime, endTime);
    }

    public SpellData GetSpellByAbbrv(string abbrv)
    {
      SpellData result = null;
      if (abbrv.Length > 0 && abbrv != Labels.UNKNOWN_SPELL && SpellsAbbrvDB.ContainsKey(abbrv))
      {
        result = SpellsAbbrvDB[abbrv];
      }
      return result;
    }

    public SpellData GetSpellByName(string name)
    {
      SpellData result = null;
      if (name.Length > 0 && name != Labels.UNKNOWN_SPELL && SpellsNameDB.ContainsKey(name))
      {
        result = SpellsNameDB[name];
      }
      return result;
    }

    public NonPlayer GetNonPlayer(string name)
    {
      ActiveNonPlayerMap.TryGetValue(name, out NonPlayer npc);
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
      if (PlayerToClass.TryGetValue(name, out SpellClassCounter counter))
      {
        className = ClassNames[counter.CurrentClass];
      }
      return className;
    }

    public string GetPlayerFromPet(string pet)
    {
      PetToPlayerMap.TryGetValue(pet, out string player);
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
        if (!AllUniqueSpellsCache.TryGetValue(value, out result))
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
          AllUniqueSpellsCache[value] = result;
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
      return ActiveNonPlayerMap.TryRemove(name, out NonPlayer npc);
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
      if (SpellsToClass.TryGetValue(cast.Spell, out SpellClasses theClass))
      {
        if (!PlayerToClass.TryGetValue(cast.Caster, out SpellClassCounter counter))
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
      if (!IsProbablyNotAPlayer(name) && UnVerifiedPetOrPlayer.TryAdd(name, 1))
      {
        // only need to check first time added
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

    private void CheckNolongerNPC(string name)
    {
      // remove from NPC map if it exists
      CheckNonPlayerMap(name);

      // remove from ProbablyNotAPlayer if it exists
      ProbablyNotAPlayer.TryRemove(name, out long value);

      UnVerifiedPetOrPlayer.TryRemove(name, out byte bvalue);
    }

    private void CheckNonPlayerMap(string name)
    {
      bool removed = ActiveNonPlayerMap.TryRemove(name, out NonPlayer npc);
      removed = LifetimeNonPlayerMap.TryRemove(name, out byte bnpc) || removed;

      if (removed)
      {
        EventsRemovedNonPlayer(this, name);
      }
    }

    private List<ActionBlock> SearchActions(List<ActionBlock> allActions, double beginTime, double endTime)
    {
      ActionBlock startBlock = new ActionBlock() { BeginTime = beginTime - 1 };
      ActionBlock endBlock = new ActionBlock() { BeginTime = endTime + 1 };

      int startIndex = allActions.BinarySearch(startBlock, Helpers.TimedActionComparer);
      if (startIndex < 0)
      {
        startIndex = Math.Abs(startIndex) - 1;
      }

      int endIndex = allActions.BinarySearch(endBlock, Helpers.TimedActionComparer);
      if (endIndex < 0)
      {
        endIndex = Math.Abs(endIndex) - 1;
      }

      int last = endIndex - startIndex;
      return last > 0 ? allActions.GetRange(startIndex, last) : new List<ActionBlock>();
    }

    private void AddAction(List<ActionBlock> blockList, IAction action, double beginTime)
    {
      if (blockList.LastOrDefault() is ActionBlock last && last.BeginTime == beginTime)
      {
        last.Actions.Add(action);
      }
      else
      {
        var newSegment = new ActionBlock() { Actions = new List<IAction>(), BeginTime = beginTime };
        newSegment.Actions.Add(action);
        blockList.Add(newSegment);
      }
    }

    private class SpellClassCounter
    {
      public int CurrentMax { get; set; }
      public SpellClasses CurrentClass { get; set; }
      public ConcurrentDictionary<SpellClasses, int> ClassCounts { get; set; }
    }
  }
}