using log4net;
using System.Reflection;

namespace EQLogParser
{
  // The tap (D1): subscribes to the existing parser and registry events and appends immutable
  // facts — attacker/defender exactly as fired (parser name handling is factual normalization
  // only), plus the raw action line carried on the event, from which ownership evidence is
  // extracted registry-free. No parser logic changes; no classification happens here.
  //
  // Beyond damage/death, two pipeline inputs that could change fight state are captured in
  // consumer order: registry verifications and taunts. The legacy engine dropped an active fight on a
  // verification and opened one on a taunt; the fact queues keep the same arrival order so any fold
  // sees exactly what the parse saw.
  //
  // Thread model: parser events are raised by the LogProcessor consumer task on a single thread,
  // so appends need no locking (same contract as DamageFactTable).
  internal sealed class CombatCapture
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

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

      // One capture, one tally of the fact-packing guards (FactTime's clamp counter, HealFact's mask/label counters). They
      // are process statics because a row is a struct with no session to hang them on, and a fresh capture is the point
      // where "so far" becomes meaningless.
      ResetFactGuards();
    }

    /*
     * Everything that could go wrong in a row's narrowed fields: a timestamp outside 1931-2068 (clamped), a heal mask
     * needing a bit the byte does not have (low byte kept), a 0xFF mask wearing the "no modifier text" sentinel, or a heal
     * label that is neither Heal nor Hot. Zero for a real capture — and the first derive pass says the word when it is
     * not, so an operator's own log carries the evidence rather than a screen of subtly wrong times being read as a layout bug.
     */
    internal static long FactGuardCount =>
      FactTime.OutOfRange + HealFact.MasksBeyondByte + HealFact.MaskSentinelCollisions + HealFact.UnknownTypeLabels;

    internal static void ResetFactGuards()
    {
      FactTime.ResetOutOfRange();
      HealFact.ResetPackingCounters();
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
      MiscLineParser.EventsLooterIdentified += OnLooter;
      MiscLineParser.EventsCompanionCalled += OnCompanionCalled;
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
      MiscLineParser.EventsLooterIdentified -= OnLooter;
      MiscLineParser.EventsCompanionCalled -= OnCompanionCalled;
      MiscLineParser.EventsCharm -= OnCharm;
      CastLineParser.EventsCastEvidence -= OnCast;

      var registry = PlayerRegistry.Instance;
      registry.EventsNewVerifiedPet -= OnVerifiedPet;
      registry.EventsNewVerifiedPlayer -= OnVerifiedPlayer;
      registry.EventsRemoveVerifiedPet -= OnRemovedVerifiedPet;
      registry.EventsRemoveVerifiedPlayer -= OnRemovedVerifiedPlayer;
    }

    // Chat reaches the engine through the pipeline's IChatSink seam (the app adapter and the test
    // harness fan out here); every chat line becomes one EvChat fact, channel included.
    // Runs derivation logic against the fact table with ingest parked at the gate.
    public T DeriveQuiescent<T>(Func<T> derive)
    {
      lock (_gate) return derive();
    }

    /// <summary>How much slack is worth parking ingest for: below this, the copy costs more than the memory is worth.</summary>
    internal const long MinSlackToCompact = 4L * 1024 * 1024;

    // Facts the capture held at the last trim. See CompactRows for what it buys.
    private int _factsAtLastCompact;

    /*
     * Hand back the row arrays' doubling slack: measured 47 MB of the 397 MB a loaded session retains (damage 29 + heal 18)
     * is address space the last Array.Resize reserved and nothing ever wrote. Both tables grow by doubling - a law pinned on
     * purpose, because extra growth steps would be paid for on the parse thread, which is where load time actually goes -
     * so what costs nothing during a load is left stranded the moment the load stops growing.
     *
     * Two rules keep this from becoming the thing it exists to avoid. It runs AT THE GATE, the same lock every fact takes,
     * so it can never resize underneath an append. And it runs at most once per DOUBLING of the capture: a trim leaves
     * capacity equal to count, so the next fact doubles the array again, and trimming from every pass after that would copy
     * the whole table once per new fact - quadratic where doubling was amortized. Asking for a doubling's worth of new facts
     * first means each trim lands exactly where a doubling already copied those same bytes, which is also the only moment
     * there is fresh slack to reclaim.
     *
     * Shrinking is safe under a reader that took Facts before: the new array holds the same rows at the same indices, so a
     * stale span still reads the same facts and every ordinal FightFactIndex stores still names the same row.
     */
    internal long CompactRows()
    {
      lock (_gate)
      {
        if (_factsAtLastCompact > 0 && _facts.FactCount < (long)_factsAtLastCompact * 2) return 0L;
        var slack = _facts.SlackBytes + (_heals?.SlackBytes ?? 0L);
        if (slack < MinSlackToCompact) return 0L;

        var freed = _facts.CompactToCount();
        if (_heals is not null) freed += _heals.CompactToCount();
        _factsAtLastCompact = _facts.FactCount;
        Log.Debug($"capture: trimmed row-array slack - {freed / (1024 * 1024):N0} MB released " +
                  $"({_facts.FactCount:N0} damage facts, {_heals?.HealCount ?? 0:N0} heals)");
        return freed;
      }
    }

    public void HandleChat(ChatType chat)
    {
      if (chat is null || string.IsNullOrEmpty(chat.Sender)) return;
      Emit(EvidenceFact.EvChat, chat.Sender, chat.BeginTime, chat.Channel);
    }

    private void OnPreLineEvidence(string name, double time, byte kind) => Emit(kind, name, time);

    private void OnWhoRoster(string name, string cls, double time) => Emit(EvidenceFact.EvWhoRoster, name, time, cls);

    // The companion line names the person the summon came TO (MiscLineParser's census), so this fact is about the
    // summoner and carries no pet — which pair belongs to the possessive words and to petmapping.txt instead.
    // A loot line names the person who took the item (R23-loot). No combat fact ever carries that knowledge, so this
    // publish is the only route from the line into the rule book.
    private void OnLooter(string name, double time) => Emit(EvidenceFact.EvLooter, name, time);

    private void OnCompanionCalled(string name, double time) => Emit(EvidenceFact.EvCompanionCalled, name, time);

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

      /*
       * Every flag here is readable off the line the parser just handled — that is the whole rule, and it is why
       * two of them are gone. FlagAttkPlayerSide / FlagDefPlayerSide used to record what PlayerRegistry answered for
       * each name at the instant the event fired: ~15 million lookups over a 952 MiB capture, and — the reason they
       * were deleted rather than optimised — an answer no consumer can reproduce. It is not a property of the line:
       * the same name carries the bit only from the second its evidence arrived, so two runs over one file with a
       * different store warm-up differ in it, and a parallel or sharded reader cannot get it right at all (RangeSpike's
       * own census was built to prove that, and said so). Identity is derived per name by the rule book from captured
       * evidence, and WHEN the registry learned something travels honestly as IdentityEvent (VerifiedPlayer/Pet with
       * Seq and TimeS) — which is the non-lossy form of the same knowledge. What stays on the fact is what the line
       * says: a spell standing in for an absent caster, and an ownership word inside a name.
       */
      var flags = (byte)0;
      if (r.AttackerIsSpell) flags |= DamageFact.FlagAttackerIsSpell;
      if (HasOwnershipInLine(e.Action, r.Attacker)) flags |= DamageFact.FlagOwnerInLine;

      lock (_gate)
      {
        MarkTs(e.BeginTime);

        // Empty answers NoSubtype on the intern side itself; the clamp that used to sit here was only there because
        // InternSubtype returned a short that wrapped past 32768, and the wrap landed on 0 = the first subtype.
        var subIdx = _facts.InternSubtype(r.SubType);
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

      // Same law as the damage tap: line-derived bits only. See HandleDamage for what replaced the registry answers.
      var flags = (byte)0;
      if (HasOwnershipInName(r.Healer)) flags |= HealFact.FlagOwnerInLine;

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
