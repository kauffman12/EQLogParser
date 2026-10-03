namespace EQLogParser.Mirror
{
  // The tap (D1): subscribes to the existing parser and registry events and appends immutable
  // facts — attacker/defender exactly as fired (parser name handling is factual normalization
  // only), plus the raw action line carried on the event, from which ownership evidence is
  // extracted registry-free. No parser logic changes; no classification happens here.
  //
  // Beyond damage/death, two pipeline inputs that can change fight state are captured in
  // consumer order: registry verifications (EventsNewVerifiedPet makes FightManager drop the
  // matching active fight) and taunts (a taunt opens a fight if none is active).
  //
  // Thread model: parser events are raised by the LogProcessor consumer task on a single thread,
  // so appends need no locking (same contract as DamageFactTable).
  internal sealed class CombatCapture
  {
    private const string OwnerToken = "Owner:";

    private readonly IFactTable _facts;

    /*
     * The heal stream, or null when the caller did not ask for one. Own table by choice — see HealFact's
     * comment for why heals are not rows in the damage table. Capture works the same way as damage: append
     * at the gate, in the shared sequence, with the registry's answer at this instant stamped in.
     */
    private readonly IHealFactTable _heals;

    // Ingest/derive gate: appends take it per fact (uncontended ~free), a derivation holds it
    // for its whole pass. Readers of the fact table therefore never observe mid-append mutation
    // while tailing continues — no stop/start games, no dropped facts; live ingest just pauses
    // for the duration of one derivation.
    private readonly object _gate = new();
    private int _sequence;
    // Registry events carry no timestamp; this is the most recent BeginTime seen on any tapped
    // event, used only as a best-effort TimeS for IdentityEvent (replay orders by Seq).
    private long _lastSeenTs = -1;
    private long _firstSeenTs = -1;

    public CombatCapture(IFactTable facts, IHealFactTable heals = null)
    {
      _facts = facts ?? throw new ArgumentNullException(nameof(facts));
      _heals = heals;
    }

    public IFactTable Facts => _facts;

    // Null unless a heal table was handed in — the tap does not invent storage behind its owner's back.
    public IHealFactTable HealFacts => _heals;

    // Time window of tapped line events (double.NaN until the first event). The registry seed
    // uses it to decide ingest-replay vs retroactive evidence times.
    public double FirstEventTime => _firstSeenTs < 0 ? double.NaN : _firstSeenTs;
    public double LastEventTime => _lastSeenTs < 0 ? double.NaN : _lastSeenTs;

    public void Start()
    {
      DamageLineParser.EventsDamageProcessed += HandleDamage;
      DamageLineParser.EventsNewDeath += HandleDeath;
      DamageLineParser.EventsNewTaunt += HandleTaunt;

      // Fires for every heal the parser stored, immediately after RecordsStore.Add — so "heal facts" and
      // "what the healing board reads" are the same population, which is what makes the fidelity ledger
      // (HealFactCaptureTest) an equality test rather than an approximation.
      if (_heals is not null) HealingLineParser.EventsHealProcessed += HandleHeal;

      PreLineParser.EventsEvidence += OnPreLineEvidence;
      MiscLineParser.EventsWhoRoster += OnWhoRoster;
      MiscLineParser.EventsCalledToOwner += OnCalledToOwner;
      MiscLineParser.EventsCharm += OnCharm;
      CastLineParser.EventsCastEvidence += OnCast;

      var registry = PlayerRegistry.Instance;
      registry.EventsNewVerifiedPet += OnVerifiedPet;
      registry.EventsNewVerifiedPlayer += OnVerifiedPlayer;
      registry.EventsRemoveVerifiedPet += OnRemovedVerifiedPet;
      registry.EventsRemoveVerifiedPlayer += OnRemovedVerifiedPlayer;
    }

    public void Stop()
    {
      DamageLineParser.EventsDamageProcessed -= HandleDamage;
      DamageLineParser.EventsNewDeath -= HandleDeath;
      DamageLineParser.EventsNewTaunt -= HandleTaunt;

      if (_heals is not null) HealingLineParser.EventsHealProcessed -= HandleHeal;

      PreLineParser.EventsEvidence -= OnPreLineEvidence;
      MiscLineParser.EventsWhoRoster -= OnWhoRoster;
      MiscLineParser.EventsCalledToOwner -= OnCalledToOwner;
      MiscLineParser.EventsCharm -= OnCharm;
      CastLineParser.EventsCastEvidence -= OnCast;

      var registry = PlayerRegistry.Instance;
      registry.EventsNewVerifiedPet -= OnVerifiedPet;
      registry.EventsNewVerifiedPlayer -= OnVerifiedPlayer;
      registry.EventsRemoveVerifiedPet -= OnRemovedVerifiedPet;
      registry.EventsRemoveVerifiedPlayer -= OnRemovedVerifiedPlayer;
    }

    // Chat reaches the mirror through the pipeline's IChatSink seam (the app adapter and the test
    // harness fan out here); every chat line becomes one EvChat fact, channel included.
    // Runs derivation logic against the fact table with ingest parked at the gate.
    public T DeriveQuiescent<T>(Func<T> derive)
    {
      lock (_gate) return derive();
    }

    public void HandleChat(ChatType chat)
    {
      if (chat is null || string.IsNullOrEmpty(chat.Sender)) return;
      Emit(EvidenceFact.EvChat, chat.Sender, chat.BeginTime, chat.Channel);
    }

    private void OnPreLineEvidence(string name, double time, byte kind) => Emit(kind, name, time);

    private void OnWhoRoster(string name, string cls, double time) => Emit(EvidenceFact.EvWhoRoster, name, time, cls);

    private void OnCalledToOwner(string name, double time) => Emit(EvidenceFact.EvCalledToOwner, name, time);

    // The start line never says who charmed anything (measured: no charm cast text precedes a
    // third-party "has been charmed." inside 20 s in any capture), so aux is null there; the wear-off
    // line does name its caster, and that owner rides on the end fact for the window policy to apply
    // retroactively.
    private void OnCharm(string name, double time, bool isStart, string owner) => Emit(isStart ? EvidenceFact.EvCharmStart : EvidenceFact.EvCharmEnd, name, time, owner);

    private void OnCast(string caster, string spell, double time) => Emit(EvidenceFact.EvCast, caster, time, spell);

    private void Emit(byte kind, string name, double time, string aux = null)
    {
      if (string.IsNullOrEmpty(name)) return;
      lock (_gate)
      {
        var ts = MarkTs(time);
        _facts.AddEvidence(new EvidenceFact(++_sequence, ts, _facts.InternName(name), kind, _facts.InternAux(aux)));
      }
    }

    private void OnVerifiedPet(string name) => HandleIdentity(IdentityEvent.VerifiedPet, name);

    private void OnVerifiedPlayer(string name) => HandleIdentity(IdentityEvent.VerifiedPlayer, name);

    private void OnRemovedVerifiedPet(string name) => HandleIdentity(IdentityEvent.RemovedVerifiedPet, name);

    private void OnRemovedVerifiedPlayer(string name) => HandleIdentity(IdentityEvent.RemovedVerifiedPlayer, name);

    private void HandleDamage(DamageProcessedEvent e)
    {
      var r = e.Record;
      if (string.IsNullOrEmpty(r.Attacker) || string.IsNullOrEmpty(r.Defender)) return;

      // Registry verdicts at this exact instant (same thread/instant as FightManager's own call —
      // handlers run back-to-back in one event dispatch; nothing mutates the registry between them).
      var registry = PlayerRegistry.Instance;

      var flags = (byte)0;
      if (r.AttackerIsSpell) flags |= DamageFact.FlagAttackerIsSpell;
      if (HasOwnershipInLine(e.Action, r.Attacker)) flags |= DamageFact.FlagOwnerInLine;
      if (registry.IsPetOrPlayerOrMerc(r.Attacker)) flags |= DamageFact.FlagAttkPlayerSide;
      if (registry.IsPetOrPlayerOrMerc(r.Defender)) flags |= DamageFact.FlagDefPlayerSide;

      lock (_gate)
      {
        MarkTs(e.BeginTime);

        var subIdx = string.IsNullOrEmpty(r.SubType) ? DamageFactTable.NoSubtype : (ushort)Math.Max(0, (int)_facts.InternSubtype(r.SubType));
        _facts.AddFact(new DamageFact(
          seq: ++_sequence,
          timeS: ToTimeS(e.BeginTime),
          atkIdx: _facts.InternName(r.Attacker),
          defIdx: _facts.InternName(r.Defender),
          total: r.Total,
          typeId: LabelTypes.IdOf(r.Type),
          flags: flags,

          // The six modifier filters' input. Its old four bytes came from deleting the always-zero OverTotal,
          // so DamageFact is still 32 B — and the derived damage summary stopped reading high whenever one of
          // those filters was switched off, which is what FightSummarySource used to have to warn about.
          modMask: r.ModifiersMask,
          subIdx: subIdx));
      }
    }

    /*
     * One heal, captured exactly as the parser fired it: healer/healed as named, what landed and what the
     * line asked for, the spell behind it, and the modifier mask. Nothing here decides whether a heal counts
     * toward a fight — that question belongs to the projection, and answers differently for damage (aimed at
     * an entity) than for healing (done inside a window).
     */
    private void HandleHeal(HealProcessedEvent e)
    {
      var r = e.Record;
      if (r is null || string.IsNullOrEmpty(r.Healer) || string.IsNullOrEmpty(r.Healed)) return;

      var registry = PlayerRegistry.Instance;

      var flags = (byte)0;
      if (HasOwnershipInName(r.Healer)) flags |= HealFact.FlagOwnerInLine;
      if (registry.IsPetOrPlayerOrMerc(r.Healer)) flags |= HealFact.FlagHealerPlayerSide;
      if (registry.IsPetOrPlayerOrMerc(r.Healed)) flags |= HealFact.FlagHealedPlayerSide;

      lock (_gate)
      {
        MarkTs(e.BeginTime);
        _heals.AddHeal(new HealFact(
          seq: ++_sequence,
          timeS: ToTimeS(e.BeginTime),
          healerIdx: _heals.InternName(r.Healer),
          healedIdx: _heals.InternName(r.Healed),
          total: r.Total,

          // Verbatim, zero included — see HealFact.OverTotal for what normalising it would quietly cost.
          overTotal: r.OverTotal,
          typeId: LabelTypes.IdOf(r.Type),
          flags: flags,
          modMask: r.ModifiersMask,
          subIdx: _heals.InternSpell(r.SubType)));
      }
    }

    private void HandleDeath(DeathEvent e)
    {
      if (string.IsNullOrEmpty(e.Record.Killed)) return;
      MarkTs(e.BeginTime);
      lock (_gate) _facts.AddDeath(new DeathFact(
        seq: ++_sequence,
        timeS: ToTimeS(e.BeginTime),
        killedIdx: _facts.InternName(e.Record.Killed),
        killerIdx: string.IsNullOrEmpty(e.Record.Killer) ? (short)-1 : _facts.InternName(e.Record.Killer)));
    }

    private void HandleTaunt(TauntEvent e)
    {
      if (string.IsNullOrEmpty(e.Record.Npc)) return;
      MarkTs(e.BeginTime);

      // The outcome words ride the fact as bits: the taunt board counts them and the deriver ignores them.
      var flags = 0;
      if (e.Record.Success) flags |= TauntFact.TauntSuccess;
      if (e.Record.IsImproved) flags |= TauntFact.TauntImproved;

      lock (_gate) _facts.AddTaunt(new TauntFact(
        seq: ++_sequence,
        timeS: ToTimeS(e.BeginTime),
        npcIdx: _facts.InternName(e.Record.Npc),
        attackerIdx: string.IsNullOrEmpty(e.Record.Player) ? (short)-1 : _facts.InternName(e.Record.Player),
        flags: (byte)flags));
    }

    private void HandleIdentity(byte kind, string name)
    {
      if (string.IsNullOrEmpty(name)) return;
      lock (_gate) _facts.AddIdentity(new IdentityEvent(
        seq: ++_sequence,
        timeS: _lastSeenTs,
        nameIdx: _facts.InternName(name),
        kind: kind));
    }

    // Line-intrinsic ownership evidence (R5 input, stored as a flag here; interpretation is the
    // rules' job from Phase 2). Covers the two shapes that exist in the logs: "X`s pet"/"X`s
    // warder" attacker names and explicit "Owner: X" annotations.
    private static bool HasOwnershipInLine(string action, string attacker)
    {
      if (string.IsNullOrEmpty(action)) return false;
      if (action.Contains(OwnerToken, StringComparison.OrdinalIgnoreCase)) return true;
      return HasOwnershipInName(attacker);
    }

    /*
     * The ownership word inside the name itself. Both streams read it — a pet that heals is still somebody's
     * pet — and the word list lives in exactly one place (ClassificationRules.OwnerSuffixes) so the flag on a
     * fact and the rule that reads it can never disagree about what counts as owned. Adding a word there adds
     * it here; HitStatSplitTest-style, the vocabulary is asserted at its size.
     */
    private static bool HasOwnershipInName(string name) => ClassificationRules.OwnerInName(name) is not null;

    // the parser's dotnet-epoch seconds (year 0001 origin, ~6.4e10 in the 2020s) — long, or the
    // cast silently wraps and every derived timestamp is garbage
    internal static long ToTimeS(double beginTime) => (long)Math.Round(beginTime);

    private long MarkTs(double time)
    {
      var ts = ToTimeS(time);
      if (_firstSeenTs < 0) _firstSeenTs = ts;
      _lastSeenTs = ts;
      return ts;
    }
  }
}
