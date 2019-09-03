using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EQLogParser
{
  class PlayerManager
  {
    internal event EventHandler<string> EventsNewLikelyPlayer;
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

    private readonly ConcurrentDictionary<string, byte> LikelyPlayer = new ConcurrentDictionary<string, byte>();
    private readonly ConcurrentDictionary<string, Dictionary<string, int>> LikelyPlayerStats = new ConcurrentDictionary<string, Dictionary<string, int>>();
    private readonly ConcurrentDictionary<string, string> PetToPlayer = new ConcurrentDictionary<string, string>();
    private readonly ConcurrentDictionary<string, SpellClassCounter> PlayerToClass = new ConcurrentDictionary<string, SpellClassCounter>();
    private readonly ConcurrentDictionary<string, byte> TakenPetOrPlayerAction = new ConcurrentDictionary<string, byte>();
    private readonly ConcurrentDictionary<string, byte> VerifiedPets = new ConcurrentDictionary<string, byte>();
    private readonly ConcurrentDictionary<string, byte> VerifiedPlayers = new ConcurrentDictionary<string, byte>();

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
      if (!PetToPlayer.ContainsKey(pet) || PetToPlayer[pet] != player)
      {
        PetToPlayer[pet] = player;
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
        PlayersUpdated = true;
      }
    }

    internal List<string> GetClassList()
    {
      var list = ClassNames.Values.ToList();
      list.Sort();
      return list;
    }

    internal static string GetClassName(SpellClass type)
    {
      return Properties.Resources.ResourceManager.GetString(Enum.GetName(typeof(SpellClass), type), CultureInfo.CurrentCulture);
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

    internal string GetPlayerFromPet(string pet)
    {
      string player = null;

      if (!string.IsNullOrEmpty(pet))
      {
        PetToPlayer.TryGetValue(pet, out player);
      }

      return player;
    }

    internal bool IsPlayerDamage(DamageRecord record)
    {
      bool valid = false;
      if (record != null && record.Defender != record.Attacker)
      {
        var isAttackerPlayer = IsPetOrPlayer(record.Attacker);
        var isDefenderPlayer = IsPetOrPlayer(record.Defender);
        var attackerCouldBePlayer = isAttackerPlayer || Helpers.IsPossiblePlayerName(record.Attacker);
        var defenderProbablyNotPlayer = !isDefenderPlayer && !IsLikelyPlayer(record.DefenderOwner);

        valid = attackerCouldBePlayer && (defenderProbablyNotPlayer || (record.Attacker == record.Defender && PetToPlayer.ContainsKey(record.Attacker)));

        if (!isAttackerPlayer && attackerCouldBePlayer && defenderProbablyNotPlayer && !Helpers.IsPossiblePlayerName(record.Defender))
        {
          IncrementLikelyPlayer(record.Attacker, record.Defender);
        }
      }

      return valid;
    }

    internal bool IsLikelyPlayer(string name)
    {
      return !string.IsNullOrEmpty(name) && (IsVerifiedPlayer(name) || LikelyPlayer.ContainsKey(name));
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
      return !string.IsNullOrEmpty(name) && (IsVerifiedPlayer(name) || IsVerifiedPet(name) || LikelyPlayer.ContainsKey(name) || TakenPetOrPlayerAction.ContainsKey(name));
    }

    internal void RemoveVerifiedPet(string name)
    {
      if (!string.IsNullOrEmpty(name) && VerifiedPets.TryRemove(name, out _))
      {
        if (PetToPlayer.ContainsKey(name))
        {
          PetToPlayer.TryRemove(name, out _);
        }

        EventsRemoveVerifiedPet(this, name);
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

        EventsRemoveVerifiedPlayer(this, name);
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

      return string.Intern(result);
    }

    internal void Clear()
    {
      lock (this)
      {
        LikelyPlayer.Clear();
        LikelyPlayerStats.Clear();
        PetToPlayer.Clear();
        PlayerToClass.Clear();
        TakenPetOrPlayerAction.Clear();
        VerifiedPets.Clear();
        VerifiedPlayers.Clear();

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
        var filtered = PetToPlayer.Where(keypair => Helpers.IsPossiblePlayerName(keypair.Key) && Helpers.IsPossiblePlayerName(keypair.Value) && keypair.Value != Labels.UNASSIGNED);
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

    private void AddMultiCase(string[] values, ConcurrentDictionary<string, byte> dict)
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

    private void IncrementLikelyPlayer(string attacker, string defender)
    {
      if (!LikelyPlayerStats.TryGetValue(attacker, out Dictionary<string, int> defenders))
      {
        lock (LikelyPlayerStats)
        {
          defenders = new Dictionary<string, int>();
          LikelyPlayerStats.TryAdd(attacker, defenders);
        }
      }

      bool newLikelyPlayer = false;

      lock (defenders)
      {
        int newValue = 1;
        if (defenders.TryGetValue(defender, out int value))
        {
          newValue += value;
        }

        defenders[defender] = newValue;

        if (newValue > 5 || defenders.Count > 1)
        {
          LikelyPlayer[attacker] = 1;
          newLikelyPlayer = true;
        }
      }

      if (newLikelyPlayer)
      {
        EventsNewLikelyPlayer(this, attacker);
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
