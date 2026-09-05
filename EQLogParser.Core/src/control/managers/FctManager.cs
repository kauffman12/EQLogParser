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
    Crit
  }

  /* One floating text for a canvas to draw. Kept UI-agnostic so Core can own the feed. */
  internal sealed class FctHitCommand
  {
    public FctLane Lane;
    public double Value;
    public string Source; // "(melee)" / "(Fireball)" / ...
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
      var self = record.Attacker == ConfigUtil.PlayerName ||
                 PlayerRegistry.Instance.GetPlayerFromPet(record.Attacker) == ConfigUtil.PlayerName;
      var crit = LineModifiersParser.IsCrit(record.ModifiersMask);

      Raise([new FctHitCommand
      {
        Lane = crit ? FctLane.Crit : self ? FctLane.DamageDealt : FctLane.DamageTaken,
        Value = record.Total + record.OverTotal,
        Source = $"({record.SubType})",
      }]);
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
      if (!healedMe && !dealtByMe)
      {
        return; // party-wide healing lands with the group config
      }

      Raise([new FctHitCommand
      {
        // a self-heal reads as healing on me
        Lane = healedMe ? FctLane.HealingReceived : FctLane.HealingDealt,
        Value = e.Record.Total + e.Record.OverTotal,
        Source = $"({e.Record.SubType})",
      }]);
    }

    private void Raise(IReadOnlyList<FctHitCommand> batch) => EventsHitsProcessed?.Invoke(batch);
  }
}
