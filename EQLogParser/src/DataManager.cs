using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser

{
  class DataManager
  {
    public static DataManager Instance = new DataManager();

    public const string UNASSIGNED_PET_OWNER = "Unassigned Pets";
    public event EventHandler<PetMapping> EventsNewPetMapping;
    public event EventHandler<string> EventsNewVerifiedPet;
    public event EventHandler<string> EventsNewVerifiedPlayer;
    public event EventHandler<string> EventsRemovedNonPlayer;
    public event EventHandler<string> EventsNewUnverifiedPetOrPlayer;
    public event EventHandler<NonPlayer> EventsNewNonPlayer;
    public event EventHandler<NonPlayer> EventsUpdatedNonPlayer;

    private List<SpellCast> AllSpellCasts = new List<SpellCast>();
    private ConcurrentDictionary<string, NonPlayer> ActiveNonPlayerMap = new ConcurrentDictionary<string, NonPlayer>();
    private ConcurrentDictionary<string, string> PetToPlayerMap = new ConcurrentDictionary<string, string>();
    private ConcurrentDictionary<string, byte> LifetimeNonPlayerMap = new ConcurrentDictionary<string, byte>();
    private ConcurrentDictionary<string, string> AttackerReplacement = new ConcurrentDictionary<string, string>();
    private ConcurrentDictionary<string, byte> GameGeneratedPets = new ConcurrentDictionary<string, byte>();
    private ConcurrentDictionary<string, long> ProbablyNotAPlayer = new ConcurrentDictionary<string, long>();
    private ConcurrentDictionary<string, byte> UnverifiedPetOrPlayer = new ConcurrentDictionary<string, byte>();
    private ConcurrentDictionary<string, byte> VerifiedPets = new ConcurrentDictionary<string, byte>();
    private ConcurrentDictionary<string, byte> VerifiedPlayers = new ConcurrentDictionary<string, byte>();

    private DataManager()
    {
      // Populated generated pets
      long length = new System.IO.FileInfo(@"data\petnames.txt").Length;
      if (length < 20000)
      {
        string[] lines = System.IO.File.ReadAllLines(@"data\petnames.txt");
        lines.ToList().ForEach(line => GameGeneratedPets[line.TrimEnd()] = 1);
      }

      VerifiedPlayers["himself"] = 1;
      VerifiedPlayers["herself"] = 1;
      VerifiedPlayers["itself"] = 1;
      VerifiedPlayers["you"] = 1;
      VerifiedPlayers["YOU"] = 1;
      VerifiedPlayers["You"] = 1;
      VerifiedPlayers["your"] = 1;
      VerifiedPlayers["Your"] = 1;
      VerifiedPlayers["YOUR"] = 1;
    }

    public void Clear()
    {
      ActiveNonPlayerMap.Clear();
      LifetimeNonPlayerMap.Clear();
      ProbablyNotAPlayer.Clear();
      UnverifiedPetOrPlayer.Clear();
      AllSpellCasts.Clear();
    }

    public void AddSpellCast(SpellCast cast)
    {
      bool replaced;
      cast.Caster = ReplaceAttacker(cast.Caster, out replaced);

      lock (AllSpellCasts)
      {
        AllSpellCasts.Add(cast);
      }
    }

    public List<SpellCast> GetSpellCasts()
    {
      List<SpellCast> list;
      lock(AllSpellCasts)
      {
        list = AllSpellCasts.ToList();
      }
      return list;
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

    public NonPlayer GetNonPlayer(string name)
    {
      NonPlayer npc = null;
      ActiveNonPlayerMap.TryGetValue(name, out npc);
      return npc;
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
  }
}