using System;
using System.Collections.Generic;

namespace EQLogParser
{
  /*
   * What happens to an incoming hit: fold it into a live number, spawn it, queue it on its lane, or lose it to capacity.
   * Shared by every backend for the same reason as FctLayout — with two renderers, per-copy policy drifts
   * immediately. Rationale and the numbers behind these constants: docs/DesignNotes.md → Floating Combat Text.
   */
  internal sealed class FctIngest
  {
    /*
     * Hard ceiling of concurrent hits per lane. The adaptive lifetime already aims at
     * FctLifeController.Capacity (5-7); this is the backstop that stops a raid AoE stacking twenty
     * overlapping numbers. It merges into a live number before it drops, so totals survive overload.
     */
    private const int LaneCap = 12;

    /*
     * Folding is now only ever collapsing identical hits (same ability, same face value), so how wide the window is has
     * nothing to do with correctness and everything to do with what reads as one exchange. It is generous for that reason:
     * DoT ticks land seconds apart and a trinket proc repeats on its own schedule, and "×4" over 2.5 s is four real hits.
     * There is no sum to get wrong any more, so the only cost of a long window is that a number gains a count late.
     */
    private const double AbsorbWindowMs = 2500;
    private const double AbsorbMaxLifeFrac = 0.6;

    /* Crits do not adapt (they must stay prominent) and do not absorb; this is their whole display time. */
    private const double CritLifetimeMs = 2800;

    /* What "as short as a number may get" means once the player has asked for a faster overlay (§FctScale): below this a hit
     * appears and disappears quicker than it can be read, which is a flicker blamed on the overlay rather than snappiness. */
    private const double MinLifetimeMs = 900;
    private const double MinFadeMs = 200;

    /*
     * A full lane is a decision about who owns the slot, so a newcomer takes one away from something else only when it is
     * clearly the more informative of the two: at least this many times as significant. Significance is face value with a
     * proc discounted — FctStyle and ApplyProcTempo already say a proc is subordinate to the hit that provoked it, so an
     * old proc is the cheapest slot on screen and a big direct cast costs almost nothing to place.
     */
    private const double EvictValueFactor = 2.0;
    private const double ProcSignificanceFrac = 0.5;

    private readonly FctLifeController _life = new();
    private readonly FctConveyor _conveyor = new();
    private readonly Random _rand;

    public FctIngest(Random rand = null) => _rand = rand ?? new Random();

    /*
     * Motion style for new hits (see FctMotionStyle): how text moves, as opposed to where it goes, which the layout
     * decides and does not ask about. A canvas setting rather than a per-record one: a feed that mixed styles from log
     * line to log line would look like a bug, not a feature.
     */
    public FctMotionStyle Style = FctMotionStyle.Freeze;

    /*
     * The region scheme for new hits (see FctStage): where a number goes — which stream it belongs to, which edge it starts
     * from and how far the territory is — as opposed to how it moves, which Style decides. Same canvas-setting contract:
     * in-flight numbers keep the stage they were born under (it is baked into their bands and travel at spawn), so flipping
     * the layout mid-fight changes what comes next rather than teleporting what is already on screen.
     */
    public FctLayoutChoice Layout = FctLayoutChoice.Bands;

    /* Hits that had nowhere to go, surfaced in the settings panel's stats line so overload stays visible. */
    public int DroppedCount { get; private set; }

    /*
     * Numbers below this are never drawn — the MSBT "damage threshold" dial, offered in the settings panel. It is a
     * display filter applied at the gate, not a parser change: the log and every counter still see all of it. Zero (the
     * default) means off. Heals and zero-damage labels are exempt by design; see Accept.
     */
    public double Threshold;

    /* How many numbers the threshold has hidden, surfaced next to the drop count so a filter that is working is never
     * mistaken for a filter that is losing things silently. */
    public int HiddenCount { get; private set; }

    /*
     * Which sides reach the screen at all: my damage, damage on me, and healing. The show list stopped offering these, so they
     * now come from the lane choice — a category with no column in split mode is a category that is not drawn
     * (FctConfigState.OutgoingShown and friends) — which is also why a word belongs to its side: "miss" is my attack failing,
     * so hiding my attacks hides it. Fountain has no lanes to ask, so all three are true there.
     *
     * These are identity filters — "what story does this overlay tell" — where Threshold is the noise filter, which is
     * why they get their own switch and their own count instead of stretching the ladder. All three default on: the
     * controls are opt-outs, never things somebody has to discover. A category that is off pays nothing — no folding,
     * no placement, no glyphs — and its labels follow it, because a "Miss" belongs to whose damage story it is. The
     * log, the meters and exports never see any of this: FCT only ever decides what to draw.
     */
    public bool ShowDealt = true;
    public bool ShowTaken = true;
    public bool ShowHeals = true;

    /*
     * The rows: the same question asked one level finer, because "my damage" is three different complaints ("the crits are
     * too loud", "the ticks are noise", "I do not want to watch my pet"). One row claims each number — FctManager resolves
     * that where the parse still knows who hit what (FctRow) — and a number pays for its own row and nothing else, so hiding
     * spell crits cannot quiet a melee hit. The nine default on for the same reason the categories do: these are opt-outs.
     *
     * Rows never contradict the categories, they narrow them: a number has to pass both, which is why "pet melee off, my
     * melee on" and "all outgoing off, pet on" are both unrepresentable rather than ambiguous — the side switch wins, as it
     * does for the words, because hiding whose story it is hides what happened in it.
     */
    public bool ShowMeleeHits = true;
    public bool ShowMeleeCrits = true;
    public bool ShowSpellHits = true;
    public bool ShowSpellCrits = true;
    public bool ShowPetMelee = true;
    public bool ShowPetSpells = true;
    public bool ShowHealing = true;
    public bool ShowHealingCrits = true;

    /* Procs — the 20% weapon abilities that arrive as their own numbers — asked for a switch of their own: a player who
       wants the swing often reads the proc line as double-counting it, and one who does not wants silence. It is an
       extra opt-out on top of the categories (a proc is damage too), so hiding "my damage" hides procs with it; this
       only ever removes more, never restores. It is also the procs row (FctRow.Procs) — one switch, named after what the
       panel calls it, because "procs" was already the word for this event before there were rows to put it in. */
    public bool ShowProcs = true;

    /*
     * The words, one switch each. Categories and the threshold both work on numbers; the eight texts here are what the
     * parser writes into ValueText when a fight event has no number at all — the complete set is FctManager's
     * IsDefensiveLabel plus Resist — and a player's complaint about them is always specific: not "fewer words" but
     * ""miss" is drowning everything". So each stands alone; there is no master word switch, because the master answer
     * to "too many words" would be the side switches above, which already quiet whole stories at once. Words belong to
     * no gate but their own and the categories: the threshold never touches them (information, not volume), and a word
     * whose side is switched off was never going to draw anyway — these only ever remove more.
     */
    public bool ShowMiss = true;
    public bool ShowParry = true;
    public bool ShowDodge = true;
    public bool ShowBlock = true;
    public bool ShowRiposte = true;
    public bool ShowResist = true;
    public bool ShowAbsorb = true;
    public bool ShowInvulnerable = true;

    /* Counted apart from HiddenCount on purpose: "filtered" is a category the player switched off, "hidden" is a number
     * under their threshold — two choices, so two numbers, and neither one borrows the other's meaning. */
    public int FilteredCount { get; private set; }

    /* Word to its switch. The words arrive as Labels constants (FctManager stamps record.Type or Labels.Resist into the
     * command's ValueText), so this matches compile-time strings, not runtime text — and anything unknown draws: a word
     * these switches have never heard of is not silently somebody's opt-out side effect. */
    public bool WordShown(string word) => word switch
    {
      Labels.Miss => ShowMiss,
      Labels.Parry => ShowParry,
      Labels.Dodge => ShowDodge,
      Labels.Block => ShowBlock,
      Labels.Riposte => ShowRiposte,
      Labels.Resist => ShowResist,
      Labels.Absorb => ShowAbsorb,
      Labels.Invulnerable => ShowInvulnerable,
      _ => true,
    };

    /* Row to its switch, the same table and the same promise as WordShown: a row these switches have never heard of draws,
     * so a new Labels kind can never be silenced by being forgotten. FctRow.Word has no row switch — words are gated by
     * their text — and answers true here so the two gates stay independent rather than one implying the other. */
    public bool RowShown(FctRow row) => row switch
    {
      FctRow.MeleeHits => ShowMeleeHits,
      FctRow.MeleeCrits => ShowMeleeCrits,
      FctRow.SpellHits => ShowSpellHits,
      FctRow.SpellCrits => ShowSpellCrits,
      FctRow.Procs => ShowProcs,
      FctRow.PetMelee => ShowPetMelee,
      FctRow.PetSpells => ShowPetSpells,
      FctRow.Healing => ShowHealing,
      FctRow.HealingCrits => ShowHealingCrits,
      _ => true,
    };

    /* Assignment through the same map, so a caller holding a row (the settings window, which sees them as one alphabetical
     * list) never has to know which field backs it. Answers whether the row was one of these switches at all. */
    public bool SetRowShown(FctRow row, bool shown)
    {
      switch (row)
      {
        case FctRow.MeleeHits: ShowMeleeHits = shown; break;
        case FctRow.MeleeCrits: ShowMeleeCrits = shown; break;
        case FctRow.SpellHits: ShowSpellHits = shown; break;
        case FctRow.SpellCrits: ShowSpellCrits = shown; break;
        case FctRow.Procs: ShowProcs = shown; break;
        case FctRow.PetMelee: ShowPetMelee = shown; break;
        case FctRow.PetSpells: ShowPetSpells = shown; break;
        case FctRow.Healing: ShowHealing = shown; break;
        case FctRow.HealingCrits: ShowHealingCrits = shown; break;
        default: return false;
      }

      return true;
    }

    /* Assignment through the same map; answers whether the word was one of these switches at all, which is how the
     * canvas knows its demo restart is warranted. */
    public bool SetWordShown(string word, bool shown)
    {
      switch (word)
      {
        case Labels.Miss: ShowMiss = shown; break;
        case Labels.Parry: ShowParry = shown; break;
        case Labels.Dodge: ShowDodge = shown; break;
        case Labels.Block: ShowBlock = shown; break;
        case Labels.Riposte: ShowRiposte = shown; break;
        case Labels.Resist: ShowResist = shown; break;
        case Labels.Absorb: ShowAbsorb = shown; break;
        case Labels.Invulnerable: ShowInvulnerable = shown; break;
        default: return false;
      }

      return true;
    }

    /*
     * Everything a second ingest must agree with this one in order to preview it: the category switches, the words and the
     * threshold — never the counters, which belong to whichever feed is real. The demo loop runs its own ingest so sample
     * numbers can never reach the player's counts, which means every gate has to be handed over; written out once here rather
     * than at the call site because a long list of assignments copied by hand is a chance to forget one every time a switch is
     * added, and the symptom is a preview that contradicts its own controls.
     */
    public void CopyGatesFrom(FctIngest other)
    {
      if (other is null)
      {
        return;
      }

      Threshold = other.Threshold;
      ShowDealt = other.ShowDealt;
      ShowTaken = other.ShowTaken;
      ShowHeals = other.ShowHeals;
      ShowMeleeHits = other.ShowMeleeHits;
      ShowMeleeCrits = other.ShowMeleeCrits;
      ShowSpellHits = other.ShowSpellHits;
      ShowSpellCrits = other.ShowSpellCrits;
      ShowProcs = other.ShowProcs;
      ShowPetMelee = other.ShowPetMelee;
      ShowPetSpells = other.ShowPetSpells;
      ShowHealing = other.ShowHealing;
      ShowHealingCrits = other.ShowHealingCrits;
      ShowMiss = other.ShowMiss;
      ShowParry = other.ShowParry;
      ShowDodge = other.ShowDodge;
      ShowBlock = other.ShowBlock;
      ShowRiposte = other.ShowRiposte;
      ShowResist = other.ShowResist;
      ShowAbsorb = other.ShowAbsorb;
      ShowInvulnerable = other.ShowInvulnerable;
    }

    /* A count over the caller's list, so it is static: nothing about which ingest is counting decides how many rows of a
       lane are on screen — only which list is handed in. */
    public static int LiveCount(List<FctHitState> hits, FctLane lane)
    {
      var live = 0;
      for (var i = 0; i < hits.Count; i++)
      {
        if (hits[i].Lane == lane)
        {
          live++;
        }
      }

      return live;
    }

    /*
     * The single entry point both backends call. Returns the newly spawned hit — the caller still has to
     * build its glyphs — or null when the hit was folded into an existing one or dropped at the cap.
     *
     * `evicting` is called for any hit whose screen space this one took over, which today means the conveyor pushing a
     * cell. The caller owns releasing what it kept for that hit (glyph runs, halo references), and the hit is already gone
     * from the list by then.
     */
    public FctHitState Accept(List<FctHitState> hits, FctLane lane, double value, string source, bool crit, bool minor, bool periodic,
      string fixedText, double w, double h, double now, bool proc = false, FctRow row = FctRow.Word,
      FctSpecial special = FctSpecial.None, Action<FctHitState> evicting = null)
    {
      if (w < 100 || h < 100)
      {
        return null; // nothing sane can be laid out yet (window not measured)
      }

      var incoming = FctLayout.IsIncoming(lane);          // before pooling: a taken crit stays on the incoming side
      /* Also captured before pooling, for the same reason: a heal crit lands on FctLane.Crit, where its lane no longer says what it was. */
      var heal = lane is FctLane.HealingDealt or FctLane.HealingReceived;
      var pooled = crit ? FctLane.Crit : lane;

      /* The category gate, before even the threshold: whether this kind of number belongs on this overlay at all is a
         question the player already answered, and a filtered hit should not so much as be measured against the dial.
         The proc clause stays beside the row gates on purpose: the row a proc answers to is FctRow.Procs, which reads this
         same switch, so this only ever catches a caller that named a proc without naming a row — and both spellings of "a
         proc" agreeing on one switch is the point. */
      if (!(heal ? ShowHeals : incoming ? ShowTaken : ShowDealt) || (proc && !ShowProcs))
      {
        FilteredCount++;
        return null;
      }

      /* The row gate, one condition behind the categories and ahead of everything else for the same reason: a number whose row
         is off costs no folding, no placement, no glyph work — and it joins `filtered`, because "you hid that" and "it was
         under your threshold" are different answers and both need to be given. Rows are checked after the side they belong to
         because hiding whose story it is hides what happened in it (FctRow). */
      if (!RowShown(row))
      {
        FilteredCount++;
        return null;
      }

      /* The word gate, right behind the categories and for the same accounting: a switched-off word spends nothing —
         no folding, no placement, no glyph work — and joins the `filtered` count, because a player who mutes "miss"
         deserves to be told the overlay stopped drawing things, not to wonder where the fight went. */
      if (fixedText is not null && !WordShown(fixedText))
      {
        FilteredCount++;
        return null;
      }

      /*
       * The threshold gate, before folding, eviction and everything else: a number the player asked not to see should
       * cost none of that. Only damage is filtered — heals and the fixed-text labels (Miss, Dodge, Resist) are information
       * rather than volume, and hiding "you are being resisted" because the number beside it is small is exactly the
       * surprise this dial must not produce. Nothing disappears silently: HiddenCount says how many it has taken.
       */
      if (Threshold > 0 && fixedText is null && !heal && value < Threshold)
      {
        HiddenCount++;
        return null;
      }

      /* Everything below measures against the side's own region, and a stage is that question answered for this size. */
      var stage = Layout.Stage(w, h);

      /*
       * A rail scrolls across whatever region owns its side, and in bands that region is the canvas — strip included. The
       * settings panel cannot select one there, but settings.ini can be hand-written, which is why the degradation lives
       * here rather than only in the UI (see FctMotionStyle.Arc).
       */
      var style = stage.Mode is FctLayoutMode.Bands && FctMotionStyles.IsRail(Style)
        ? FctMotionStyle.Freeze
        : Style;

      /* Every rail is a conveyor rather than a placement problem (FctConveyor): one clock per column, spacing bought at
         entry, congestion scaled for the whole lane at once. It replaces the eviction check below too — a queued row has no
         slot to be stolen — so it is decided here, where the two other capacity rules (folding, the lane cap) can see which
         of them applies. */
      var conveyor = UseConveyor(style);

      if (fixedText is null)
      {
        if (!crit && ShouldAbsorb(pooled, periodic) &&
            TryAbsorb(hits, pooled, incoming, proc, periodic, special, source, value, now))
        {
          return null;
        }
      }

      /* The lane cap is a scatter rule: it asks "is there a slot?" and answers by taking one away from somebody. A conveyor
         row has no slot to be taken — its place in the queue was bought at entry, or it is waiting behind the mouth for it —
         so eviction would steal a visible number for an invisible one. The conveyor's own backlog ceiling decides capacity
         there, and every loss still counts in DroppedCount. */
      if (!conveyor && LiveCount(hits, pooled) >= LaneCap)
      {
        /*
         * The lane is full. A duplicate of something already on screen was folded away above — that attempt does not care
         * about occupancy, so a stream of identical hits never costs a slot.
         *
         * What is left is a number the lane has nowhere to put: take the slot from the least significant number already on
         * screen, but only if this newcomer clearly outranks it, so the thing that disappears is smaller and less worth
         * reading than the thing that arrives. This is what stops the cap from hiding a big cast behind twelve routine
         * swings.
         *
         * Only then count a drop. Everything above either put the number on screen or added it to a count that says how
         * many it stands for.
         */
        var taken = PickEvictionTarget(hits, pooled, incoming, Significance(value, 1, proc));
        if (taken is null)
        {
          DroppedCount++;
          return null;
        }

        hits.Remove(taken);
        evicting?.Invoke(taken);   // same contract as any other eviction: the backend releases what it kept for it
      }

      var hit = new FctHitState
      {
        Lane = pooled,
        Proc = proc,
        Periodic = periodic,
        Special = special,
        Heal = heal,
        Incoming = incoming,
        Style = style,
        SpawnMs = now,
        Source = source, // the name as the log said it; its drawn form is fitted to the column below, once, not per frame in the draw pass
        FixedText = fixedText,
        Value = value,
      };

      FctStyle.ApplyTo(hit, pooled, minor || periodic);

      /* Seeded BEFORE the placement that uses it. The clamp that keeps a row inside its own column is derived from the drawn width — and
         until a canvas exists to measure glyphs this estimate is all the geometry has, so stamping it afterwards meant every number was
         placed against a zero-width block and only the per-frame draw clamp ever caught up. */
      hit.FormattedValue = fixedText is null ? FctText.FormatHitValue(value) : null;
      hit.ValueWidth = FctLayout.EstimateTextWidth(fixedText ?? FctText.FormatHit(value, 1, heal), hit.ValueFontSize);

      FctLayout.Spawn(hit, stage, _rand);
      AssignLifetime(hit, hits, stage, now);

      /* What this row's own column can pay for in words. Placement gives the hit its territory (SideMin/SideMax), so the room is known the
         moment it is placed — and the label is then trimmed to it, never below what still says something and never shorter than it has to
         be: a small font or a wide window keeps a long name whole. The canvas re-takes this decision with measured glyphs (and again on a
         resize), because the estimate is only the geometry's best guess about what the renderer will draw. */
      /* The label is fitted to what the spine left it on each side (FitSource), not to the width of the region: at this point the row has its rail,
         so the numbers can be cut with the same geometry that will draw them. */
      FctLayout.FitSource(hit, FctLayout.EstimateTextWidth);

      if (conveyor)
      {
        /*
         * The row enters at its column's mouth and nowhere else: no search, no depth ladder, no emergency column, because
         * there is nothing to score — the lane already knows where this row may stand (FctConveyor). Pinning it is still the
         * layout's own maths (FctPlacement.Pin), so the flight, the clamp band and the odometer rail are exactly what the
         * scatter styles would have measured; only the choice of where to enter differs, and that choice is a queue.
         *
         * Nothing here trades readability for room. If the column cannot take the row even at its fastest, with twelve
         * arrivals already waiting, this one goes back — counted in DroppedCount, after folding already collapsed every
         * duplicate it could have been. Two numbers on top of each other is the one outcome this mode is not allowed.
         */
        var region = stage.RegionFor(hit);
        hit = FctPlacement.Pin(hit, stage, _rand,
          region.X + (region.Width / 2), stage.UpFor(hit) > 0 ? hit.BandMaxY : hit.BandMinY);

        if (!_conveyor.Enrol(hit, stage, now))
        {
          DroppedCount++;
          return null;
        }

        /* Not the row's tempo but its estimate of the lane's, for the things that still speak in milliseconds: absorb
           windows, statistics, anything that asks how long this number will be around. Its position is never read from it. */
        FinalizeRailTempo(hit);
      }
      else
      {
        /*
         * Text that travels gets free placement rather than a queue: several legal throws are measured along their whole
         * paths and the least crowded one wins. The hit that comes back may be one of those trials rather than the object
         * that went in. Every rail is a conveyor by now, so what reaches this branch is choreography — freeze, spray,
         * fountain — which is thrown, lands where it lands, and asks nothing of its neighbours but room.
         */
        hit = FctPlacement.Place(hit, hits, stage, _rand);
      }

      FctMotion.RefreshText(hit);
      hits.Add(hit);
      return hit;
    }

    /* Drops expired hits, newest-last so the list keeps its order. Returns how many went. */
    public int PruneExpired(List<FctHitState> hits, double now, Action<FctHitState> onRemoved = null)
    {
      /* The conveyor's clock runs here because this is the one verb every host already calls once a frame: a lane nobody
         advances is a column of numbers standing still (FctConveyor.Advance). Rows are stamped before the sweep below, so a
         row drawn this frame was drawn at the position the lane had reached by now. */
      _conveyor.Advance(hits, now);

      var removed = 0;
      for (var i = hits.Count - 1; i > -1; i--)
      {
        if (!Expired(hits[i], now))
        {
          continue;
        }

        onRemoved?.Invoke(hits[i]);
        hits.RemoveAt(i);
        removed++;
      }

      return removed;
    }

    /*
     * Which arrangements run as a conveyor: any rail — the straight line AND the arc, which are the two shapes split
     * offers and the reason the mode exists. The dial chooses the PATH (straight up the column, or MSBT's bowing chain),
     * never whether the traffic is ordered: rows that scroll a lane the player reads have to keep their spacing and one
     * speed, which is FctConveyor's whole subject. Fountain and spray are choreography and keep their scatter, and freeze
     * parks numbers where they landed — none of those is a queue and none needs one.
     *
     * No mode check, because there is nothing left to check: bands degrades every rail to freeze a few lines up, so a style
     * that is still a rail here belongs to the scheme whose regions are columns. Owning a column is what makes one train
     * per lane a promise instead of a coincidence (FctStage), and that promise is why the stream this replaced — flight
     * scoring among braided candidate columns, with congestion valves underneath — went away rather than being kept for
     * some second rail scheme.
     */
    private static bool UseConveyor(FctMotionStyle style) => FctMotionStyles.IsRail(style);

    /* A conveyor row's life is a distance, not a duration: it is finished when the lane has carried its own flight past it,
     * which is how a lane under pressure can clear a row in half the nominal time without that row blinking out early in the
     * middle of the column. Degenerate flights (no travel to speak of) fall back on the clock so nothing survives forever. */
    private static bool Expired(FctHitState hit, double now) =>
      hit.OnConveyor && hit.ConveyorTravel > 0
        ? hit.ConveyorQ >= hit.ConveyorTravel
        : now - hit.SpawnMs > hit.LifetimeMs;

    /*
     * Whether an incoming hit may be folded into a live number at all — the fold key itself is TryAbsorb's, and it now
     * includes the face value, so this is only about which kinds of event are allowed to be collapsed.
     *
     * Periodic ticks always may: one number reading "412 ×5" beats five overlapping 412s. Direct damage may too, but in
     * practice only when something identical is on screen — EQ repeats exact values constantly, so the collapse happens for
     * real without bending any rule.
     *
     * Healing direct casts never may, because players read heals one by one and collapsing two casts hides who got patched
     * and for how much. A heal with nowhere to go gets a slot taken for it (PickEvictionTarget) or is counted as dropped;
     * it does not join another heal's number.
     */
    private static bool ShouldAbsorb(FctLane lane, bool periodic) =>
      periodic || lane is FctLane.DamageDealt or FctLane.DamageTaken;

    /*
     * Folds a hit into the live number already showing exactly this — NAG's accumulateHits, with NAG's key plus the face
     * value: same pooled lane, same side, same proc/direct and periodic/direct kind, same ability name, same amount. Then the
     * age rules: the target has to be young enough that one number standing for several still reads as one exchange, and have
     * most of its life left, so a count never lands on something about to fade out. Crits never absorb — each one is the event
     * — and their overload is what the drop counter exists to make visible.
     *
     * Matching on the value is what makes "×N" a fact rather than an estimate: 2,040 ×2 really was two hits of 2,040.
     * Summing instead, which this used to do, put a number on screen that no hit ever landed for and made the player divide
     * to find out what happened.
     *
     * The rest of the key is load-bearing too. Matching on lane alone — the original — let an Immolation tick grow a melee
     * number that then read "(Spinning Attack)", and let two different DoTs taken collapse into whichever landed first; NAG
     * folds only into a component with identical flags, and Mik's Scrolling Battle Text merges only on matching event type
     * *and* skill name.
     */
    private static bool TryAbsorb(List<FctHitState> hits, FctLane lane, bool incoming, bool proc, bool periodic, FctSpecial special,
      string source, double value, double now)
    {
      /* The one part of the key that costs anything is formatted once for the incoming hit and reused across candidates — 
         and still only when a candidate has survived every cheaper test, exactly as before. The candidates carry theirs from
         spawn (FctHitState.FormattedValue), so the other side of the comparison no longer allocates at all. */
      string incomingText = null;
      for (var i = hits.Count - 1; i > -1; i--)
      {
        var hit = hits[i];
        if (hit.Lane != lane || hit.Blowout || hit.FixedText is not null
            || hit.Incoming != incoming || hit.Proc != proc || hit.Periodic != periodic
            // marks blow out like crits, so no marked row can be a fold TARGET; this comparison covers the other direction —
            // an incoming mark must not quietly fold into a plain row of the same number and lose its event
            || hit.Special != special
            || !string.Equals(hit.Source, source, StringComparison.Ordinal)

            /*
             * "Identical" means identical at the precision the player can read: two hits that draw the same main line are the
             * same number as far as anyone can tell, so 12,480 and 12,520 both being "12.5k" may share a count while 2,040 and
             * 2,041 may not. Compared last, because it is the only part of the key that allocates.
             */
            || !string.Equals(FoldValue(hit), incomingText ??= FctText.FormatHitValue(value), StringComparison.Ordinal))
        {
          continue;
        }

        var age = now - hit.SpawnMs;
        if (age > Math.Min(AbsorbWindowMs, hit.LifetimeMs * AbsorbMaxLifeFrac))
        {
          continue;
        }

        /* The one thing a fold does: say so. The drawn amount stays the face value of a single hit. */
        hit.MergeCount++;
        FctMotion.RefreshText(hit);
        return true;
      }

      return false;
    }

    /* The fold key's text for a live number, read through the cache it carries (see FctHitState.FormattedValue). The fallback
     * exists for hits placed straight into a list — tests mostly, and anything that bypasses Accept. */
    private static string FoldValue(FctHitState hit) => hit.FormattedValue ??= FctText.FormatHitValue(hit.Value);

    /*
     * What a number is worth keeping on screen: everything it stands for, with a proc discounted because one is subordinate
     * by design. The count matters because eviction is a choice about what to lose — dropping a number that represents six
     * identical hits loses six hits, not one, whatever its face value says.
     */
    private static double Significance(double value, int mergeCount, bool proc) =>
      value * Math.Max(1, mergeCount) * (proc ? ProcSignificanceFrac : 1.0);

    /*
     * The cheapest occupied slot in this lane and side: the least significant number there, excluding crits (a crit keeps
     * the ground its pop and glow bought — stealing it to show something smaller would be backwards) and labels (nothing
     * numeric to compare). Returns null unless the newcomer clearly outranks the weakest occupant, which is the whole
     * point: an occupied lane must not shuffle numbers around for a hit nobody would have missed.
     */
    private static FctHitState PickEvictionTarget(List<FctHitState> hits, FctLane lane, bool incoming, double incomingSignificance)
    {
      FctHitState weakest = null;
      var weakestScore = double.MaxValue;

      for (var i = 0; i < hits.Count; i++)
      {
        var hit = hits[i];
        if (hit.Lane != lane || hit.Incoming != incoming || hit.Blowout || hit.FixedText is not null)
        {
          continue;
        }

        var score = Significance(hit.Value, hit.MergeCount, hit.Proc);
        if (score < weakestScore)
        {
          weakest = hit;
          weakestScore = score;
        }
      }

      return weakest is not null && weakestScore * EvictValueFactor <= incomingSignificance ? weakest : null;
    }

    private void AssignLifetime(FctHitState hit, List<FctHitState> hits, FctStage stage, double now)
    {
      /*
       * The choreographed styles (fountain, spray) share one shape: travel, then accelerate along a fall for the rest
       * of life — no hold phase, and the fade spans exactly the fall. Both mirror that fall on the incoming band in
       * bands mode rather than dropping it: gravity points at the bottom of the screen, and their band is the last
       * thing before that edge, so a literal downward fall parks the number against its own bottom edge for half its
       * life, which reads as stuck rather than as physics. Falling back up toward the gap keeps the overshoot-and-settle
       * shape on both sides, keeps the two directions reading as one animation in opposite signs, and cannot reach the
       * protected strip because the return is a fraction of travel already spent below it.
       *
       * Freeze falls through to the adaptive lifetime: it has no fall, so it needs no life dictated by a choreography.
       * The fall itself is FctLayout.ApplyFall's, because placement may re-roll the origin afterwards
       * and has to be able to ask for it again.
       */
      if (hit.Style is FctMotionStyle.Fountain or FctMotionStyle.Spray)
      {
        // the choreography is the life: travel then fall, no hold phase, and the fade spans exactly the fall. Each style
        // owns its tempo — spray runs shorter, because sharing fountain's flight time was half of why the two looked alike
        // (and hit.Style, not the ingest's current one: a hit keeps the style it was born with)
        var window = hit.Style is FctMotionStyle.Spray ? FctMotion.SprayMotionWindowMs : FctMotion.MotionWindowMs;

        hit.LifetimeMs = window;
        hit.MotionMs = window;
        hit.FadeMs = window * FctMotion.FallPhaseFrac;

        // Rise is already assigned: layout runs before lifetime assignment. FctPlacement re-runs the same call on a trial origin.
        FctLayout.ApplyFall(hit, stage);
        ApplyProcTempo(hit);
        ApplyPlayerTempo(hit);
        return;
      }

      /* The rail's tempo belongs to the lane, not here: a conveyor row has its edge pinned after this call, so its Rise is
       * still provisional at this point in Accept — and a duration computed from a distance that changes afterwards is how
       * three rows on one rail each ended up with their own private tempo, phase drift shearing apart exactly the chain
       * this style exists to make. FctConveyor.Enrol and FinalizeRailTempo settle it once the flight is final. */
      if (FctMotionStyles.IsRail(hit.Style))
      {
        return;
      }

      // adaptive display time (see FctLifeController); crits keep a fixed lifetime and stay prominent
      hit.LifetimeMs = hit.Lane == FctLane.Crit ? CritLifetimeMs : _life.NextLifetime(hit.Lane, LiveCount(hits, hit.Lane), now);
      hit.MotionMs = Math.Min(FctMotion.MotionWindowMs, hit.LifetimeMs);
      hit.FadeMs = Math.Clamp(hit.LifetimeMs * 0.25, 250, 1000); // fade is a share of the life, capped
      ApplyProcTempo(hit);
      ApplyPlayerTempo(hit);
    }

    /*
     * The rail's tempo: ONE SCROLL RATE for everything in split — damage, procs, crits, words, both directions.
     * It is a rate, not a duration: each row's time is its own travel (edge to edge minus the room it reserves for
     * its own height — a crit gives up more road than a miss word does) over the shared px-per-second. The first
     * version shared a DURATION computed from the region instead, which is precisely a per-category speed difference:
     * measured 195.6 px/s for damage against 201.8 for words and slower still for crits, and players read it —
     * correctly — as the streams not agreeing. A shared rate costs almost nothing and keeps every chain property the
     * column relies on: two rows a beat apart keep that gap of road forever BECAUSE they move at one rate; bows run
     * their own arcs above the rail, and the lane's clock is what keeps the spacing underneath them. Rows that
     * parked at end of travel would all park in one place and the column would become a queue for a parking space:
     * motion spans the life, so nothing parks.
     *
     * Stamped where the flight becomes final — after FctPlacement.Pin has put the row on its edge (which is also why Pin
     * restamps a trial: neighbours carry their real tempo, and pricing a candidate at anything else rates it against the
     * traffic as if it moved at some other speed) — and again on a resize, where stretching the window must buy a row more
     * time rather than more speed. Recomputed from travel over the shared scroll rate, then scaled once by the player's
     * dial; an assignment rather than a multiplier, so nothing compounds however often it is called.
     */
    internal static void FinalizeRailTempo(FctHitState hit)
    {
      if (!FctMotionStyles.IsRail(hit.Style) || Math.Abs(hit.Rise) <= 1.0)
      {
        return;
      }

      /* RailPress rides in here rather than in the scroll-rate constant: the constant is the rhythm a rail keeps when it
         can keep it, the press is how much of that rhythm congestion takes away, and one row's acceleration must never
         change its neighbours' (rows that share a rail share their BIRTH tempo, not a live one - a row that sped up
         mid-flight would be an odometer lying about where it is going). */
      hit.LifetimeMs = Math.Abs(hit.Rise) * FctMotion.ArcScrollMsPerPx * hit.RailPress;
      hit.MotionMs = hit.LifetimeMs;
      hit.FadeMs = Math.Clamp(hit.LifetimeMs * 0.25, 250, 1000);
      ApplyPlayerTempo(hit);
    }

    /*
     * The player's speed setting last and on top of everything above, because it is the outermost dial: proc shortening and the adaptive controller
     * decide a number's tempo, this decides how fast that tempo plays. It arrives as FctScale.Time, a duration - the reciprocal of what the slider
     * measures, converted in one place so that nothing here has to remember which way the dial points. Lifetime, travel and fade scale together:
     * scaling only the lifetime leaves a number hanging in mid-air past the end of its animation, which reads as a stutter and not as a slower overlay.
     *
     * The floor matters because the multipliers compound: a proc is already at 0.7 of its lane's life (ApplyProcTempo) under an adaptive lifetime that
     * shrinks with load, and the fast end of the dial leaves a number on screen for less than half the time it was choreographed at. Without floors that
     * asks for well under a second, which is a flicker rather than a fast overlay.
     *
     * Applied unconditionally, including at the shipped speed: the dial's middle is 0.877 of the measured time rather than 1.0, because the baseline
     * turned out to sit on the slow side of useful (docs/DesignNotes.md). There is no longer an "unset" case in which this should keep its hands off the
     * numbers and let the floors stand down.
     */
    private static void ApplyPlayerTempo(FctHitState hit)
    {
      hit.LifetimeMs = Math.Max(MinLifetimeMs, hit.LifetimeMs * FctScale.Time);
      hit.MotionMs = Math.Min(hit.MotionMs * FctScale.Time, hit.LifetimeMs);
      hit.FadeMs = Math.Clamp(hit.FadeMs * FctScale.Time, MinFadeMs, hit.LifetimeMs);
    }

    /*
     * A proc is not the number the player was watching for: items and spell procs fire on their own schedule, often
     * several times a pull, and they land on top of the hit that provoked them. So the whole tempo shortens — travel,
     * hold and fade together, not just the tail — which pairs with the smaller type from FctStyle to keep a proc beside
     * a direct hit instead of competing with it.
     *
     * A crit proc is exempt: by definition that one deserves a look, and two rules quietly reducing the loudest number
     * in the log would be worse than either rule alone. Same exemption FctStyle applies to its size.
     */
    private static void ApplyProcTempo(FctHitState hit)
    {
      if (!hit.Proc || hit.Blowout)
      {
        return;
      }

      hit.LifetimeMs *= FctMotion.ProcTimeFrac;
      hit.MotionMs = Math.Min(hit.MotionMs * FctMotion.ProcTimeFrac, hit.LifetimeMs);
      hit.FadeMs *= FctMotion.ProcTimeFrac;
    }

  }
}