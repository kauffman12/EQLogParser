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

    internal static PlayerManager Instance = new PlayerManager();
    private readonly ConcurrentDictionary<SpellClass, string> ClassNames = new ConcurrentDictionary<SpellClass, string>();
    private readonly ConcurrentDictionary<string, byte> GameGeneratedPets = new ConcurrentDictionary<string, byte>();
    private readonly ConcurrentDictionary<string, byte> SecondPerson = new ConcurrentDictionary<string, byte>();
    private readonly ConcurrentDictionary<string, byte> ThirdPerson = new ConcurrentDictionary<string, byte>();
    private readonly ConcurrentDictionary<string, long> HitByPlayer = new ConcurrentDictionary<string, long>();
    private readonly ConcurrentDictionary<string, string> PetToPlayerMap = new ConcurrentDictionary<string, string>();
    private readonly ConcurrentDictionary<string, SpellClassCounter> PlayerToClass = new ConcurrentDictionary<string, SpellClassCounter>();
    private readonly ConcurrentDictionary<string, byte> TakenPetOrPlayerAction = new ConcurrentDictionary<string, byte>();
    private readonly ConcurrentDictionary<string, byte> VerifiedPets = new ConcurrentDictionary<string, byte>();
    private readonly ConcurrentDictionary<string, byte> VerifiedPlayers = new ConcurrentDictionary<string, byte>();

    private bool PetMappingUpdated = false;

    private PlayerManager()
    {
      AddMultiCase(new string[] { "you", "your", "yourself" }, SecondPerson);
      AddMultiCase(new string[] { "himself", "herself", "itself" }, ThirdPerson);

      // populate ClassNames from SpellClass enum and resource table
      foreach (var item in Enum.GetValues(typeof(SpellClass)))
      {
        ClassNames[(SpellClass)item] = Properties.Resources.ResourceManager.GetString(Enum.GetName(typeof(SpellClass), item), CultureInfo.CurrentCulture);
      }

      // Populate generated pets
      ConfigUtil.ReadList(@"data\petnames.txt").ForEach(line => GameGeneratedPets[line.TrimEnd()] = 1);
    }

    internal void AddPetOrPlayerAction(string name)
    {
      if (!IsVerifiedPlayer(name) && !IsVerifiedPet(name) && TakenPetOrPlayerAction.TryAdd(name, 1))
      {
        EventsNewTakenPetOrPlayerAction(this, name);
      }
    }

    internal void AddPetToPlayer(string pet, string player)
    {
      if (!PetToPlayerMap.ContainsKey(pet) || PetToPlayerMap[pet] != player)
      {
        PetToPlayerMap[pet] = player;
        EventsNewPetMapping(this, new PetMapping() { Pet = pet, Owner = player });
        PetMappingUpdated = true;
      }
    }

    internal void AddVerifiedPet(string name)
    {
      TakenPetOrPlayerAction.TryRemove(name, out _);
      VerifiedPlayers.TryRemove(name, out _);

      if (VerifiedPets.TryAdd(name, 1))
      {
        EventsNewVerifiedPet(this, name);
      }
    }

    internal void AddVerifiedPlayer(string name)
    {
      TakenPetOrPlayerAction.TryRemove(name, out _);
      VerifiedPets.TryRemove(name, out _);

      if (VerifiedPlayers.TryAdd(name, 1))
      {
        EventsNewVerifiedPlayer(this, name);
      }
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

      if (PlayerToClass.TryGetValue(name, out SpellClassCounter counter))
      {
        className = ClassNames[counter.CurrentClass];
      }

      return className;
    }

    internal string GetPlayerFromPet(string pet)
    {
      PetToPlayerMap.TryGetValue(pet, out string player);
      return player;
    }

    internal void IncrementHitByPlayer(string player, string defender)
    {
      if (IsPetOrPlayer(player))
      {
        long newValue = HitByPlayer.TryGetValue(defender, out long value) ? value + 1 : 1;
        HitByPlayer[defender] = newValue;
      }
    }

    internal bool IsValidAttacker(DamageRecord record)
    {
      bool valid = false;
      if (record != null)
      {
        // attacker is player, defender isnt unless its a charmed NPC with the same name, and if its a pet it's not owned by a valid player
        valid = IsPetOrPlayer(record.Attacker) && (!IsPetOrPlayer(record.Defender) || record.Attacker == record.Defender) && (string.IsNullOrEmpty(record.DefenderOwner) || !IsPetOrPlayer(record.DefenderOwner));
      }

      return valid;
    }

    internal bool IsValidDamage(DamageRecord record)
    {
      // players obviously valid
      bool valid = IsVerifiedPlayer(record.Attacker);

      if (!valid)
      {
        // if it at least looks like a pet or unknown player test their name
        if (IsVerifiedPet(record.Attacker) || TakenPetOrPlayerAction.ContainsKey(record.Attacker))
        {
          if (!Helpers.IsPossiblePlayerName(record.Attacker))
          {
            // charmed pets seem to 'hit' instead of use their normal attack so allow this case
            valid = record.Attacker != record.Defender || "hits".Equals(record.Type, StringComparison.OrdinalIgnoreCase);
          }
          else
          {
            valid = true;
          }
        }
        else
        {
          valid = Helpers.IsPossiblePlayerName(record.Attacker);
        }
      }

      return valid;
    }

    internal bool IsVerifiedPet(string name)
    {
      bool found = VerifiedPets.ContainsKey(name);
      bool isGameGenerated = !found && GameGeneratedPets.ContainsKey(name);

      if (isGameGenerated)
      {
        AddPetToPlayer(name, Labels.UNASSIGNED);
      }

      return found || isGameGenerated;
    }

    internal bool IsVerifiedPlayer(string name)
    {
      return name == Labels.UNASSIGNED || SecondPerson.ContainsKey(name) || ThirdPerson.ContainsKey(name) || VerifiedPlayers.ContainsKey(name);
    }

    internal bool IsPetOrPlayer(string name)
    {
      return IsVerifiedPlayer(name) || IsVerifiedPet(name) || TakenPetOrPlayerAction.ContainsKey(name);
    }

    internal bool HasBeenHitByPlayers(string name)
    {
      return HitByPlayer.TryGetValue(name, out long value) && value > 3;
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

      return string.Intern(result);
    }

    internal void ResetHiyByPlayer()
    {
      HitByPlayer.Clear();
    }

    internal void Clear()
    {
      lock(this)
      {
        PetToPlayerMap.Clear();
        PlayerToClass.Clear();
        TakenPetOrPlayerAction.Clear();
        VerifiedPets.Clear();
        VerifiedPlayers.Clear();
        VerifiedPlayers[ConfigUtil.PlayerName] = 1;

        foreach (var keypair in ConfigUtil.ReadPetMapping())
        {
          AddVerifiedPlayer(keypair.Value);
          AddVerifiedPet(keypair.Key);
          AddPetToPlayer(keypair.Key, keypair.Value);
        }
      }
    }

    internal void Save()
    {
      if (PetMappingUpdated)
      {
        var filtered = PetToPlayerMap.Where(keypair => Helpers.IsPossiblePlayerName(keypair.Value) && keypair.Value != Labels.UNASSIGNED);
        ConfigUtil.SavePetMapping(filtered);
        PetMappingUpdated = false;
      }
    }

    internal void UpdatePlayerClassFromSpell(SpellCast cast, SpellClass theClass)
    {
      if (!PlayerToClass.TryGetValue(cast.Caster, out SpellClassCounter counter))
      {
        lock(PlayerToClass)
        {
          PlayerToClass.TryAdd(cast.Caster, new SpellClassCounter() { ClassCounts = new ConcurrentDictionary<SpellClass, int>() });
          counter = PlayerToClass[cast.Caster];
        }
      }

      lock (counter)
      {
        if (!counter.ClassCounts.ContainsKey(theClass))
        {
          counter.ClassCounts.TryAdd(theClass, 0);
        }

        int value = ++counter.ClassCounts[theClass];
        if (value > counter.CurrentMax)
        {
          counter.CurrentMax = value;
          counter.CurrentClass = theClass;
        }
      }
    }

    private void AddMultiCase(string[] values, ConcurrentDictionary<string, byte> dict)
    {
      if (values.Length > 0)
      {
        foreach(var value in values)
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
      internal ConcurrentDictionary<SpellClass, int> ClassCounts { get; set; }
    }
  }
}
