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

    public enum SpellClasses
    {
      WAR = 1, CLR = 2, PAL = 4, RNG = 8, SHD = 16, DRU = 32, MNK = 64, BRD = 128, ROG = 256,
      SHM = 512, NEC = 1024, WIZ = 2048, MAG = 4096, ENC = 8192, BST = 16384, BER = 32768
    }

    public static DataManager Instance = new DataManager();
    public const string UNASSIGNED_PET_OWNER = "Unassigned Pets";
    public event EventHandler<PetMapping> EventsNewPetMapping;
    public event EventHandler<string> EventsNewVerifiedPet;
    public event EventHandler<string> EventsNewVerifiedPlayer;
    public event EventHandler<string> EventsRemovedNonPlayer;
    public event EventHandler<string> EventsNewUnverifiedPetOrPlayer;
    public event EventHandler<NonPlayer> EventsNewNonPlayer;
    public event EventHandler<NonPlayer> EventsUpdatedNonPlayer;
    public event EventHandler<bool> EventsClearedActiveData;

    private List<SpellCast> AllSpellCasts = new List<SpellCast>();
    private Dictionary<string, byte> AllUniqueSpellCasts = new Dictionary<string, byte>();
    private LRUCache<string> AllUniqueSpellsLRU = new LRUCache<string>(2000, 500, false);
    private List<ReceivedSpell> AllReceivedSpells = new List<ReceivedSpell>();
    private Dictionary<string, List<string>> PosessiveLandsOnOthers = new Dictionary<string, List<string>>();
    private Dictionary<string, List<string>> NonPosessiveLandsOnOthers = new Dictionary<string, List<string>>();
    private Dictionary<string, List<string>> LandsOnYou = new Dictionary<string, List<string>>();
    private Dictionary<SpellClasses, string> ClassNames = new Dictionary<SpellClasses, string>();
    private Dictionary<string, SpellData> SpellsDB = new Dictionary<string, SpellData>();
    private Dictionary<string, SpellClasses> SpellsToClass = new Dictionary<string, SpellClasses>();

    private ConcurrentDictionary<string, NonPlayer> ActiveNonPlayerMap = new ConcurrentDictionary<string, NonPlayer>();
    private ConcurrentDictionary<string, string> AttackerReplacement = new ConcurrentDictionary<string, string>();
    private ConcurrentDictionary<string, byte> GameGeneratedPets = new ConcurrentDictionary<string, byte>();
    private ConcurrentDictionary<string, byte> LifetimeNonPlayerMap = new ConcurrentDictionary<string, byte>();
    private ConcurrentDictionary<string, string> PetToPlayerMap = new ConcurrentDictionary<string, string>();
    private ConcurrentDictionary<string, SpellClassCounter> PlayerToClass = new ConcurrentDictionary<string, SpellClassCounter>();
    private ConcurrentDictionary<string, long> ProbablyNotAPlayer = new ConcurrentDictionary<string, long>();
    private ConcurrentDictionary<string, byte> UnverifiedPetOrPlayer = new ConcurrentDictionary<string, byte>();
    private ConcurrentDictionary<string, byte> VerifiedPets = new ConcurrentDictionary<string, byte>();
    private ConcurrentDictionary<string, byte> VerifiedPlayers = new ConcurrentDictionary<string, byte>();

    private DataManager()
    {
      try
      {
        // Populated generated pets
        string[] lines = System.IO.File.ReadAllLines(@"data\petnames.txt");
        lines.ToList().ForEach(line => GameGeneratedPets[line.TrimEnd()] = 1);

        VerifiedPlayers["himself"] = 1;
        VerifiedPlayers["herself"] = 1;
        VerifiedPlayers["itself"] = 1;
        VerifiedPlayers["you"] = 1;
        VerifiedPlayers["YOU"] = 1;
        VerifiedPlayers["You"] = 1;
        VerifiedPlayers["your"] = 1;
        VerifiedPlayers["Your"] = 1;
        VerifiedPlayers["YOUR"] = 1;

        DictionaryListHelper<string, string> helper = new DictionaryListHelper<string, string>();
        lines = System.IO.File.ReadAllLines(@"data\spells.txt");

        foreach (string line in lines)
        {
          string[] data = line.Split('^');
          int beneficial;
          Int32.TryParse(data[1], out beneficial);
          int classMask;
          Int32.TryParse(data[2], out classMask);
          SpellData spellData = new SpellData()
          {
            Spell = data[0],
            SpellAbbrv = Helpers.AbbreviateSpellName(data[0]),
            Beneficial = (beneficial != 0),
            ClassMask = classMask,
            LandsOnYou = data[3],
            LandsOnOther = data[4]
          };

          SpellsDB[spellData.Spell] = spellData;

          if (spellData.LandsOnOther.StartsWith("'s "))
          {
            helper.AddToList(PosessiveLandsOnOthers, spellData.LandsOnOther.Substring(3), spellData.SpellAbbrv);
          }
          else if (spellData.LandsOnOther.Length > 1)
          {
            helper.AddToList(NonPosessiveLandsOnOthers, spellData.LandsOnOther.Substring(1), spellData.SpellAbbrv);
          }

          if (spellData.LandsOnYou != "" && spellData.LandsOnOther != "") // just do stuff in common
          {
            helper.AddToList(LandsOnYou, spellData.LandsOnYou, spellData.SpellAbbrv);
          }
        }
      }
      catch(Exception e)
      {
        LOG.Error(e);
      }

      var classEnums = Enum.GetValues(typeof(SpellClasses)).Cast<SpellClasses>().ToList();
      foreach(var spell in SpellsDB.Values)
      {
        // exact match meaning class-only spell
        if (classEnums.Contains((SpellClasses) spell.ClassMask))
        {
          SpellsToClass[spell.Spell] = (SpellClasses) spell.ClassMask;
        }
      }

      ClassNames[SpellClasses.WAR] = "Warrior";
      ClassNames[SpellClasses.CLR] = "Cleric";
      ClassNames[SpellClasses.PAL] = "Paladin";
      ClassNames[SpellClasses.RNG] = "Ranger";
      ClassNames[SpellClasses.SHD] = "Shadow Knight";
      ClassNames[SpellClasses.DRU] = "Druid";
      ClassNames[SpellClasses.MNK] = "Monk";
      ClassNames[SpellClasses.BRD] = "Bard";
      ClassNames[SpellClasses.ROG] = "Rogue";
      ClassNames[SpellClasses.SHM] = "Shaman";
      ClassNames[SpellClasses.NEC] = "Necromancer";
      ClassNames[SpellClasses.WIZ] = "Wizard";
      ClassNames[SpellClasses.MAG] = "Magician";
      ClassNames[SpellClasses.ENC] = "Enchanter";
      ClassNames[SpellClasses.BST] = "Beastlord";
      ClassNames[SpellClasses.BER] = "Berserker";
    }

    public void Clear()
    {
      ActiveNonPlayerMap.Clear();
      LifetimeNonPlayerMap.Clear();
      ProbablyNotAPlayer.Clear();
      UnverifiedPetOrPlayer.Clear();
      AllSpellCasts.Clear();
      EventsClearedActiveData(this, true);
    }

    public void AddNonPlayerMapBreak(string text)
    {
      NonPlayer divider = new NonPlayer() { FightID = -1, BeginTimeString = NonPlayer.BREAK_TIME, Name = text };
      EventsNewNonPlayer(this, divider);
    }

    public void AddSpellCast(SpellCast cast)
    {
      bool replaced;
      cast.Caster = ReplaceAttacker(cast.Caster, out replaced);

      lock (AllSpellCasts)
      {
        AllSpellCasts.Add(cast);
        AllUniqueSpellCasts[cast.SpellAbbrv] = 1;
      }

      UpdatePlayerClassFromSpell(cast);
    }

    public void AddReceivedSpell(ReceivedSpell received)
    {
      bool replaced;
      received.Receiver = ReplaceAttacker(received.Receiver, out replaced);

      lock (AllReceivedSpells)
      {
        AllReceivedSpells.Add(received);
      }
    }

    public void SetPlayerName(string name)
    {
      AttackerReplacement["you"] = name;
      AttackerReplacement["You"] = name;
      AttackerReplacement["YOU"] = name;
      AttackerReplacement["your"] = name;
      AttackerReplacement["Your"] = name;
      AttackerReplacement["YOUR"] = name;
    }

    public string ReplaceAttacker(string attacker, out bool replaced)
    {
      replaced = false;
      string result = attacker;

      string found;
      if (AttackerReplacement.TryGetValue(attacker, out found))
      {
        replaced = true;
        result = found;
      }

      return result;
    }

    public bool CheckNameForUnverifiedPetOrPlayer(string name)
    {
      return UnverifiedPetOrPlayer.ContainsKey(name);
    }

    public bool CheckNameForPet(string name)
    {
      bool isPet = false;

      if (GameGeneratedPets.ContainsKey(name))
      {
        UpdateVerifiedPets(name);
        isPet = true;

        UpdatePetToPlayer(name, UNASSIGNED_PET_OWNER);
      }
      else
      {
        isPet = VerifiedPets.ContainsKey(name);
      }

      return isPet;
    }

    public bool CheckNameForPlayer(string name)
    {
      return VerifiedPlayers.ContainsKey(name);
    }

    public List<SpellCast> GetSpellCasts()
    {
      List<SpellCast> list;
      lock (AllSpellCasts)
      {
        list = AllSpellCasts.ToList();
      }
      return list;
    }

    public List<ReceivedSpell> GetAllReceivedSpells()
    {
      List<ReceivedSpell> list;
      lock (AllReceivedSpells)
      {
        list = AllReceivedSpells.ToList();
      }
      return list;
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
      return PetToPlayerMap.ContainsKey(pet) ? PetToPlayerMap[pet] : null;
    }

    public string GetNonPosessiveLandsOnOther(string value)
    {
      string result = null;
      List<string> output;
      if (NonPosessiveLandsOnOthers.TryGetValue(value, out output))
      {
        result = FindByLandsOn(value, output);
      }
      return result;
    }

    public string GetPosessiveLandsOnOther(string value)
    {
      string result = null;
      List<string> output;
      if (PosessiveLandsOnOthers.TryGetValue(value, out output))
      {
        result = FindByLandsOn(value, output);
      }
      return result;
    }

    public string GetLandsOnYou(string value)
    {
      string result = null;
      List<string> output;
      if (LandsOnYou.TryGetValue(value, out output))
      {
        result = FindByLandsOn(value, output);
      }
      return result;
    }

    private string FindByLandsOn(string value, List<string> output)
    {
      string result = null;
      if (output.Count == 1)
      {
        result = output[0];
      }
      else if (output.Count > 1)
      {
        if (!AllUniqueSpellsLRU.TryGet(value, out result))
        {
          result = output.Find(name => AllUniqueSpellCasts.ContainsKey(name));
          AllUniqueSpellsLRU.AddReplace(value, result);
        }
      }
      return result;
    }

    public bool IsProbablyNotAPlayer(string name)
    {
      bool probably = false;

      if (!VerifiedPlayers.ContainsKey(name) && !VerifiedPets.ContainsKey(name) && !GameGeneratedPets.ContainsKey(name) 
        && !UnverifiedPetOrPlayer.ContainsKey(name) && ProbablyNotAPlayer.ContainsKey(name))
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
      else
      {
        EventsUpdatedNonPlayer(this, npc);
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

    public void UpdateUnverifiedPetOrPlayer(string name, bool alreadyChecked = false)
    {
      if (alreadyChecked || (!CheckNameForPet(name) && !CheckNameForPlayer(name)))
      {
        UnverifiedPetOrPlayer[name] = 1;
        EventsNewUnverifiedPetOrPlayer(this, name);
      }
    }

    public void UpdatePetToPlayer(string pet, string player)
    {
      if (!PetToPlayerMap.ContainsKey(pet))
      {
        PetToPlayerMap[pet] = player;
        EventsNewPetMapping(this, new PetMapping() { Pet = pet, Owner = player });
      }
    }

    public void UpdateProbablyNotAPlayer(string name)
    {
      long value = 0;
      if (ProbablyNotAPlayer.ContainsKey(name))
      {
        value = ProbablyNotAPlayer[name];
      }

      ProbablyNotAPlayer[name] = ++value;
    }

    public void UpdateVerifiedPets(string name)
    {
      if (!VerifiedPets.ContainsKey(name))
      {
        VerifiedPets[name] = 1;
        EventsNewVerifiedPet(this, name);
        CheckNonPlayerMap(name);
      }
    }

    public void UpdateVerifiedPlayers(string name)
    {
      if (!VerifiedPlayers.ContainsKey(name))
      {
        VerifiedPlayers[name] = 1;
        EventsNewVerifiedPlayer(this, name);
        CheckNonPlayerMap(name);
      }
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

    private class SpellClassCounter
    {
      public int CurrentMax { get; set; }
      public SpellClasses CurrentClass { get; set; }
      public ConcurrentDictionary<SpellClasses, int> ClassCounts { get; set; }
    }
  }
}