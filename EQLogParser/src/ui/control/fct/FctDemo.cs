using System;
using System.Collections.Generic;

namespace EQLogParser
{
  /*
   * A short loop of numbers that plays while configure mode is up, so a size or style change can be seen moving without waiting for a
   * fight to produce one of each type on demand. Close to thirty events over twelve seconds: melee swings from a two-digit graze up to a
   * seven-figure nuke — every value shape the formatter can produce gets at least one cue, because the odometer is only convincing when its
   * narrowest and widest columns scroll side by side — plus a crit or two, a damage-over-time tick repeated three times (the `412 ×3` fold),
   * a proc, healing received from a HoT tick to a twenty-thousand-point crit heal, hits landing on you, the zero-damage words, and all five
   * special-attack marks. That is the vocabulary a player has to be able to tell apart, delivered in the order a fight would deliver it
   * rather than as exhibits pinned to a board.
   *
   * The events run through a private FctIngest into a private list. Same choreography as play — style, band, travel, fold, placement,
   * adaptive lifetime — and the same text building, which is the only reason what you see is what you get. It is a separate ingest
   * rather than the overlay's real one because the real one owns the counters: folding a demo number into a live hit, or counting a
   * demo drop as lost data, would put the demo inside the data. The list is drawn after the real numbers, and the only thing special about it is
   * that it animates on its own account (Animated): the canvas pumps frames for its own numbers and has to be told these move too. A real number
   * that arrives during configure mode behaves exactly as it always has, and appears on top of the demo.
   *
   * The vocabulary is EverQuest's own, taken from the files the parser itself reads: melee verbs are the ones in
   * StatsUtil.RegularMeleeTypes in the base form FctManager.DisplaySource shows them ("Bite", not "Bites"); spell names come from
   * data/spells.txt and proc names from data/procs.txt; the words are Labels constants so they cannot drift from what the parser
   * assigns. Invented names would teach a player to expect text that never appears. FctDemoTest checks these claims against the
   * shipped data files rather than trusting them.
   */
  internal sealed class FctDemo
  {
    /*
     * One loop of the script. Twelve seconds with the last cue at 7 s: every number has room to launch, travel, hold and fade before
     * the loop restarts, so a cycle never cuts one off mid-flight — and there is a beat of quiet at the end, which is what makes it
     * readable as a loop rather than as noise.
     */
    public const double CycleMs = 12000;

    /* How far the last cue sits from the end of the cycle: enough for the longest lifetime in play to finish inside the loop. */
    internal const double TailMs = CycleMs - 7480;

    /* One scheduled event. Same shape as the queue command a real log line becomes, which is deliberate: the demo feeds the ingest
       nothing a parse could not have fed it. */
    internal readonly struct Cue
    {
      public readonly double OffsetMs;
      public readonly FctLane Lane;
      public readonly double Value;
      public readonly string Source;
      public readonly bool Crit;
      public readonly bool Periodic;
      public readonly string ValueText;
      public readonly bool Proc;
      public readonly FctSpecial Special;

      internal Cue(double offsetMs, FctLane lane, double value = 0, string source = null, bool crit = false,
        bool periodic = false, string valueText = null, bool proc = false, FctSpecial special = FctSpecial.None)
      {
        OffsetMs = offsetMs;
        Lane = lane;
        Value = value;
        Source = source;
        Crit = crit;
        Periodic = periodic;
        ValueText = valueText;
        Proc = proc;
        Special = special;
      }
    }

    /* The script. Offsets are increasing: Advance walks it once per cycle, which is cheaper than searching and enough. */
    public static readonly IReadOnlyList<Cue> Script = new List<Cue>
    {
      new Cue(0, FctLane.DamageDealt, 1140, "Slash"),
      new Cue(280, FctLane.HealingReceived, 2480, "Complete Heal"),
      new Cue(560, FctLane.DamageTaken, 612, "Bite"),
      new Cue(760, FctLane.DamageDealt, 3418, "Crush", crit: true),
      new Cue(1040, FctLane.Defensive, valueText: Labels.Dodge),

      // three identical ticks, eight hundred ms apart: the fold needs to be seen happening, not described
      new Cue(1280, FctLane.DamageDealt, 412, "Venin", periodic: true),
      new Cue(1640, FctLane.DamageDealt, 412, "Venin", periodic: true),

      new Cue(1900, FctLane.DamageTaken, 830, "Claw"),
      new Cue(2200, FctLane.DamageDealt, 2170, "Flare"),
      new Cue(2520, FctLane.Missed, valueText: Labels.Miss),
      new Cue(2840, FctLane.DamageDealt, 412, "Venin", periodic: true),

      new Cue(3200, FctLane.HealingReceived, 3120, "Complete Heal", crit: true),
      new Cue(3560, FctLane.Defensive, valueText: Labels.Parry),
      new Cue(3900, FctLane.DamageTaken, 268, "Disease", periodic: true),

      // the marked events: the rare kind that earns a glyph, the purple hue and a crit's size (all five appear once per
      // cycle, spread through it — a configure session should teach the whole family, and every loop replays the lesson)
      new Cue(4080, FctLane.DamageDealt, 896805, "Decapitation XVIII", crit: true, special: FctSpecial.Decapitation),

      // an item or spell proc: a little smaller than the swing that provoked it, and gone sooner
      new Cue(4300, FctLane.DamageDealt, 1380, "Arcane Jolt", proc: true),

      // the odometer's narrow extreme next to the decapitation's 896.8k: a graze that abbreviates to nothing
      new Cue(4560, FctLane.DamageDealt, 47, "Punch"),

      new Cue(4720, FctLane.DamageDealt, 968, "Punch"),
      new Cue(5080, FctLane.HealingReceived, 1450, "Complete Heal"),
      new Cue(5260, FctLane.DamageDealt, 18240, "Backstab", crit: true, special: FctSpecial.Assassinate),
      new Cue(5460, FctLane.DamageTaken, 1742, "Crush", crit: true),
      new Cue(5700, FctLane.DamageDealt, 12470, "Slash", crit: true, special: FctSpecial.FinishingBlow),
      new Cue(5900, FctLane.Defensive, valueText: Labels.Riposte),

      // deliberately NOT a crit: the mark carries a crit's size on its own, and this cue is the proof of it
      new Cue(6100, FctLane.DamageDealt, 10460, "Pierce", special: FctSpecial.Headshot),
      new Cue(6450, FctLane.DamageDealt, 7310, "Crush", special: FctSpecial.SlayUndead),
      new Cue(6700, FctLane.HealingReceived, 1080, "Complete Heal", periodic: true),
      new Cue(6850, FctLane.HealingReceived, 24800, "Complete Heal", crit: true),
      new Cue(7150, FctLane.DamageDealt, 4126, "Slash", crit: true),

      // the m band: what a big nuke looks like after the server rates did their thing
      new Cue(7300, FctLane.DamageDealt, 1240000, "Flare", crit: true),

      // my spell failed the way a punch gets blocked: the word lane for outgoing failures, same as Miss
      new Cue(7480, FctLane.Missed, valueText: Labels.Resist),
    };

    /* Its own ingest on purpose: see the class comment. Nothing in here can reach the overlay's counters or its live numbers. */
    private readonly FctIngest _ingest = new();
    private readonly List<FctHitState> _hits = [];

    private double _cycleStartMs;
    private int _next;

    public bool Active { get; private set; }

    /* Drawn by the backend after the real hits, through the same per-hit draw path, aged from SpawnMs like any other number. */
    public IReadOnlyList<FctHitState> Hits => _hits;

    /*
     * True while demo text is on screen and moving, which is how the render pump knows to keep asking for frames. The question has to be asked of
     * the demo explicitly: a pump that invalidates only when it holds live real numbers repaints twice a second here - once when a cue fires, once
     * when something expires - so numbers hang in place and then jump, which is a slideshow and not an animation. Asking whenever the demo is merely
     * switched on is the opposite mistake: it would paint an empty canvas for the four quiet seconds at the end of every cycle.
     */
    public bool Animated => Active && _hits.Count > 0;

    /* Begins (or restarts) a cycle. Live numbers already on screen stay live: restarting the loop is not a reason to blank the overlay. */
    public void Start(double nowMs)
    {
      Active = true;
      _cycleStartMs = nowMs;
      _next = 0;
    }

    /*
     * Spawns whatever is due, ages out whatever finished, and wraps the cycle. Returns whether anything changed, so a backend that
     * only repaints on change knows to ask for a frame — the demo is animation, and animation that never sets the flag stands still.
     *
     * Wrapping resets the schedule and nothing else. Old numbers are deliberately left alone to finish their flights: clearing them
     * at the seam would make every loop visibly truncate a fade, which looks like a bug in the overlay rather than like a loop.
     *
     * The motion style and the layout choice arrive as parameters of the frame rather than as things set once on the demo, and that is
     * the whole lesson of this class: the loop runs its own FctIngest so it cannot touch the real counters, which also means it has its
     * own copy of both. Set those copies from the outside and forgetting once is invisible - selecting pulse played hold, and the
     * dropdown looked broken. Passed in beside the canvas size, there is nothing to remember, and neither can go stale between a control
     * changing and a number spawning.
     */
    public bool Advance(double nowMs, double w, double h, FctMotionStyle style, FctLayoutChoice layout, Action<FctHitState> spawned,
      Action<FctHitState> released, FctIngest gates = null)
    {
      if (!Active)
      {
        return false;
      }

      _ingest.Style = style;
      _ingest.Layout = layout;

      /* The gate settings ride along from the real ingest for the same reason style and layout do — this loop has its
       * own ingest, so everything it must agree with arrives fresh every frame. A demo that ignored the category
       * switches would cheerfully spawn exactly what the player just asked not to see, and a preview that contradicts
       * its controls is the bug this method's signature exists to prevent; it is optional only so the existing tests
       * (which are about the script) need not invent a feed to borrow gates from. */
      if (gates is not null)
      {
        _ingest.Threshold = gates.Threshold;
        _ingest.ShowDealt = gates.ShowDealt;
        _ingest.ShowTaken = gates.ShowTaken;
        _ingest.ShowHeals = gates.ShowHeals;
      }

      var changed = false;
      var elapsed = nowMs - _cycleStartMs;

      if (elapsed >= CycleMs)
      {
        _cycleStartMs = nowMs;
        _next = 0;
        elapsed = 0;
        changed = true;
      }

      while (_next < Script.Count && Script[_next].OffsetMs <= elapsed)
      {
        var cue = Script[_next++];

        // the real path, including whatever folding or placement it decides: a demo that skipped those would be showing a rehearsal
        var hit = _ingest.Accept(_hits, cue.Lane, cue.Value, cue.Source, cue.Crit, minor: false, cue.Periodic,
          cue.ValueText, w, h, nowMs, cue.Proc, cue.Special, released);

        if (hit is not null)
        {
          spawned?.Invoke(hit);
          changed = true;
        }
      }

      // a folded or dropped cue returns null: it still changed the picture (a count went up somewhere), so repaint anyway
      if (_ingest.PruneExpired(_hits, nowMs, released) > 0)
      {
        changed = true;
      }

      return changed;
    }

    /* Takes the demo away: every number released to the backend's cache cleanup, exactly as an expired real hit would be. */
    public void Clear(Action<FctHitState> released)
    {
      foreach (var hit in _hits)
      {
        released?.Invoke(hit);
      }

      _hits.Clear();
      _next = 0;
      Active = false;
    }

    /* A resize moves demo numbers the way it moves real ones — they were laid out against the old size and motion never revisits it. */
    public void Rescale(double oldW, double oldH, double w, double h) => FctResize.Rescale(_hits, oldW, oldH, w, h);
  }
}