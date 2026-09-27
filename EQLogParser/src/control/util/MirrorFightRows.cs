using System;
using System.Collections.Generic;

using EQLogParser.Mirror;

namespace EQLogParser
{
  // One row of the derived fight list: a fight, or an inactivity divider (D5 — legacy's
  // IsInactivity pseudo-row shape: "Inactivity > mm:ss"). Strings are formatted once at build
  // time; the grid binds plain properties.
  internal sealed class MirrorFightRow
  {
    // Row numbers are not data: the grid's row-header template shows the live position, exactly
    // like the current Fight Table - divider rows count, hidden dividers renumber. No `No` field.
    public bool IsDivider { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Identity { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Begin { get; init; } = string.Empty;
    public string Last { get; init; } = string.Empty;
    public string Duration { get; init; } = string.Empty;

    // Numeric so grid sorting is numeric.
    public long Damage { get; init; }
    public long Hits { get; init; }
    public string Status { get; init; } = string.Empty;

    // The row's data, for the one thing the grid has to be able to DO right now: hand a selection to a
    // stats run. Formatted strings are what the grid shows; this is what a selection means.
    internal DerivedFight Fight { get; init; }
  }

  internal sealed class MirrorSnapshot
  {
    public List<MirrorFightRow> Rows = [];
    public int FightCount;
    public long FactCount;
    public double ElapsedMs;
    public DateTime DerivedAt;

    // Kept for the interactive step: identity overrides call ClassificationRules.ApplyManualOverride
    // against this timeline, then trigger a re-derive.
    internal EntityTimeline Timeline;

    // What a selection needs in order to become stats input: the captured facts of THIS pass and, per
    // fight, which of them were aimed at its own name. Both belong to the snapshot rather than to the
    // session because a re-derive replaces the projection wholesale — rows from the old list must never
    // be materialized against the new one's classification.
    internal DamageFactTable Facts;
    internal MirrorDamageIndex DamageIndex;

    /*
     * The heal stream as of this pass. Nothing is displayed from it yet — that is the heal projection's job —
     * but a snapshot has to be able to say what was captured, and a selection has to be materializable
     * against the same pass that made its rows (the damage index above exists for exactly that reason).
     */
    internal HealFactTable Heals;
  }

  internal static class MirrorFightRows
  {
    public static MirrorSnapshot Build(IReadOnlyList<DerivedFight> fights, EntityTimeline timeline, long factCount,
      DamageFactTable facts, MirrorDamageIndex damageIndex)
    {
      var snapshot = new MirrorSnapshot
      {
        FactCount = factCount,
        DerivedAt = DateTime.Now,
        Timeline = timeline,
        Facts = facts,
        DamageIndex = damageIndex,
      };

      foreach (var (isDivider, gapFrom, gapTo, fight) in Sectionizer.ToDisplayRows(fights))
      {
        if (isDivider)
        {
          snapshot.Rows.Add(new MirrorFightRow
          {
            IsDivider = true,
            Name = "Inactivity > " + DateUtil.FormatGeneralTime(Math.Max(0, gapTo - gapFrom)),
          });
          continue;
        }

        snapshot.FightCount++;
        var identity = timeline.IdentityWithSource(fight.Name, out var source);
        snapshot.Rows.Add(new MirrorFightRow
        {
          Name = fight.Name,
          Identity = identity.ToString(),
          Source = source ?? string.Empty,
          Begin = fight.BeginTimeString,
          Last = DateUtil.FormatDotNetDateSeconds(fight.LastTime),
          Duration = DateUtil.FormatGeneralTime(Math.Max(0, fight.EndTime - fight.BeginTime)),
          Damage = fight.DamageTotal,
          Hits = fight.DamageHits,
          Status = StatusOf(fight),
          Fight = fight,
        });
      }

      return snapshot;
    }

    /*
     * The status column: how the row ended, and who owned it when it did. A charm close says "dead, charmed"
     * because that is what it is — the raid finished that mob by taking it off the enemy list, and this column
     * is the only way the grid has of showing a death at all (agreed 2026-10: keep the charm as the REASON and
     * treat the row like the death it replaced, rather than inventing a third visual state).
     *
     * A row that merely stopped — an inactivity gap, or still open on a live capture — stays blank exactly as
     * before, and "charmed" alone is a charmed raider's row: she is here because a charm put her on the enemy
     * side, which is not a death and must not look like one.
     */
    private static string StatusOf(DerivedFight fight)
      => fight.EndReason switch
      {
        DerivedFightEnd.Charmed => "dead, charmed",
        DerivedFightEnd.Slain => fight.CharmedOwned ? "dead, charmed" : "dead",
        _ => fight.Dead ? "dead" : fight.CharmedOwned ? "charmed" : string.Empty,
      };
  }
}
