using log4net;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

using EQLogParser;

/*
 * Annotations only, no null-flow analysis: this project builds with Nullable=disable, and the census API genuinely
 * speaks in optional strings (a name may have no class, no owner, no reason). The pragma states that without turning
 * on a warning wave across code written before nullable existed.
 */
#nullable enable annotations

namespace EQLogParser
{
  /*
   * The Player/NPC Identity window (menu name; class still NamesTable): every name in the capture, what it was called, and WHY. It replaces the two hand-maintained verdict
   * panes (Verified Players / Verified Pets), which could show what somebody typed but never what the classifier
   * concluded - so a wrong verdict had no surface to be noticed on, only a meter that looked odd. Pet Owners keeps
   * its own window: it curates the persistent owner pairs (petmapping.txt) across logs, which is a different job
   * from reporting what one capture's evidence says.
   *
   * Two things this deliberately is not:
   *
   *   - Not a live grid, and deliberately so. A derive runs every few seconds while a log loads and a census nobody has
   *     open would be thrown away each time; worse, a list that rearranges itself under a reader cannot be read at all.
   *     So the census is built ON DEMAND ONLY — when the pane is first shown, when the menu opens it, when a new log
   *     starts, and by the Refresh button — off the UI thread, applied in one swap. Nothing subscribes to the derive.
   *   - Not a second source of truth. Every verb here writes through ClassificationCommands into the same per-server
   *     files the old panes used (verdicts, roster, rejections) plus the sighting ledger. Nothing is stored here, so
   *     closing the window loses nothing and a file edited by hand still wins on the next open.
   */
  public partial class NamesTable
  {
    /*
     * One grid row, four columns wide: who, what the classifier called it, which rule said so, and their class. Two
     * things it no longer carries.
     *
     *   - Damage/Healing: this is an identity list, and the numbers invited reading a name's importance off its output
     *     instead of off the damage board (the census still totals them internally — that is its row order).
     *   - Owner: PlayerRegistry answers it, which is exactly what the Pet Owners window lists. Two panes over one file.
     *
     * TYPE is the word the grid shows for `ClassificationReport.Row.Kind`; the enum keeps its name (381 places under
     * parsing/derive read IdentityKind), but "Kind" as a header made people look for a mob/npc KIND, so the column says
     * Type and the value says NPC rather than Npc.
     */
    internal sealed class NameRow
    {
      public string Name { get; init; } = string.Empty;
      public string Type { get; init; } = string.Empty;
      public string Why { get; init; } = string.Empty;
      public string? PlayerClass { get; init; }

      /// <summary>The WHY cell's tooltip: the cast a spell verdict rests on, plus whatever else qualifies this row.</summary>
      public string Provenance { get; init; } = string.Empty;
    }

    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    private readonly ObservableCollection<NameRow> _rows = [];
    private int _refreshInFlight;

    // Spent by the first show that had a session to census. After that only an explicit ask rebuilds the list — see the
    // class comment and FillOnFirstShow.
    private bool _filledOnce;

    public NamesTable()
    {
      InitializeComponent();
      namesGrid.ItemsSource = _rows;

      /*
       * ONCE, on the first time the pane is really visible. This used to refresh on EVERY visibility change, which for
       * a docked pane means every auto-hide slide, tab switch and restore — and a census rebuilt while you read it
       * looks like a table that keeps updating itself. The menu-open path and a new log both call Refresh() directly,
       * so nothing is left empty by this being one-shot.
       */
      IsVisibleChanged += static (s, e) =>
      {
        if (e.NewValue is not true || s is not NamesTable table) return;
        table.FillOnFirstShow();
      };
    }

    /// <summary>Rebuild the list from the captured facts. Safe to call from anywhere; harmless while one is running.</summary>
    public void Refresh()
    {
      var session = DeriveEngine.Active;
      if (session is null)
      {
        // A log that has not opened yet is not an error and gets no message: the pane says "Names" over an empty grid,
        // because a caption that changes with state is the status line this window was told not to have.
        return;
      }

      if (Interlocked.Exchange(ref _refreshInFlight, 1) == 1) return;

      _ = Task.Run(() =>
      {
        ClassificationReport? census = null;
        try { census = session.BuildNameCensus(); }
        catch (Exception ex) { Log.Error("Name census failed", ex); }
        finally { Interlocked.Exchange(ref _refreshInFlight, 0); }

        Dispatcher.BeginInvoke(() =>
        {
          /*
           * The answer may land after the capture moved on: this log closed and another opened (or just this one
           * closed) is exactly when a stale list is most likely to be applied LAST. The session that built it must
           * still be the active one - not merely "a" session.
           */
          if (!ReferenceEquals(session, DeriveEngine.Active)) return;
          Apply(census);
        });
      });
    }

    // Census row to grid row. Split out because it is the whole of this window's logic, and the parts worth pinning
    // (which sentences appear for which combination) are inside ProvenanceFor.
    internal static NameRow RowFrom(ClassificationReport.Row row) => new()
    {
      Name = row.Name,
      Type = TypeWord(row.Kind),
      Why = row.Reason,
      PlayerClass = row.Class,
      Provenance = ProvenanceFor(row),
    };

    /// <summary>The enum's own word would print "Npc"; a column called Type should not.</summary>
    internal static string TypeWord(IdentityKind kind) => kind switch
    {
      IdentityKind.Player => "Player",
      IdentityKind.Pet => "Pet",
      IdentityKind.Merc => "Merc",
      IdentityKind.Npc => "NPC",
      _ => "Unknown",
    };

    /*
     * The WHY cell's tooltip: everything that qualifies a verdict but has no column, one sentence per line.
     *
     * It used to be a Notes column, and it was deleted as a column on the grounds that most rows had nothing to say —
     * but two of its sentences have nowhere else to live, and losing them is worse than demoting them: "your verdict"
     * and "no claim (you took it back)" are the difference between a row you can ignore and one you wrote yourself,
     * and neither Type nor Why can say that. The spell behind an R4/R20 verdict rides here too (ReasonDetail), which is
     * exactly the kind of detail worth a hover and not worth a column.
     *
     * The wording is pinned by NamesTableTest because each sentence tells a person whether to ACT: one resting on
     * their own click needs no correction, one resting on an older log might, and "roster says player, rules say NPC"
     * is the row where somebody's meter is wrong right now.
     */
    internal static string ProvenanceFor(ClassificationReport.Row row)
    {
      var lines = new List<string>();
      if (row.ReasonDetail is not null) lines.Add($"cast that proved it: {row.ReasonDetail}");
      if (row.IsOperatorVerdict) lines.Add("your verdict");
      if (row.IsRejected) lines.Add("no claim (you took it back)");
      if (row.IsPrior) lines.Add($"older logs on this server, x{row.PriorSightings}");
      if (row.IsDisagreement) lines.Add("roster says player, rules say NPC");
      else if (row.LegacySaysPlayer && row.Kind != IdentityKind.Player) lines.Add("in players.txt");
      if (!row.HasFacts) lines.Add("not in this log");
      return string.Join("\n", lines);
    }

    private void Apply(ClassificationReport? census)
    {
      if (census is null) return;

      _rows.Clear();
      foreach (var row in census.Rows) _rows.Add(RowFrom(row));

      /*
       * The census summary on HOVER, not in a line of its own. It is the same one-breath answer (what the capture holds,
       * how much of it is unplaced, how many rows contradict the roster) and it keeps every counter on
       * ClassificationReport read by something; it just no longer occupies the title bar while somebody reads names.
       */
      namesCaption.ToolTip = $"{census.TotalNames:N0} names: {census.Players:N0} players, {census.Pets:N0} pets, " +
                             $"{census.Mercs:N0} mercs, {census.Npcs:N0} NPCs, {census.Rejected:N0} rejected" +
                             (census.UnresolvedInCapture > 0 ? $" · {census.UnresolvedInCapture:N0} unplaced in this log" : string.Empty) +
                             (census.Disagreements > 0 ? $" · {census.Disagreements:N0} contradict the roster" : string.Empty);
    }

    /*
     * The one automatic census. A show with no session yet does NOT spend the shot: at startup the pane can be laid out
     * before (or instead of, with AutoMonitor off) any log opening, and burning the flag there would leave a later tab
     * click — which is not one of the explicit doors — reading an empty grid forever. What it does not do is fill a
     * second time: a census taken mid-bulk lists what has been captured so far, and that is the accepted cost of a
     * pane that never changes underneath a reader — Refresh is right there.
     */
    private void FillOnFirstShow()
    {
      if (_filledOnce || DeriveEngine.Active is null) return;
      _filledOnce = true;
      Refresh();
    }

    private IEnumerable<string> SelectedNames()
    {
      foreach (var item in namesGrid.SelectedItems)
        if (item is NameRow row && !string.IsNullOrEmpty(row.Name)) yield return row.Name;
    }

    // Every verb writes through ClassificationCommands and then re-scans: the files are the truth, and the list has to
    // show what a save actually produced - including a name that moves kind because the rules now have an owner for it.
    private void SetVerdict(IdentityKind kind)
    {
      var names = SelectedNames().ToList();
      if (names.Count == 0) return;
      foreach (var name in names)
        ClassificationCommands.SetVerdict(IdentityOverrideStore.Instance, PlayerRegistry.Instance, name, kind);
      Refresh();
    }

    private void SetNpcClick(object sender, RoutedEventArgs e) => SetVerdict(IdentityKind.Npc);
    private void SetPetClick(object sender, RoutedEventArgs e) => SetVerdict(IdentityKind.Pet);
    private void SetMercClick(object sender, RoutedEventArgs e) => SetVerdict(IdentityKind.Merc);

    private void RejectClick(object sender, RoutedEventArgs e)
    {
      var names = SelectedNames().ToList();
      if (names.Count == 0) return;
      foreach (var name in names)
        ClassificationCommands.Reject(IdentityOverrideStore.Instance, PlayerRegistry.Instance, name);
      Refresh();
    }

    private void ClearPriorClick(object sender, RoutedEventArgs e)
    {
      foreach (var name in SelectedNames()) IdentityPriorStore.Instance.Remove(name);
      Refresh();
    }

    private void RefreshClick(object sender, RoutedEventArgs e) => Refresh();
  }
}
