using FontAwesome5;
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
using System.Windows.Input;

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
   *
   * HOW A NAME IS CORRECTED: the pencil in its own cell. Type opens the whole verdict list (Player / Pet / Mercenary /
   * NPC / Clear claim) and Class opens the class list, both through UiElementUtil.OpenCellPopup - the click-the-icon-then-
   * pick popup MainWindow's Pet Owners edit and DamageSummary's Group cell already call, so this pane joins them instead
   * of hand-rolling a Popup (placement, sizing, focus-back and the close hook are in that helper). There is no context
   * menu on this grid at all: right-drag and right-click
   * are how a person grabs a block of rows out of a long table, and a menu drawn over that gesture hid the one verb
   * almost nobody knew existed ("clear my claim" lived only in the fight grids). The two title-bar icons keep taking the
   * selection, because an icon with no row of its own has nothing else to act on.
   */
  public partial class NamesTable
  {
    /*
     * One grid row, four columns wide: who, what the classifier called it, what kind of evidence said so, and their
     * class. Two of those cells carry a pencil (Type and Class) while nothing in the grid is typed into — a row is read,
     * and edited by picking from a list whose entries are verdicts rather than free text.
     *
     * Two things it no longer carries:
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

      /// <summary>The verdict itself, not its word: the Type dropdown preselects it and refuses to write what the row
      /// already says (a click that changes nothing must not spend a derive pass or rewrite the overrides file).</summary>
      public IdentityKind Kind { get; init; }

      /*
       * Whether this row gets a class pencil. Setting a class is not a display choice: PlayerRegistry.SetDefaultPlayerClass
       * writes the name into players.txt as a verified player, so the icon appears exactly where that claim is already
       * true. An NPC row gets no icon (typing a mob into the roster is the pollution this window exists to catch), and
       * neither does a Pet or a Merc — nothing persists mercenaries, and a pet's class belongs to nobody's roster.
       * A name the rules have not placed declares itself through Type first; the pencil follows.
       */
      public bool ClassEditable => Kind == IdentityKind.Player;

      /// <summary>The WHY cell's tooltip: the details behind the two words, one short line each.</summary>
      public string Provenance { get; init; } = string.Empty;
    }

    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    private readonly ObservableCollection<NameRow> _rows = [];
    private int _refreshInFlight;

    // Which row's dropdown is open. One at a time by construction (StaysOpen=False), cleared when the popup closes.
    private NameRow? _typeEditRow;
    private NameRow? _classEditRow;

    // Applied once, then only on a theme change: re-sizing on every Loaded would fight an operator who dragged a column.
    private bool _widthsApplied;

    // Spent by the first show that had a session to census. After that only an explicit ask rebuilds the list — see the
    // class comment and FillOnFirstShow.
    private bool _filledOnce;

    public NamesTable()
    {
      InitializeComponent();
      namesGrid.ItemsSource = _rows;
      typeEditComboBox.ItemsSource = IdentityVocabulary.TypeOptions;

      /*
       * ThemeConfig cannot answer a single size before MainWindow finishes its own init, which happens AFTER this markup
       * has been parsed — so the widths written in the XAML are only the first paint, and the real ones are applied on
       * the first layout pass (and from then on by the theme change handler, like every other grid here).
       */
      Loaded += static (s, e) =>
      {
        if (s is not NamesTable table || table._widthsApplied) return;
        table._widthsApplied = true;
        table.ApplyColumnWidths();
      };
      ThemeConfig.EventsThemeChanged += EventsThemeChanged;

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
      Type = IdentityVocabulary.TypeWord(row.Kind),
      Why = IdentityVocabulary.WhyWord(row.Reason),
      PlayerClass = row.Class,
      Kind = row.Kind,
      Provenance = ProvenanceFor(row),
    };

    /*
     * TYPE and WHY are words, not codes, and the mapping lives in Core (IdentityVocabulary) beside the rules that write
     * them — so its coverage is asserted by tests that run anywhere, including a pass over the rules fixture that fails
     * if any verdict reaches the screen still wearing its code. This pane assembles the cells around that table.
     */

    /*
     * The WHY cell's tooltip: the detail behind those two words, one SHORT line each — a name, a count, a state. It is
     * not prose any more for the same reason the column is not: hovering should answer in a blink ("Boastful Bellow
     * XLVII", "Healed by 20 raiders", "You chose NPC"), and a sentence per line made the whole pane feel like a report.
     *
     * What each line still has to be able to say, though, is unchanged and pinned by NamesTableTest: whether the row
     * rests on the operator's own click (needs no correction), on an older log (might), or on players.txt while the
     * rules say NPC (somebody's meter is wrong right now). Those are the three answers a person acts on, and the tooltip
     * is the only place any of them appear.
     */
    internal static string ProvenanceFor(ClassificationReport.Row row)
    {
      var lines = new List<string>();
      if (row.ReasonDetail is not null) lines.Add($"Cast: {row.ReasonDetail}");
      if (row.HealedByCasters > 0) lines.Add($"Healed by {row.HealedByCasters:N0} raiders");
      if (row.IsOperatorVerdict) lines.Add($"You chose {IdentityVocabulary.TypeWord(row.Kind)}");
      if (row.IsRejected) lines.Add("Claim taken back");
      if (row.IsPrior) lines.Add($"Earlier logs x{row.PriorSightings:N0}");
      if (row.IsDisagreement) lines.Add("players.txt says player");
      else if (row.LegacySaysPlayer && row.Kind != IdentityKind.Player) lines.Add("In players.txt");
      if (!row.HasFacts) lines.Add("Not in this log");
      return string.Join("\n", lines);
    }

    /*
     * Column widths from the same theme-scaled variables every other table sizes itself with — CurrentNameWidth for
     * names, and medium/shortest sums for the rest — so a font or theme change moves this grid with the others instead
     * of leaving four columns clipped at their startup pixel counts.
     *
     * This grid is deliberately NOT routed through DataGridUtil.RefreshTableColumns: its Type and Class cells each carry
     * an edit icon (a word plus a pencil, which no other table's content is), so those two need an allowance that table
     * has no category for — and adding "Type" or "PlayerClass" to its mapping list would resize every OTHER grid that
     * happens to map a column by those words. AllowResizingColumns stays on, so anybody who wants more room takes it;
     * these are the widths that fit the longest word each column can print.
     */
    private void ApplyColumnWidths()
    {
      if (namesGrid?.Columns is null) return;

      // The pencil is EQIconStyle — a square of the current font size — plus the 8+8 margins every icon in these panes carries.
      var iconAllowance = ThemeConfig.CurrentFontSize + 16;

      foreach (var column in namesGrid.Columns)
      {
        var width = column.MappingName switch
        {
          "Name" => ThemeConfig.CurrentNameWidth,
          "Type" => ThemeConfig.CurrentShortWidth + iconAllowance,
          "Why" => ThemeConfig.CurrentMediumWidth + ThemeConfig.CurrentShortestWidth,
          "PlayerClass" => ThemeConfig.CurrentMediumWidth + ThemeConfig.CurrentShortestWidth,
          _ => 0.0,
        };
        if (width > 0) column.Width = width;
      }
    }

    private void EventsThemeChanged(string _) => ApplyColumnWidths();

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

    /*
     * The Type dropdown: click the pencil in a row, the list opens over the cell, and picking an entry writes that one
     * name. A cell edit edits its cell — batching stays where it always was (the two icons in the title bar act on the
     * selection, and the fight grids' right-click menu still takes a multi-select), because a pencil drawn on ONE row
     * promises that row and nothing else.
     */
    private void TypeEditMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
      if (sender is not ImageAwesome icon || icon.DataContext is not NameRow row) return;
      if (UiElementUtil.FindGridCell(icon) is not { } cell) return;

      _typeEditRow = row;

      // Preselect what the row already says, so the list opens on the current verdict — and so the guard in
      // TypeSelectionChanged can tell "the click that opened this" from "a different answer", which is the difference
      // between refreshing a window and rewriting mirror-overrides.txt for nothing. An unplaced name selects nothing:
      // "Clear claim" is an action, not what the row currently is.
      typeEditComboBox.SelectedValue = row.Kind == IdentityKind.Unknown ? null : row.Kind;

      UiElementUtil.OpenCellPopup(typeEditPopup, typeEditComboBox, cell, () =>
      {
        _typeEditRow = null;
        typeEditComboBox.SelectedItem = null;
      });
    }

    private void TypeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (sender is not ComboBox combo || combo.SelectedItem is not IdentityVocabulary.TypeOption option) return;

      var row = _typeEditRow;
      if (row is null || option.Kind == row.Kind) return;

      if (option.Kind == IdentityKind.Unknown) ClassificationCommands.ClearVerdict(IdentityOverrideStore.Instance, row.Name);
      else ClassificationCommands.SetVerdict(IdentityOverrideStore.Instance, PlayerRegistry.Instance, row.Name, option.Kind);

      Reconcile();
      typeEditPopup.IsOpen = false;
    }

    private void ClassEditMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
      if (sender is not ImageAwesome icon || icon.DataContext is not NameRow row || !row.ClassEditable) return;
      if (UiElementUtil.FindGridCell(icon) is not { } cell) return;

      _classEditRow = row;
      classEditComboBox.SelectedItem = (object?)row.PlayerClass ?? "";   // ClassList's first entry is the blank

      UiElementUtil.OpenCellPopup(classEditPopup, classEditComboBox, cell, () =>
      {
        _classEditRow = null;
        classEditComboBox.SelectedItem = null;
      });
    }

    private void ClassSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (sender is not ComboBox combo || combo.SelectedValue is not string className) return;

      var row = _classEditRow;
      if (row is null || row.PlayerClass == className) return;

      // SetDefaultPlayerClass validates the word itself (CombatRecordLookup.IsValidClassName), so the blank entry that
      // MainActions.ClassList leads with — the old window's way of showing "no class" — writes nothing at all rather
      // than a roster row with an empty class.
      if (string.IsNullOrEmpty(className)) return;

      PlayerRegistry.Instance.SetDefaultPlayerClass(row.Name, className);
      Reconcile();
      classEditPopup.IsOpen = false;
    }

    /*
     * After any write: re-scan this window AND ask for a pass. The census builds its own timeline (so it is correct on
     * its own), but the fight list, the overlay and every board read the LAST derive's snapshot — which is why this pane
     * used to be the one place an override showed up: FightTable's "Set as …" menu calls RederiveAsync beside its write
     * and this window called nothing, so a verdict made here left the rest of the application classifying by the old
     * answer until the derive cadence happened to run (and on a log that stopped growing, never at all).
     */
    private void Reconcile()
    {
      DeriveEngine.Active?.RederiveAsync();
      Refresh();
    }

    // The two band icons. They take the SELECTION (a title-bar icon has no row of its own), and their tooltips say so.
    private void RejectClick(object sender, RoutedEventArgs e)
    {
      var names = SelectedNames().ToList();
      if (names.Count == 0) return;
      foreach (var name in names)
        ClassificationCommands.Reject(IdentityOverrideStore.Instance, PlayerRegistry.Instance, name);
      Reconcile();
    }

    private void ClearPriorClick(object sender, RoutedEventArgs e)
    {
      var names = SelectedNames().ToList();
      if (names.Count == 0) return;
      foreach (var name in names) IdentityPriorStore.Instance.Remove(name);
      Reconcile();
    }

    private void RefreshClick(object sender, RoutedEventArgs e) => Refresh();
  }
}
