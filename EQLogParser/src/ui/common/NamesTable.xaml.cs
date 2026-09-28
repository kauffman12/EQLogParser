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

using EQLogParser.Mirror;

/*
 * Annotations only, no null-flow analysis: this project builds with Nullable=disable, and the census API genuinely
 * speaks in optional strings (a name may have no class, no owner, no reason). The pragma states that without turning
 * on a warning wave across code written before nullable existed.
 */
#nullable enable annotations

namespace EQLogParser
{
  /*
   * The Names window: every name in the capture, what it was called, and WHY. It replaces the three hand-maintained
   * panes (Verified Players / Verified Pets / Pet Owners), which could show what somebody typed but never what the
   * classifier concluded - so a wrong verdict had no surface to be noticed on, only a meter that looked odd.
   *
   * Two things this deliberately is not:
   *
   *   - Not a live grid. A derive runs every few seconds while a log loads and a census nobody has open would be
   *     thrown away each time, so the list is built on demand (open, Re-scan, or a derive that lands while it is
   *     visible) off the UI thread, and applied in one swap.
   *   - Not a second source of truth. Every verb here writes through ClassificationCommands into the same per-server
   *     files the old panes used (verdicts, roster, rejections) plus the sighting ledger. Nothing is stored here, so
   *     closing the window loses nothing and a file edited by hand still wins on the next open.
   */
  public partial class NamesTable
  {
    /// <summary>One grid row. Flattened from ClassificationReport.Row because the grid compares strings, not kinds.</summary>
    internal sealed class NameRow
    {
      public string Name { get; init; } = string.Empty;
      public string Kind { get; init; } = string.Empty;
      public string Why { get; init; } = string.Empty;
      public string? PlayerClass { get; init; }
      public string? PetOwner { get; init; }
      public string Flags { get; init; } = string.Empty;
      public double Damage { get; init; }
      public double Healing { get; init; }
    }

    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    private readonly ObservableCollection<NameRow> _rows = [];
    private MirrorSession? _session;
    private int _refreshInFlight;

    public NamesTable()
    {
      InitializeComponent();
      namesGrid.ItemsSource = _rows;

      // Opening the window is the moment someone wants an answer, and the census is only as old as the last derive.
      IsVisibleChanged += static (s, e) => { if (e.NewValue is true && s is NamesTable table) table.Refresh(); };
    }

    /// <summary>Rebuild the list from the captured facts. Safe to call from anywhere; harmless while one is running.</summary>
    public void Refresh()
    {
      var session = MirrorSession.Active;
      if (session is null)
      {
        // A log that has not opened yet is not an error: the window simply has nothing to classify.
        namesStatusText.Text = "Names: open a log to classify the names in it";
        return;
      }

      _session = session;
      if (Interlocked.Exchange(ref _refreshInFlight, 1) == 1) return;

      _ = Task.Run(() =>
      {
        ClassificationReport? census = null;
        try { census = session.BuildNameCensus(); }
        catch (Exception ex) { Log.Error("Name census failed", ex); }
        finally { Interlocked.Exchange(ref _refreshInFlight, 0); }

        Dispatcher.BeginInvoke(() => Apply(census));
      });
    }

    private void Apply(ClassificationReport? census)
    {
      if (census is null) return;

      _rows.Clear();
      foreach (var row in census.Rows) _rows.Add(RowFrom(row));

      namesStatusText.Text = $"Names {census.TotalNames:N0}: {census.Players:N0} players, {census.Pets:N0} pets, " +
                             $"{census.Mercs:N0} mercs, {census.Npcs:N0} NPCs, {census.Rejected:N0} rejected" +
                             (census.UnresolvedInCapture > 0 ? $" · {census.UnresolvedInCapture:N0} unplaced in this log" : string.Empty) +
                             (census.Disagreements > 0 ? $" · {census.Disagreements:N0} contradict the roster" : string.Empty);
    }

    // Census row to grid row. Split out because it is the whole of this window's logic, and the parts worth pinning
    // (which flags appear for which combination) are inside FlagsFor.
    internal static NameRow RowFrom(ClassificationReport.Row row) => new()
    {
      Name = row.Name,
      Kind = row.Kind.ToString(),
      Why = row.Reason,
      PlayerClass = row.Class,
      PetOwner = row.PetOwner,
      Flags = FlagsFor(row),
      Damage = row.Damage,
      Healing = row.Healing,
    };

    /*
     * What a verdict rests on that the Kind/Why columns cannot say by themselves. Kept as one short sentence per row
     * rather than six checkboxes: an operator scanning for trouble reads this column top to bottom, and "who said so"
     * is the thing they need to decide whether to act.
     */
    internal static string FlagsFor(ClassificationReport.Row row)
    {
      var flags = new List<string>();
      if (row.IsOperatorVerdict) flags.Add("your verdict");
      if (row.IsRejected) flags.Add("no claim (you took it back)");
      if (row.IsPrior) flags.Add($"older logs on this server, x{row.PriorSightings}");
      if (row.IsDisagreement) flags.Add("roster says player, rules say NPC");
      else if (row.LegacySaysPlayer && row.Kind != IdentityKind.Player) flags.Add("in players.txt");
      if (!row.HasFacts) flags.Add("not in this log");
      return string.Join("; ", flags);
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
        ClassificationCommands.SetVerdict(MirrorOverrideStore.Instance, PlayerRegistry.Instance, name, kind);
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
        ClassificationCommands.Reject(MirrorOverrideStore.Instance, PlayerRegistry.Instance, name);
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
