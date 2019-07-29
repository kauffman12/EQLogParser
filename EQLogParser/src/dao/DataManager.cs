using System;
using System.Collections;
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
    private static readonly SpellAbbrvComparer AbbrvComparer = new SpellAbbrvComparer();

    private bool PetMappingUpdated = false;
    private bool SettingsUpdated = false;

    private readonly List<ActionBlock> PlayerAttackDamageBlocks = new List<ActionBlock>();
    private readonly List<ActionBlock> PlayerDefendDamageBlocks = new List<ActionBlock>();
    private readonly List<ActionBlock> AllHealBlocks = new List<ActionBlock>();
    private readonly List<ActionBlock> AllSpellCastBlocks = new List<ActionBlock>();
    private readonly List<ActionBlock> AllReceivedSpellBlocks = new List<ActionBlock>();
    private readonly List<ActionBlock> AllResists = new List<ActionBlock>();
    private readonly List<ActionBlock> PlayerDeaths = new List<ActionBlock>();

    private readonly Dictionary<string, SpellData> AllUniqueSpellsCache = new Dictionary<string, SpellData>();
    private readonly Dictionary<string, List<SpellData>> PosessiveLandsOnOthers = new Dictionary<string, List<SpellData>>();
    private readonly Dictionary<string, List<SpellData>> NonPosessiveLandsOnOthers = new Dictionary<string, List<SpellData>>();
    private readonly Dictionary<string, List<SpellData>> LandsOnYou = new Dictionary<string, List<SpellData>>();
    private readonly Dictionary<SpellClass, string> ClassNames = new Dictionary<SpellClass, string>();
    private readonly Dictionary<string, SpellData> SpellsNameDB = new Dictionary<string, SpellData>();
    private readonly Dictionary<string, SpellData> SpellsAbbrvDB = new Dictionary<string, SpellData>();
    private readonly Dictionary<string, SpellClass> SpellsToClass = new Dictionary<string, SpellClass>();
    private readonly Dictionary<string, byte> GameGeneratedPets = new Dictionary<string, byte>();
    private readonly Dictionary<string, string> PlayerReplacement = new Dictionary<string, string>();

    private readonly ConcurrentDictionary<string, string> ApplicationSettings = new ConcurrentDictionary<string, string>();
    private readonly ConcurrentDictionary<string, NonPlayer> ActiveNonPlayerMap = new ConcurrentDictionary<string, NonPlayer>();
    private readonly ConcurrentDictionary<string, byte> AllUniqueSpellCasts = new ConcurrentDictionary<string, byte>();
    private readonly ConcurrentDictionary<string, byte> LifetimeNonPlayerMap = new ConcurrentDictionary<string, byte>();
    private readonly ConcurrentDictionary<string, string> PetToPlayerMap = new ConcurrentDictionary<string, string>();
    private readonly ConcurrentDictionary<string, SpellClassCounter> PlayerToClass = new ConcurrentDictionary<string, SpellClassCounter>();
    private readonly ConcurrentDictionary<string, byte> DefinitelyNotAPlayer = new ConcurrentDictionary<string, byte>();
    private readonly ConcurrentDictionary<string, long> ProbablyNotAPlayer = new ConcurrentDictionary<string, long>();
    private readonly ConcurrentDictionary<string, byte> UnVerifiedPetOrPlayer = new ConcurrentDictionary<string, byte>();
    private readonly ConcurrentDictionary<string, byte> VerifiedPets = new ConcurrentDictionary<string, byte>();
    private readonly ConcurrentDictionary<string, byte> VerifiedPlayers = new ConcurrentDictionary<string, byte>();
    private readonly ConcurrentDictionary<string, byte> OtherSelves = new ConcurrentDictionary<string, byte>();

    private DataManager()
    {
      // static data first
      VerifiedPlayers["himself"] = 1;
      VerifiedPlayers["herself"] = 1;
      VerifiedPlayers["itself"] = 1;
      VerifiedPlayers["yourself"] = 1;
      VerifiedPlayers["you"] = 1;
      VerifiedPlayers["YOU"] = 1;
      VerifiedPlayers["You"] = 1;
      VerifiedPlayers["your"] = 1;
      VerifiedPlayers["Your"] = 1;
      VerifiedPlayers["YOUR"] = 1;
      OtherSelves["himself"] = 1;
      OtherSelves["herself"] = 1;
      OtherSelves["itself"] = 1;
      OtherSelves["Himself"] = 1;
      OtherSelves["Herself"] = 1;
      OtherSelves["Itself"] = 1;
      DefinitelyNotAPlayer["Unknown"] = 1;
      DefinitelyNotAPlayer["unknown"] = 1;
      ClassNames[SpellClass.WAR] = string.Intern("Warrior");
      ClassNames[SpellClass.CLR] = string.Intern("Cleric");
      ClassNames[SpellClass.PAL] = string.Intern("Paladin");
      ClassNames[SpellClass.RNG] = string.Intern("Ranger");
      ClassNames[SpellClass.SHD] = string.Intern("Shadow Knight");
      ClassNames[SpellClass.DRU] = string.Intern("Druid");
      ClassNames[SpellClass.MNK] = string.Intern("Monk");
      ClassNames[SpellClass.BRD] = string.Intern("Bard");
      ClassNames[SpellClass.ROG] = string.Intern("Rogue");
      ClassNames[SpellClass.SHM] = string.Intern("Shaman");
      ClassNames[SpellClass.NEC] = string.Intern("Necromancer");
      ClassNames[SpellClass.WIZ] = string.Intern("Wizard");
      ClassNames[SpellClass.MAG] = string.Intern("Magician");
      ClassNames[SpellClass.ENC] = string.Intern("Enchanter");
      ClassNames[SpellClass.BST] = string.Intern("Beastlord");
      ClassNames[SpellClass.BER] = string.Intern("Berserker");

      // needs to be during class initialization for some reason
      CONFIG_DIR = Environment.ExpandEnvironmentVariables(@"%AppData%\EQLogParser\config");
      ARCHIVE_DIR = Environment.ExpandEnvironmentVariables(@"%AppData%\EQLogParser\archive\");
      PETMAP_FILE = CONFIG_DIR + @"\petmapping.txt";
      SETTINGS_FILE = CONFIG_DIR + @"\settings.txt";

      // create config dir if it doesn't exist
      Directory.CreateDirectory(CONFIG_DIR);

      Helpers.ReadList(SETTINGS_FILE).ForEach(line =>
      {
        string[] parts = line.Split('=');
        if (parts != null && parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
        {
          ApplicationSettings[parts[0]] = parts[1];
        }
      });

      // Populate generated pets
      Helpers.ReadList(@"data\petnames.txt").ForEach(line => GameGeneratedPets[line.TrimEnd()] = 1);

      DictionaryListHelper<string, SpellData> helper = new DictionaryListHelper<string, SpellData>();
      var spellList = new List<SpellData>();
      Helpers.ReadList(@"data\spells.txt").ForEach(line =>
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

        spellList.Add(spellData);
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

        if (spellData.LandsOnYou.Length > 0) // just do stuff in common
        {
          helper.AddToList(LandsOnYou, spellData.LandsOnYou, spellData);
        }
      });

      Dictionary<string, byte> keepOut = new Dictionary<string, byte>();
      var classEnums = Enum.GetValues(typeof(SpellClass)).Cast<SpellClass>().ToList();
      spellList.ForEach(spell =>
      {
        // exact match meaning class-only spell
        if (classEnums.Contains((SpellClass)spell.ClassMask))
        {
          // these need to be unique and keep track if a conflict is found
          if (SpellsToClass.ContainsKey(spell.Spell))
          {
            SpellsToClass.Remove(spell.Spell);
            keepOut[spell.Spell] = 1;
          }
          else if (!keepOut.ContainsKey(spell.Spell))
          {
            SpellsToClass[spell.Spell] = (SpellClass)spell.ClassMask;
          }
        }
      });
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
      AllResists.Clear();
      PlayerAttackDamageBlocks.Clear();
      PlayerDefendDamageBlocks.Clear();
      AllHealBlocks.Clear();
      PlayerDeaths.Clear();
      EventsClearedActiveData(this, true);
    }

    public void LoadState()
    {
      lock (this)
      {
        // Pet settings
        UpdateVerifiedPlayers(Labels.UNASSIGNED);
        Helpers.ReadList(PETMAP_FILE).ForEach(line =>
        {
          string[] parts = line.Split('=');
          if (parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
          {
            UpdatePetToPlayer(parts[0], parts[1]);
            UpdateVerifiedPlayers(parts[1]);
            UpdateVerifiedPets(parts[0]);
          }
        });

        // dont count initial load
        PetMappingUpdated = false;
      }
    }

    public void SaveState()
    {
      lock (this)
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
        if (ApplicationSettings.TryRemove(key, out _))
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
        AddAction(PlayerAttackDamageBlocks, record, beginTime);
      }
      else
      {
        // ReplacePlayer is done in the line parser already
        AddAction(PlayerDefendDamageBlocks, record, beginTime);
      }
    }

    public void AddResistRecord(ResistRecord record, double beginTime)
    {
      // Resists are only seen by current player
      AddAction(AllResists, record, beginTime);
    }

    public void AddHealRecord(HealRecord record, double beginTime)
    {
      record.Healer = ReplacePlayer(record.Healer, record.Healed, out _);
      record.Healed = ReplacePlayer(record.Healed, record.Healer, out _);

      AddAction(AllHealBlocks, record, beginTime);

      // can use heals to determine additional players
      bool foundHealer = CheckNameForPlayer(record.Healer);
      bool foundHealed = CheckNameForPlayer(record.Healed) || CheckNameForPet(record.Healed);

      if (!foundHealer && foundHealed && Helpers.IsPossiblePlayerName(record.Healer, record.Healer.Length))
      {
        UpdateVerifiedPlayers(record.Healer);
      }
    }

    public void HandleSpellInterrupt(string player, string spell, double beginTime)
    {
      player = ReplacePlayer(player, player, out _);

      for (int i = AllSpellCastBlocks.Count - 1; i >= 0 && beginTime - AllSpellCastBlocks[i].BeginTime <= 5; i--)
      {
        int index = AllSpellCastBlocks[i].Actions.FindLastIndex(action => ((SpellCast)action).Spell == spell && ((SpellCast)action).Caster == player);
        if (index > -1)
        {
          AllSpellCastBlocks[i].Actions.RemoveAt(index);
          break;
        }
      }
    }

    public void AddSpellCast(SpellCast cast, double beginTime)
    {
      if (SpellsNameDB.ContainsKey(cast.Spell))
      {
        cast.Caster = ReplacePlayer(cast.Caster, cast.Receiver, out _);
        AddAction(AllSpellCastBlocks, cast, beginTime);

        string abbrv = Helpers.AbbreviateSpellName(cast.Spell);
        if (abbrv != null)
        {
          AllUniqueSpellCasts[abbrv] = 1;
        }

        UpdatePlayerClassFromSpell(cast);
      }
    }

    public void AddReceivedSpell(ReceivedSpell received, double beginTime)
    {
      received.Receiver = ReplacePlayer(received.Receiver, received.Receiver, out _);
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
      PlayerReplacement["yourself"] = name;
      PlayerReplacement["Yourself"] = name;
      UpdateVerifiedPlayers(name);
      PlayerName = name;
    }

    public void SetServerName(string server)
    {
      ServerName = server;
    }

    public string ReplacePlayer(string name, string alternative, out bool replaced)
    {
      replaced = false;
      string result = name;

      if (OtherSelves.ContainsKey(name))
      {
        result = alternative;
      }
      else
      {
        if (PlayerReplacement.TryGetValue(name, out string found))
        {
          replaced = true;
          result = found;
        }
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
          UpdatePetToPlayer(name, Labels.UNASSIGNED);
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

    public List<ActionBlock> GetAttackDamageDuring(double beginTime, double endTime)
    {
      return SearchActions(PlayerAttackDamageBlocks, beginTime, endTime);
    }

    public List<ActionBlock> GetDefendDamageDuring(double beginTime, double endTime)
    {
      return SearchActions(PlayerDefendDamageBlocks, beginTime, endTime);
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
      if (abbrv.Length > 0 && abbrv != Labels.UNKSPELL && SpellsAbbrvDB.ContainsKey(abbrv))
      {
        result = SpellsAbbrvDB[abbrv];
      }
      return result;
    }

    public SpellData GetSpellByName(string name)
    {
      SpellData result = null;
      if (name.Length > 0 && name != Labels.UNKSPELL && SpellsNameDB.ContainsKey(name))
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

    public string GetClassName(SpellClass type)
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
            var distinct = output.Distinct(AbbrvComparer).ToList();
            if (distinct.Count == 1)
            {
              result = distinct.Last();
            }
            else
            {
              // see if the spells look similar and pick one
              bool found = true;
              var data = distinct.First();
              foreach (var spell in distinct.Skip(1))
              {
                if (spell.Beneficial != data.Beneficial || spell.ClassMask != data.ClassMask || spell.Target != data.Target ||
                  spell.Damaging != data.Damaging)
                {
                  found = false;
                  break;
                }
              }

              if (found)
              {
                result = distinct.Last();
              }
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
      return ActiveNonPlayerMap.TryRemove(name, out _);
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
      if (SpellsToClass.TryGetValue(cast.Spell, out SpellClass theClass))
      {
        if (!PlayerToClass.TryGetValue(cast.Caster, out SpellClassCounter counter))
        {
          PlayerToClass.TryAdd(cast.Caster, new SpellClassCounter() { ClassCounts = new ConcurrentDictionary<SpellClass, int>() });
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

    public bool UpdateProbablyNotAPlayer(string name, bool addIfMissing = true)
    {
      bool updated = false;
      if (!VerifiedPlayers.ContainsKey(name) && !VerifiedPets.ContainsKey(name) && !GameGeneratedPets.ContainsKey(name) &&
        !UnVerifiedPetOrPlayer.ContainsKey(name))
      {
        if (!DefinitelyNotAPlayer.ContainsKey(name) && Helpers.IsPossiblePlayerName(name))
        {
          if (addIfMissing)
          {
            long value = 0;
            if (ProbablyNotAPlayer.ContainsKey(name))
            {
              value = ProbablyNotAPlayer[name];
            }

            ProbablyNotAPlayer[name] = ++value;
            updated = true;
          }
          else
          {
            updated = ProbablyNotAPlayer.ContainsKey(name);
          }
        }
        else
        {
          DefinitelyNotAPlayer[name] = 1;
          updated = true;
        }
      }
      return updated;
    }

    public void UpdateUnVerifiedPetOrPlayer(string name)
    {
      // avoid checking to remove unless needed
      if (!IsProbablyNotAPlayer(name) && UnVerifiedPetOrPlayer.TryAdd(name, 1))
      {
        // only need to check first time added
        CheckNonPlayerMap(name);
      }
    }

    public void UpdateVerifiedPets(string name)
    {
      if (VerifiedPets.TryAdd(name, 1))
      {
        UnVerifiedPetOrPlayer.TryRemove(name, out _);
        ProbablyNotAPlayer.TryRemove(name, out _);

        EventsNewVerifiedPet(this, name);
        CheckNonPlayerMap(name);
      }
    }

    public void UpdateVerifiedPlayers(string name)
    {
      if (VerifiedPlayers.TryAdd(name, 1))
      {
        UnVerifiedPetOrPlayer.TryRemove(name, out _);
        ProbablyNotAPlayer.TryRemove(name, out _);

        EventsNewVerifiedPlayer(this, name);
        CheckNonPlayerMap(name);
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
            if (keypair.Value != Labels.UNASSIGNED)
            {
              lines.Add(keypair.Key + "=" + keypair.Value);
            }
          }

          File.WriteAllLines(fileName, lines);
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
      public SpellClass CurrentClass { get; set; }
      public ConcurrentDictionary<SpellClass, int> ClassCounts { get; set; }
    }

    private class SpellAbbrvComparer : IEqualityComparer<SpellData>
    {
      public bool Equals(SpellData x, SpellData y)
      {
        return x.SpellAbbrv == y.SpellAbbrv;
      }

      public int GetHashCode(SpellData obj)
      {
        return obj.SpellAbbrv.GetHashCode();
      }
    }
  }
}