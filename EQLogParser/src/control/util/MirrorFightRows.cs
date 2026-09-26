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
  }

  internal static class MirrorFightRows
  {
    public static MirrorSnapshot Build(IReadOnlyList<DerivedFight> fights, EntityTimeline timeline, long factCount)
    {
      var snapshot = new MirrorSnapshot
      {
        FactCount = factCount,
        DerivedAt = DateTime.Now,
        Timeline = timeline,
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
          Status = $"{(fight.Dead ? "dead" : string.Empty)}{(fight.Dead && fight.CharmedOwned ? ", " : string.Empty)}{(fight.CharmedOwned ? "charmed" : string.Empty)}",
        });
      }

      return snapshot;
    }
  }
}
