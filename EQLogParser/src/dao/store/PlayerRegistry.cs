using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace EQLogParser
{
  class PlayerRegistry : ILifecycle
  {
    internal event Action<PetMapping> EventsNewPetMapping;
    internal event Action<string> EventsNewVerifiedPet;
    internal event Action<string> EventsNewVerifiedPlayer;
    internal event Action<string> EventsRemoveVerifiedPet;
    internal event Action<string> EventsRemoveVerifiedPlayer;
    internal event Action<PlayerClassMapping> EventsUpdateDefaultPlayerClass;

    // singleton
    internal static PlayerRegistry Instance { get; } = new();

    // Icon file names — resolved to BitmapImage by the UI layer via GetPlayerIconPath()
    // Only used for default/fallback icon resolution in the core layer
    internal const string UnkIconName = "Unk.png";

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
    private readonly Timer _saveTimer;
    private readonly TimeSpan _saveInterval = TimeSpan.FromSeconds(30);
    private readonly object _lock = new();
    private volatile bool _petMappingUpdated;
    private volatile bool _playersUpdated;

    private PlayerRegistry()
    {
      // Populate generated pets
      ConfigUtil.ReadList(@"data\petnames.txt").ForEach(line => _gameGeneratedPets[line.TrimEnd()] = 1);
      _saveTimer = new Timer(SaveTimerTick, null, _saveInterval, _saveInterval);
      LifecycleManager.Register(this);
    }

    internal bool IsVerifiedPlayer(string name) => !string.IsNullOrEmpty(name) && (name == Labels.Unassigned || SecondPerson.Contains(name)
      || ThirdPerson.Contains(name) || _verifiedPlayers.ContainsKey(name));
    internal bool IsPetOrPlayerOrMerc(string name) => !string.IsNullOrEmpty(name) && (IsVerifiedPlayer(name) || IsVerifiedPet(name) || IsMerc(name));
    internal bool IsPetOrPlayerOrSpell(string name) => IsPetOrPlayerOrMerc(name) || EQDataStore.Instance.IsPlayerSpell(name);
    internal bool IsMerc(string name) => _mercs.TryGetValue(TextUtils.ToUpper(name), out _);
    internal List<string> GetVerifiedPlayers() => [.. _verifiedPlayers.Keys];
    internal List<string> GetVerifiedPets() => [.. _verifiedPets.Keys];
    internal List<PetMapping> GetPetMappings() => [.. _petToPlayer.Select(kv => new PetMapping(kv.Key, kv.Value))];

    public void Clear(bool serverChanged = true)
    {
      if (serverChanged)
      {
        if (!string.IsNullOrEmpty(ConfigUtil.ServerName))
        {
          Save();
        }

        lock (_lock)
        {
          _defaultPlayerClass.Clear();
          _petToPlayer.Clear();
          _activePlayerClass.Clear();
          _takenPetOrPlayerAction.Clear();
          _verifiedPets.Clear();
          _verifiedPlayers.Clear();
          _mercs.Clear();
          _playersUpdated = false;
          _petMappingUpdated = false;
        }
      }
    }

    public void Shutdown()
    {
      Clear();
      _saveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    internal void AddPetToPlayer(string pet, string player, bool init = false)
    {
      var needEvent = false;

      lock (_lock)
      {
        needEvent = AddPetToPlayerNoLock(pet, player, init);
      }

      if (needEvent)
      {
        EventsNewPetMapping?.Invoke(new PetMapping(pet, player));
      }
    }

    internal void AddMerc(string name)
    {
      if (string.IsNullOrEmpty(name))
        return;

      name = string.Intern(TextUtils.ToUpper(name));
      _mercs[name] = 1;
    }

    internal void AddVerifiedPet(string name, bool init = false)
    {
      if (string.IsNullOrEmpty(name))
        return;

      var needEvent = false;
      var petMappingEvent = false;
      var petMapping = default(PetMapping);

      lock (_lock)
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
            petMappingEvent = AddPetToPlayerNoLock(name, Labels.Unassigned, init);
            if (petMappingEvent)
              petMapping = new PetMapping(name, Labels.Unassigned);
          }

          _takenPetOrPlayerAction.TryRemove(name, out _);

          if (_verifiedPets.TryAdd(name, 1) && !init)
          {
            _playersUpdated = true;
            needEvent = true;
          }
        }
      }

      if (petMappingEvent) EventsNewPetMapping?.Invoke(petMapping);
      if (needEvent) EventsNewVerifiedPet?.Invoke(name);
    }

    internal void AddVerifiedPlayer(string name, double playerTime, bool init = false)
    {
      if (string.IsNullOrEmpty(name))
        return;

      var needPlayerEvent = false;
      var needPetEvent = false;

      lock (_lock)
      {
        if (_verifiedPlayers.TryGetValue(name, out var lastTime))
        {
          if (playerTime > lastTime)
          {
            _verifiedPlayers[name] = playerTime;
            _playersUpdated = true;
          }
        }
        else
        {
          name = string.Intern(name);
          _verifiedPlayers[name] = playerTime;

          if (!init)
          {
            needPlayerEvent = true;
            _playersUpdated = true;
          }
        }

        _takenPetOrPlayerAction.TryRemove(name, out _);

        if (_verifiedPets.TryRemove(name, out _))
        {
          TryRemovePetMappingNoLock(name);

          if (!init)
          {
            _playersUpdated = true;
            needPetEvent = true;
          }
        }
      }

      if (needPlayerEvent) EventsNewVerifiedPlayer?.Invoke(name);
      if (needPetEvent) EventsRemoveVerifiedPet?.Invoke(name);
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
      if (string.IsNullOrEmpty(name))
        return;

      var needEvent = false;
      lock (_lock)
      {
        if (_verifiedPets.TryRemove(name, out _))
        {
          TryRemovePetMappingNoLock(name);
          needEvent = true;
        }
      }

      if (needEvent) EventsRemoveVerifiedPet?.Invoke(name);
    }

    internal void RemoveVerifiedPlayer(string name)
    {
      if (string.IsNullOrEmpty(name))
        return;

      var needEvent = false;

      lock (_lock)
      {
        if (_verifiedPlayers.TryRemove(name, out _))
        {
          var toRemove = new List<string>();
          foreach (var kv in _petToPlayer)
          {
            if (kv.Value.Equals(name, StringComparison.OrdinalIgnoreCase))
              toRemove.Add(kv.Key);
          }

          foreach (var pet in toRemove)
            TryRemovePetMappingNoLock(pet);

          _playersUpdated = true;
          needEvent = true;
        }
      }

      if (needEvent) EventsRemoveVerifiedPlayer?.Invoke(name);
    }

    internal void Init()
    {
      lock (_lock)
      {
        _defaultPlayerClass.Clear();
        _petToPlayer.Clear();
        _activePlayerClass.Clear();
        _takenPetOrPlayerAction.Clear();
        _verifiedPets.Clear();
        _verifiedPlayers.Clear();
        _mercs.Clear();
        _playersUpdated = false;
        _petMappingUpdated = false;

        AddVerifiedPlayer(ConfigUtil.PlayerName, DateUtil.ToDotNetSeconds(DateTime.Now), true);

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
      List<string> playerList = null;
      List<KeyValuePair<string, string>> petList = null;
      var serverName = ConfigUtil.ServerName;

      lock (_lock)
      {
        if (_playersUpdated)
        {
          playerList = [];
          var now = DateTime.Now;
          foreach (var kv in _verifiedPlayers)
          {
            if (!string.IsNullOrEmpty(kv.Key) && IsPossiblePlayerName(kv.Key))
            {
              if (kv.Value != 0 && (now - DateUtil.FromDotNetSeconds(kv.Value)).TotalDays < 200)
              {
                var output = kv.Key + "=" + Math.Round(kv.Value);
                if (_defaultPlayerClass.TryGetValue(kv.Key, out var className))
                {
                  output += "," + className;
                }

                playerList.Add(output);
              }
              else
              {
                _petToPlayer.TryRemove(kv.Key, out _);
              }
            }
          }

          _playersUpdated = false;
        }

        if (_petMappingUpdated)
        {
          // no generated or unassigned pets but allow for warders
          var filtered = _petToPlayer.Where(kv => !_gameGeneratedPets.ContainsKey(kv.Key) && kv.Value != Labels.Unassigned &&
            (IsPossiblePlayerName(kv.Key) || kv.Key.EndsWith("`s warder", StringComparison.OrdinalIgnoreCase)));
          petList = [.. filtered];
          _petMappingUpdated = false;
        }

        // if method is called manually then restart the timer
        _saveTimer?.Change(_saveInterval, _saveInterval);
      }

      if (playerList != null)
      {
        ConfigUtil.SavePlayers(playerList, serverName);
      }

      if (petList != null)
      {
        ConfigUtil.SavePetMapping(petList, serverName);
      }
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
        var needEvent = false;

        lock (_lock)
        {
          _defaultPlayerClass[name] = className;

          if (!init)
          {
            // make sure player data is saved
            _verifiedPlayers[name] = DateUtil.ToDotNetSeconds(DateTime.Now);
            _playersUpdated = true;
            needEvent = true;
          }
        }

        if (needEvent) EventsUpdateDefaultPlayerClass?.Invoke(new PlayerClassMapping { Player = name, ClassName = className });
      }
    }

    internal static bool IsPossiblePlayerName(string part, int stop = -1) => FindPossiblePlayerName(part, out var _, 0, stop) > -1;

    internal static string GetPlayerIconPath(string className)
    {
      if (EQDataStore.Instance.GetClassEnum(className) is { } theClass)
      {
        return theClass switch
        {
          SpellClass.Ber => "Ber.png",
          SpellClass.Brd => "Brd.png",
          SpellClass.Bst => "Bst.png",
          SpellClass.Clr => "Clr.png",
          SpellClass.Dru => "Dru.png",
          SpellClass.Enc => "Enc.png",
          SpellClass.Mag => "Mag.png",
          SpellClass.Mnk => "Mnk.png",
          SpellClass.Nec => "Nec.png",
          SpellClass.Pal => "Pal.png",
          SpellClass.Rng => "Rng.png",
          SpellClass.Rog => "Rog.png",
          SpellClass.Shd => "Shd.png",
          SpellClass.Shm => "Shm.png",
          SpellClass.War => "War.png",
          SpellClass.Wiz => "Wiz.png",
          _ => UnkIconName
        };
      }
      return UnkIconName;
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

    private void SaveTimerTick(object state) => Save();

    private bool TryRemovePetMappingNoLock(string name)
    {
      if (!string.IsNullOrEmpty(name) && _petToPlayer.TryRemove(name, out _))
      {
        _petMappingUpdated = true;
        return true;
      }

      return false;
    }

    private bool AddPetToPlayerNoLock(string pet, string player, bool init = false)
    {
      if (string.IsNullOrEmpty(pet) || string.IsNullOrEmpty(player))
        return false;

      if ((!_petToPlayer.TryGetValue(pet, out var value) || value != player) && !IsVerifiedPlayer(pet))
      {
        _petToPlayer[pet] = player;

        if (!init)
          _petMappingUpdated = true;

        return !init;
      }

      return false;
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
