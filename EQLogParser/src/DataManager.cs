using System;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser

{
  class DataManager
  {
    public static DataManager Instance = new DataManager();

    public event EventHandler<PetMapping> EventsNewPetMapping;
    public event EventHandler<string> EventsNewVerifiedPet;
    public event EventHandler<string> EventsNewVerifiedPlayer;
    public event EventHandler<string> EventsRemovedNonPlayer;
    public event EventHandler<string> EventsNewUnverifiedPetOrPlayer;
    public event EventHandler<NonPlayer> EventsNewNonPlayer;
    public event EventHandler<NonPlayer> EventsUpdatedNonPlayer;

    private Dictionary<string, NonPlayer> ActiveNonPlayerMap = new Dictionary<string, NonPlayer>();
    private Dictionary<string, string> PetToPlayerMap = new Dictionary<string, string>();
    private Dictionary<string, byte> LifetimeNonPlayerMap = new Dictionary<string, byte>();
    private Dictionary<string, string> AttackerReplacement = new Dictionary<string, string>();
    private Dictionary<string, byte> GameGeneratedPets = new Dictionary<string, byte>();
    private Dictionary<string, long> ProbablyNotAPlayer = new Dictionary<string, long>();
    private Dictionary<string, byte> UnverifiedPetOrPlayer = new Dictionary<string, byte>();
    private Dictionary<string, byte> VerifiedPets = new Dictionary<string, byte>();
    private Dictionary<string, byte> VerifiedPlayers = new Dictionary<string, byte>()
    {
      { "himself", 1 }, { "herself", 1 }, { "itself", 1}, { "you",  1 }, { "YOU", 1 }, {"You", 1}, {"your", 1}, {"Your", 1}, {"YOUR", 1}
    };

    private DataManager()
    {
      // Populated generated pets
      long length = new System.IO.FileInfo(@"data\petnames.txt").Length;
      if (length < 20000)
      {
        string[] lines = System.IO.File.ReadAllLines(@"data\petnames.txt");
        lines.ToList().ForEach(line => GameGeneratedPets[line.TrimEnd()] = 1);
      }
    }

    public void Clear()
    {
      ActiveNonPlayerMap.Clear();
      LifetimeNonPlayerMap.Clear();
      ProbablyNotAPlayer.Clear();
      UnverifiedPetOrPlayer.Clear();
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

    public NonPlayer GetNonPlayer(string name)
    {
      return ActiveNonPlayerMap.ContainsKey(name) ? ActiveNonPlayerMap[name] : null;
    }

    public string GetPlayerFromPet(string pet)
    {
      return PetToPlayerMap.ContainsKey(pet) ? PetToPlayerMap[pet] : null;
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
      return ActiveNonPlayerMap.Remove(name);
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
      bool removed = ActiveNonPlayerMap.Remove(name);
      removed = LifetimeNonPlayerMap.Remove(name) || removed;

      if (removed)
      {
        EventsRemovedNonPlayer(this, name);
      }
    }
  }
}