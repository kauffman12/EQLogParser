namespace EQLogParser
{
  /*
   * Lanes the floating combat text pipeline understands. Incoming lanes (the ones about something happening to me)
   * get the overlay's incoming region and outgoing lanes get the other; which region that is — bottom band, or the
   * left half — is the renderer's layout mode (FctLayoutMode), not this enum's concern. Crits stay on the region of
   * the lane that produced them. The renderers pool a crit into Crit, so FctManager never emits it — see
   * docs/DesignNotes.md → Floating Combat Text.
   */
  internal enum FctLane
  {
    DamageDealt,
    DamageTaken,
    HealingDealt,
    HealingReceived,
    Crit,
    // zero-damage evades: Defensive = they failed against me, Missed = my own attack failed
    Defensive,
    Missed
  }

  /* One floating text handed to the renderer. Kept UI-agnostic so Core owns the feed, and kept free
   * of presentation: Source carries a bare ability/verb name (no parentheses, no casing tricks) and
   * ValueText is the literal main line for the zero-damage labels. See docs/DesignNotes.md. */
  internal sealed class FctHitCommand
  {
    public FctLane Lane;

    // the source lane survives even for crits: the renderer pools a crit onto its producing lane's region
    public bool Crit;

    // DoT/HoT tick: smaller type, and the first candidate for grouping (docs/combat-text-overlay-design.md §4)
    public bool Periodic;

    public double Value;

    // "(Fireball)" is the renderer's job — it already owns fonts and layout
    public string Source;

    // non-numeric main-line text (defensive labels); null = format Value
    public string ValueText;

    // Environment.TickCount64 at enqueue; the consumer drops commands that queued up behind a stalled UI
    public long EnqueueTick;
  }
}
