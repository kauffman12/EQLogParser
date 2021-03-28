using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EQLogParser
{
  class PlayerManager
  {
    internal event EventHandler<PetMapping> EventsNewPetMapping;
    internal event EventHandler<string> EventsNewTakenPetOrPlayerAction;
    internal event EventHandler<string> EventsNewVerifiedPet;
    internal event EventHandler<string> EventsNewVerifiedPlayer;
    internal event EventHandler<string> EventsRemoveVerifiedPet;
    internal event EventHandler<string> EventsRemoveVerifiedPlayer;

    internal static PlayerManager Instance = new PlayerManager();

    // static data
    private readonly ConcurrentDictionary<SpellClass, string> ClassNames = new ConcurrentDictionary<SpellClass, string>();
    private readonly ConcurrentDictionary<string, byte> GameGeneratedPets = new ConcurrentDictionary<string, byte>();
    private readonly ConcurrentDictionary<string, byte> SecondPerson = new ConcurrentDictionary<string, byte>();
    private readonly ConcurrentDictionary<string, byte> ThirdPerson = new ConcurrentDictionary<string, byte>();
    private readonly ConcurrentDictionary<string, string> PetToPlayer = new ConcurrentDictionary<string, string>();
    private readonly ConcurrentDictionary<string, SpellClassCounter> PlayerToClass = new ConcurrentDictionary<string, SpellClassCounter>();
    private readonly ConcurrentDictionary<string, byte> TakenPetOrPlayerAction = new ConcurrentDictionary<string, byte>();
    private readonly ConcurrentDictionary<string, byte> VerifiedPets = new ConcurrentDictionary<string, byte>();
    private readonly ConcurrentDictionary<string, byte> VerifiedPlayers = new ConcurrentDictionary<string, byte>();
    private readonly ConcurrentDictionary<string, bool> PossiblePlayerCache = new ConcurrentDictionary<string, bool>();
    private readonly ConcurrentDictionary<string, byte> DoTClasses = new ConcurrentDictionary<string, byte>();

    private bool PetMappingUpdated = false;
    private bool PlayersUpdated = false;

    private PlayerManager()
    {
      AddMultiCase(new string[] { "you", "your", "yourself" }, SecondPerson);
      AddMultiCase(new string[] { "himself", "herself", "itself" }, ThirdPerson);

      // populate ClassNames from SpellClass enum and resource table
      foreach (var item in Enum.GetValues(typeof(SpellClass)))
      {
        ClassNames[(SpellClass)item] = Properties.Resources.ResourceManager.GetString(Enum.GetName(typeof(SpellClass), item), CultureInfo.CurrentCulture);
      }

      DoTClasses[ClassNames[SpellClass.BRD]] = 1;
      DoTClasses[ClassNames[SpellClass.BST]] = 1;
      DoTClasses[ClassNames[SpellClass.DRU]] = 1;
      DoTClasses[ClassNames[SpellClass.ENC]] = 1;
      DoTClasses[ClassNames[SpellClass.NEC]] = 1;
      DoTClasses[ClassNames[SpellClass.RNG]] = 1;
      DoTClasses[ClassNames[SpellClass.SHD]] = 1;
      DoTClasses[ClassNames[SpellClass.SHM]] = 1;

      // Populate generated pets
      ConfigUtil.ReadList(@"data\petnames.txt").ForEach(line => GameGeneratedPets[line.TrimEnd()] = 1);
    }

    internal bool IsDoTClass(string name) => !string.IsNullOrEmpty(name) && DoTClasses.ContainsKey(name);

    internal void AddPetOrPlayerAction(string name)
    {
      if (!IsVerifiedPlayer(name) && !IsVerifiedPet(name) && TakenPetOrPlayerAction.TryAdd(name, 1))
      {
        EventsNewTakenPetOrPlayerAction?.Invoke(this, name);
      }
    }

    internal void AddPetToPlayer(string pet, string player)
    {
      if ((!PetToPlayer.ContainsKey(pet) || PetToPlayer[pet] != player) && !IsVerifiedPlayer(pet))
      {
        PetToPlayer[pet] = player;
        EventsNewPetMapping?.Invoke(this, new PetMapping() { Pet = pet, Owner = player });
        PetMappingUpdated = true;
      }
    }

    internal void AddVerifiedPet(string name)
    {
      TakenPetOrPlayerAction.TryRemove(name, out _);
      VerifiedPlayers.TryRemove(name, out _);

      if (VerifiedPets.TryAdd(name, 1))
      {
        EventsNewVerifiedPet?.Invoke(this, name);
      }
    }

    internal void AddVerifiedPlayer(string name)
    {
      TakenPetOrPlayerAction.TryRemove(name, out _);
      VerifiedPets.TryRemove(name, out _);

      if (VerifiedPlayers.TryAdd(name, 1))
      {
        EventsNewVerifiedPlayer?.Invoke(this, name);
        PlayersUpdated = true;
      }
    }

    internal string GetClassName(SpellClass spellClass)
    {
      ClassNames.TryGetValue(spellClass, out string name);
      return name;
    }

    internal List<string> GetClassList()
    {
      var list = ClassNames.Values.ToList();
      list.Sort();
      return list;
    }

    internal string GetPlayerClass(string name)
    {
      string className = "";

      if (!string.IsNullOrEmpty(name) && PlayerToClass.TryGetValue(name, out SpellClassCounter counter))
      {
        className = ClassNames[counter.CurrentClass];
      }

      return className;
    }

    internal SpellClass GetPlayerClassEnum(string name)
    {
      SpellClass spellClass = 0;

      if (!string.IsNullOrEmpty(name) && PlayerToClass.TryGetValue(name, out SpellClassCounter counter))
      {
        spellClass = counter.CurrentClass;
      }

      return spellClass;
    }

    internal string GetPlayerFromPet(string pet)
    {
      string player = null;

      if (!string.IsNullOrEmpty(pet))
      {
        PetToPlayer.TryGetValue(pet, out player);
      }

      return player;
    }
    
    internal bool IsVerifiedPet(string name)
    {
      bool found = false;
      bool isGameGenerated = false;

      if (!string.IsNullOrEmpty(name))
      {
        found = VerifiedPets.ContainsKey(name);
        isGameGenerated = !found && GameGeneratedPets.ContainsKey(name);

        if (isGameGenerated)
        {
          AddPetToPlayer(name, Labels.UNASSIGNED);
        }
      }

      return found || isGameGenerated;
    }

    internal bool IsVerifiedPlayer(string name)
    {
      return !string.IsNullOrEmpty(name) && (name == Labels.UNASSIGNED || SecondPerson.ContainsKey(name) || ThirdPerson.ContainsKey(name) || VerifiedPlayers.ContainsKey(name));
    }

    internal bool IsPetOrPlayer(string name)
    {
      return !string.IsNullOrEmpty(name) && (IsVerifiedPlayer(name) || IsVerifiedPet(name) || TakenPetOrPlayerAction.ContainsKey(name));
    }

    internal bool IsPetOrPlayerOrSpell(string name)
    {
      return IsPetOrPlayer(name) || DataManager.Instance.IsPlayerSpell(name);
    }

    internal void RemoveVerifiedPet(string name)
    {
      if (!string.IsNullOrEmpty(name) && VerifiedPets.TryRemove(name, out _))
      {
        if (PetToPlayer.ContainsKey(name))
        {
          PetToPlayer.TryRemove(name, out _);
        }

        EventsRemoveVerifiedPet?.Invoke(this, name);
      }
    }

    internal void RemoveVerifiedPlayer(string name)
    {
      if (!string.IsNullOrEmpty(name) && VerifiedPlayers.TryRemove(name, out _))
      {
        string found = null;
        foreach (var keypair in PetToPlayer)
        {
          if (keypair.Value.Equals(name, StringComparison.OrdinalIgnoreCase))
          {
            found = keypair.Key;
          }
        }

        if (!string.IsNullOrEmpty(found))
        {
          PetToPlayer.TryRemove(found, out _);
        }

        EventsRemoveVerifiedPlayer?.Invoke(this, name);
      }
    }

    internal string ReplacePlayer(string name, string alternative)
    {
      string result = name;

      if (ThirdPerson.ContainsKey(name))
      {
        result = alternative;
      }
      else if (SecondPerson.ContainsKey(name))
      {
        result = ConfigUtil.PlayerName;
      }

      return result;
    }

    internal void Clear()
    {
      lock (this)
      {
        PetToPlayer.Clear();
        PlayerToClass.Clear();
        TakenPetOrPlayerAction.Clear();
        VerifiedPets.Clear();
        VerifiedPlayers.Clear();
        PossiblePlayerCache.Clear();

        AddVerifiedPlayer(ConfigUtil.PlayerName);
        foreach (var keypair in ConfigUtil.ReadPetMapping())
        {
          AddVerifiedPlayer(keypair.Value);
          AddVerifiedPet(keypair.Key);
          AddPetToPlayer(keypair.Key, keypair.Value);
        }

        ConfigUtil.ReadPlayers().ForEach(player => AddVerifiedPlayer(player));
        PetMappingUpdated = PlayersUpdated = false;
      }
    }

    internal void Save()
    {
      if (PetMappingUpdated)
      {
        var filtered = PetToPlayer.Where(keypair =>IsPossiblePlayerName(keypair.Key) && IsPossiblePlayerName(keypair.Value) && keypair.Value != Labels.UNASSIGNED);
        ConfigUtil.SavePetMapping(filtered);
        PetMappingUpdated = false;
      }

      if (PlayersUpdated)
      {
        ConfigUtil.SavePlayers(VerifiedPlayers.Keys.ToList());
        PlayersUpdated = false;
      }
    }

    internal void UpdatePlayerClassFromSpell(SpellCast cast, SpellClass theClass)
    {
      if (!PlayerToClass.TryGetValue(cast.Caster, out SpellClassCounter counter))
      {
        lock (PlayerToClass)
        {
          counter = new SpellClassCounter() { ClassCounts = new Dictionary<SpellClass, int>() };
          PlayerToClass.TryAdd(cast.Caster, counter);
        }
      }

      lock (counter)
      {
        int newValue = 1;
        if (counter.ClassCounts.TryGetValue(theClass, out int value))
        {
          newValue += value;
        }

        counter.ClassCounts[theClass] = newValue;

        if (newValue > counter.CurrentMax)
        {
          counter.CurrentMax = value;
          counter.CurrentClass = theClass;
        }
      }
    }

    internal bool IsPossiblePlayerName(string part, int stop = -1)
    {
      bool found = false;

      if (part != null)
      {
        string key = part + "-" + stop;
        if (PossiblePlayerCache.TryGetValue(key, out bool value))
        {
          found = value;
        }
        else
        {
          if (stop == -1)
          {
            stop = part.Length;
          }

          found = stop >= 3;
          for (int i = 0; found != false && i < stop; i++)
          {
            if (!char.IsLetter(part, i))
            {
              found = false;
              break;
            }
          }

          PossiblePlayerCache.TryAdd(key, found);
        }
      }

      return found;
    }

    private static void AddMultiCase(string[] values, ConcurrentDictionary<string, byte> dict)
    {
      if (values.Length > 0)
      {
        foreach (var value in values)
        {
          if (!string.IsNullOrEmpty(value) && value.Length >= 2)
          {
            dict[value] = 1;
            dict[value.ToUpper(CultureInfo.CurrentCulture)] = 1;
            dict[char.ToUpper(value[0], CultureInfo.CurrentCulture) + value.Substring(1)] = 1;
          }
        }
      }
    }

    private class SpellClassCounter
    {
      internal int CurrentMax { get; set; }
      internal SpellClass CurrentClass { get; set; }
      internal Dictionary<SpellClass, int> ClassCounts { get; set; }
    }
  }
}
