using System;
using System.Collections.Generic;
using System.ComponentModel;

using EQLogParser.Mirror;

namespace EQLogParser
{
  // One row of the derived fight list: a fight, or an inactivity divider (D5 — legacy's
  // IsInactivity pseudo-row shape: "Inactivity > mm:ss"). Strings are formatted once at build
  // time; the grid binds plain properties.
  // INotifyPropertyChanged is LOAD-BEARING as an interface, not just as an event: WPF's binding engine checks
  // whether the item IMPLEMENTS the interface and never looks for a bare `PropertyChanged` event by name. A row
  // class that declares the event without implementing it fails SILENTLY — every realized row keeps its painted
  // value while only rows realized AFTER the change (scrolled into view) show it. That is exactly how the search
  // mark stopped moving between visible rows: cycling worked, ScrollInView was a correct no-op, and the green
  // appeared only on the row scrolled to. MirrorFightRowTest pins the interface.
  internal sealed class MirrorFightRow : INotifyPropertyChanged
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

    // The row search highlighted. The one live property on an otherwise value-object row - it changes after
    // construction, which is why the grid's DataTrigger reaches it through PropertyChanged (legacy's own
    // Fight.IsSearchResult works the same way — and legacy's Fight really does implement the interface).
    private bool _isSearchResult;

    public bool IsSearchResult
    {
      get => _isSearchResult;
      set
      {
        if (_isSearchResult == value) return;
        _isSearchResult = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSearchResult)));
      }
    }

    public event PropertyChangedEventHandler PropertyChanged;

    /*
     * The row tooltip the grid's TemplateToolTip binds, in legacy's exact shape (FightManager):
     *   "#Hits To Players: t, #Hits From Players: f, Time Alive: Ns"
     * Duration and hits left the columns with this table's slim-down to the legacy three, so they live here.
     * The hits-to-players number is not DerivedFight.TankHits - FightProjection never stamps that field - it
     * is the count of facts aimed AT the name, which is what FightManager counted unconditionally (outcomes
     * included), and the damage index already keeps those ordinals for the tank board. The end status rides
     * on the end when there is one, since no column shows it any more either.
     */
    public string TooltipText { get; init; } = string.Empty;

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

    /*
     * The projection in full, including the rows the grid hides (CharmPetRows: a charmed mob's own output is our
     * pet's work, not an encounter). Rows are shown from Visible() but a click's stats build has to see all of
     * them, or hiding would delete the charmer's +Pets damage.
     */
    internal IReadOnlyList<DerivedFight> AllFights = [];
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

      snapshot.AllFights = fights;

      foreach (var (isDivider, gapFrom, gapTo, fight) in Sectionizer.ToDisplayRows(CharmPetRows.Visible(fights)))
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
        var status = StatusOf(fight);
        var tooltip = $"#Hits To Players: {damageIndex.TankingOrdinalsFor(fight).Count}, "
                    + $"#Hits From Players: {fight.DamageHits}, Time Alive: {(long)fight.DurationSeconds}s";
        if (status.Length > 0)
          tooltip += $", {status}";
        snapshot.Rows.Add(new MirrorFightRow
        {
          Name = fight.Name,
          Identity = identity.ToString(),
          Source = source ?? string.Empty,
          Begin = fight.BeginTimeString,
          Last = DateUtil.FormatDotNetDateSeconds(fight.LastTime),
          /*
           * Seconds, not words. FormatGeneralTime is the fuzzy "5 minutes" formatter, and on this column it was
           * worse than approximate: under a minute it returns an empty string (so the 46-second first life of
           * Waxwork Abolishion showed no duration at all), and 112 s and 162 s both read "1 minute"/"2 minutes",
           * which is no way to compare two pulls. The legacy grid has no duration column — the seconds lived in the
           * row tooltip (`Time Alive: 46s`) — so this is the mirror's own number and it might as well be exact.
           *
           * And it counts them the way the product counts them: INCLUSIVE (DerivedFight.DurationSeconds), which is
           * what makes this cell agree with the tooltip of the legacy row above it and, one click away, with the
           * damage summary's DPS denominator. An exclusive span here would print "00:00" for a mob hit once inside a
           * second while its own board says "Time Alive: 1s" and divides by one second.
           */
          Duration = DateUtil.FormatTicks(TimeSpan.FromSeconds(fight.DurationSeconds).Ticks,
                                         DateUtil.TimeFormat.HMSCompact),
          Damage = fight.DamageTotal,
          Hits = fight.DamageHits,
          Status = status,
          TooltipText = tooltip,
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
     * before, and "charmed" alone is a charmed raid member's row: she is here because a charm put her on the enemy
     * side, which is not a death and must not look like one. A charmed MOB in that same state never reaches this
     * column at all — it is our pet, and CharmPetRows keeps pets off the list.
     */
    private static string StatusOf(DerivedFight fight)
      => fight.EndReason switch
      {
        DerivedFightEnd.Charmed => "dead, charmed",
        _ => fight.Dead ? "dead" : fight.CharmedOwned && !fight.RaidPet ? "charmed" : string.Empty,
      };
  }
}
