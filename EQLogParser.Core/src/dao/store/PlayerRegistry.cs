using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using log4net;

/*
 * Annotations only, no null-flow analysis (the project builds with Nullable=disable): TryReadRosterLine answers with an
 * optional class name, and saying so is what the annotation is for. Same convention as IdentityPriorStore.
 */
#nullable enable annotations

namespace EQLogParser
{
  /* Process-lifetime singleton: its Timer is stopped by Shutdown() when LifecycleManager tears the app
   * down (and it is never re-created afterwards), so it is deliberately not IDisposable. */
  [SuppressMessage("Microsoft.Design", "CA1001:TypesThatOwnDisposableFieldsShouldBeDisposable",
    Justification = "Singleton owned by LifecycleManager; the save timer is stopped in Shutdown(). See class comment.")]
  class PlayerRegistry : ILifecycle
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    internal event Action<PetMapping> EventsNewPetMapping;
    internal event Action<string> EventsNewVerifiedPet;
    internal event Action<string> EventsNewVerifiedPlayer;
    internal event Action<string> EventsRemoveVerifiedPet;
    internal event Action<string> EventsRemoveVerifiedPlayer;
    internal event Action<PlayerClassMapping> EventsUpdateDefaultPlayerClass;

    // singleton
    internal static PlayerRegistry Instance { get; } = new();

    /*
     * Two numbers, asked so that a decision can be made with them rather than about them (PerfCounters: handles come from
     * field initializers, so no name lookup and no lock in the measured path; both are `uiThread: false` because this store
     * is filled by the parsing thread and drained by the stats builders — neither is the UI thread, and naming either in a
     * stall's "in progress" would send a reader to the wrong window).
     *
     *   reg.class  — timed: count, average and worst of GetPlayerClass, the read every board row pays for its Class column.
     *                This is the number that decides whether names need flat ids. A live raid printing 20,000 reads/s at a
     *                few microseconds says string-keyed lookups are not the wall and "flat name -> id" (weeks of work,
     *                tens of MB) is not worth starting; a worst-case in milliseconds says it is.
     *   reg.write  — counted: write calls the parse makes (a verified-player claim, a pet pair, a class sighting). Volume
     *                here is what a possessive line costs — one `X`s pet` line asks for a claim AND a mapping, and a capture
     *                holds half a million of them.
     *
     * Neither reaches eqlogparser.log unless the operator asked: PerfJournal.Enabled gates the heartbeat that prints them
     * (docs/DesignNotes.md → "Instrumenting the UI thread"). Cheap spans sort themselves out of the table when they are
     * cheap, which is also an answer.
     */
    private static readonly int ClassReadId = PerfCounters.Register("reg.class", uiThread: false);
    private static readonly int WriteId = PerfCounters.Register("reg.write", uiThread: false);

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


    private readonly ConcurrentDictionary<string, string> _petToPlayer = new();
    private readonly ConcurrentDictionary<string, ActivePlayerClass> _activePlayerClass = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _takenPetOrPlayerAction = new();
    private readonly ConcurrentDictionary<string, byte> _verifiedPets = new();
    private readonly ConcurrentDictionary<string, double> _verifiedPlayers = new();

    private readonly ConcurrentDictionary<string, byte> _mercs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _saveTimer;
    private readonly TimeSpan _saveInterval = TimeSpan.FromSeconds(30);
    private readonly object _lock = new();

    /*
     * MEMBERSHIP HAS NO FILE OF ITS OWN ANY MORE (2026-10-09). players.txt used to be rewritten from here every 30 seconds; the
     * roster now lives in identity-priors.txt's roster lane, which models the same statement (the name, the dotnet-epoch second
     * this application last saw it) plus the class players.txt had nowhere to put. This flag says "membership rows were added or
     * taken away since the last flush", so Save's timer still batches a night of sightings into ONE file write.
     *
     * ONE flag for both lanes, because one file carries them: membership (`Ours`) and ownership (`Owner`) are columns of the same
     * ledger row, and the ledger writes that row itself. Registry lock -> ledger gate is the ONLY nesting order in this class (Init
     * reads the ledger's lanes under _lock; the ledger never calls back here — StaleDays is a constant). Flip that order and the
     * flush deadlocks against a busy parse thread.
     */
    private volatile bool _ledgerDirty;

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
          _verifiedPlayers.Clear();
          _mercs.Clear();
          _ledgerDirty = false;
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
      // Load is not a sighting, and it is not traffic either: Init() replays every stored pair through here.
      if (!init) PerfCounters.Note(WriteId);

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
       * A sighting refreshes the row's clock — including for a pet this application already knew, which is the case that matters:
       * the mapping that came in from memory whose summon walked back into tonight's log. The clock lives on the LEDGER row now
       * (`RememberPet` moves `SeenAtS` forward and never back), so there is no per-name stamp left to keep here.
       */
      if (!init) TouchPetStampNoLock(name);

      lock (_lock)
      {
        if (!_verifiedPets.ContainsKey(name))
        {
          name = StringCache.GetOrAdd(name);

          if (_verifiedPlayers.TryRemove(name, out _))
          {
            /*
             * The name is a pet now, so it comes off the roster — which since 2026-10-09 means the LEDGER's membership bit rather
             * than a row of players.txt. It stays an eviction, not a veto: a later capture that meets this name as a raider is free
             * to put it back, and a remembered VERDICT on that ledger row is a different statement and survives.
             */
            ForgetInLedger(name);
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
            // _verifiedPets is session memory (Init clears it, a log refills it); the roster write above is the durable half.
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

      if (!init) PerfCounters.Note(WriteId);

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

            // Gated on !init inside, because a LOAD is not a sighting: stamping on a load is how petmapping.txt aged its whole file out.
            RememberInLedger(name, playerTime, loading: init);
          }
        }
        else
        {
          name = StringCache.GetOrAdd(name);
          _verifiedPlayers[name] = playerTime;

          if (!init)
          {
            needPlayerEvent = true;
            RememberInLedger(name, playerTime, loading: init);
          }
        }

        _takenPetOrPlayerAction.TryRemove(name, out _);

        if (_verifiedPets.TryRemove(name, out _))
        {
          TryRemovePetMappingNoLock(name);

          // The roster write above already said the durable part (this name is one of ours); the pet list is session memory.
          if (!init) needPetEvent = true;
        }

        // also remove from merc list if it was there — session memory; the roster write above is what says the durable part
        if (_mercs.TryRemove(name, out _))
        {
        }
      }

      if (needPlayerEvent) EventsNewVerifiedPlayer?.Invoke(name);
      if (needPetEvent) EventsRemoveVerifiedPet?.Invoke(name);
    }

    /*
     * "This application called this name one of ours at <capture second>" — the statement players.txt used to hold, now carried by
     * identity-priors.txt's roster lane together with the class column that file had nowhere to put. persist:false because a parse
     * pass confirms names on thousands of lines, and Save's timer flushes the batch exactly as it used to rewrite the whole roster.
     *
     * `loading` swallows the write, which is the law this class keeps repeating: A LOAD IS NOT A SIGHTING. Re-stamping at startup is
     * how AddPetToPlayer once aged 96.6 % of petmapping.txt out in one session; the roster lane moves a stamp FORWARD only, so a
     * replayed old backup can raise an old second but a start-up cannot.
     *
     * The server guard is not paranoia. IdentityPriorStore files every row under the server name IT holds, and during a log switch the
     * ledger can still be answering for the previous folder — writing tonight's names there is exactly the "one folder quietly acquires
     * another's roster" hazard RosterImport refuses. A skipped write costs nothing: memory already has the name, and the registry
     * re-seeds from the right ledger the moment the switch completes.
     */
    private void RememberInLedger(string name, double playerTimeS, bool loading)
    {
      if (loading) return;

      var ledger = IdentityPriorStore.Instance;
      if (!string.Equals(ledger.ServerName, ConfigUtil.ServerName, StringComparison.OrdinalIgnoreCase))
      {
        Log.Debug($"roster sighting skipped for '{name}': the ledger still answers for '{ledger.ServerName}'");
        return;
      }

      ledger.RememberRoster(name, (long)Math.Max(0, playerTimeS), GetDefaultPlayerClass(name), persist: false);
      _ledgerDirty = true;
    }

    /*
     * The other direction: membership taken away — an operator's eviction, or a name that turned out to be somebody's pet. Only the
     * bit and its class leave; a rule's remembered VERDICT on the same row and a pet mapping are separate statements and survive (see
     * IdentityPriorStore.ForgetRoster). Same batching, same server guard as the write.
     */
    private void ForgetInLedger(string name)
    {
      var ledger = IdentityPriorStore.Instance;
      if (!string.Equals(ledger.ServerName, ConfigUtil.ServerName, StringComparison.OrdinalIgnoreCase)) return;

      ledger.ForgetRoster(name, persist: false);
      _ledgerDirty = true;
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

    /*
     * The class a name held AT a moment, read inside the per-name lock instead of copied out of it.
     *
     * This is called once per player row per board rebuild — DamageStatsBuilder, TankingStatsBuilder and HealingStatsBuilder
     * each fill a Class column for every row they write, so a live raid asks a couple of hundred times a second, and a
     * select-all asks thousands at once. It used to answer by copying the whole boundary list into a new array and
     * binary-searching that outside the lock: one allocation per row, to avoid holding a lock for the four comparisons it
     * actually makes (the list is one entry for almost every name — a class changes mid-session about as often as a raid
     * member rerolls). `GetLastKnownPlayerClass` beside it has always read in place, and that is the shape this now matches.
     *
     * No lock order is introduced: the fallback below reads `_defaultPlayerClass`, a concurrent dictionary that takes no
     * lock, so `active` remains the only lock held here.
     */
    internal string GetPlayerClass(string name, double t)
    {
      var mark = PerfCounters.Begin(ClassReadId);
      try
      {
        if (string.IsNullOrEmpty(name) || !_activePlayerClass.TryGetValue(name, out var active))
          return GetDefaultPlayerClass(name);

        lock (active)
        {
          var records = active.Records;
          if (records.Count == 0)
          {
            return GetDefaultPlayerClass(name);
          }

          // first index where BeginTime > t
          var idx = UpperBoundByBeginTime(records, t);

          return idx == 0
            ? records[0].ClassName     // nothing started at or before t, so the next boundary is the answer
            : records[idx - 1].ClassName; // last boundary at or before t
        }
      }
      finally
      {
        PerfCounters.End(mark);
      }
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

    /*
     * A pure question — this used to WRITE. When a name stood in petnames.txt (the game's own pet-name list) and had no owner
     * row, asking it minted one: `AddPetToPlayer(name, Labels.Unassigned)`. Nothing distinguished that row from a learned pair
     * except the save path, which filters game-generated names out when writing petmapping.txt — so the row was ephemeral,
     * yet `RegistrySeed` walks the pet map and files a Pet claim for whatever it finds there. Measured on
     * eqlog_Kizant_xegony-09-03-26.txt: classify, build the healing board (which asks this question per name), classify again
     * over an UNCHANGED capture, and one name answered `Pet` where the first pass said `Player · R15-healed`; that single move
     * re-routed 8 damage-taken facts (308,103 points) and rebuilt every board a second time. So an unowned game-generated name
     * still ANSWERS true here — that is what the list is for — but it earns no pet-map row. A pair enters `_petToPlayer` when
     * something actually observes one: `AddVerifiedPet` (a line that named the pet) or the Pet Owners grid (an operator's word).
     */
    internal bool IsVerifiedPet(string name) =>
      !string.IsNullOrEmpty(name)
      && (_verifiedPets.ContainsKey(name) || _gameGeneratedPets.ContainsKey(name));

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
        ForgetInLedger(name);
      }

      EventsRemoveVerifiedPlayer?.Invoke(name);
    }

    /*
     * Forget everything this registry believes about ONE name. The operator's manual verdict means "what I am telling you
     * replaces what you worked out" (2026-10-09), and that knowledge sits in six places: the verified-player list (which is
     * players.txt), the verified-pet list, the game-generated pet names, the mercenary set, the "took a player or pet action"
     * flag, and the pet map with its own aging stamp (petmapping.txt). Leaving any one of them standing is how a verdict looks
     * like it did nothing — the case that made this necessary is concrete: AddPetToPlayerNoLock refuses to make a VERIFIED
     * PLAYER somebody's pet, and a name listed as a player is exactly what an operator assigns ("this thing on my damage board
     * is Atvar's summon"), so the menu's write landed nowhere at all.
     *
     * Two lanes are deliberately untouched. Class keeps its own record and its own verb (the Class cell): class answers a
     * different question, and one operator decision silently cancelling another nobody was asked about is worse than a stale
     * cell — especially the hand-typed default. And "X is the owner of Fluffy" says nothing about what X IS, so pairs where
     * this name is the OWNER stay; only the pair that claims THIS name is a pet leaves.
     *
     * Keys go through the same normalization the Add paths used (StringCache interns and capitalizes the first letter), plus
     * the raw form, because a hand-edited file can arrive either way — a forget that misses by case is a verdict that did not.
     */
    internal void ForgetName(string name)
    {
      if (string.IsNullOrEmpty(name)) return;

      RemoveVerifiedPlayer(name);
      RemoveVerifiedPet(name);          // takes the pet-map row for the same name with it

      var capitalized = TextUtils.CapitalizeFirst(name);
      lock (_lock)
      {
        _gameGeneratedPets.TryRemove(capitalized, out _);
        _mercs.TryRemove(capitalized, out _);
        _takenPetOrPlayerAction.TryRemove(capitalized, out _);

        foreach (var key in new[] { capitalized, name })
        {
          _verifiedPlayers.TryRemove(key, out _);
          _verifiedPets.TryRemove(key, out _);
          _mercs.TryRemove(key, out _);
          _takenPetOrPlayerAction.TryRemove(key, out _);
          TryRemovePetMappingNoLock(key);   // takes the ledger's owner column with it
        }
      }
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
        _ledgerDirty = false;

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

        /*
         * players.txt used to be read here as live input. It is not: the roster lane below is the memory, and the ONE reader of the
         * file is now RosterImport.ImportPlayersFileOnce — which runs before this method at log open, so a folder's curated list is
         * already in the ledger (identical stamps, no re-ageing) when these lines seed. Deleting a server's identity-priors.txt
         * therefore brings its players.txt back to life, which is the rollback path; nothing else re-reads the file, and nothing
         * writes it. docs/DesignNotes.md → "players.txt is a feed now".
         */

        /*
         * The ledger's roster lane is the durable half of this list (identity-priors.txt holds membership plus the class a
         * name was seen casting, which players.txt has nowhere to put). Seeding from it costs nothing while both files
         * exist - the names are already here - and it is what keeps memory warm on the day players.txt stops being read:
         * the registry becomes a mirror of the ledger rather than a store that loses its memory when one file goes.
         *
         * The ServerName guard is load-bearing. MainWindow loads this registry while the ledger may still be answering for
         * the PREVIOUS server (a log switch), and seeding then would put another server's raid into tonight's You-mapping.
         */
        var ledger = IdentityPriorStore.Instance;
        if (string.Equals(ledger.ServerName, ConfigUtil.ServerName, StringComparison.OrdinalIgnoreCase))
        {
          /*
           * The ownership lane first, and it is no longer a warm-up beside a file this class also reads — it is the only input.
           * `RosterImport.ImportPetMapOnce` lifts petmapping.txt into the lane at log open (once per folder, stamps carried
           * verbatim) and NOTHING writes that file again, so these lines are where every pair — a decade of operator edits
           * included — enters the session. The pairs still land in `_petToPlayer`, which stays the parse's live mirror: the
           * ingest checks read it mid-line, and `EventsNewPetMapping` still fires for whatever is learned tonight.
           *
           * A pet whose ledger owner is not otherwise known also lands in the player list, exactly as the file loop above
           * does: an owner is a person by construction, and +Pets folding and the You-mapping both ask that list.
           */
          foreach (var entry in ledger.PetEntries())
          {
            var pet = entry.Key;
            if (_petToPlayer.ContainsKey(pet)) continue;

            var owner = entry.Value.Owner;
            if (string.IsNullOrEmpty(owner)) continue;
            if ("You".Equals(owner, StringComparison.OrdinalIgnoreCase)) owner = ConfigUtil.PlayerName;

            if (!_verifiedPlayers.ContainsKey(owner!)) AddVerifiedPlayer(owner!, 0d, true);
            AddVerifiedPet(pet, true);
            AddPetToPlayer(pet, owner!, true);
            // A load is not a sighting: the lane's own stamp rides on the row, and nothing here moves it forward.
          }

          foreach (var entry in ledger.RosterEntries())
          {
            var name = entry.Key;
            if (_verifiedPlayers.ContainsKey(name)) continue;

            /*
             * init:true, always. This is a load, not a sighting: the stamp on the row is the last time anything saw this
             * name, and overwriting it with today's date is exactly how petmapping.txt aged a whole file out in one
             * startup (docs/DesignNotes.md → "A load is not a sighting").
             */
            AddVerifiedPlayer(name, entry.Value.SeenAtS, true);
            SetDefaultPlayerClass(name, entry.Value.Class, true);
          }
        }
      }
    }

    /*
     * The petmapping.txt grammar, ONE copy: `<pet>=<owner>[|<dotnet seconds>]`. `RosterImport.ImportPetMapOnce` is its only reader
     * now that the file is frozen, and it stays a shared helper rather than being folded into the import so a future "re-read this
     * folder's map" verb cannot invent a second grammar for the same lines. The shape of the old disagreement is still worth
     * stating: a pet one reader adopts and the other refuses is a pet whose owner folds on one board and not the other.
     *
     * The stamp tail belongs to THIS application ("when did we last see this name in a log"), not to the owner's name, and
     * it is stripped here so nothing downstream — the Pet Owners grid, +Pets folding, the meters — ever has to know it
     * exists. A row with no tail is kept: rows without a time are statements (typed by hand, or written by a build that
     * did not stamp), and StaleDays only ever retires observations.
     */
    internal static bool TryReadPetMapLine(string? pet, string? ownerValue, out string owner, out double seenAtS)
    {
      owner = string.Empty;
      seenAtS = 0d;

      if (string.IsNullOrEmpty(pet)) return false;

      var value = ownerValue ?? string.Empty;
      var bar = value.IndexOf('|');
      if (bar >= 0)
      {
        if (double.TryParse(value.AsSpan(bar + 1), out var stamped) && stamped > 0) seenAtS = stamped;
        value = value[..bar];
      }

      // An owner of "" is a row the Pet Owners grid cannot show and no board can fold onto. "Unassigned" is NOT refused:
      // that text is data the operator sees and edits, and judging it here would make two authorities of what a row means.
      if (value.Length == 0) return false;

      owner = value;
      return true;
    }

    /*
     * The players.txt grammar, ONE copy: `Name[=<dotnet seconds>[,<Class>]]`. Init loads through it and RosterImport
     * carries the same file into the ledger, so the two readers cannot drift into disagreeing about which lines are names
     * (a name one reader sees and the other refuses is a name on half the memory).
     *
     * False means "this line is not a claim about a player", and there are three shapes of that: empty or too short to be
     * a name, a leading '!' (the shape a person reaches for to cross something out - ignored as input, honoured as nothing;
     * see this class's header on the deleted rejection tombstone), and a name no player can have (`Unk`, an empty tail).
     * A line whose timestamp does not parse is NOT refused: it keeps 0, which means "a statement, never retires" - the
     * hand-typed half of every old file.
     */
    internal static bool TryReadRosterLine(string? line, out string name, out double seenAtS, out string? className)
    {
      name = string.Empty;
      seenAtS = 0d;
      className = null;

      if (string.IsNullOrEmpty(line) || line.Length <= 2 || line.StartsWith('!')) return false;

      var split = line.Split('=');
      if (split.Length == 1)
      {
        name = line.Trim();
      }
      else
      {
        // Only the FIRST '=' is read as a separator: the rest of the row is timestamp then class, and a name carrying
        // one is not a thing EQ writes, so split[0] is the whole name either way.
        name = split[0].Trim();
        var value = split[1].Split(',');
        double.TryParse(value[0], NumberStyles.Any, CultureInfo.InvariantCulture, out seenAtS);
        if (value.Length >= 2 && value[1].Length > 0) className = value[1];
      }

      // A row whose name is blank after trimming is not a row: whitespace only, or "=123". Nothing downstream can use it,
      // and AddVerifiedPlayer would have stored the empty string as a roster key.
      if (name.Length == 0) return false;

      // Deliberately NOT IsPossiblePlayerName: that gate wants letters only, and a curated file legitimately holds
      // names it fails ("Akini, Xanathan"). Refusing such a row at LOAD is how a curated list disappears silently - the
      // bug this class's header is full of. A caller may tighten further on its own reasons; RosterImport does.
      return true;
    }

    /*
     * The 30-second write, and the one run at log close / shutdown. ONE thing leaves this class now: a single batched flush into
     * identity-priors.txt, whose lanes hold membership (`Ours`) and ownership (`Owner`) side by side.
     *
     * Neither old file is written any more. players.txt stopped on 2026-10-09; petmapping.txt followed the same day for the same
     * reason — its rows are exactly what a ledger row already models (the pet name, its owner in the `Owner` column, and the
     * dotnet-epoch second this application last saw it), so keeping both meant two memories of one fact, free to disagree silently,
     * with whichever loaded last winning. Both files stay on disk untouched as their importer's source: read at most once per folder,
     * never edited, never rewritten by a conclusion this build reached. That is what freezing the feeds buys — nothing the rules
     * decide can strand a name the curated file got wrong, because the ledger (ageing on one dial, `StaleDays`) is the memory from
     * here on. docs/DesignNotes.md → "players.txt is a feed now", "petmapping.txt is a feed now".
     */
    internal void Save()
    {
      var flushMemory = false;

      lock (_lock)
      {
        if (_ledgerDirty)
        {
          flushMemory = true;
          _ledgerDirty = false;
        }

        // if method is called manually then restart the timer
        _saveTimer?.Change(_saveInterval, _saveInterval);
      }

      /*
       * Outside the lock: the ledger takes its own gate and writes its own file, and it applies the expiry (StaleDays against the wall
       * clock) to BOTH of its lanes itself — including dropping a pet's owner column when that summon has not been in any log this
       * application read. This class no longer decides who stays in memory; it reports sightings.
       */
      if (flushMemory) IdentityPriorStore.Instance.FlushChanges();
    }

    internal void SetActivePlayerClass(string name, string className, byte confidence, double beginTime)
    {
      if (string.IsNullOrEmpty(name) || !CombatRecordLookup.IsValidClassName(className) || confidence is < 1 or > 2)
        return;

      /*
       * Counted per call rather than per committed boundary: what costs is the ask (a frenzy line arrives ~85,000 times in a
       * night and every one of them takes this name's lock to conclude "still a berserker"), not the rare insert.
       */
      PerfCounters.Note(WriteId);

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
      // The literal "You" is resolved to ConfigUtil.PlayerName by the parsers; this is the safety check behind that rule, kept
      // because a durable row named "You" would be unreachable through the lookups that normalise it.
      if (string.IsNullOrEmpty(name) || "You".Equals(name, StringComparison.OrdinalIgnoreCase)) return;

      var clear = string.IsNullOrEmpty(className);
      if (!clear && !CombatRecordLookup.IsValidClassName(className)) return;

      var needEvent = false;

      lock (_lock)
      {
        // A blank selection means "no default any more" — remove it, never store an empty word the readers must re-filter.
        if (clear) _defaultPlayerClass.TryRemove(name, out _);
        else _defaultPlayerClass[name] = className!;

        if (!init)
        {
          /*
           * An operator edit is not a sighting. This used to stamp `_verifiedPlayers[name]` with today's date and mark
           * players.txt dirty, so "call this a bard" also asserted "we just watched this name in a line" — resetting the age
           * clock of a name nothing observed, the same law whose violation aged 96.6 % of petmapping.txt out in one startup.
           * The durable home is the roster lane (IdentityPriorStore.SetRosterClass), whose row for an edit carries a 0 stamp
           * (never retires) and is seeded back into this map at Init — so a name whose ONLY fact is this class assignment
           * still comes back next launch, without the file it used to be written into.
           */
          IdentityPriorStore.Instance.SetRosterClass(name, clear ? null : className);
          needEvent = true;
        }
      }

      if (needEvent) EventsUpdateDefaultPlayerClass?.Invoke(new PlayerClassMapping { Player = name, ClassName = clear ? string.Empty : className });
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
        // The durable half goes with it. A verdict or roster bit on the same name is a different statement and stays.
        IdentityPriorStore.Instance.ForgetPet(name, persist: false);
        _ledgerDirty = true;
        return true;
      }

      return false;
    }

    /*
     * "This summon appeared in a line tonight" is NOT the same claim as "this pair exists": a pet that is already mapped still has
     * to have its clock moved, or the 200-day dial ages out a mapping for a pet that walks into the log every night. `RememberPet`
     * moves `SeenAtS` forward and never back, so replaying an old backup cannot rewind it (docs/DesignNotes.md → "A load is not a
     * sighting"). Cheap by construction: a name with no live pair writes nothing.
     */
    private void TouchPetStampNoLock(string pet)
    {
      if (string.IsNullOrEmpty(pet) || !_petToPlayer.TryGetValue(pet, out var owner)) return;

      IdentityPriorStore.Instance.RememberPet(pet, owner, (long)SessionStamp, persist: false);
      _ledgerDirty = true;
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
          /*
           * The pair is written ONCE, into the ledger's ownership lane: identity-priors.txt carries it as a column of the pet's own
           * row, so membership, verdict and owner live in one file instead of two memories of one fact. `SeenAtS` moves forward —
           * which is how an operator's edit keeps a mapping that would otherwise have aged out, exactly as on the old file.
           * `persist: false` because the 30-second tick batches a night of sightings into one write; operator verbs flush through
           * their own door.
           */
          IdentityPriorStore.Instance.RememberPet(pet, player, (long)SessionStamp, persist: false);
          _ledgerDirty = true;
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

    private static int UpperBoundByBeginTime(List<ClassRecord> records, double t)
    {
      int lo = 0, hi = records.Count;
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
