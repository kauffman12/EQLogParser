namespace EQLogParser.Mirror
{
  // The tap (D1): subscribes to the existing parser events and appends immutable facts —
  // attacker/defender exactly as fired (parser name handling is factual normalization only),
  // plus the raw action line carried on the event, from which ownership evidence is extracted
  // registry-free. No parser logic changes; no classification happens here.
  //
  // Thread model: parser events are raised by the LogProcessor consumer task on a single thread,
  // so appends need no locking (same contract as DamageFactTable).
  internal sealed class CombatMirror
  {
    private const string OwnerToken = "Owner:";

    private readonly IFactTable _facts;
    private int _sequence;

    public CombatMirror(IFactTable facts)
    {
      _facts = facts ?? throw new ArgumentNullException(nameof(facts));
    }

    public IFactTable Facts => _facts;

    public void Start()
    {
      DamageLineParser.EventsDamageProcessed += HandleDamage;
      DamageLineParser.EventsNewDeath += HandleDeath;
    }

    public void Stop()
    {
      DamageLineParser.EventsDamageProcessed -= HandleDamage;
      DamageLineParser.EventsNewDeath -= HandleDeath;
    }

    private void HandleDamage(DamageProcessedEvent e)
    {
      var r = e.Record;
      if (string.IsNullOrEmpty(r.Attacker) || string.IsNullOrEmpty(r.Defender)) return;

      var flags = (byte)0;
      if (r.AttackerIsSpell) flags |= DamageFact.FlagAttackerIsSpell;
      if (HasOwnershipInLine(e.Action, r.Attacker)) flags |= DamageFact.FlagOwnerInLine;

      var subIdx = string.IsNullOrEmpty(r.SubType) ? DamageFactTable.NoSubtype : (ushort)Math.Max(0, (int)_facts.InternSubtype(r.SubType));

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
      _facts.AddDeath(new DeathFact(
        seq: ++_sequence,
        timeS: ToTimeS(e.BeginTime),
        killedIdx: _facts.InternName(e.Record.Killed),
        killerIdx: string.IsNullOrEmpty(e.Record.Killer) ? (short)-1 : _facts.InternName(e.Record.Killer)));
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
