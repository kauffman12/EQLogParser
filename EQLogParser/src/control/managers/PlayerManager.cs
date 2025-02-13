using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace EQLogParser
{
  class PlayerManager
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    internal event EventHandler<PetMapping> EventsNewPetMapping;
    internal event EventHandler<string> EventsNewVerifiedPet;
    internal event EventHandler<string> EventsNewVerifiedPlayer;
    internal event EventHandler<string> EventsRemoveVerifiedPet;
    internal event EventHandler<string> EventsRemoveVerifiedPlayer;
    internal event EventHandler<string> EventsUpdatePlayerClass;

    internal static PlayerManager Instance = new();
    internal static readonly BitmapImage BerIcon = new(new Uri(@"pack://application:,,,/icons/Ber.png"));
    internal static readonly BitmapImage BrdIcon = new(new Uri(@"pack://application:,,,/icons/Brd.png"));
    internal static readonly BitmapImage BstIcon = new(new Uri(@"pack://application:,,,/icons/Bst.png"));
    internal static readonly BitmapImage ClrIcon = new(new Uri(@"pack://application:,,,/icons/Clr.png"));
    internal static readonly BitmapImage DruIcon = new(new Uri(@"pack://application:,,,/icons/Dru.png"));
    internal static readonly BitmapImage EncIcon = new(new Uri(@"pack://application:,,,/icons/Enc.png"));
    internal static readonly BitmapImage MagIcon = new(new Uri(@"pack://application:,,,/icons/Mag.png"));
    internal static readonly BitmapImage MnkIcon = new(new Uri(@"pack://application:,,,/icons/Mnk.png"));
    internal static readonly BitmapImage NecIcon = new(new Uri(@"pack://application:,,,/icons/Nec.png"));
    internal static readonly BitmapImage PalIcon = new(new Uri(@"pack://application:,,,/icons/Pal.png"));
    internal static readonly BitmapImage RngIcon = new(new Uri(@"pack://application:,,,/icons/Rng.png"));
    internal static readonly BitmapImage RogIcon = new(new Uri(@"pack://application:,,,/icons/Rog.png"));
    internal static readonly BitmapImage ShdIcon = new(new Uri(@"pack://application:,,,/icons/Shd.png"));
    internal static readonly BitmapImage UnkIcon = new(new Uri(@"pack://application:,,,/icons/Unk.png"));
    internal static readonly BitmapImage ShmIcon = new(new Uri(@"pack://application:,,,/icons/Shm.png"));
    internal static readonly BitmapImage WarIcon = new(new Uri(@"pack://application:,,,/icons/War.png"));
    internal static readonly BitmapImage WizIcon = new(new Uri(@"pack://application:,,,/icons/Wiz.png"));

    // static data
    private readonly ConcurrentDictionary<SpellClass, string> _classNames = new();
    private readonly ConcurrentDictionary<string, SpellClass> _classesByName = new();
    private readonly ConcurrentDictionary<string, byte> _gameGeneratedPets = new();
    private readonly ConcurrentDictionary<string, byte> _secondPerson = new();
    private readonly ConcurrentDictionary<string, byte> _thirdPerson = new();
    private readonly ConcurrentDictionary<string, string> _petToPlayer = new();
    private readonly ConcurrentDictionary<string, SpellClassCounter> _playerToClass = new();
    private readonly ConcurrentDictionary<string, byte> _takenPetOrPlayerAction = new();
    private readonly ConcurrentDictionary<string, byte> _verifiedPets = new();
    private readonly ConcurrentDictionary<string, double> _verifiedPlayers = new();
    private readonly ConcurrentDictionary<string, byte> _mercs = new();
    private readonly List<string> _sortedClassList = [];
    private readonly List<string> _sortedClassListWithNull = [];
    private static readonly object LockObject = new();
    private bool _petMappingUpdated;
    private bool _playersUpdated;

    private PlayerManager()
    {
      AddMultiCase(["you", "your", "yourself"], _secondPerson);
      AddMultiCase(["himself", "herself", "itself"], _thirdPerson);

      // populate ClassNames from SpellClass enum and resource table
      foreach (var item in Enum.GetValues<SpellClass>())
      {
        var name = Resource.ResourceManager.GetString(Enum.GetName(item)?.ToUpperInvariant() ?? string.Empty, CultureInfo.InvariantCulture);
        if (name != null)
        {
          _classNames[item] = name;
          _classesByName[name] = item;
        }
      }

      _sortedClassList.AddRange(_classNames.Values);
      _sortedClassList.Sort();
      _sortedClassListWithNull.AddRange(_sortedClassList);
      _sortedClassListWithNull.Insert(0, "");

      // Populate generated pets
      ConfigUtil.ReadList(@"data\petnames.txt").ForEach(line => _gameGeneratedPets[line.TrimEnd()] = 1);

      var saveTimer = new DispatcherTimer();
      saveTimer.Tick += SaveTimer_Tick;
      saveTimer.Interval = new TimeSpan(0, 0, 30);
      saveTimer.Start();
    }

    private void SaveTimer_Tick(object sender, EventArgs e) => Save();
    internal bool IsVerifiedPlayer(string name) => !string.IsNullOrEmpty(name) && (name == Labels.Unassigned || _secondPerson.ContainsKey(name)
      || _thirdPerson.ContainsKey(name) || _verifiedPlayers.ContainsKey(name));
    internal bool IsPetOrPlayerOrMerc(string name) => !string.IsNullOrEmpty(name) && (IsVerifiedPlayer(name) || IsVerifiedPet(name) || IsMerc(name));
    internal bool IsPetOrPlayerOrSpell(string name) => IsPetOrPlayerOrMerc(name) || DataManager.Instance.IsPlayerSpell(name);
    internal List<string> GetClassList(bool withNull = false) => withNull ? [.. _sortedClassListWithNull] : [.. _sortedClassList];
    internal bool IsMerc(string name) => _mercs.TryGetValue(TextUtils.ToUpper(name), out _);

    internal void AddPetToPlayer(string pet, string player, bool initialLoad = false)
    {
      if (!string.IsNullOrEmpty(pet) && !string.IsNullOrEmpty(player))
      {
        lock (LockObject)
        {
          if ((!_petToPlayer.TryGetValue(pet, out var value) || value != player) && !IsVerifiedPlayer(pet))
          {
            _petToPlayer[pet] = player;
            EventsNewPetMapping?.Invoke(this, new PetMapping { Pet = pet, Owner = player });

            if (!initialLoad)
            {
              _petMappingUpdated = true;
            }
          }
        }
      }
    }

    internal void AddMerc(string name)
    {
      if (!string.IsNullOrEmpty(name))
      {
        name = string.Intern(name);
        _mercs[TextUtils.ToUpper(name)] = 1;
      }
    }

    internal void AddVerifiedPet(string name)
    {
      if (!string.IsNullOrEmpty(name))
      {
        lock (LockObject)
        {
          if (!_verifiedPets.ContainsKey(name))
          {
            name = string.Intern(name);
            if (_verifiedPlayers.TryRemove(name, out _))
            {
              _playersUpdated = true;
            }

            if (IsPossiblePlayerName(name) && !_petToPlayer.ContainsKey(name))
            {
              AddPetToPlayer(name, Labels.Unassigned);
            }

            _takenPetOrPlayerAction.TryRemove(name, out _);

            if (_verifiedPets.TryAdd(name, 1))
            {
              EventsNewVerifiedPet?.Invoke(this, name);
              _playersUpdated = true;
            }
          }
        }
      }
    }

    internal void AddVerifiedPlayer(string name, double playerTime)
    {
      if (!string.IsNullOrEmpty(name))
      {
        lock (LockObject)
        {
          name = string.Intern(name);
          if (_verifiedPlayers.TryGetValue(name, out var lastTime))
          {
            if (playerTime > lastTime)
            {
              _verifiedPlayers[name] = playerTime;
            }
          }
          else
          {
            _verifiedPlayers[name] = playerTime;
            EventsNewVerifiedPlayer?.Invoke(this, name);
          }

          _takenPetOrPlayerAction.TryRemove(name, out _);
          if (_verifiedPets.TryRemove(name, out _))
          {
            TryRemovePetMapping(name);
            EventsRemoveVerifiedPet?.Invoke(this, name);
          }
        }
      }
    }

    internal string GetPlayerClass(string name)
    {
      var className = "";

      if (!string.IsNullOrEmpty(name) && _playerToClass.TryGetValue(name, out var counter))
      {
        if (_classNames.TryGetValue(counter.CurrentClass, out var found))
        {
          className = found;
        }
      }

      return className;
    }

    internal BitmapImage GetPlayerIcon(string name)
    {
      var icon = GetPlayerClassEnum(name) switch
      {
        SpellClass.Ber => BerIcon,
        SpellClass.Brd => BrdIcon,
        SpellClass.Bst => BstIcon,
        SpellClass.Clr => ClrIcon,
        SpellClass.Dru => DruIcon,
        SpellClass.Enc => EncIcon,
        SpellClass.Mag => MagIcon,
        SpellClass.Mnk => MnkIcon,
        SpellClass.Nec => NecIcon,
        SpellClass.Pal => PalIcon,
        SpellClass.Rng => RngIcon,
        SpellClass.Rog => RogIcon,
        SpellClass.Shd => ShdIcon,
        SpellClass.Shm => ShmIcon,
        SpellClass.War => WarIcon,
        SpellClass.Wiz => WizIcon,
        _ => UnkIcon
      };

      return icon;
    }

    internal SpellClass GetPlayerClassEnum(string name)
    {
      SpellClass spellClass = 0;

      if (!string.IsNullOrEmpty(name) && _playerToClass.TryGetValue(name, out var counter))
      {
        spellClass = counter.CurrentClass;
      }

      return spellClass;
    }

    internal string GetPlayerClassReason(string name)
    {
      var result = "";

      if (!string.IsNullOrEmpty(name) && _playerToClass.TryGetValue(name, out var counter))
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
        _petToPlayer.TryGetValue(pet, out player);
      }

      return player;
    }

    internal ImmutableDictionary<string, string> GetPetPlayerMappings()
    {
      return _petToPlayer.ToImmutableDictionary();
    }

    internal bool IsVerifiedPet(string name)
    {
      var found = false;
      var isGameGenerated = false;

      if (!string.IsNullOrEmpty(name))
      {
        found = _verifiedPets.ContainsKey(name);
        isGameGenerated = !found && _gameGeneratedPets.ContainsKey(name);

        if (isGameGenerated && !_petToPlayer.ContainsKey(name))
        {
          AddPetToPlayer(name, Labels.Unassigned);
        }
      }

      return found || isGameGenerated;
    }

    internal void RemoveVerifiedPet(string name)
    {
      if (!string.IsNullOrEmpty(name))
      {
        lock (LockObject)
        {
          if (_verifiedPets.TryRemove(name, out _))
          {
            TryRemovePetMapping(name);
          }

          EventsRemoveVerifiedPet?.Invoke(this, name);
        }
      }
    }

    internal void RemoveVerifiedPlayer(string name)
    {
      if (!string.IsNullOrEmpty(name))
      {
        lock (LockObject)
        {
          if (_verifiedPlayers.TryRemove(name, out _))
          {
            string found = null;

            foreach (var kv in _petToPlayer)
            {
              if (kv.Value.Equals(name, StringComparison.OrdinalIgnoreCase))
              {
                found = kv.Key;
              }

              TryRemovePetMapping(found);
            }

            _playersUpdated = true;
          }

          EventsRemoveVerifiedPlayer?.Invoke(this, name);
        }
      }
    }

    internal string ReplacePlayer(string name, string alternative)
    {
      var result = name;

      if (_thirdPerson.ContainsKey(name))
      {
        result = alternative;
      }
      else if (_secondPerson.ContainsKey(name))
      {
        result = ConfigUtil.PlayerName;
      }

      return result;
    }

    internal void Init()
    {
      lock (LockObject)
      {
        _petToPlayer.Clear();
        _playerToClass.Clear();
        _takenPetOrPlayerAction.Clear();
        _verifiedPets.Clear();
        _verifiedPlayers.Clear();
        _mercs.Clear();

        AddVerifiedPlayer(ConfigUtil.PlayerName, DateUtil.ToDouble(DateTime.Now));

        ConfigUtil.ReadPlayers().ForEach(player =>
        {
          if (!string.IsNullOrEmpty(player) && player.Length > 2)
          {
            var parsed = 0d;
            string name;
            string className = null;
            var reason = "";
            var split = player.Split('=');
            if (split.Length == 2)
            {
              name = split[0];
              var split2 = split[1].Split(',');
              if (split2.Length > 2)
              {
                double.TryParse(split2[0], NumberStyles.Any, CultureInfo.InvariantCulture, out parsed);
                className = split2[1];
                reason = split2[2];
              }
              else if (split2.Length == 2)
              {
                double.TryParse(split2[0], NumberStyles.Any, CultureInfo.InvariantCulture, out parsed);
                className = split2[1];
              }
              else
              {
                double.TryParse(split[1], NumberStyles.Any, CultureInfo.InvariantCulture, out parsed);
              }
            }
            else
            {
              name = player;
            }

            AddVerifiedPlayer(name, parsed);

            if (className != null)
            {
              SetPlayerClass(name, className, reason);
            }
          }
        });

        var mapping = ConfigUtil.ReadPetMapping();
        foreach (var key in mapping.Keys)
        {
          if (!_verifiedPlayers.ContainsKey(mapping[key]))
          {
            AddVerifiedPlayer(mapping[key], 0d);
          }

          AddVerifiedPet(key);
          AddPetToPlayer(key, mapping[key], true);
        }

        _petMappingUpdated = false;
      }
    }

    internal void Save()
    {
      if (_petMappingUpdated)
      {
        lock (_petToPlayer)
        {
          // no generated or unassigned pets but allow for warders
          var filtered = _petToPlayer.Where(kv => !_gameGeneratedPets.ContainsKey(kv.Key) && kv.Value != Labels.Unassigned &&
            (IsPossiblePlayerName(kv.Key) || kv.Key.EndsWith("`s warder", StringComparison.OrdinalIgnoreCase)));
          ConfigUtil.SavePetMapping(filtered);
        }

        _petMappingUpdated = false;
      }

      if (_playersUpdated)
      {
        lock (_verifiedPlayers)
        {
          var list = new List<string>();
          var now = DateTime.Now;
          foreach (var kv in _verifiedPlayers)
          {
            if (!string.IsNullOrEmpty(kv.Key) && IsPossiblePlayerName(kv.Key))
            {
              if (kv.Value != 0 && (now - DateUtil.FromDouble(kv.Value)).TotalDays < 300)
              {
                var output = kv.Key + "=" + Math.Round(kv.Value);
                if (_playerToClass.TryGetValue(kv.Key, out var value) && value.CurrentMax == long.MaxValue &&
                  _classNames.TryGetValue(value.CurrentClass, out var className))
                {
                  output += "," + className;
                  output += "," + value.Reason;
                }

                list.Add(output);
              }
            }
          }

          ConfigUtil.SavePlayers(list);
        }

        _playersUpdated = false;
      }
    }

    internal void SetPlayerClass(string player, string className, string reason)
    {
      if (_classesByName.TryGetValue(className, out var value))
      {
        SetPlayerClass(player, value, reason);
      }
      else
      {
        _playerToClass.TryRemove(player, out _);
      }
    }

    internal void SetPlayerClass(string player, SpellClass theClass, string reason)
    {
      if (!_playerToClass.TryGetValue(player, out var counter))
      {
        lock (_playerToClass)
        {
          counter = new SpellClassCounter { ClassCounts = [] };
          _playerToClass.TryAdd(player, counter);
        }
      }

      lock (counter)
      {
        if (!theClass.Equals(counter.CurrentClass) || counter.CurrentMax != long.MaxValue || string.IsNullOrEmpty(counter.Reason))
        {
          lock (LockObject)
          {
            counter.CurrentClass = theClass;
            counter.Reason = reason;
            counter.ClassCounts[theClass] = long.MaxValue;
            counter.CurrentMax = long.MaxValue;
            EventsUpdatePlayerClass?.Invoke(player, _classNames[theClass]);
            Log.Debug("Assigning " + player + " as " + theClass + ". " + reason);
            _playersUpdated = true;
          }
        }
      }
    }

    internal void UpdatePlayerClassFromSpell(SpellCast cast, SpellClass theClass)
    {
      if (!_playerToClass.TryGetValue(cast.Caster, out var counter))
      {
        lock (_playerToClass)
        {
          counter = new SpellClassCounter { ClassCounts = [] };
          _playerToClass.TryAdd(cast.Caster, counter);
        }
      }

      lock (counter)
      {
        if (counter.CurrentMax != long.MaxValue)
        {
          long newValue = 1;
          if (cast.SpellData?.Rank > 1)
          {
            newValue = 10;
          }

          if (counter.ClassCounts.TryGetValue(theClass, out var value))
          {
            newValue += value;
          }

          counter.ClassCounts[theClass] = newValue;

          if (newValue > counter.CurrentMax)
          {
            counter.CurrentMax = newValue;
            if (!theClass.Equals(counter.CurrentClass))
            {
              lock (LockObject)
              {
                counter.CurrentClass = theClass;
                counter.Reason = "Class chosen based on " + cast.Spell + ".";
                EventsUpdatePlayerClass?.Invoke(cast.Caster, _classNames[theClass]);
                Log.Debug("Assigning " + cast.Caster + " as " + theClass + " from " + cast.Spell);
                _playersUpdated = true;
              }
            }
          }
        }
      }
    }

    private void TryRemovePetMapping(string name)
    {
      if (!string.IsNullOrEmpty(name))
      {
        lock (LockObject)
        {
          if (_petToPlayer.TryRemove(name, out _))
          {
            _petMappingUpdated = true;
          }
        }
      }
    }

    internal static int FindPossiblePlayerName(string part, out bool isCrossServer, int start = 0, int stop = -1, char end = char.MaxValue)
    {
      isCrossServer = false;
      var dotCount = 0;

      if (part != null)
      {
        if (stop == -1)
        {
          stop = part.Length;
        }

        if (start <= stop && (stop - start) >= 3)
        {
          for (var i = start; i < stop; i++)
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

    internal static bool IsPossiblePlayerName(string part, int stop = -1) => FindPossiblePlayerName(part, out var _, 0, stop) > -1;

    private static void AddMultiCase(IReadOnlyCollection<string> values, ConcurrentDictionary<string, byte> dict)
    {
      if (values.Count != 0)
      {
        foreach (var value in values)
        {
          if (!string.IsNullOrEmpty(value) && value.Length >= 2)
          {
            dict[value] = 1;
            dict[value.ToUpper(CultureInfo.CurrentCulture)] = 1;
            dict[char.ToUpper(value[0], CultureInfo.CurrentCulture) + value[1..]] = 1;
          }
        }
      }
    }

    private class SpellClassCounter
    {
      internal long CurrentMax { get; set; }
      internal SpellClass CurrentClass { get; set; }
      internal Dictionary<SpellClass, long> ClassCounts { get; init; }
      internal string Reason { get; set; } = "";
    }
  }
}
