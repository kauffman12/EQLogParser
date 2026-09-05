namespace EQLogParser
{
  /* Lanes the FCT renderer understands. Crit gets its own random band, like NAG. */
  internal enum FctLane
  {
    DamageDealt,
    DamageTaken,
    Healing,
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
   * Feeds floating combat text from parsed records. Damage and heal events arrive on the log reader
   * thread and also fire during historical replay (opening a log file), so everything before the
   * first monitor line — stamped by LogProcessor via MarkLiveLine — is dropped. Subscribers are
   * responsible for batching onto their own UI thread; see FctSkiaCanvas and
   * docs/NagFctReference.md for the rendering side.
   */
  internal class FctManager : ILifecycle
  {
    /* tolerate a record timestamped a hair before the first monitor line (same-millisecond edges) */
    private const double ReplayGraceMs = 50;

    // singleton with set for unit test, like FightManager
    internal static FctManager Instance { get; set; } = new();

    /* Reader thread — subscribers must marshal to their UI thread. */
    internal event Action<IReadOnlyList<FctHitCommand>> EventsHitsProcessed;

    /* cross-thread: read via Volatile.Read, like FightManager.LastFightProcessTime */
    private double _liveStart = double.NegativeInfinity;

    private FctManager()
    {
      DamageLineParser.EventsDamageProcessed += HandleDamage;
      HealingLineParser.EventsHealProcessed += HandleHeal;
      LifecycleManager.Register(this);
    }

    public void Clear(bool serverChanged) => Volatile.Write(ref _liveStart, double.NegativeInfinity);

    public void Shutdown()
    {
    }

    /* First monitor line of the current session; replay records carry earlier timestamps. */
    internal void MarkLiveLine(double beginTime)
    {
      if (!double.IsNaN(beginTime) && Volatile.Read(ref _liveStart) == double.NegativeInfinity)
      {
        Volatile.Write(ref _liveStart, beginTime);
      }
    }

    /* Internal so unit tests can drive the feed without parsing log lines. */
    internal void HandleDamage(DamageProcessedEvent e)
    {
      if (e.Record is null || IsReplay(e.BeginTime))
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
      if (e.Record is null || IsReplay(e.BeginTime))
      {
        return;
      }

      // heals I deal for now; healed-by-me lands with the group config once lanes are user-defined
      if (e.Record.Healer != ConfigUtil.PlayerName &&
          PlayerRegistry.Instance.GetPlayerFromPet(e.Record.Healer) != ConfigUtil.PlayerName)
      {
        return;
      }

      Raise([new FctHitCommand
      {
        Lane = FctLane.Healing,
        Value = e.Record.Total + e.Record.OverTotal,
        Source = $"({e.Record.SubType})",
      }]);
    }

    private bool IsReplay(double beginTime)
    {
      if (double.IsNaN(beginTime))
      {
        return true;
      }

      var liveStart = Volatile.Read(ref _liveStart);
      return liveStart == double.NegativeInfinity || beginTime + ReplayGraceMs < liveStart;
    }

    private void Raise(IReadOnlyList<FctHitCommand> batch) => EventsHitsProcessed?.Invoke(batch);
  }
}
