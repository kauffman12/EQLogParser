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
  internal sealed class CombatMirror
  {
    private const string OwnerToken = "Owner:";

    private readonly IFactTable _facts;
    private int _sequence;
    // Registry events carry no timestamp; this is the most recent BeginTime seen on any tapped
    // event, used only as a best-effort TimeS for IdentityEvent (replay orders by Seq).
    private long _lastSeenTs = -1;

    public CombatMirror(IFactTable facts)
    {
      _facts = facts ?? throw new ArgumentNullException(nameof(facts));
    }

    public IFactTable Facts => _facts;

    public void Start()
    {
      DamageLineParser.EventsDamageProcessed += HandleDamage;
      DamageLineParser.EventsNewDeath += HandleDeath;
      DamageLineParser.EventsNewTaunt += HandleTaunt;

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

      var registry = PlayerRegistry.Instance;
      registry.EventsNewVerifiedPet -= OnVerifiedPet;
      registry.EventsNewVerifiedPlayer -= OnVerifiedPlayer;
      registry.EventsRemoveVerifiedPet -= OnRemovedVerifiedPet;
      registry.EventsRemoveVerifiedPlayer -= OnRemovedVerifiedPlayer;
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

      var subIdx = string.IsNullOrEmpty(r.SubType) ? DamageFactTable.NoSubtype : (ushort)Math.Max(0, (int)_facts.InternSubtype(r.SubType));
      _lastSeenTs = ToTimeS(e.BeginTime);

      _facts.AddFact(new DamageFact(
        seq: ++_sequence,
        timeS: ToTimeS(e.BeginTime),
        atkIdx: _facts.InternName(r.Attacker),
        defIdx: _facts.InternName(r.Defender),
        total: r.Total,
        overTotal: r.OverTotal,
        typeId: LabelTypes.IdOf(r.Type),
        flags: flags,
        subIdx: subIdx));
    }

    private void HandleDeath(DeathEvent e)
    {
      if (string.IsNullOrEmpty(e.Record.Killed)) return;
      _lastSeenTs = ToTimeS(e.BeginTime);
      _facts.AddDeath(new DeathFact(
        seq: ++_sequence,
        timeS: ToTimeS(e.BeginTime),
        killedIdx: _facts.InternName(e.Record.Killed),
        killerIdx: string.IsNullOrEmpty(e.Record.Killer) ? (short)-1 : _facts.InternName(e.Record.Killer)));
    }

    private void HandleTaunt(TauntEvent e)
    {
      if (string.IsNullOrEmpty(e.Record.Npc)) return;
      _lastSeenTs = ToTimeS(e.BeginTime);
      _facts.AddTaunt(new TauntFact(
        seq: ++_sequence,
        timeS: ToTimeS(e.BeginTime),
        npcIdx: _facts.InternName(e.Record.Npc)));
    }

    private void HandleIdentity(byte kind, string name)
    {
      if (string.IsNullOrEmpty(name)) return;
      _facts.AddIdentity(new IdentityEvent(
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
      return attacker.EndsWith("`s pet", StringComparison.Ordinal) || attacker.EndsWith("`s warder", StringComparison.Ordinal);
    }

    // the parser's dotnet-epoch seconds (year 0001 origin, ~6.4e10 in the 2020s) — long, or the
    // cast silently wraps and every derived timestamp is garbage
    internal static long ToTimeS(double beginTime) => (long)Math.Round(beginTime);
  }
}
