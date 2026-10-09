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
   *     So the census is built OFF THE UI THREAD and swapped in whole. What it does now is keep itself current: it
   *     follows the derive while the pane is visible (at most twice a second) and rebuilds every time it becomes
   *     visible, so opening the tab never shows yesterday's list. It was one-shot behind a "Refresh" button before, which
   *     read as a broken pane the first time a name was missing; the subscription is taken on show and given back on
   *     hide, so a census nobody is looking at is never built.
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
   * almost nobody knew existed ("clear my claim" lived only in the fight grids). BATCH work — setting a whole block of
   * selected names at once — is the one thing a cell cannot express, and it lives in the fight grids' context menu; this
   * pane keeps no buttons of its own any more, only the two pencils.
   */
  public partial class NamesTable
  {
    /*
     * One grid row, four columns wide in one order: who, what the classifier called it, their class, and what kind of
     * evidence said so. Two of those cells carry a pencil (Type and Class) while nothing in the grid is typed into — a row
     * is read, and edited by picking from a list whose entries are verdicts rather than free text. A cell with no pencil
     * still reserves its width (PlaceholderVisibilityConverter), so the two columns do not go ragged where the exceptions are.
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

      /*
       * Whether this row may be overruled at all — false where the name itself decides (a summon whose spelling carries its
       * owner, a spell effect), so the pencil is not drawn rather than offering three wrong answers beside the one right
       * one. ClassificationReport.Row passes it through; IdentityVocabulary.CanOverrule is the rule.
       */
      public bool Overrulable { get; init; }

      /*
       * The Type dropdown for THIS row. The window has one ComboBox reused by every cell, so the list is set when a popup
       * opens — and it is built in Core (IdentityVocabulary.TypeOptionsFor) by the same recognizers that hide a pencil, so
       * no row offers Mercenary onto a raider or any kind at all onto an eye. Includes what the row already is, because
       * the popup preselects that value.
       */
      public IReadOnlyList<IdentityVocabulary.TypeOption> TypeChoices { get; init; } = IdentityVocabulary.TypeOptions;

      /// <summary>The WHY cell's tooltip: the proof in one line, plus a short flag when one changes what to do.</summary>
      public string Provenance { get; init; } = string.Empty;
    }

    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    private readonly ObservableCollection<NameRow> _rows = [];

    /// <summary>The list itself, so a test can see whether a census actually landed rather than being held behind an open editor.</summary>
    internal ObservableCollection<NameRow> RowsForTest => _rows;
    private int _refreshInFlight;

    // The engine this pane follows, and the floor between automatic rebuilds (see EventsDerived). An explicit Refresh()
    // — menu, first show, after a write — never waits.
    private DeriveEngine? _followed;
    private long _lastAutoRefreshMs;
    private const long AutoRefreshFloorMs = 2000;

    // Which row's dropdown is open. One at a time by construction (StaysOpen=False), cleared when the popup closes.
    private NameRow? _typeEditRow;
    private NameRow? _classEditRow;

    /*
     * While a cell editor (the Type or Class popup) is open, NO census lands: the pane follows the derive, a verdict written from
     * another pane forces a pass immediately, and merging rows tears out the very cell the popup is anchored to — WPF closes the
     * popup, its close hook clears the row being edited, and the click already on its way writes nothing (2026-10-09: "after
     * changing a player to NPC from the damage summary i wasnt able to change the type or reset claim in the identity window").
     * The newest deferred census paints when the gesture ends.
     */
    internal readonly EditorHold Editors = new();

    /*
     * Whether the next census re-ranks the list or merges into the order already on screen. Set when the pane becomes
     * visible and whenever the capture changes: ranking is information, and it belongs at the moment somebody navigates
     * to the tab — not twice a second while they are reading it (MergeRows).
     */
    private bool _rankOnApply = true;

    // Applied once, then only on a theme change: re-sizing on every Loaded would fight an operator who dragged a column.
    private bool _widthsApplied;

    public NamesTable()
    {
      InitializeComponent();
      namesGrid.ItemsSource = _rows;
      // The Type list is NOT set here: one ComboBox serves every cell, so it is filled per row when that cell's popup opens
      // (TypeEditMouseLeftButtonUp), from NameRow.TypeChoices.

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
       * Current while visible, silent while hidden. A docked pane's visibility flips on every auto-hide slide and tab
       * switch, which is why the first version of this window subscribed to nothing at all — but the other half of that
       * design was a "Refresh" button, and an operator who does not know the button exists concludes the feature is
       * broken. So: take the subscription on show, give it back on hide, rebuild once per show (before the reader's eye
       * reaches the grid) and then on the derive's own beat while the pane is up.
       *
       * ActiveChanged belongs in the same contract: a log closing leaves no engine to follow and nothing worth reading,
       * and opening another must not leave this window attached to the capture that just died.
       */
      IsVisibleChanged += static (s, e) =>
      {
        if (s is not NamesTable table) return;
        if (e.NewValue is true) table.FollowSession();
        else table.UnfollowSession();
      };
      DeriveEngine.ActiveChanged += EventsActiveChanged;
    }

    /*
     * Follow whatever capture is open: subscribe to its derive, take a census, and empty the grid if there is nothing to
     * census. Idempotent — re-attaching to the engine already followed does not double-subscribe.
     */
    private void FollowSession()
    {
      var session = DeriveEngine.Active;
      if (!ReferenceEquals(_followed, session))
      {
        UnfollowSession();
        if (session is not null)
        {
          session.Derived += EventsDerived;
          _followed = session;
        }
      }

      if (session is null) ClearRows();
      else
      {
        _rankOnApply = true;   // you just opened it: show the ranking, then hold it still (MergeRows)
        Refresh();
      }
    }

    private void UnfollowSession()
    {
      // Hidden (or the session moved): drop any repaint waiting behind an editor, and the hold itself, so a close that never came
      // cannot leave the pane one pass short when it is shown again.
      Editors.Abandon();

      if (_followed is null) return;
      _followed.Derived -= EventsDerived;
      _followed = null;
    }

    private void EventsActiveChanged()
    {
      if (IsVisible) FollowSession();
      else UnfollowSession();
    }

    /*
     * One derive pass finished. Throttled: a live raid hands out passes around twice a second and the census walks every
     * name in the capture plus the heal stream (R15's caster count), which is enough to be felt if it ran at full rate.
     * Two seconds behind while the pane is open is invisible next to a list that never moved.
     */
    private void EventsDerived(DerivedSnapshot snapshot)
    {
      // The census walks the capture this pane is following; a pass from one that is no longer open (Dispose does not
      // join a running pass) must not repaint it with the previous log's names — DerivedSnapshot.SessionId.
      if (!snapshot.FromLiveSession) return;
      ThrottledRefresh();
    }

    internal void ThrottledRefresh()
    {
      var now = Environment.TickCount64;
      if (now - _lastAutoRefreshMs < AutoRefreshFloorMs) return;
      _lastAutoRefreshMs = now;
      Refresh();
    }

    /*
     * Empty the grid: the capture this window was reading is gone. Last night's names sitting over a closed log are worse
     * than an empty table, and nothing here could explain them — this pane shows no status line.
     */
    private void ClearRows()
    {
      // The capture this pane was reading is gone, so a census queued behind an open editor describes a log nobody is looking at.
      Editors.Abandon();
      _rankOnApply = true;
      _rows.Clear();
    }

    /// <summary>Rebuild the list from the captured facts. Safe to call from anywhere; harmless while one is running.</summary>
    public void Refresh()
    {
      var session = DeriveEngine.Active;
      if (session is null)
      {
        // A log that has not opened yet is not an error and gets no message: the pane shows an empty grid, because a
        // heading that changes with state is the status line this window was told not to have.
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
      // Core decides the cell word (a Spell row carries its caster side: "NPC Spell" / "Player Spell"); the fallback keeps a row
      // built outside the census — every test fixture, and any future caller that assembles a Row by hand — answering with the
      // plain kind word rather than an empty cell.
      Type = row.TypeDisplay.Length > 0 ? row.TypeDisplay : IdentityVocabulary.TypeWord(row.Kind),
      Why = IdentityVocabulary.WhyWord(row.Reason, row.Kind),
      PlayerClass = row.Class,
      Kind = row.Kind,
      Overrulable = row.Overrulable,
      TypeChoices = IdentityVocabulary.TypeOptionsFor(row.Name, row.Kind, row.Reason),
      Provenance = ProvenanceFor(row),
    };

    /*
     * TYPE and WHY are words, not codes, and the mapping lives in Core (IdentityVocabulary) beside the rules that write
     * them — so its coverage is asserted by tests that run anywhere, including a pass over the rules fixture that fails
     * if any verdict reaches the screen still wearing its code. This pane assembles the cells around that table.
     */

    /*
     * The first line is the PROOF, in one clause: "Cast Spire of Arcanum", "Healed by 20 Raiders", "From /who", "Owner
     * in Name", "From Chat in previous log". Core builds it from the same source string that decided the cell
     * (IdentityVocabulary.ProofText), so a verdict remembered from an older capture still names its evidence instead of
     * printing "(earlier)" and leaving the detail out — which is the half that made the old tooltip useless: the cell said
     * one word, the hover said which rule number, and neither said what the rule had read.
     *
     * Nothing rides behind it any more. It used to append the roster's opinion ("players.txt says Player", or just "in
     * players.txt"), which read as a second verdict from a source the operator cannot see — and on a row this window had
     * already decided, naming a FILE answered "which file said so" rather than the one question a hover is for. What that
     * flag was pointing at is still a fact about the row (`ClassificationReport.Row.IsDisagreement`) but it is printed
     * nowhere: this pane has no header strip to carry a census-wide count, and a per-row badge would paint half the list.
     * "Not in this log" is gone on the same grounds — it read as an error on a row whose Type already says Unknown. A hover
     * is never empty: an unplaced name answers "Nothing Identified It".
     */
    internal static string ProvenanceFor(ClassificationReport.Row row)
    {
      /*
       * ONE line, always, and no newline anywhere in this method — the hover answers "why does it say That", and a four-line
       * form makes the eye work for the one clause that matters (docs/DesignNotes.md → "The Names window: four columns, dropdowns in two of them").
       *
       * The proof clause is whatever the deciding rule can say, and NOTHING where no rule says anything - which is the
       * same state the dropdown words "Clear claim": taking a verdict back leaves the name unclaimed rather than
       * inventing an answer for the hover to explain. One clause, no separators: every rider that ever joined this string
       * (the roster's opinion, "not in this log", the file names) explained something the reader had not asked about.
       */
      var proof = IdentityVocabulary.ProofText(row.Reason, row.Kind, row.HealedByCasters);

      // The sighting count rides on the proof clause too: "From Chat in previous log x7".
      if (row.IsPrior && row.PriorSightings > 1) proof = $"{proof} x{row.PriorSightings:N0}";

      /*
       * Then whatever ELSE applies, one phrase per line, computed in Core (`Row.OtherEvidence`): the other rules that claimed
       * this name and what the capture watched it do ("Damaged Players"). A row one rule claimed and whose facts point nowhere
       * has nothing here and hovers as the single sentence it always did — the extra lines only exist where there is more to say,
       * which is what the operator asked for after "why does it just say A Spell?".
       */
      return row.OtherEvidence.Length == 0 ? proof : proof + "\n" + row.OtherEvidence;
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
    /*
     * What this grid wants to be wide, asked by ThemeConfig to size the right-hand identity strip (MainWindow's
     * namesWindow/petMappingWindow pair). The two panes are tabbed into ONE panel, so they must ask for the SAME width or
     * the strip jumps when you switch tabs — and the width has to come from this file's own column arithmetic, because a
     * number written next to the dock markup drifts the day a column changes and the last column ends up behind a
     * horizontal scrollbar in a pane nobody can widen past the strip.
     */
    internal static double DesiredPaneWidth()
    {
      var fontSize = ThemeConfig.CurrentFontSize;
      var iconAllowance = fontSize + 16;                       // EQIconStyle square + the 8+8 margins
      var columns = ThemeConfig.CurrentNameWidth
                  + TypeColumnWidth() + iconAllowance                                               // Type + its pencil
                  + 2 * (ThemeConfig.CurrentMediumWidth + ThemeConfig.CurrentShortestWidth);         // Class and Why
      var rowHeader = Application.Current.Resources["EQTableRowHeaderWidth"] is double w ? w : 32.0;  // ShowRowHeader="True"
      return columns + rowHeader + 18;                        // 18: the vertical scrollbar that comes with a long list
    }

    /*
     * The Type column is as wide as the LONGEST word it can ever have to show, because the words are a closed list and a
     * clipped one reads as a broken cell: the row's Type appeared as "Player Spe…". It used to take the theme's Short
     * bucket (5 ems), which was chosen when the widest answer was "Mercenary" — and the direction words that Spell rows
     * carry ("Player Spell") are longer than any of those, so one bucket no longer covers the vocabulary.
     *
     * Measured from the list itself rather than hard-coded: a new Type word pays for its own column at the next theme
     * change, and `DesiredPaneWidth` reads the same call, so the identity strip never hides the Why column behind a
     * horizontal scrollbar because a word grew. The factor is the widest ordinary advance in these UI fonts (0.62 em),
     * floored at the Medium bucket so a small application font cannot make this column narrower than the other short ones.
     */
    internal static double TypeColumnWidth()
    {
      var longest = Math.Max(IdentityVocabulary.NpcSpellWord.Length, IdentityVocabulary.PlayerSpellWord.Length);
      foreach (var option in IdentityVocabulary.TypeOptions) longest = Math.Max(longest, option.Word.Length);
      return Math.Max(ThemeConfig.CurrentMediumWidth, longest * ThemeConfig.CurrentFontSize * 0.62);
    }

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
          "Type" => TypeColumnWidth() + iconAllowance,
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

      // An EMPTY census still lands: "this capture has no names" clears the list, and dropping it would keep last night's rows.
      AcceptCensus(census.Rows);
    }

    /// <summary>A census arriving from the derive: fold it in now, or hold it until the open cell editor closes. True when it painted.</summary>
    internal bool AcceptCensus(IReadOnlyList<ClassificationReport.Row> rows)
    {
      if (Editors.Defer(() => MergeInto(rows))) return false;

      MergeInto(rows);
      return true;
    }

    private void MergeInto(IReadOnlyList<ClassificationReport.Row> rows)
    {
      /*
       * The selection is a row OBJECT, and MergeRows replaces rows rather than mutating them, so it has to be handed back
       * by NAME: the instance it pointed at is gone even though the line on screen never moved. An open dropdown's row is
       * re-pointed for the same reason — its write goes out by name, so a stale reference costs only the popup's preselect,
       * and a Type cell that opens blank reads as a bug in the classifier rather than in the refresh.
       */
      var selectedName = (namesGrid.SelectedItem as NameRow)?.Name;

      MergeRows(_rows, rows, _rankOnApply);
      _rankOnApply = false;

      if (selectedName is not null) namesGrid.SelectedItem = FindRow(selectedName);
      if (_typeEditRow is not null) _typeEditRow = FindRow(_typeEditRow.Name);
      if (_classEditRow is not null) _classEditRow = FindRow(_classEditRow.Name);
    }

    /*
     * Put a census into the list without moving what the reader is looking at.
     *
     * This pane follows the derive, so a pass lands every couple of seconds while a log grows. The version this replaces
     * cleared the collection and re-added every row, which cost two things on each beat: the selection (so the title-bar
     * icons lost their row) and the ORDER — rows are ranked, so as verdicts and totals moved the line under the cursor slid
     * somewhere else. A list that rearranges itself twice a second cannot be read while it is live, which is the only time
     * anybody wants it live.
     *
     * Three rules, in this order:
     *
     *   - A name already on the list KEEPS ITS SLOT. Its cells are replaced at that index (the collection's indexer), so a
     *     row whose Type changed says the new thing without moving anything, itself included.
     *   - A name the census no longer has leaves the list. That is the capture saying this name is gone; a verdict from the
     *     first half of the night parked above an empty slot is exactly what this window was rewritten to stop.
     *   - Names new since the last pass join at the BOTTOM, in census order. Putting them at their rank would shift every
     *     row beneath them — the thing this method exists to prevent — so the ranking arrives on the next view instead
     *     (_rankOnApply), which is when it is worth something.
     *
     * `rebuild` is that next view: everything out in census order, which is what opening the tab shows. An empty list also
     * takes this path, so a pane opened on a fresh capture needs no special case.
     */
    internal static void MergeRows(ObservableCollection<NameRow> listed, IReadOnlyList<ClassificationReport.Row> census, bool rebuild)
    {
      if (rebuild || listed.Count == 0)
      {
        listed.Clear();
        foreach (var row in census) listed.Add(RowFrom(row));
        return;
      }

      var incoming = new Dictionary<string, ClassificationReport.Row>(census.Count, StringComparer.OrdinalIgnoreCase);
      foreach (var row in census) incoming[row.Name] = row;

      // Reverse because rows leave while we walk; names that survive are replaced where they stand.
      var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      for (var i = listed.Count - 1; i >= 0; i--)
      {
        if (!incoming.TryGetValue(listed[i].Name, out var fresh)) { listed.RemoveAt(i); continue; }
        kept.Add(listed[i].Name);
        listed[i] = RowFrom(fresh);
      }

      // `kept.Add` is true the first time a name is seen, so one pass both dedupes and appends in census order.
      foreach (var row in census)
        if (kept.Add(row.Name)) listed.Add(RowFrom(row));
    }

    /// <summary>The live row for a name, or null: used to hand the selection and an open popup back after a merge.</summary>
    private NameRow? FindRow(string name) => _rows.FirstOrDefault(r => name.Equals(r.Name, StringComparison.OrdinalIgnoreCase));

    /*
     * The Type dropdown: click the pencil in a row, the list opens over the cell, and picking an entry writes that one
     * name. A cell edit edits its cell — batching stays where it always was, in the fight grids' right-click menu, which
     * takes a multi-selection through the same ClassificationCommands; a pencil drawn on ONE row promises that row and
     * nothing else.
     */
    private void TypeEditMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
      if (sender is not ImageAwesome icon || icon.DataContext is not NameRow row) return;
      if (UiElementUtil.FindGridCell(icon) is not { } cell) return;

      Editors.Open();
      _typeEditRow = row;

      // This row's own list — the kinds its name can still be (IdentityVocabulary.TypeOptionsFor).
      typeEditComboBox.ItemsSource = row.TypeChoices;

      // Preselect what the row already says, so the list opens on the current verdict — and so the guard in
      // TypeSelectionChanged can tell "the click that opened this" from "a different answer", which is the difference
      // between refreshing a window and rewriting identity-overrides.txt for nothing. An unplaced name selects nothing:
      // "Clear claim" is an action, not what the row currently is.
      typeEditComboBox.SelectedValue = row.Kind == IdentityKind.Unknown ? null : row.Kind;

      UiElementUtil.OpenCellPopup(typeEditPopup, typeEditComboBox, cell, () =>
      {
        _typeEditRow = null;
        typeEditComboBox.SelectedItem = null;

        // The gesture is over — whether the click finished it or WPF closed the popup: let the census land, including the one it queued.
        Editors.Close();
      });
    }

    private void TypeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (sender is not ComboBox combo || combo.SelectedItem is not IdentityVocabulary.TypeOption option) return;

      /*
       * Three refusals before a write: no row (the click that opened the popup, or a popup already closed), the answer it
       * already gives (a no-op must not spend a derive pass or rewrite identity-overrides.txt), and a name whose own spelling
       * settles the kind. The last one is redundant with the list this popup was opened with — deliberately so: the guard
       * and the menu read the same recognizer, and if they ever drift the guard wins rather than writing what the pane
       * never offered.
       */
      var row = _typeEditRow;
      if (row is null || option.Kind == row.Kind || !row.Overrulable) return;
      if (!row.TypeChoices.Contains(option)) return;

      /*
       * "Clear claim" takes back EVERYTHING this window remembers about the name, not only the line in
       * identity-overrides.txt: without the ledger entry going too, the row comes straight back on the next derive wearing
       * its remembered verdict, which is the opposite of what the click looks like it did. Setting a verdict drops the
       * prior as well — this capture's answer now outranks it, and IdentityPriorStore.Recall stops offering it, so the
       * "... in previous log" tooltip cannot survive the override that replaced it.
       */
      // One call, both halves: FORGET what this app believed (ledger row included) then ASSERT the operator's word.
      // The eviction is not a bonus — a verdict that only adds a claim leaves players.txt and petmapping.txt answering
      // underneath it, which is how a name kept behaving like a player after being told NPC.
      ClassificationCommands.ApplyVerdict(row.Name, option.Kind);

      Reconcile();
      typeEditPopup.IsOpen = false;
    }

    private void ClassEditMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
      if (sender is not ImageAwesome icon || icon.DataContext is not NameRow row || !row.ClassEditable) return;
      if (UiElementUtil.FindGridCell(icon) is not { } cell) return;

      Editors.Open();
      _classEditRow = row;
      classEditComboBox.SelectedItem = (object?)row.PlayerClass ?? "";   // ClassList's first entry is the blank

      UiElementUtil.OpenCellPopup(classEditPopup, classEditComboBox, cell, () =>
      {
        _classEditRow = null;
        classEditComboBox.SelectedItem = null;
        Editors.Close();
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
      // Same debt IdentityVerdictMenu.Write records: the census below repaints from its own classification, but the fight list and
      // the boards need the pass — and that pass must not be declined as if it were traffic (UnaskedRefresh).
      UnaskedRefresh.OweGesture("identity edit");

      DeriveEngine.Active?.RederiveAsync();
      Refresh();
    }

    /*
     * The three controls that used to sit right of the caption are gone, and their verbs with them rather than hidden:
     *
     *   "Refresh"       — the window follows the derive while it is visible (FollowSession), so nothing needs asking.
     *   "Not a player"  — DelVerdict plus a re-derive, which the Type cell's "Clear claim" does on the row it is drawn on.
     *                     Nothing in this app writes a players.txt `!Name` veto and nothing reads one: that machinery
     *                     arrived seven hours before the only window that could set it lost its menu entry, three days
     *                     after the last release, so no shipped build ever had a door to it (see
     *                     PlayerRegistry.RemoveVerifiedPlayer). "Take this name off my roster" is still a thing Remove
     *                     does; "never work out who this is again" was never a thing anybody could ask for.
     *   "Forget older   — same reach as choosing a Type here: both drop the ledger entry beside the verdict (above), so a
     *    logs decisions"  remembered answer cannot outlive the click that overruled it.
     */
  }
}
