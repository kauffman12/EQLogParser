using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace EQLogParser
{
  class PlayerManager
  {
    internal event EventHandler<PetMapping> EventsNewPetMapping;
    internal event EventHandler<string> EventsNewVerifiedPet;
    internal event EventHandler<string> EventsNewVerifiedPlayer;
    internal event EventHandler<string> EventsRemoveVerifiedPet;
    internal event EventHandler<string> EventsRemoveVerifiedPlayer;
    internal event EventHandler<PlayerClassMapping> EventsUpdateDefaultPlayerClass;

    internal static PlayerManager Instance = new();
    internal static readonly BitmapImage BerIcon = UiElementUtil.CreateBitmapFromInternalUri(@"pack://application:,,,/icons/Ber.png");
    internal static readonly BitmapImage BrdIcon = UiElementUtil.CreateBitmapFromInternalUri(@"pack://application:,,,/icons/Brd.png");
    internal static readonly BitmapImage BstIcon = UiElementUtil.CreateBitmapFromInternalUri(@"pack://application:,,,/icons/Bst.png");
    internal static readonly BitmapImage ClrIcon = UiElementUtil.CreateBitmapFromInternalUri(@"pack://application:,,,/icons/Clr.png");
    internal static readonly BitmapImage DruIcon = UiElementUtil.CreateBitmapFromInternalUri(@"pack://application:,,,/icons/Dru.png");
    internal static readonly BitmapImage EncIcon = UiElementUtil.CreateBitmapFromInternalUri(@"pack://application:,,,/icons/Enc.png");
    internal static readonly BitmapImage MagIcon = UiElementUtil.CreateBitmapFromInternalUri(@"pack://application:,,,/icons/Mag.png");
    internal static readonly BitmapImage MnkIcon = UiElementUtil.CreateBitmapFromInternalUri(@"pack://application:,,,/icons/Mnk.png");
    internal static readonly BitmapImage NecIcon = UiElementUtil.CreateBitmapFromInternalUri(@"pack://application:,,,/icons/Nec.png");
    internal static readonly BitmapImage PalIcon = UiElementUtil.CreateBitmapFromInternalUri(@"pack://application:,,,/icons/Pal.png");
    internal static readonly BitmapImage RngIcon = UiElementUtil.CreateBitmapFromInternalUri(@"pack://application:,,,/icons/Rng.png");
    internal static readonly BitmapImage RogIcon = UiElementUtil.CreateBitmapFromInternalUri(@"pack://application:,,,/icons/Rog.png");
    internal static readonly BitmapImage ShdIcon = UiElementUtil.CreateBitmapFromInternalUri(@"pack://application:,,,/icons/Shd.png");
    internal static readonly BitmapImage UnkIcon = UiElementUtil.CreateBitmapFromInternalUri(@"pack://application:,,,/icons/Unk.png");
    internal static readonly BitmapImage ShmIcon = UiElementUtil.CreateBitmapFromInternalUri(@"pack://application:,,,/icons/Shm.png");
    internal static readonly BitmapImage WarIcon = UiElementUtil.CreateBitmapFromInternalUri(@"pack://application:,,,/icons/War.png");
    internal static readonly BitmapImage WizIcon = UiElementUtil.CreateBitmapFromInternalUri(@"pack://application:,,,/icons/Wiz.png");

    // static data
    private const int LowConfidenceThreshold = 8;
    private static readonly FrozenSet<string> SecondPerson = new[] { "you", "yourself", "your" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> ThirdPerson = new[] { "himself", "herself", "itself" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, string> _defaultPlayerClass = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _gameGeneratedPets = new();
    private readonly ConcurrentDictionary<string, string> _petToPlayer = new();
    private readonly ConcurrentDictionary<string, ActivePlayerClass> _activePlayerClass = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _takenPetOrPlayerAction = new();
    private readonly ConcurrentDictionary<string, byte> _verifiedPets = new();
    private readonly ConcurrentDictionary<string, double> _verifiedPlayers = new();
    private readonly ConcurrentDictionary<string, byte> _mercs = new();
    private readonly DispatcherTimer _saveTimer;
    private static readonly object LockObject = new();
    private volatile bool _petMappingUpdated;
    private volatile bool _playersUpdated;

    private PlayerManager()
    {
      // Populate generated pets
      ConfigUtil.ReadList(@"data\petnames.txt").ForEach(line => _gameGeneratedPets[line.TrimEnd()] = 1);
      _saveTimer = UiUtil.CreateTimer(SaveTimerTick, 30000, true, DispatcherPriority.Background);
    }

    internal bool IsVerifiedPlayer(string name) => !string.IsNullOrEmpty(name) && (name == Labels.Unassigned || SecondPerson.Contains(name)
      || ThirdPerson.Contains(name) || _verifiedPlayers.ContainsKey(name));
    internal bool IsPetOrPlayerOrMerc(string name) => !string.IsNullOrEmpty(name) && (IsVerifiedPlayer(name) || IsVerifiedPet(name) || IsMerc(name));
    internal bool IsPetOrPlayerOrSpell(string name) => IsPetOrPlayerOrMerc(name) || EQDataStore.Instance.IsPlayerSpell(name);
    internal bool IsMerc(string name) => _mercs.TryGetValue(TextUtils.ToUpper(name), out _);
    internal List<string> GetVerifiedPlayers() => [.. _verifiedPlayers.Keys];
    internal List<string> GetVerifiedPets() => [.. _verifiedPets.Keys];
    internal List<PetMapping> GetPetMappings() => [.. _petToPlayer.Select(kv => new PetMapping(kv.Key, kv.Value))];
    internal void Stop() => _saveTimer?.Stop();

    internal void AddPetToPlayer(string pet, string player, bool init = false)
    {
      if (!string.IsNullOrEmpty(pet) && !string.IsNullOrEmpty(player))
      {
        lock (LockObject)
        {
          if ((!_petToPlayer.TryGetValue(pet, out var value) || value != player) && !IsVerifiedPlayer(pet))
          {
            _petToPlayer[pet] = player;

            if (!init)
            {
              EventsNewPetMapping?.Invoke(this, new PetMapping(pet, player));
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

    internal void AddVerifiedPet(string name, bool init = false)
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
              AddPetToPlayer(name, Labels.Unassigned, init);
            }

            _takenPetOrPlayerAction.TryRemove(name, out _);

            if (_verifiedPets.TryAdd(name, 1))
            {
              if (!init) EventsNewVerifiedPet?.Invoke(this, name);
              _playersUpdated = true;
            }
          }
        }
      }
    }

    internal void AddVerifiedPlayer(string name, double playerTime, bool init = false)
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
            if (!init) EventsNewVerifiedPlayer?.Invoke(this, name);
          }

          _takenPetOrPlayerAction.TryRemove(name, out _);
          if (_verifiedPets.TryRemove(name, out _))
          {
            TryRemovePetMapping(name);
            if (!init) EventsRemoveVerifiedPet?.Invoke(this, name);
          }
        }
      }
    }

    internal string GetDefaultPlayerClass(string name)
    {
      if (!string.IsNullOrEmpty(name) && _defaultPlayerClass.TryGetValue(name, out var className))
      {
        return className;
      }

      return string.Empty;
    }

    internal string GetLastKnownPlayerClass(string name)
    {
      if (string.IsNullOrEmpty(name) || !_activePlayerClass.TryGetValue(name, out var active))
        return GetDefaultPlayerClass(name);

      lock (active)
      {
        var records = active.Records;
        return records.Count > 0 ? records[^1].ClassName : GetDefaultPlayerClass(name);
      }
    }

    internal string GetPlayerClass(string name, double t)
    {
      if (string.IsNullOrEmpty(name) || !_activePlayerClass.TryGetValue(name, out var active))
        return GetDefaultPlayerClass(name);

      ClassRecord[] snapshot;
      lock (active)
      {
        if (active.Records.Count == 0)
        {
          return GetDefaultPlayerClass(name);
        }

        snapshot = [.. active.Records];
      }

      // first index where BeginTime > t
      var idx = UpperBoundByBeginTime(snapshot, t);

      if (idx == 0)
      {
        return snapshot[0].ClassName;  // no <= t, so use next after t
      }

      return snapshot[idx - 1].ClassName; // last <= t
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
            var toRemove = new List<string>();
            foreach (var kv in _petToPlayer)
            {
              if (kv.Value.Equals(name, StringComparison.OrdinalIgnoreCase))
              {
                toRemove.Add(kv.Key);
              }
            }

            foreach (var pet in toRemove)
            {
              TryRemovePetMapping(pet);
            }

            _playersUpdated = true;
          }

          EventsRemoveVerifiedPlayer?.Invoke(this, name);
        }
      }
    }

    internal void Init()
    {
      lock (LockObject)
      {
        _defaultPlayerClass.Clear();
        _petToPlayer.Clear();
        _activePlayerClass.Clear();
        _takenPetOrPlayerAction.Clear();
        _verifiedPets.Clear();
        _verifiedPlayers.Clear();
        _mercs.Clear();

        AddVerifiedPlayer(ConfigUtil.PlayerName, DateUtil.ToDouble(DateTime.Now), true);

        ConfigUtil.ReadPlayers().ForEach(player =>
        {
          if (!string.IsNullOrEmpty(player) && player.Length > 2)
          {
            var parsed = 0d;
            string name;
            string className = null;
            var split = player.Split('=');
            if (split.Length == 2)
            {
              name = split[0];
              var split2 = split[1].Split(',');
              if (split2.Length >= 2)
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

            AddVerifiedPlayer(name, parsed, true);
            SetDefaultPlayerClass(name, className, true);
          }
        });

        var mapping = ConfigUtil.ReadPetMapping();
        foreach (var key in mapping.Keys)
        {
          if (!_verifiedPlayers.ContainsKey(mapping[key]))
          {
            AddVerifiedPlayer(mapping[key], 0d, true);
          }

          AddVerifiedPet(key, true);
          AddPetToPlayer(key, mapping[key], true);
        }

        _petMappingUpdated = false;
      }
    }

    internal void Save()
    {
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
              if (kv.Value != 0 && (now - DateUtil.FromDouble(kv.Value)).TotalDays < 200)
              {
                var output = kv.Key + "=" + Math.Round(kv.Value);
                if (_defaultPlayerClass.TryGetValue(kv.Key, out var className))
                {
                  output += "," + className;
                }

                list.Add(output);
              }
              else
              {
                _petToPlayer.TryRemove(kv.Key, out _);
              }
            }
          }

          ConfigUtil.SavePlayers(list);
        }

        _playersUpdated = false;
      }

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

      // if method is called manually then restart the timer
      _saveTimer?.Stop();
      _saveTimer.Start();
    }

    internal void SetActivePlayerClass(string name, string className, byte confidence, double beginTime)
    {
      if (string.IsNullOrEmpty(name) || !EQDataStore.Instance.IsValidClassName(className) || confidence is < 1 or > 2)
        return;

      var active = _activePlayerClass.GetOrAdd(name, _ => new ActivePlayerClass());

      lock (active)
      {
        // If multiple threads could call this concurrently, consider: lock (active) { ...whole method... }
        var records = active.Records;

        // Detect “new stream in the past” (someone opened an older log)
        if (beginTime < active.LastSeenBeginTime)
        {
          active.AltClassCounts.Clear();
        }

        active.LastSeenBeginTime = beginTime;

        if (records.Count == 0)
        {
          CommitClassRecordSorted(active, className, confidence, beginTime);
          active.AltClassCounts.Clear();
          return;
        }

        // Find where this beginTime belongs (records kept sorted)
        var insertAt = LowerBoundByBeginTime(records, beginTime);

        // Determine the "current" record that applies at beginTime
        var exactAtTime = insertAt < records.Count && records[insertAt].BeginTime == beginTime;
        var currentIndex = exactAtTime ? insertAt : insertAt - 1;

        var currentClass = currentIndex >= 0 ? records[currentIndex].ClassName : null;
        var currentConf = currentIndex >= 0 ? records[currentIndex].Confidence : (byte)0;

        // If class is the same do nothing
        if (className.Equals(currentClass, StringComparison.OrdinalIgnoreCase) && (currentConf == 1 || confidence == 2))
        {
          active.AltClassCounts.Clear();
          return;
        }

        // Upgrade to High Confidence
        if (confidence == 1)
        {
          if (currentIndex < 0 &&
              records.Count > 0 &&
              string.Equals(records[0].ClassName, className, StringComparison.OrdinalIgnoreCase))
          {
            if (records[0].Confidence == 2)
              records[0].Confidence = 1;

            active.AltClassCounts.Clear();
            return;
          }

          if (currentIndex >= 0 &&
              string.Equals(records[currentIndex].ClassName, className, StringComparison.OrdinalIgnoreCase))
          {
            if (records[currentIndex].Confidence == 2)
              records[currentIndex].Confidence = 1;

            active.AltClassCounts.Clear();
            return;
          }

          CommitClassRecordSorted(active, className, confidence, beginTime);
          active.AltClassCounts.Clear();
          return;
        }
        else
        {
          // alternative hypothesis -> count it.
          if (!active.AltClassCounts.TryGetValue(className, out var pending))
          {
            active.AltClassCounts.Clear();
            active.AltClassCounts[className] = new PendingClass { Count = 1, FirstTime = beginTime };
            return;
          }

          pending.Count++;
          active.AltClassCounts[className] = pending;

          if (pending.Count >= LowConfidenceThreshold)
          {
            CommitClassRecordSorted(active, className, confidence, pending.FirstTime);
            active.AltClassCounts.Clear();
          }
        }
      }
    }


    // only do this from user interaction
    internal void SetDefaultPlayerClass(string name, string className, bool init = false)
    {
      if (!string.IsNullOrEmpty(name) && EQDataStore.Instance.IsValidClassName(className))
      {
        _defaultPlayerClass[name] = className;

        if (!init)
        {
          // make sure player data is saved
          _verifiedPlayers[name] = DateUtil.ToDouble(DateTime.Now);
          EventsUpdateDefaultPlayerClass?.Invoke(this, new PlayerClassMapping { Player = name, ClassName = className });
        }
      }
    }

    internal static bool IsPossiblePlayerName(string part, int stop = -1) => FindPossiblePlayerName(part, out var _, 0, stop) > -1;

    internal static BitmapImage GetPlayerIcon(string className)
    {
      var icon = UnkIcon;
      if (EQDataStore.Instance.GetClassEnum(className) is { } theClass)
      {
        icon = theClass switch
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
      }
      return icon;
    }

    internal static string ReplacePlayer(string name, string alternative)
    {
      var result = name;

      if (ThirdPerson.Contains(name))
      {
        result = alternative;
      }
      else if (SecondPerson.Contains(name))
      {
        result = ConfigUtil.PlayerName;
      }

      return result;
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

    private void SaveTimerTick(object sender, EventArgs e) => Save();

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

    private static int LowerBoundByBeginTime(List<ClassRecord> records, double time)
    {
      int lo = 0, hi = records.Count;
      while (lo < hi)
      {
        var mid = lo + ((hi - lo) >> 1);
        if (records[mid].BeginTime < time)
          lo = mid + 1;
        else
          hi = mid;
      }
      return lo; // first index with BeginTime >= time
    }

    private static int UpperBoundByBeginTime(ClassRecord[] records, double t)
    {
      int lo = 0, hi = records.Length;
      while (lo < hi)
      {
        var mid = lo + ((hi - lo) >> 1);
        if (records[mid].BeginTime <= t) lo = mid + 1;
        else hi = mid;
      }
      return lo;
    }

    private static void CommitClassRecordSorted(ActivePlayerClass active, string className, byte confidence, double beginTime)
    {
      var records = active.Records;
      var idx = LowerBoundByBeginTime(records, beginTime);

      // Exact-time record exists
      if (idx < records.Count && records[idx].BeginTime == beginTime)
      {
        var existing = records[idx];

        // Same class at same time: only upgrade confidence (never downgrade)
        if (string.Equals(existing.ClassName, className, StringComparison.OrdinalIgnoreCase))
        {
          if (existing.Confidence == 2 && confidence == 1)
            existing.Confidence = 1;

          return; // nothing else to do
        }

        // Different class at same time: replace the boundary record
        records[idx] = new ClassRecord
        {
          ClassName = className,
          Confidence = confidence,
          BeginTime = beginTime
        };

        CoalesceAround(records, idx);
        return;
      }

      // No exact-time record: insert new boundary
      records.Insert(idx, new ClassRecord
      {
        ClassName = className,
        Confidence = confidence,
        BeginTime = beginTime
      });

      CoalesceAround(records, idx);
    }

    private static void CoalesceAround(List<ClassRecord> records, int idx)
    {
      if (records.Count == 0 || idx < 0 || idx >= records.Count)
        return;

      // Merge with previous if same class (current record becomes redundant)
      if (idx > 0 && string.Equals(records[idx - 1].ClassName, records[idx].ClassName, StringComparison.OrdinalIgnoreCase))
      {
        records.RemoveAt(idx);
        idx--;
        if (idx < 0) return;
      }

      // Merge with next if same class (next record becomes redundant)
      if (idx + 1 < records.Count && string.Equals(records[idx + 1].ClassName, records[idx].ClassName, StringComparison.OrdinalIgnoreCase))
      {
        records.RemoveAt(idx + 1);
      }
    }

    private struct PendingClass
    {
      public int Count;
      public double FirstTime;
    }

    private class ClassRecord : TimedAction
    {
      internal string ClassName { get; init; }
      internal byte Confidence { get; set; }
    }

    private sealed class ActivePlayerClass
    {
      internal List<ClassRecord> Records { get; } = [];
      internal Dictionary<string, PendingClass> AltClassCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
      internal double LastSeenBeginTime { get; set; } = double.NegativeInfinity;
    }
  }
}
