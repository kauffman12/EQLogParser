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

    private enum Side : byte { Unknown, Player, Npc }

    public static List<DerivedFight> Build(DamageFactTable facts, EntityTimeline timeline)
    {
      // Open row per name; a gap (or a slain line) closes it and the next exchange opens a fresh
      // row under the same key. Rows keep arrival order until the final sort.
      Dictionary<string, DerivedFight> open = new(StringComparer.Ordinal);
      List<DerivedFight> rows = [];

      Dictionary<string, Queue<long>> deathsByName = new(StringComparer.Ordinal);
      foreach (var death in facts.Deaths)
      {
        var killed = facts.NameOf(death.KilledIdx);
        if (!deathsByName.TryGetValue(killed, out var q)) deathsByName[killed] = q = new Queue<long>();
        q.Enqueue(death.TimeS);
      }

      foreach (var fact in facts.Facts)
      {
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

        if (atkSide == Side.Player && defSide == Side.Player) continue; // friendly fire

        if (atkSide == Side.Player && (defSide == Side.Npc || defSide == Side.Unknown))
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

        // A slain line at/before this fact ends the engagement that was open when it died.
        if (row is not null && deathsByName.TryGetValue(key, out var deaths)
            && deaths.TryPeek(out var dt) && dt <= t)
        {
          deaths.Dequeue();
          row.Dead = true;
          rows.Add(row);
          open.Remove(key);
          row = null;   // any further deaths wait for a later fact of this name
        }

        if (row is not null && t - row.LastTime > EngagementGapS)
        {
          rows.Add(row);
          open.Remove(key);
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
        if (charmed || IsFlipped(timeline, key, t)) row.CharmedOwned = true;

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
      => timeline.AffiliationAt(name, t, out var source) == AffiliationKind.Friendly
         && source is not null && source.StartsWith("R9-charm", StringComparison.Ordinal);

  }
}
