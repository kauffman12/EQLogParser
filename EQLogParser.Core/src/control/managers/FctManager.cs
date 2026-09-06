namespace EQLogParser
{
  /* Lanes the FCT renderer understands. Incoming sits left of center, outgoing right of center;
   * crits stay on the half of the lane that produced them. */
  internal enum FctLane
  {
    DamageDealt,
    DamageTaken,
    HealingDealt,
    HealingReceived,
    Crit,
    // zero-damage evades: Defensive = they failed on me (blue), Missed = I whiffed (dim gray)
    Defensive,
    Missed
  }

  /* One floating text for a canvas to draw. Kept UI-agnostic so Core can own the feed. */
  internal sealed class FctHitCommand
  {
    // the source lane, even for crits: the renderer pools a crit onto its producing side's half,
    // which it can only know if the lane survives here unpooled
    public FctLane Lane;
    public bool Crit;
    public double Value;
    public string Source; // "(melee)" / "(Fireball)" / ...
    public string ValueText; // non-numeric main-line text (defensive labels); null = formatted Value
  }

  /*
   * Feeds floating combat text from parsed records. Both parser events fire during historical
   * replay (opening a log file) and live monitoring alike, but every event carries IsMonitor
   * (threaded from LogReader's line flag via LineData), so replay records are simply dropped.
   * Events arrive on the log reader thread; subscribers are responsible for batching onto their
   * own UI thread. See FctSkiaCanvas and docs/NagFctReference.md for the rendering side.
   */
  internal class FctManager
  {
    // singleton with set for unit test, like FightManager
    internal static FctManager Instance { get; set; } = new();

    /* Reader thread — subscribers must marshal to their UI thread. */
    internal event Action<IReadOnlyList<FctHitCommand>> EventsHitsProcessed;

    private FctManager()
    {
      DamageLineParser.EventsDamageProcessed += HandleDamage;
      HealingLineParser.EventsHealProcessed += HandleHeal;
    }

    /* Internal so unit tests can drive the feed without parsing log lines. */
    internal void HandleDamage(DamageProcessedEvent e)
    {
      if (e.Record is null || !e.IsMonitor)
      {
        return;
      }

      var record = e.Record;
      var iAmAttacker = record.Attacker == ConfigUtil.PlayerName ||
                         PlayerRegistry.Instance.GetPlayerFromPet(record.Attacker) == ConfigUtil.PlayerName;
      var iAmDefender = record.Defender == ConfigUtil.PlayerName ||
                        PlayerRegistry.Instance.GetPlayerFromPet(record.Defender) == ConfigUtil.PlayerName;

      // FCT covers my character's fight only; anything else is noise
      if (!iAmAttacker && !iAmDefender)
      {
        return;
      }

      // evades arrive as zero-damage records carrying a label in Type (see DamageLineParser)
      if (record.Total == 0 && IsDefensiveLabel(record.Type))
      {
        Raise([new FctHitCommand
        {
          Lane = iAmDefender ? FctLane.Defensive : FctLane.Missed,
          ValueText = record.Type,
          Source = string.IsNullOrEmpty(record.SubType) ? null : $"({SingularizeVerb(record.SubType)})",
        }]);
        return;
      }

      if (record.Total == 0)
      {
        return; // nothing numeric to show
      }

      var crit = LineModifiersParser.IsCrit(record.ModifiersMask);

      Raise([new FctHitCommand
      {
        Lane = iAmAttacker ? FctLane.DamageDealt : FctLane.DamageTaken,
        Crit = crit,
        // Total is the amount actually dealt (damage records never carry OverTotal today)
        Value = record.Total,
        Source = string.IsNullOrEmpty(record.SubType) ? null : $"({SingularizeVerb(record.SubType)})",
      }]);
    }

    /* The labels DamageLineParser assigns to zero-damage evade lines. */
    private static bool IsDefensiveLabel(string type) =>
      type is Labels.Miss or Labels.Dodge or Labels.Block or Labels.Parry or Labels.Riposte or Labels.Absorb or Labels.Invulnerable;

    /* Display-only polish: some lines capture the third-person verb form ("bites", "crushes");
     * FCT reads better with the base form. */
    private static string SingularizeVerb(string verb)
    {
      if (string.IsNullOrEmpty(verb) || !verb.EndsWith('s') || verb.EndsWith("ss", StringComparison.Ordinal))
      {
        return verb;
      }

      var lower = verb.ToLowerInvariant();
      var dropsEs = lower.EndsWith("ses", StringComparison.Ordinal) || lower.EndsWith("xes", StringComparison.Ordinal) ||
                    lower.EndsWith("zes", StringComparison.Ordinal) || lower.EndsWith("ches", StringComparison.Ordinal) ||
                    lower.EndsWith("shes", StringComparison.Ordinal);
      return dropsEs ? verb[..^2] : verb[..^1];
    }

    internal void HandleHeal(HealProcessedEvent e)
    {
      if (e.Record is null || !e.IsMonitor)
      {
        return;
      }

      var healedMe = e.Record.Healed == ConfigUtil.PlayerName ||
                     PlayerRegistry.Instance.GetPlayerFromPet(e.Record.Healed) == ConfigUtil.PlayerName;
      var dealtByMe = e.Record.Healer == ConfigUtil.PlayerName ||
                      PlayerRegistry.Instance.GetPlayerFromPet(e.Record.Healer) == ConfigUtil.PlayerName;
      var crit = LineModifiersParser.IsCrit(e.Record.ModifiersMask);
      if (!healedMe && !dealtByMe)
      {
        return; // party-wide healing lands with the group config
      }

      // EQ heal lines read "for 9409 (11000)": Total is the effective amount, OverTotal the gross
      // (it already includes Total when present) — show effective and drop zero-effective overheals
      if (e.Record.Total == 0)
      {
        return;
      }

      Raise([new FctHitCommand
      {
        // a self-heal reads as healing on me
        Lane = healedMe ? FctLane.HealingReceived : FctLane.HealingDealt,
        Crit = crit,
        Value = e.Record.Total,
        Source = $"({e.Record.SubType})",
      }]);
    }

    private void Raise(IReadOnlyList<FctHitCommand> batch) => EventsHitsProcessed?.Invoke(batch);
  }
}
