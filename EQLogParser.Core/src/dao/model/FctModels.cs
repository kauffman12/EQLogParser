namespace EQLogParser
{
  /*
   * Lanes the floating combat text pipeline understands. Direction is vertical: incoming lanes (the ones about something
   * happening to me) get the overlay's bottom band and outgoing lanes get the top one. Crits stay on the region of the lane
   * that produced them. The renderers pool a crit into Crit, so FctManager never emits it — see
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

  /*
   * The rare events worth a mark beside the number: four combat procs the log flags in the record's modifier mask
   * (LineModifiersParser) and one that is only ever a spell name — Decapitation is the Berserker two-hander ability,
   * logged as ordinary spell damage ("... by Decapitation XVIII") with no modifier of its own. Each renders as a
   * glyph beside the value in one shared epic-purple family hue: purple answers "what kind of event", which is the
   * same question the lane palette answers, just at the rare end (docs/DesignNotes.md → Five events get marks).
   */
  internal enum FctSpecial
  {
    None = 0,
    Assassinate,
    Headshot,
    SlayUndead,
    FinishingBlow,
    Decapitation,
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

    /* A proc (Labels.Proc): an item or spell effect that fires on its own rather than the swing or cast the player
     * aimed. Subordinate by presentation — slightly smaller and shorter-lived — because it arrives on top of the
     * number the player was actually watching for. See docs/DesignNotes.md → "Procs are subordinate". */
    public bool Proc;

    public double Value;

    // "(Fireball)" is the renderer's job — it already owns fonts and layout
    public string Source;

    // non-numeric main-line text (defensive labels); null = format Value
    public string ValueText;

    /* The rare-event mark (assassinate, headshot, ...); None for everything a fight is mostly made of. */
    public FctSpecial Special;

    // Environment.TickCount64 at enqueue; the consumer drops commands that queued up behind a stalled UI
    public long EnqueueTick;
  }
}