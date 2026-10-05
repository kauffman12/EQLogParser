using System;
using System.Collections.Generic;
using System.ComponentModel;

using EQLogParser;

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
  // appeared only on the row scrolled to. DerivedFightRowTest pins the interface.
  internal sealed class DerivedFightRow : INotifyPropertyChanged
  {
    // Row numbers are not data: the grid's row-header template shows the live position, exactly
    // like the current Fight Table - divider rows count, hidden dividers renumber. No `No` field.
    public bool IsDivider { get; init; }

    /*
     * What identifies this row from one derive pass to the next. For a fight it is the pair the selection restore already
     * trusted (name + start time - see FightTable.FightKey for why the row number cannot be it); for an inactivity divider it is
     * where the gap starts, which is stable while its LABEL grows: "Inactivity > 5 minutes" becoming "> 7 minutes" is the same gap
     * measured against a newer fact, and keying on the text would count it as a row leaving and another arriving.
     *
     * The key is stamped at build time because only the builder knows which kind of row this is and what its gap begins at; RowPatch
     * then asks for nothing but this string.
     */
    internal string Key { get; init; } = string.Empty;

    // Displayed cells. Settable with change notification so a derive pass can update a row IN PLACE (RowPatch) instead of handing
    // the grid a new object for it: the setter is where "did this cell change?" gets answered, per cell.
    private string _name = string.Empty;
    private string _identity = string.Empty;
    private string _source = string.Empty;
    private string _begin = string.Empty;
    private string _last = string.Empty;
    private string _duration = string.Empty;
    private string _status = string.Empty;
    private string _tooltip = string.Empty;
    private long _damage;
    private long _hits;

    public string Name { get => _name; set => Set(ref _name, value); }
    public string Identity { get => _identity; set => Set(ref _identity, value); }
    public string Source { get => _source; set => Set(ref _source, value); }
    public string Begin { get => _begin; set => Set(ref _begin, value); }
    public string Last { get => _last; set => Set(ref _last, value); }
    public string Duration { get => _duration; set => Set(ref _duration, value); }

    // Numeric so grid sorting is numeric.
    public long Damage { get => _damage; set => Set(ref _damage, value); }
    public long Hits { get => _hits; set => Set(ref _hits, value); }
    public string Status { get => _status; set => Set(ref _status, value); }

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
     * Everything the row SHOWS, compared cell by cell. Used by RowPatch to decide whether this row needs anything at all: on a live
     * raid night roughly 95 % of rows come back identical and get no write, no notification and no grid work (measured: median 42
     * changed rows of ~770; docs/DesignNotes.md → "Would an equality gate have saved anything?"). `Key`, `IsDivider` and
     * `IsSearchResult` are deliberately NOT part of it — the first two define identity, and the third is the reader's own mark, which a
     * rebuild must not be able to erase by arriving with a fresh one.
     */
    internal bool SameDisplayAs(DerivedFightRow other) =>
      Name == other.Name && Identity == other.Identity && Source == other.Source && Begin == other.Begin
      && Last == other.Last && Duration == other.Duration && Damage == other.Damage && Hits == other.Hits
      && Status == other.Status && TooltipText == other.TooltipText;

    /*
     * Copy the displayed cells (and the fight behind them) from the pass that just arrived, notifying per cell through the setters.
     * `Fight` rides along because a selection is materialized from it: leaving the OLD object here would keep feeding the boards last
     * pass's numbers to a row whose cells just moved. The instance stays, the data does not.
     */
    internal void CopyDisplayFrom(DerivedFightRow source)
    {
      Name = source.Name;
      Identity = source.Identity;
      Source = source.Source;
      Begin = source.Begin;
      Last = source.Last;
      Duration = source.Duration;
      Damage = source.Damage;
      Hits = source.Hits;
      Status = source.Status;
      TooltipText = source.TooltipText;
      Fight = source.Fight;
    }

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
      if (EqualityComparer<T>.Default.Equals(field, value)) return;
      field = value;
      PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /*
     * The row tooltip the grid's TemplateToolTip binds, in legacy's exact shape (FightManager):
     *   "#Hits To Players: t, #Hits From Players: f, Time Alive: Ns"
     * Duration and hits left the columns with this table's slim-down to the legacy three, so they live here.
     * The hits-to-players number is not DerivedFight.TankHits - FightProjection never stamps that field - it
     * is the count of facts aimed AT the name, which is what FightManager counted unconditionally (outcomes
     * included), and the damage index already keeps those ordinals for the tank board. The end status rides
     * on the end when there is one, since no column shows it any more either.
     */
    public string TooltipText { get => _tooltip; set => Set(ref _tooltip, value); }

    // The row's data, for the one thing the grid has to be able to DO right now: hand a selection to a
    // stats run. Formatted strings are what the grid shows; this is what a selection means. Settable because a patched row keeps its
    // object and must still point at the CURRENT pass's fight (CopyDisplayFrom), or the boards under a selection go stale by one pass.
    internal DerivedFight Fight { get; set; }
  }

  internal sealed class DerivedSnapshot
  {
    public List<DerivedFightRow> Rows = [];
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
    internal FightFactIndex DamageIndex;

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

  internal static class DerivedFightRows
  {
    public static DerivedSnapshot Build(IReadOnlyList<DerivedFight> fights, EntityTimeline timeline, long factCount,
      DamageFactTable facts, FightFactIndex damageIndex)
    {
      var snapshot = new DerivedSnapshot
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
          snapshot.Rows.Add(new DerivedFightRow
          {
            IsDivider = true,
            Key = DividerKey(gapFrom),
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
        snapshot.Rows.Add(new DerivedFightRow
        {
          Key = FightKey(fight),
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
           * row tooltip (`Time Alive: 46s`) — so this is the engine's own number and it might as well be exact.
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

    // The two key shapes, in one place so a fight row and a divider row cannot drift into colliding keys (a collision is what RowPatch
    // refuses outright - it cannot guess which of two rows an incoming one is).
    internal static string FightKey(DerivedFight fight) => $"F:{fight.Name}\u0000{fight.BeginTime.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}";

    internal static string DividerKey(double gapFrom) =>
      $"D:{gapFrom.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}";

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
