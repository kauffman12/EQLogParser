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
    public const string UNASSIGNED_PET_OWNER = "Unknown Pet Owner";
    public event EventHandler<PetMapping> EventsUpdatePetMapping;
    public event EventHandler<string> EventsNewVerifiedPet;
    public event EventHandler<string> EventsNewVerifiedPlayer;
    public event EventHandler<string> EventsRemovedNonPlayer;
    public event EventHandler<NonPlayer> EventsNewNonPlayer;
    public event EventHandler<NonPlayer> EventsUpdatedNonPlayer;
    public event EventHandler<bool> EventsClearedActiveData;

    private List<SpellCast> AllSpellCasts = new List<SpellCast>();
    private Dictionary<string, byte> AllUniqueSpellCasts = new Dictionary<string, byte>();
    private LRUCache<SpellData> AllUniqueSpellsLRU = new LRUCache<SpellData>(2000, 500, false);
    private List<ReceivedSpell> AllReceivedSpells = new List<ReceivedSpell>();
    private Dictionary<string, List<SpellData>> PosessiveLandsOnOthers = new Dictionary<string, List<SpellData>>();
    private Dictionary<string, List<SpellData>> NonPosessiveLandsOnOthers = new Dictionary<string, List<SpellData>>();
    private Dictionary<string, List<SpellData>> LandsOnYou = new Dictionary<string, List<SpellData>>();
    private Dictionary<SpellClasses, string> ClassNames = new Dictionary<SpellClasses, string>();
    private Dictionary<string, SpellData> SpellsDB = new Dictionary<string, SpellData>();
    private Dictionary<string, SpellData> SpellsAbbrvDB = new Dictionary<string, SpellData>();
    private Dictionary<string, SpellClasses> SpellsToClass = new Dictionary<string, SpellClasses>();

    private ConcurrentDictionary<string, NonPlayer> ActiveNonPlayerMap = new ConcurrentDictionary<string, NonPlayer>();
    private ConcurrentDictionary<string, string> AttackerReplacement = new ConcurrentDictionary<string, string>();
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

        DictionaryListHelper<string, SpellData> helper = new DictionaryListHelper<string, SpellData>();
        lines = System.IO.File.ReadAllLines(@"data\spells.txt");

        foreach (string line in lines)
        {
          string[] data = line.Split('^');
          int beneficial;
          Int32.TryParse(data[2], out beneficial);
          int classMask;
          Int32.TryParse(data[3], out classMask);
          SpellData spellData = new SpellData()
          {
            ID = data[0],
            Spell = data[1],
            SpellAbbrv = Helpers.AbbreviateSpellName(data[1]),
            Beneficial = (beneficial != 0),
            ClassMask = classMask,
            LandsOnYou = data[4],
            LandsOnOther = data[5]
          };

          SpellsDB[spellData.ID] = spellData;
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
      catch (Exception e)
      {
        LOG.Error(e);
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
            SpellsToClass[spell.Spell] = (SpellClasses)spell.ClassMask;
          }
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
      DefinitelyNotAPlayer.Clear();
      AllSpellCasts.Clear();
      AllUniqueSpellCasts.Clear();
      AllUniqueSpellsLRU.Clear();
      AllReceivedSpells.Clear();
      EventsClearedActiveData(this, true);
    }

    public void AddNonPlayerMapBreak(string text)
    {
      NonPlayer divider = new NonPlayer() { GroupID = -1, BeginTimeString = NonPlayer.BREAK_TIME, Name = text };
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
      UpdateVerifiedPlayers(name);
      UpdateVerifiedPlayers(UNASSIGNED_PET_OWNER);
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

    public bool CheckNameForPet(string name)
    {
      bool isPet = VerifiedPets.ContainsKey(name);

      if (!isPet && GameGeneratedPets.ContainsKey(name))
      {
        UpdateVerifiedPets(name);
        isPet = true;

        if (!PetToPlayerMap.ContainsKey(name))
        {
          UpdatePetToPlayer(name, UNASSIGNED_PET_OWNER);
        }
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
    
    public SpellData GetSpellByAbbrv(string abbrv)
    {
      SpellData result = null;
      if (SpellsAbbrvDB.ContainsKey(abbrv))
      {
        result = SpellsAbbrvDB[abbrv];
      }
      return result;
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

    public SpellData GetNonPosessiveLandsOnOther(string value)
    {
      SpellData result = null;
      List<SpellData> output;
      if (NonPosessiveLandsOnOthers.TryGetValue(value, out output))
      {
        result = FindByLandsOn(value, output);
      }
      return result;
    }

    public SpellData GetPosessiveLandsOnOther(string value)
    {
      SpellData result = null;
      List<SpellData> output;
      if (PosessiveLandsOnOthers.TryGetValue(value, out output))
      {
        result = FindByLandsOn(value, output);
      }
      return result;
    }

    public SpellData GetLandsOnYou(string value)
    {
      SpellData result = null;
      List<SpellData> output;
      if (LandsOnYou.TryGetValue(value, out output))
      {
        result = FindByLandsOn(value, output);
      }
      return result;
    }

    private SpellData FindByLandsOn(string value, List<SpellData> output)
    {
      if (value.Contains("is strengthened by a gift of magic"))
      {
        if (true)
        {

        }
      }

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

    public void UpdatePetToPlayer(string pet, string player)
    {
      if (!PetToPlayerMap.ContainsKey(pet) || PetToPlayerMap[pet] != player)
      {
        PetToPlayerMap[pet] = player;
        EventsUpdatePetMapping(this, new PetMapping() { Pet = pet, Owner = player });
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
      if (!IsProbablyNotAPlayer(name))
      {
        UnVerifiedPetOrPlayer[name] = 1;
        CheckNolongerNPC(name);
      }
    }

    public void UpdateVerifiedPets(string name)
    {
      if (!VerifiedPets.ContainsKey(name))
      {
        VerifiedPets[name] = 1;
        EventsNewVerifiedPet(this, name);
        CheckNolongerNPC(name);
      }
    }

    public void UpdateVerifiedPlayers(string name)
    {
      if (!VerifiedPlayers.ContainsKey(name))
      {
        VerifiedPlayers[name] = 1;
        EventsNewVerifiedPlayer(this, name);
        CheckNolongerNPC(name);
      }
    }

    private void CheckNolongerNPC(string name)
    {
      // remove from NPC map if it exists
      CheckNonPlayerMap(name);

      // remove from ProbablyNotAPlayer if it exists
      long value;
      ProbablyNotAPlayer.TryRemove(name, out value);
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