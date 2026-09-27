using System;
using System.Collections.Generic;

namespace EQLogParser.Mirror
{
  // The displayed fight list: a pure projection of the immutable fact table over the CURRENT
  // classification. FightDeriver answers "what would the current pipeline have keyed?"; this
  // answers "which NPC-side entity is this exchange with, given everything we now know?".
  // Rows therefore migrate when evidence arrives - an exchange that started as a defender-keyed
  // guess under legacy's tiebreak moves to its true NPC row the moment R4/R9/overrides classify
  // one side - without a single fact changing. Rebuild is cheap and idempotent; MirrorSession
  // reruns it after every classification pass, which is the whole dynamic-update mechanism.
  //
  // Side rules (per fact, at the fact's own timestamp):
  //   Player/Pet/Merc              -> player side; a Friendly (charm) interval flips it to NPC side,
  //                                   so a mesmerised raider can own her own row and a charmed mob's
  //                                   output counts toward its defender's fight.
  //   Npc                          -> NPC side; Friendly interval makes it player side instead.
  //   Unknown                      -> resolved against the anchored neighbour (whoever attacks a
  //                                   known player is the NPC of that exchange and vice versa);
  //                                   when neither side is classified, the legacy name-heuristic
  //                                   tiebreak keys the row so unclassified bosses still appear.
  //   both players                 -> dropped: friendly fire and spell feedback are not fights.
  internal static class FightProjection
  {
    // A name's exchange stream splits into one row per engagement: two facts for the same owner
    // separated by more than this gap start a new fight, the way re-engaging a boss hours later
    // is a different fight from the legacy list's point of view. (The legacy list keyed those
    // boundaries off reset/slain events it could see live; a time gap is the classification-free,
    // idempotent equivalent - and never invents a boundary inside one continuous brawl.)
    public const double EngagementGapS = 300;

    // How far BEFORE a death to look for the charm window that death closed. See DiedWhileCharmed.
    private const double CharmDeathSlackS = 1;

    private enum Side : byte { Unknown, Player, Npc }

    /*
     * Optional reporter: which row a fact ended up owning. The projection is the single place that
     * decides sides, so anything that needs "the facts of this fight" (the damage summary fed from the
     * derived list, MirrorSummaryFights) has to be told here rather than re-deciding somewhere else —
     * a second copy of these rules would drift the moment a rule changes, and the drift would show up
     * as a summary that disagrees with the row it was opened from.
     *
     * `towardOwner` is the fact's direction inside its row: true when the row's own name was the DEFENDER,
     * i.e. the fact is damage done TO that entity rather than by it — the same test that splits DamageToOwner
     * from DamageByOwner, and the split a damage summary needs (FightManager puts everything aimed at the npc
     * in DamageBlocks and the mob's own output in TankingBlocks). Note it is not the same as "the attacker was
     * player-side": an unclassified name hitting a known NPC aims at the owner too, and belongs in those
     * blocks even while its per-player credit waits for a classification pass. Consumers cannot re-derive this
     * later, because which side a name was on depends on the timeline at the fact's own second.
     */
    internal delegate void FactOwnershipHandler(DamageFact fact, int ordinal, DerivedFight owner, bool towardOwner);

    public static List<DerivedFight> Build(DamageFactTable facts, EntityTimeline timeline, FactOwnershipHandler ownerSink = null)
    {
      // Open row per name; a gap (or a slain line) closes it and the next exchange opens a fresh
      // row under the same key. Rows keep arrival order until the final sort.
      Dictionary<string, DerivedFight> open = new(StringComparer.Ordinal);

      // The last CLOSED row per name, so a pet row can point at the encounter its charm closed. CharmPetRows needs
      // that link: the pet's facts begin after the encounter ends, so "does it overlap the selection?" alone can
      // never bring a hidden pet row back into a stats build — and hiding must not delete damage.
      Dictionary<string, DerivedFight> lastClosed = new(StringComparer.Ordinal);
      List<DerivedFight> rows = [];

      Dictionary<string, Queue<long>> deathsByName = new(StringComparer.Ordinal);
      foreach (var death in facts.Deaths)
      {
        var killed = facts.NameOf(death.KilledIdx);

        /*
         * A name that dies WHILE charmed does not die to the raid. Its NPC row was already closed at the
         * charm itself (DerivedFightEnd.Charmed — the raid got that mob by taking it), so letting this death
         * mark a row as slain would hand out a second kill for one corpse, and would do so on whichever row
         * of that name happened to be open — possibly a different instance three pulls away. It is skipped
         * here exactly as a party member's death is not a raid kill. Nothing is lost: the charm window itself
         * records this death as the reason it closed (CharmEndReason.Death on MirrorRuleOutcome.Charms).
         */
        if (DiedWhileCharmed(timeline, killed, death.TimeS)) continue;

        if (!deathsByName.TryGetValue(killed, out var q)) deathsByName[killed] = q = new Queue<long>();
        q.Enqueue(death.TimeS);
      }

      var allFacts = facts.Facts;
      for (var ordinal = 0; ordinal < allFacts.Length; ordinal++)
      {
        var fact = allFacts[ordinal];
        if (fact.AtkIdx == fact.DefIdx) continue;                    // self damage
        var atkName = facts.NameOf(fact.AtkIdx);
        if (ClassificationRules.IsSelfTargetDamageSpell(atkName)) continue; // spell feedback

        var defName = facts.NameOf(fact.DefIdx);
        var t = fact.TimeS;
        var atkSide = SideAt(timeline, atkName, t);
        var defSide = SideAt(timeline, defName, t);

        string key;
        bool creditAttacker;  // attacker was player-side: it gets damage credit in the owner's roll-up
        bool charmed = false;

        if (atkSide == Side.Player && defSide == Side.Player)
        {
          /*
           * Friendly fire is dropped — EXCEPT when the defender is a charmed mob. A mob under charm reads
           * player-side, but it is not a raider: swings that land on it come from the raid's own mistake (AoE
           * splash, or an add the charm did not take out of the fight) and they are real damage a meter has to
           * keep. Silently deleting them would shrink a player's total for the crime of hitting their own pet,
           * which is how this looked before charm windows could be seen at all. They key on the mob's name, so
           * the row that opens is the mob's post-charm half; nobody gets credit for being hit by their allies.
           *
           * A charmed RAIDER attacking us reads the other way (her identity flips to Npc-side, she owns her own
           * row), and a pet damaging its own side stays dropped — the log gives us no story to tell about it.
           */
          if (!IsFlipped(timeline, defName, t) || IsFlipped(timeline, atkName, t)) continue;
          key = defName;
          creditAttacker = true;
        }
        else if (atkSide == Side.Player && (defSide == Side.Npc || defSide == Side.Unknown))
        {
          key = defName; creditAttacker = true;
        }
        else if (defSide == Side.Player && (atkSide == Side.Npc || atkSide == Side.Unknown))
        {
          key = atkName; creditAttacker = false;
        }
        else if (atkSide == Side.Npc && (defSide == Side.Npc || defSide == Side.Unknown))
        {
          // Both sides NPC-side happens when a Friendly interval flips a player into the enemy
          // column: the charmed raider owns that row herself. Two mobs on each other (a mob pet,
          // or boss-vs-boss noise) is not a raid fight at all and stays out of the list.
          if (!IsFlipped(timeline, atkName, t)) continue;
          key = atkName; creditAttacker = false; charmed = true;
        }
        else if (defSide == Side.Npc && atkSide == Side.Unknown)
        {
          // known NPC defending against an unclassified attacker: our side is the anchor
          key = defName; creditAttacker = false;
        }
        else
        {
          // Unknown vs Unknown: nobody classified. Legacy's last-resort tiebreak keys on the
          // defender unless one name reads as obviously not-a-player, so nothing goes missing -
          // and a later classification pass redistributes these facts to real rows anyway.
          if (!PlayerRegistry.IsPossiblePlayerName(defName)) key = defName;
          else if (!PlayerRegistry.IsPossiblePlayerName(atkName)) key = atkName;
          else key = defName;
          // Nobody is classified: crediting an unclassified attacker as "player" would poison the
          // roll-up. A later pass that classifies it re-attributes the credit.
          creditAttacker = false;
        }

        open.TryGetValue(key, out var row);

        // A slain line ends the engagement only once a STRICTLY LATER timestamp arrives - the
        // same boundary legacy draws: CheckSlainQueue flushes on currentTime > _slainTime, so
        // same-second damage (the killing blow and "was slain" share a second-resolution stamp,
        // e.g. Waxwork Lancer @ 18:36:52) still lands in the old fight. "dt <= t" split that
        // combat mid-second: the first same-second fact consumed the death, closed the row at
        // the previous second, and every remaining hit of the kill - including the killing blow
        // itself - opened a zero-length live row behind the dead one.
        // A charm sighting closes this name's NPC engagement the way a slain line does — as a death, with
        // the reason kept so the list can say "charmed" instead of implying a killing blow. Checked before
        // the death boundary on purpose: when both happened, the charm came first.
        if (row is not null && ClosesForCharm(timeline, key, row.LastTime, t))
        {
          row.Dead = true;
          row.EndReason = DerivedFightEnd.Charmed;
          rows.Add(row);
          open.Remove(key);
          lastClosed[key] = row;
          row = null;
        }

        if (row is not null && deathsByName.TryGetValue(key, out var deaths)
            && deaths.TryPeek(out var dt) && dt < t)
        {
          deaths.Dequeue();
          row.Dead = true;
          row.EndReason = DerivedFightEnd.Slain;
          rows.Add(row);
          open.Remove(key);
          lastClosed[key] = row;
          row = null;   // any further deaths wait for a later fact of this name
        }

        if (row is not null && t - row.LastTime > EngagementGapS)
        {
          row.EndReason = DerivedFightEnd.Gap;
          rows.Add(row);
          open.Remove(key);
          lastClosed[key] = row;
          row = null;
        }
        if (row is null)
        {
          row = new DerivedFight
          {
            Name = key,
            BeginTime = t,
            LastTime = t,
            BeginDamageTime = t,
            LastDamageTime = t,
          };
          open[key] = row;
        }
        if (charmed || IsFlipped(timeline, key, t))
        {
          row.CharmedOwned = true;

          /*
           * Two different things are inside that condition, and only one of them is a pet: a charmed RAID MEMBER is
           * an encounter the raid has to fight (she stays in the list, badge and all), while a charmed MOB is ours —
           * a pet, and pets have no fight row (CharmPetRows).
           *
           * The answer is "does this name have an NPC reason of its own", and it has to be asked that way: the
           * charm confirm registers its OWN target as an NPC at R9-charm's strength, which outranks most real ones,
           * so the winning assignment says "NPC" for a raid member too. Hiding her would delete an encounter the
           * raid fought and took back; a charmed mob has npc.txt, its article shape or its own violence behind it.
           */
          if (timeline.HasIndependentIdentity(key, IdentityKind.Npc, t))
          {
            row.RaidPet = true;
            if (lastClosed.TryGetValue(key, out var prev))
            {
              // Chain forward: a second pet row of the same pull (the raid stopped swinging at it for ten minutes
              // and it reopened) belongs to the same encounter as the first, not to itself.
              row.EncounterRow = prev.RaidPet ? prev.EncounterRow
                                 : prev.EndReason == DerivedFightEnd.Charmed ? prev
                                 : null;
            }
          }
        }

        // Reported after the boundary checks and row creation, so every fact is announced exactly
        // once and to the row that really carries it in its totals. The direction is the same comparison
        // DamageToOwner is built from, one expression, so an index filled here cannot disagree with the row.
        ownerSink?.Invoke(fact, ordinal, row, string.Equals(key, defName, StringComparison.Ordinal));

        var isHit = LabelTypes.IsHit(fact.TypeId);
        if (isHit)
        {
          row.DamageHits++;
          row.DamageTotal += fact.Total;
          // Engagement totals in both directions land on DamageTotal; the split keeps the
          // legacy-comparable number (damage dealt TO the owner) readable next to it.
          if (string.Equals(key, defName, StringComparison.Ordinal)) row.DamageToOwner += fact.Total;
          else row.DamageByOwner += fact.Total;
        }
        if (t < row.BeginTime) row.BeginTime = t;
        if (t > row.LastTime) row.LastTime = t;

        // Player roll-up: a player-side attacker gets credit under its raw name - stronger than
        // the legacy registry-gated rollup, which silently dropped unregistered players. A
        // player-side DEFENDER is a victim, never credited.
        if (creditAttacker)
        {
          if (!row.PlayerRollup.TryGetValue(atkName, out var agg))
          {
            agg = new NameAgg();
            row.PlayerRollup[atkName] = agg;
          }
          agg.Add(fact.Total, isHit, null, t);
        }
      }

      foreach (var row in open.Values)
      {
        // Deaths after the last exchange still mark the engagement they follow.
        // A slain line lands shortly after the final exchange - inside one engagement's tail.
        if (deathsByName.TryGetValue(row.Name, out var deaths))
          while (deaths.TryPeek(out var dt) && dt <= row.LastTime + EngagementGapS)
          {
            deaths.Dequeue();
            row.Dead = true;
            row.EndReason = DerivedFightEnd.Slain;
          }

        // The usual fate of a charmed mob's row: the raid charms it and never swings again, so there is no
        // later fact to notice the boundary in. Same rule as the in-loop check, at the end of the list.
        if (!row.Dead && ClosesForCharm(timeline, row.Name, row.LastTime, double.PositiveInfinity))
        {
          row.Dead = true;
          row.EndReason = DerivedFightEnd.Charmed;
        }
        rows.Add(row);
      }

      // Engagement order is the display order; Ids are 1-based positions after sorting.
      var list = new List<DerivedFight>(rows);
      list.Sort(static (a, b) => a.BeginTime.CompareTo(b.BeginTime));
      for (var i = 0; i < list.Count; i++) list[i].Id = i + 1;
      return list;
    }

    // Which side was this name fighting on at time t - identity, then CHARM reversal only.
    // A Friendly interval from R5-called (a summoned pet) is a static statement of allegiance,
    // not a flip: only R9-charm windows reverse a name's side for their duration. Source names
    // are the rule tags stamped by ClassificationRules; "R9-charm" prefixes both charm variants.
    private static Side SideAt(EntityTimeline timeline, string name, double t)
    {
      var kind = timeline.IdentityAt(name, t);
      if (kind is IdentityKind.Unknown) return Side.Unknown;
      return IsFlipped(timeline, name, t)
        ? kind is IdentityKind.Npc ? Side.Player : Side.Npc
        : kind is IdentityKind.Npc ? Side.Npc : Side.Player;
    }

    // True when a charm interval has this name on the opposite of its identity side at t.
    private static bool IsFlipped(EntityTimeline timeline, string name, double t)
      => timeline.IsCharmedAt(name, t);

    /*
     * Was this death ours rather than the raid's? A charm that ends AT a death writes its interval up to that
     * instant, and interval bounds are exclusive — so the killing moment itself reads as "not charmed" unless
     * the question is also asked a moment earlier. The slack costs one thing worth naming: a real kill of a
     * DIFFERENT mob with the same name in the second after a window closes is skipped too, which is the
     * same-name ambiguity this feature already accepts (CharmWindowPolicy reports that share instead of
     * pretending to resolve it).
     */
    private static bool DiedWhileCharmed(EntityTimeline timeline, string name, double t)
      => timeline.IsCharmedAt(name, t) || timeline.IsCharmedAt(name, t - CharmDeathSlackS);

    /*
     * Does a charm sighting end this row? It has to be a sighting of THIS engagement: the charm begins after
     * the row's last fact and within the same engagement window that keeps rows apart (EngagementGapS), so a
     * raid charming a mob of the same name three pulls from now cannot retroactively kill this row. That also
     * means the ambiguity is inherited, not invented — two live mobs called `a skeleton` are one name to this
     * log, and CharmWindowPolicy reports that share (SameNameFactCount) rather than pretending otherwise.
     */
    private static bool ClosesForCharm(EntityTimeline timeline, string key, double lastTimeS, double nowS)
    {
      var start = timeline.CharmStartAfter(key, lastTimeS);
      return !double.IsNaN(start) && start <= nowS && start - lastTimeS <= EngagementGapS;
    }

  }
}
