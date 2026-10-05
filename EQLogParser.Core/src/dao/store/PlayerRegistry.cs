using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace EQLogParser
{
  /* Process-lifetime singleton: its Timer is stopped by Shutdown() when LifecycleManager tears the app
   * down (and it is never re-created afterwards), so it is deliberately not IDisposable. */
  [SuppressMessage("Microsoft.Design", "CA1001:TypesThatOwnDisposableFieldsShouldBeDisposable",
    Justification = "Singleton owned by LifecycleManager; the save timer is stopped in Shutdown(). See class comment.")]
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
    /*
     * One calendar dial for both files this class writes. A row retires when the application has not seen its subject
     * for this many days — players.txt rows carry their own sighting time, and petmapping.txt rows now carry one too
     * (`Fluffy=Ziggy|4021234560`), so the same sentence is true of both: *seen lately, kept; unseen, gone*. Two numbers
     * here would mean a player can be forgotten while their pet is still remembered, which is not a statement anybody
     * chose. Hand-written rows carry no time and are therefore statements rather than sightings: they never retire.
     */
    internal const int StaleDays = 200;

    /*
     * The pet side's sighting clock, in the same .NET seconds the player rows use. It is deliberately separate from
     * _petToPlayer: that dictionary is the OPERATOR's data (the Pet Owners grid edits it and its Owner text goes out to
     * the UI), while this one is bookkeeping this application keeps about when it last saw a name in a log. The stamp
     * is wall-clock rather than log time on purpose — "when did WE last see this pet", because what ages out is our
     * memory, and playing through last year's capture refreshes it today.
     */
    /*
     * The stamp this process hands out, so a name sighted many times in one session is written once (the hot path is
     * every possessive damage line) and the first sighting after a load is what replaces an old stamp. A session that
     * ran for years would stop refreshing; a raid night is measured in hours.
     */
    private static readonly double SessionStamp = DateUtil.ToDotNetSeconds(DateTime.Now);

    private readonly ConcurrentDictionary<string, double> _petSeenAt = new();

    private readonly ConcurrentDictionary<string, string> _petToPlayer = new();
    private readonly ConcurrentDictionary<string, ActivePlayerClass> _activePlayerClass = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _takenPetOrPlayerAction = new();
    private readonly ConcurrentDictionary<string, byte> _verifiedPets = new();
    private readonly ConcurrentDictionary<string, double> _verifiedPlayers = new();

    private readonly ConcurrentDictionary<string, byte> _mercs = new(StringComparer.OrdinalIgnoreCase);
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

    /*
     * The three frozen word lists, as one question: is this STRING the local player or a pronoun standing for somebody
     * ("you", "your", "himself", "Unassigned")? Shared with IdentityLookup so the seam that replaced players.txt answers
     * those rows exactly as the roster did - they are vocabulary, not knowledge, and no capture can place them. See the
     * lists above for why they can never be rebuilt from evidence.
     */
    internal static bool IsPersonWord(string name) =>
      !string.IsNullOrEmpty(name) && (name == Labels.Unassigned || SecondPerson.Contains(name) || ThirdPerson.Contains(name));

    internal bool IsVerifiedPlayer(string name) => IsPersonWord(name) || _verifiedPlayers.ContainsKey(name);
    internal bool IsPetOrPlayerOrMerc(string name) => !string.IsNullOrEmpty(name) && (IsVerifiedPlayer(name) || IsVerifiedPet(name) || IsMerc(name));
    internal bool IsPetOrPlayerOrSpell(string name) => IsPetOrPlayerOrMerc(name) || CombatRecordLookup.IsPlayerSpell(name);
    internal bool IsMerc(string name) => _mercs.TryGetValue(StringCache.GetOrAdd(name), out _);
    internal List<string> GetVerifiedPlayers() => [.. _verifiedPlayers.Keys];

    /// <summary>The roster's evidence stamp for a name: true with the unix seconds it was last confirmed, false when
    /// the roster does not know it. A TRUE result with 0 means a hand-typed (or legacy-file) entry - nothing has ever
    /// observed this name in a line. Read by ClassificationReport to tell learned rows from typed ones.</summary>
    internal bool TryGetVerifiedEntry(string name, out double lastSeenUnix)
    {
      lastSeenUnix = 0;
      if (string.IsNullOrEmpty(name) || !_verifiedPlayers.TryGetValue(name, out var seen)) return false;
      lastSeenUnix = seen;
      return true;
    }

    // name -> log time the player-side evidence appeared (used by the the derivation's Phase 1
    // seed so ingest-time identity replay matches what IsPetOrPlayerOrMerc saw at each line)
    internal IReadOnlyDictionary<string, double> GetVerifiedPlayerTimes() => _verifiedPlayers;
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
          _petSeenAt.Clear();
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

      /*
       * Make sure it is a pet too — and carry `init`, because Init() registers the pairs it just read out of
       * petmapping.txt. A load is not a sighting: without this every row in the file got today's date at startup, so
       * the aging dial could never expire anything while the file looked freshly stamped.
       */
      AddVerifiedPet(pet, init);

      if (needEvent)
      {
        EventsNewPetMapping?.Invoke(new PetMapping(pet, player));
      }
    }

    internal void AddMerc(string name)
    {
      if (string.IsNullOrEmpty(name))
        return;

      name = StringCache.GetOrAdd(name);
      _mercs[name] = 1;
    }

    internal void AddVerifiedPet(string name, bool init = false)
    {
      if (string.IsNullOrEmpty(name))
        return;

      var needEvent = false;
      var petMappingEvent = false;
      var petMapping = default(PetMapping);

      /*
       * A sighting refreshes the row's clock — including for a pet already known, which is the case that matters: the
       * mapping loaded from petmapping.txt at Init and the pet walked back into the log. Without this the row would age
       * out while being seen every night. The compare is what keeps it off the hot path: after the first sighting in a
       * session there is nothing left to write.
       */
      if (!init && (!_petSeenAt.TryGetValue(name, out var lastSeen) || lastSeen < SessionStamp))
      {
        _petSeenAt[name] = SessionStamp;
      }

      lock (_lock)
      {
        if (!_verifiedPets.ContainsKey(name))
        {
          name = StringCache.GetOrAdd(name);

          if (_verifiedPlayers.TryRemove(name, out _))
          {
            _playersUpdated = true;
          }

          if (IsPossiblePetName(name) && !_petToPlayer.ContainsKey(name))
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

      if (name.Equals("You", StringComparison.OrdinalIgnoreCase))
      {
        name = ConfigUtil.PlayerName;
      }

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
          name = StringCache.GetOrAdd(name);
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

        // also remove from merc list if it was there
        if (_mercs.TryRemove(name, out _))
        {
          if (!init) _playersUpdated = true;
        }
      }

      if (needPlayerEvent) EventsNewVerifiedPlayer?.Invoke(name);
      if (needPetEvent) EventsRemoveVerifiedPet?.Invoke(name);
    }

    /*
     * An operator assertion - the "Set as Player" menus and nothing else.
     */
    internal void AddVerifiedPlayerByOperator(string name, double playerTime)
    {
      if (string.IsNullOrEmpty(name))
        return;

      if (name.Equals("You", StringComparison.OrdinalIgnoreCase))
        name = ConfigUtil.PlayerName;

      AddVerifiedPlayer(name, playerTime);
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

      lock (_lock)
      {
        /*
         * A plain eviction, and it says nothing else: the first loot line of a later capture is free to teach this
         * name all over again, because that line is evidence and an old "not one of ours" is not. (The roster used to
         * keep a `!Name` tombstone that refused every learning path permanently. Nothing could ever write one - the
         * only control that called it lost its menu entry on the same day the tombstone was invented, three days
         * after the last release - so the veto shipped as machinery with no door and is gone.)
         *
         * It deliberately does NOT reach into _petToPlayer any more. "Ziggy is not a player" and "Fluffy belongs
         * to Ziggy" are separate statements, and the old cascade threw away rows of petmapping.txt that the
         * operator would otherwise have to retype - including, because Init() seeds every mapping owner into
         * _verifiedPlayers at time 0, the mappings of perfectly good raiders whose only evidence was the file.
         *
         * "Take that name off the list" is all this says. When the operator means "that is the enemy", that is an
         * assertion and it belongs on the engine's manual override (R10, IdentityKind.Npc), where it feeds opposition
         * instead of merely silencing a guess.
         */
        _verifiedPlayers.TryRemove(name, out _);
        _playersUpdated = true;
      }

      EventsRemoveVerifiedPlayer?.Invoke(name);
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
        _petSeenAt.Clear();
        _verifiedPlayers.Clear();
        _mercs.Clear();
        _playersUpdated = false;
        _petMappingUpdated = false;

        var saved = ConfigUtil.ReadPlayers();

        /*
         * The operator's own character is a player by definition, so this one name is not allowed to be in shadow:
         * You-mapping across the whole app reads IsVerifiedPlayer("You"'s target). With no log open there IS no such
         * character - ConfigUtil.PlayerName is null before MainWindow picks a file (and in any headless run) - and
         * ConcurrentDictionary takes that as a null key and throws, which would read as "loading the roster died".
         */
        if (!string.IsNullOrEmpty(ConfigUtil.PlayerName))
        {
          AddVerifiedPlayer(ConfigUtil.PlayerName, DateUtil.ToDotNetSeconds(DateTime.Now), true);
        }

        foreach (var player in saved)
        {
          /*
           * A leading '!' is IGNORED as input, not honoured as a verdict: the file is hand-editable and '!' is the shape a
           * person reaches for to cross something out, so a line wearing it must not come back as a player named "!Foo".
           * It carries no meaning beyond that - nothing consults it, and "this name is not one of ours" is an affirmative
           * claim that belongs on the identity override (Set as NPC). The permanent rejection this line used to implement
           * was deleted: no shipped build could write one (docs/DesignNotes.md → "A veto nobody could switch on").
           */
          if (!string.IsNullOrEmpty(player) && player.Length > 2 && !player.StartsWith('!'))
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

            // Hand-edited files may carry the literal "You"; it means whoever is playing now. (The old code
            // assigned to the ForEach parameter here, which was then never read - a foreach variable cannot be
            // reassigned, so the remap happens on `name`, where it does something.)
            if ("You".Equals(name, StringComparison.OrdinalIgnoreCase))
            {
              name = ConfigUtil.PlayerName;
            }

            AddVerifiedPlayer(name, parsed, true);
            SetDefaultPlayerClass(name, className, true);
          }
        }

        var mapping = ConfigUtil.ReadPetMapping();
        foreach (var key in mapping.Keys)
        {
          if (!mapping.TryGetValue(key, out var value) || "You".Equals(key, StringComparison.OrdinalIgnoreCase))
            continue;

          /*
           * The optional `|<dotnet seconds>` tail is this application's sighting stamp, not part of the owner's name —
           * stripped here so nothing downstream (the Pet Owners grid, +Pets folding, the meters) ever sees it. A row
           * without the tail came from an operator's keyboard or from a build that did not stamp, and is kept: rows
           * with no time are statements, and StaleDays only ever retires observations.
           */
          var bar = value.IndexOf('|');
          if (bar >= 0)
          {
            if (double.TryParse(value.AsSpan(bar + 1), out var seenAt) && seenAt > 0) _petSeenAt[key] = seenAt;
            value = value[..bar];
          }

          if ("You".Equals(value, StringComparison.OrdinalIgnoreCase))
          {
            value = ConfigUtil.PlayerName;
          }

          if (!_verifiedPlayers.ContainsKey(value))
          {
            AddVerifiedPlayer(value, 0d, true);
          }

          AddVerifiedPet(key, true);
          AddPetToPlayer(key, value, true);
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

          /*
           * Written sorted because this file is hand-edited: a stable order is what makes it diffable, and an
           * operator who cannot see what changed cannot audit the classifier that wrote it.
           */
          foreach (var kv in _verifiedPlayers.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
          {
            if (string.IsNullOrEmpty(kv.Key) || !IsPossiblePlayerName(kv.Key) ||
              "You".Equals(kv.Key, StringComparison.OrdinalIgnoreCase))
            {
              continue;
            }

            /*
             * A row with no time is a statement rather than an observation - hand-typed, or an owner seeded from
             * petmapping.txt - so it has no age to expire. Only evidence-dated rows retire.
             *
             * The expiry used to be the entire rule (kv.Value != 0 && seen recently), which deleted every
             * hand-typed name on the next save AND reached into _petToPlayer to drop its pet mapping with it:
             * Init() loads plain names at time 0, so one newly learned name was enough to cost an operator both
             * their curated entries and their ownership rows, silently.
             */
            if (kv.Value != 0 && (now - DateUtil.FromDotNetSeconds(kv.Value)).TotalDays >= StaleDays)
            {
              continue;
            }

            var hasClass = _defaultPlayerClass.TryGetValue(kv.Key, out var className);
            playerList.Add(kv.Value == 0 && !hasClass
              ? kv.Key
              : kv.Key + "=" + Math.Round(kv.Value) + (hasClass ? "," + className : ""));
          }

          _playersUpdated = false;
        }

        if (_petMappingUpdated)
        {
          // no generated or unassigned pets but allow for warders
          var filtered = _petToPlayer.Where(kv => !_gameGeneratedPets.ContainsKey(kv.Key) && IsPossiblePetName(kv.Key));
          var now = DateTime.Now;

          petList = [];
          foreach (var kv in filtered)
          {
            var seenAt = _petSeenAt.TryGetValue(kv.Key, out var stamped) ? stamped : 0d;

            // Same law as the player rows above: an un-dated row is a statement and stays; a sighting older than the
            // dial means the pet has not been in any log this application read, and its row leaves with it. That is
            // what makes the file stop carrying every summon from the last four years.
            if (seenAt != 0 && (now - DateUtil.FromDotNetSeconds(seenAt)).TotalDays >= StaleDays)
              continue;

            petList.Add(new KeyValuePair<string, string>(kv.Key, seenAt == 0 ? kv.Value : $"{kv.Value}|{Math.Round(seenAt)}"));
          }

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
      if (string.IsNullOrEmpty(name) || !CombatRecordLookup.IsValidClassName(className) || confidence is < 1 or > 2)
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
      if (!string.IsNullOrEmpty(name) && CombatRecordLookup.IsValidClassName(className))
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

    internal static bool IsPossiblePlayerName(string part, int stop = -1)
    {
      var len = FindPossiblePlayerName(part, out var _, 0, stop);
      if (len > 0 && string.Equals(part[..len], Labels.Unk, StringComparison.OrdinalIgnoreCase))
      {
        return false;
      }
      return len > -1;
    }
    internal static bool IsPossiblePetName(string name) =>
      IsPossiblePlayerName(name) || name?.EndsWith("`s warder", StringComparison.OrdinalIgnoreCase) == true;

    internal static string GetPlayerIconPath(string className)
    {
      if (CombatRecordLookup.ClassEnumByName(className) is { } theClass)
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
        {
          // Learning a pair — or re-assigning one in the Pet Owners grid — is a sighting, and it restarts the row's
          // clock. That is how an operator's edit keeps a pet that would otherwise have aged out of the file.
          _petSeenAt[pet] = SessionStamp;
          _petMappingUpdated = true;
        }

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
