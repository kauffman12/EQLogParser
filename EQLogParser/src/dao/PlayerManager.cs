using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace EQLogParser
{
  class PlayerManager
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    internal event EventHandler<PetMapping> EventsNewPetMapping;
    internal event EventHandler<string> EventsNewTakenPetOrPlayerAction;
    internal event EventHandler<string> EventsNewVerifiedPet;
    internal event EventHandler<string> EventsNewVerifiedPlayer;
    internal event EventHandler<string> EventsRemoveVerifiedPet;
    internal event EventHandler<string> EventsRemoveVerifiedPlayer;
    internal event EventHandler<string> EventsUpdatePlayerClass;

    internal static PlayerManager Instance = new PlayerManager();

    internal static readonly BitmapImage BER_ICON = new BitmapImage(new Uri(@"pack://application:,,,/icons/Ber.png"));
    internal static readonly BitmapImage BRD_ICON = new BitmapImage(new Uri(@"pack://application:,,,/icons/Brd.png"));
    internal static readonly BitmapImage BST_ICON = new BitmapImage(new Uri(@"pack://application:,,,/icons/Bst.png"));
    internal static readonly BitmapImage CLR_ICON = new BitmapImage(new Uri(@"pack://application:,,,/icons/Clr.png"));
    internal static readonly BitmapImage DRU_ICON = new BitmapImage(new Uri(@"pack://application:,,,/icons/Dru.png"));
    internal static readonly BitmapImage ENC_ICON = new BitmapImage(new Uri(@"pack://application:,,,/icons/Enc.png"));
    internal static readonly BitmapImage MAG_ICON = new BitmapImage(new Uri(@"pack://application:,,,/icons/Mag.png"));
    internal static readonly BitmapImage MNK_ICON = new BitmapImage(new Uri(@"pack://application:,,,/icons/Mnk.png"));
    internal static readonly BitmapImage NEC_ICON = new BitmapImage(new Uri(@"pack://application:,,,/icons/Nec.png"));
    internal static readonly BitmapImage PAL_ICON = new BitmapImage(new Uri(@"pack://application:,,,/icons/Pal.png"));
    internal static readonly BitmapImage RNG_ICON = new BitmapImage(new Uri(@"pack://application:,,,/icons/Rng.png"));
    internal static readonly BitmapImage ROG_ICON = new BitmapImage(new Uri(@"pack://application:,,,/icons/Rog.png"));
    internal static readonly BitmapImage SHD_ICON = new BitmapImage(new Uri(@"pack://application:,,,/icons/Shd.png"));
    internal static readonly BitmapImage UNK_ICON = new BitmapImage(new Uri(@"pack://application:,,,/icons/Unk.png"));
    internal static readonly BitmapImage SHM_ICON = new BitmapImage(new Uri(@"pack://application:,,,/icons/Shm.png"));
    internal static readonly BitmapImage WAR_ICON = new BitmapImage(new Uri(@"pack://application:,,,/icons/War.png"));
    internal static readonly BitmapImage WIZ_ICON = new BitmapImage(new Uri(@"pack://application:,,,/icons/Wiz.png"));

    // static data
    private readonly ConcurrentDictionary<SpellClass, string> ClassNames = new ConcurrentDictionary<SpellClass, string>();
    private readonly ConcurrentDictionary<string, SpellClass> ClassesByName = new ConcurrentDictionary<string, SpellClass>();
    private readonly ConcurrentDictionary<string, byte> GameGeneratedPets = new ConcurrentDictionary<string, byte>();
    private readonly ConcurrentDictionary<string, byte> SecondPerson = new ConcurrentDictionary<string, byte>();
    private readonly ConcurrentDictionary<string, byte> ThirdPerson = new ConcurrentDictionary<string, byte>();
    private readonly ConcurrentDictionary<string, string> PetToPlayer = new ConcurrentDictionary<string, string>();
    private readonly ConcurrentDictionary<string, SpellClassCounter> PlayerToClass = new ConcurrentDictionary<string, SpellClassCounter>();
    private readonly ConcurrentDictionary<string, byte> TakenPetOrPlayerAction = new ConcurrentDictionary<string, byte>();
    private readonly ConcurrentDictionary<string, byte> VerifiedPets = new ConcurrentDictionary<string, byte>();
    private readonly ConcurrentDictionary<string, double> VerifiedPlayers = new ConcurrentDictionary<string, double>();
    private readonly ConcurrentDictionary<string, byte> Mercs = new ConcurrentDictionary<string, byte>();
    private readonly ConcurrentDictionary<string, byte> DoTClasses = new ConcurrentDictionary<string, byte>();
    private readonly ConcurrentDictionary<string, byte> CharmPets = new ConcurrentDictionary<string, byte>();
    private readonly List<string> SortedClassList = new List<string>();
    private readonly List<string> SortedClassListWithNull = new List<string>();
    private static readonly object LockObject = new object();
    private bool PetMappingUpdated = false;
    private bool PlayersUpdated = false;

    private PlayerManager()
    {
      AddMultiCase(new string[] { "you", "your", "yourself" }, SecondPerson);
      AddMultiCase(new string[] { "himself", "herself", "itself" }, ThirdPerson);

      // populate ClassNames from SpellClass enum and resource table
      foreach (var item in Enum.GetValues(typeof(SpellClass)))
      {
        var name = EQLogParser.Resource.ResourceManager.GetString(Enum.GetName(typeof(SpellClass), item), CultureInfo.CurrentCulture);
        ClassNames[(SpellClass)item] = name;
        ClassesByName[name] = (SpellClass)item;
      }

      SortedClassList.AddRange(ClassNames.Values);
      SortedClassList.Sort();
      SortedClassListWithNull.AddRange(SortedClassList);
      SortedClassListWithNull.Insert(0, "");

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

      DispatcherTimer saveTimer = new DispatcherTimer();
      saveTimer.Tick += SaveTimer_Tick;
      saveTimer.Interval = new TimeSpan(0, 0, 30);
      saveTimer.Start();
    }

    private void SaveTimer_Tick(object sender, EventArgs e) => Save();
    internal bool IsCharmPet(string name) => !string.IsNullOrEmpty(name) && CharmPets.ContainsKey(name);
    internal bool IsDoTClass(string name) => !string.IsNullOrEmpty(name) && DoTClasses.ContainsKey(name);
    internal bool IsVerifiedPlayer(string name) => !string.IsNullOrEmpty(name) && (name == Labels.UNASSIGNED || SecondPerson.ContainsKey(name)
      || ThirdPerson.ContainsKey(name) || VerifiedPlayers.ContainsKey(name));
    internal bool IsPetOrPlayerOrMerc(string name) => !string.IsNullOrEmpty(name) && (IsVerifiedPlayer(name) || IsVerifiedPet(name) || IsMerc(name) || TakenPetOrPlayerAction.ContainsKey(name));
    internal bool IsPetOrPlayerOrSpell(string name) => IsPetOrPlayerOrMerc(name) || DataManager.Instance.IsPlayerSpell(name);
    internal List<string> GetClassList(bool withNull = false) => withNull ? SortedClassListWithNull : SortedClassList;
    internal bool IsMerc(string name) => Mercs.TryGetValue(TextFormatUtils.ToUpper(name), out _);

    internal void AddPetOrPlayerAction(string name)
    {
      if (!IsVerifiedPlayer(name) && !IsVerifiedPet(name) && TakenPetOrPlayerAction.TryAdd(name, 1))
      {
        EventsNewTakenPetOrPlayerAction?.Invoke(this, name);
      }
    }

    internal void AddPetToPlayer(string pet, string player, bool initialLoad = false)
    {
      if (!string.IsNullOrEmpty(pet) && !string.IsNullOrEmpty(player))
      {
        if (!PetToPlayer.ContainsKey(pet) || PetToPlayer[pet] != player)
        {
          if (!IsVerifiedPlayer(pet))
          {
            lock (LockObject)
            {
              PetToPlayer[pet] = player;
            }

            EventsNewPetMapping?.Invoke(this, new PetMapping { Pet = pet, Owner = player });
            PetMappingUpdated = !initialLoad;
          }
        }
      }
    }

    internal void AddMerc(string name)
    {
      if (!string.IsNullOrEmpty(name))
      {
        name = string.Intern(name);
        Mercs[TextFormatUtils.ToUpper(name)] = 1;
      }
    }

    internal void AddVerifiedPet(string name)
    {
      if (!string.IsNullOrEmpty(name) && !VerifiedPets.ContainsKey(name))
      {
        name = string.Intern(name);
        lock (LockObject)
        {
          if (VerifiedPlayers.TryRemove(name, out _))
          {
            PlayersUpdated = true;
          }
        }

        if (!IsPossiblePlayerName(name))
        {
          if (!name.EndsWith("`s pet", StringComparison.OrdinalIgnoreCase) && !name.EndsWith("`s ward", StringComparison.OrdinalIgnoreCase) &&
            !name.EndsWith("`s warder", StringComparison.OrdinalIgnoreCase) && !MainWindow.IsIgnoreCharmPetsEnabled)
          {
            CharmPets[name] = 1;
          }
        }
        else if (!PetToPlayer.ContainsKey(name))
        {
          AddPetToPlayer(name, Labels.UNASSIGNED);
        }

        TakenPetOrPlayerAction.TryRemove(name, out _);

        if (VerifiedPets.TryAdd(name, 1))
        {
          EventsNewVerifiedPet?.Invoke(this, name);
          PlayersUpdated = true;
        }
      }
    }

    internal void AddVerifiedPlayer(string name, double playerTime)
    {
      if (!string.IsNullOrEmpty(name))
      {
        name = string.Intern(name);
        if (VerifiedPlayers.TryGetValue(name, out double lastTime))
        {
          if (playerTime > lastTime)
          {
            lock (LockObject)
            {
              VerifiedPlayers[name] = playerTime;
            }
          }
        }
        else
        {
          lock (LockObject)
          {
            VerifiedPlayers[name] = playerTime;
          }

          EventsNewVerifiedPlayer?.Invoke(this, name);
        }

        TakenPetOrPlayerAction.TryRemove(name, out _);
        VerifiedPets.TryRemove(name, out _);
      }
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

    internal BitmapImage GetPlayerIcon(string name)
    {
      BitmapImage icon = UNK_ICON;

      switch (GetPlayerClassEnum(name))
      {
        case SpellClass.BER:
          icon = BER_ICON;
          break;
        case SpellClass.BRD:
          icon = BRD_ICON;
          break;
        case SpellClass.BST:
          icon = BST_ICON;
          break;
        case SpellClass.CLR:
          icon = CLR_ICON;
          break;
        case SpellClass.DRU:
          icon = DRU_ICON;
          break;
        case SpellClass.ENC:
          icon = ENC_ICON;
          break;
        case SpellClass.MAG:
          icon = MAG_ICON;
          break;
        case SpellClass.MNK:
          icon = MNK_ICON;
          break;
        case SpellClass.NEC:
          icon = NEC_ICON;
          break;
        case SpellClass.PAL:
          icon = PAL_ICON;
          break;
        case SpellClass.RNG:
          icon = RNG_ICON;
          break;
        case SpellClass.ROG:
          icon = ROG_ICON;
          break;
        case SpellClass.SHD:
          icon = SHD_ICON;
          break;
        case SpellClass.SHM:
          icon = SHM_ICON;
          break;
        case SpellClass.WAR:
          icon = WAR_ICON;
          break;
        case SpellClass.WIZ:
          icon = WIZ_ICON;
          break;
      }

      return icon;
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

    internal string GetPlayerClassReason(string name)
    {
      string result = "";

      if (!string.IsNullOrEmpty(name) && PlayerToClass.TryGetValue(name, out SpellClassCounter counter))
      {
        result = counter.Reason;
      }

      return result;
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

        if (isGameGenerated && !PetToPlayer.ContainsKey(name))
        {
          AddPetToPlayer(name, Labels.UNASSIGNED);
        }
      }

      return found || isGameGenerated;
    }

    internal void RemoveVerifiedPet(string name)
    {
      if (!string.IsNullOrEmpty(name) && VerifiedPets.TryRemove(name, out _))
      {
        if (PetToPlayer.ContainsKey(name))
        {
          lock (LockObject)
          {
            PetToPlayer.TryRemove(name, out _);
          }
        }

        EventsRemoveVerifiedPet?.Invoke(this, name);
      }
    }

    internal void RemoveVerifiedPlayer(string name)
    {
      bool updated = false;
      if (!string.IsNullOrEmpty(name))
      {
        lock (LockObject)
        {
          if (VerifiedPlayers.TryRemove(name, out _))
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
              PetMappingUpdated = true;
            }

            updated = true;
            PlayersUpdated = true;
          }
        }
      }

      if (updated)
      {
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

    internal void Init()
    {
      lock (LockObject)
      {
        PetToPlayer.Clear();
        PlayerToClass.Clear();
        TakenPetOrPlayerAction.Clear();
        VerifiedPets.Clear();
        VerifiedPlayers.Clear();
        Mercs.Clear();

        AddVerifiedPlayer(ConfigUtil.PlayerName, DateUtil.ToDouble(DateTime.Now));

        ConfigUtil.ReadPlayers().ForEach(player =>
        {
          if (!string.IsNullOrEmpty(player) && player.Length > 2)
          {
            double parsed = 0d;
            string name;
            string className = null;
            var split = player.Split('=');
            if (split.Length == 2)
            {
              name = split[0];
              var split2 = split[1].Split(',');
              if (split2.Length == 2)
              {
                double.TryParse(split2[0], out parsed);
                className = split2[1];
              }
              else
              {
                double.TryParse(split[1], out parsed);
              }
            }
            else
            {
              name = player;
            }

            AddVerifiedPlayer(name, parsed);

            if (className != null)
            {
              SetPlayerClass(name, className);
            }
          }
        });

        var mapping = ConfigUtil.ReadPetMapping();
        foreach (var key in mapping.Keys)
        {
          if (!VerifiedPlayers.ContainsKey(mapping[key]))
          {
            AddVerifiedPlayer(mapping[key], 0d);
          }

          AddVerifiedPet(key);
          AddPetToPlayer(key, mapping[key], true);
        }

        PetMappingUpdated = false;
      }
    }

    internal void Save()
    {
      if (PetMappingUpdated)
      {
        lock (PetToPlayer)
        {
          var filtered = PetToPlayer.Where(keypair => !GameGeneratedPets.ContainsKey(keypair.Key) && IsPossiblePlayerName(keypair.Key) &&
            keypair.Value != Labels.UNASSIGNED);
          ConfigUtil.SavePetMapping(filtered);
        }

        PetMappingUpdated = false;
      }

      if (PlayersUpdated)
      {
        lock (VerifiedPlayers)
        {
          var list = new List<string>();
          var now = DateTime.Now;
          foreach (var keypair in VerifiedPlayers)
          {
            if (!string.IsNullOrEmpty(keypair.Key) && IsPossiblePlayerName(keypair.Key))
            {
              if (keypair.Value != 0 && (now - DateUtil.FromDouble(keypair.Value)).TotalDays < 300)
              {
                var output = keypair.Key + "=" + Math.Round(keypair.Value);
                if (PlayerToClass.TryGetValue(keypair.Key, out SpellClassCounter value) && value.CurrentMax == long.MaxValue &&
                  ClassNames.TryGetValue(value.CurrentClass, out string className))
                {
                  output += "," + className;
                }

                list.Add(output);
              }
            }
          }

          ConfigUtil.SavePlayers(list);
        }

        PlayersUpdated = false;
      }
    }

    internal void SetPlayerClass(string player, string className)
    {
      if (ClassesByName.TryGetValue(className, out SpellClass value))
      {
        SetPlayerClass(player, value);
      }
      else
      {
        PlayerToClass.TryRemove(player, out _);
      }
    }

    internal void SetPlayerClass(string player, SpellClass theClass)
    {
      if (!PlayerToClass.TryGetValue(player, out SpellClassCounter counter))
      {
        lock (PlayerToClass)
        {
          counter = new SpellClassCounter { ClassCounts = new Dictionary<SpellClass, long>() };
          PlayerToClass.TryAdd(player, counter);
        }
      }

      lock (counter)
      {
        if (!theClass.Equals(counter.CurrentClass) || counter.CurrentMax != long.MaxValue)
        {
          counter.CurrentClass = theClass;
          counter.Reason = "Class chosen manually or from unique player action.";
          counter.ClassCounts[theClass] = long.MaxValue;
          counter.CurrentMax = long.MaxValue;
          EventsUpdatePlayerClass?.Invoke(player, ClassNames[theClass]);
          LOG.Debug("Assigning " + player + " as " + theClass.ToString() + " from class specific action");
        }
      }
    }

    internal void UpdatePlayerClassFromSpell(SpellCast cast, SpellClass theClass)
    {
      if (!PlayerToClass.TryGetValue(cast.Caster, out SpellClassCounter counter))
      {
        lock (PlayerToClass)
        {
          counter = new SpellClassCounter { ClassCounts = new Dictionary<SpellClass, long>() };
          PlayerToClass.TryAdd(cast.Caster, counter);
        }
      }

      lock (counter)
      {
        if (counter.CurrentMax != long.MaxValue)
        {
          long newValue = 1;
          if (cast.SpellData.Rank > 1)
          {
            newValue = 10;
          }

          if (counter.ClassCounts.TryGetValue(theClass, out long value))
          {
            newValue += value;
          }

          counter.ClassCounts[theClass] = newValue;

          if (newValue > counter.CurrentMax)
          {
            counter.CurrentMax = newValue;
            if (!theClass.Equals(counter.CurrentClass))
            {
              counter.CurrentClass = theClass;
              counter.Reason = "Class chosen based on " + cast.Spell + ".";
              EventsUpdatePlayerClass?.Invoke(cast.Caster, ClassNames[theClass]);
              LOG.Debug("Assigning " + cast.Caster + " as " + theClass.ToString() + " from " + cast.Spell);
            }
          }
        }
      }
    }

    internal static int FindPossiblePlayerName(string part, out bool isCrossServer, int start = 0, int stop = -1, char end = char.MaxValue)
    {
      isCrossServer = false;
      int dotCount = 0;

      if (part != null)
      {
        if (stop == -1)
        {
          stop = part.Length;
        }

        if (start <= stop && (stop - start) >= 3)
        {
          for (int i = start; i < stop; i++)
          {
            if (end != char.MaxValue && part[i] == end)
            {
              return i;
            }

            if (i > 2 && part[i] == '.')
            {
              isCrossServer = true;
              if (++dotCount > 1)
              {
                return -1;
              }
            }
            else if (!char.IsLetter(part, i))
            {
              return -1;
            }
          }

          if (end == char.MaxValue)
          {
            return stop;
          }
        }
      }

      return -1;
    }

    internal static bool IsPossiblePlayerName(string part, int stop = -1) => FindPossiblePlayerName(part, out bool _, 0, stop) > -1;

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
      internal long CurrentMax { get; set; }
      internal SpellClass CurrentClass { get; set; }
      internal Dictionary<SpellClass, long> ClassCounts { get; set; }
      internal string Reason { get; set; } = "";
    }
  }
}
