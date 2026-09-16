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

    /* No longer emitted: the overlay shows only heals that land on you (FctManager.HandleHeal), so nothing hands a number
     * to the "healing I did" lane any more. It stays because the direction rules and the layout tests still speak it, and
     * because a player who wants their own outgoing heals back would want them in this lane, not in a new one. */
    HealingDealt,
    HealingReceived,
    Crit,
    // zero-damage evades: Defensive = they failed against me, Missed = my own attack failed
    Defensive,
    Missed
  }

  /*
   * Which row of the settings panel's show list a record answers to. Resolved by FctManager, where the parse still knows
   * who attacked, whether it was melee and whether it crit — downstream there is only a lane and a few flags, which cannot
   * tell a pet's nuke from your own.
   *
   * Exactly one row claims every record, and a record pays for its own row and nothing else: hiding "spell crits" leaves
   * melee hits alone, hiding "pet melee" leaves your own swings alone. Two orderings do the work — a proc outranks both who
   * fired it and what fired it (an effect that fires on its own is its own event, which is also why a pet's proc belongs to
   * procs and not to the pet rows), and crit splits each kind into its own row so "hide the big numbers" is one switch.
   * Damage-over-time ticks are spell damage and go where their kind goes, crit tick included: they fold with it on screen
   * anyway (FctIngest), so a separate row would be a switch for something the eye cannot separate.
   *
   * Words are not a row. "miss", "block", "resist" and the rest keep their own switches (FctIngest.WordShown) because every
   * complaint about them has been about one word; they come through as FctRow.Word so that no row switch can reach them,
   * and the panel lists them beside these nine because that is the list a player reads.
   */
  internal enum FctRow
  {
    // nothing in this list applies: a zero-damage word, whose switch is chosen by its text
    Word,
    MeleeHits,
    MeleeCrits,
    SpellHits,
    SpellCrits,
    Procs,
    PetMelee,
    PetSpells,
    Healing,
    HealingCrits,
  }

  /*
   * The rare events worth a mark beside the number: four combat procs the log flags in the record's modifier mask
   * (LineModifiersParser) and one that is only ever a spell name — Decapitation is the Berserker two-hander ability,
   * logged as ordinary spell damage ("... by Decapitation XVIII") with no modifier of its own. Each renders as a
   * glyph beside the value in one shared epic-purple family hue: purple answers "what kind of event", which is the
   * same question the lane palette answers, just at the rare end
   * (docs/DesignNotes.md → "The purple family: special attacks wear a glyph, not a bigger number").
   */
  internal enum FctSpecial
  {
    None = 0,
    Assassinate,
    Headshot,
    SlayUndead,
    FinishingBlow,
    Decapitation,

    /* The burns: class abilities logged as ordinary spell damage — "Mana Burn" is the wizard's, "Life Burn" the
     * necromancer's. Resolved by spell name like Decapitation (LineModifiersParser.SpecialFor). */
    ManaBurn,
    LifeBurn,
  }

  /* One floating text handed to the renderer. Kept UI-agnostic so Core owns the feed, and kept free
   * of presentation: Source carries a bare ability/verb name (no parentheses, no casing tricks) and
   * ValueText is the literal main line for the zero-damage labels. See docs/DesignNotes.md. */
  internal sealed class FctHitCommand
  {
    public FctLane Lane;

    /* Which show-list row this belongs to (FctRow). Where it is drawn and which switch hides it are different questions:
     * a pet's spell damage shares my DamageDealt lane with my own and must still be hideable on its own. */
    public FctRow Row;

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