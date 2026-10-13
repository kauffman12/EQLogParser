using FontAwesome5;
using log4net;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

/*
 * Annotations only, no null-flow analysis: this project builds with Nullable=disable, and the census API genuinely
 * speaks in optional strings (a name may have no class, no owner, no reason). The pragma states that without turning
 * on a warning wave across code written before nullable existed.
 */
#nullable enable annotations

namespace EQLogParser
{
  /*
   * The Identity Table (menu name; class still NamesTable): the listable census names, what they were called, and WHY.
   * It replaces all three of the panes this application used to keep identity state in - Verified Players, Verified Pets (which could
   * show what somebody typed but never what the classifier concluded, so a wrong verdict had no surface to be noticed on) and Pet
   * Owners, whose window outlived its own file: petmapping.txt froze into the ledger's ownership lane on 2026-10-09, a pair became a
   * COLUMN of this table, and what was left of that pane was a second list of pairs held in order by an event pump.
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
   * HOW A NAME IS CORRECTED: a pencil opens a ComboBox INSIDE its cell. Type offers Player / Pet / Mercenary / NPC / Spell;
   * Class offers the class words plus blank to forget the saved class; Owner offers the names a summon can belong to.
   * The Name column's take-back button calls ClassificationCommands.Recalculate. The editor holds census publication until
   * its pick commits or it closes. See docs/DesignNotes.md → "A cell editor lives in the cell, because a popup you open yourself eats the pick".
   * Batch verdicts remain in the fight grids' context menu; this table edits one name at a time.
   */
  public partial class NamesTable
  {
    /*
     * One grid row, four columns wide in one order: who, what the classifier called it, their class, and what kind of
     * evidence said so. Two of those cells carry a pencil (Type and Class) while nothing in the grid is typed into — a row
     * is read, and edited by picking from a list whose entries are verdicts rather than free text. A cell with no pencil
     * still reserves its width (PlaceholderVisibilityConverter), so the two columns do not go ragged where the exceptions are.
     *
     * What this grid does not carry: Damage/Healing — an identity list, and the numbers invited reading a name's importance off its
     * output instead of off the damage board (the census still totals them internally — that is its row order). OWNER sits here since
     * 2026-10-09: the separate Pet Owners window is deleted, and an identity list that could neither show nor correct a summon's owner
     * left no door at all.
     *
     * TYPE is the word the grid shows for `ClassificationReport.Row.Kind`; the enum keeps its name (381 places under
     * parsing/derive read IdentityKind), but "Kind" as a header made people look for a mob/npc KIND, so the column says
     * Type and the value says NPC rather than Npc.
     */
    internal sealed class NameRow : System.ComponentModel.INotifyPropertyChanged, IIdentityCensusRow
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
       * Who owns this summon, as the cell shows it — "No Owner" when nothing claims one (NameRow.Owner keeps that word rather than an
       * empty string, because an empty cell reads as a classifier bug while "nobody said whose" is a real answer). The row's KIND decides
       * whether the pencil is drawn; this string never does (PetOwnership.CanEditOwner).
       */
      public string Owner { get; init; } = Labels.Unassigned;

      /// <summary>Only a row that reads Pet gets the owner pencil: typing an owner onto a raider asserts two things from one cell,
      /// which is what the fight grids' "Assign … as Pet of" menu is for. Core decides, so the write guard cannot disagree.</summary>
      public bool OwnerEditable => PetOwnership.CanEditOwner(Kind);

      /*
       * Whether this row may be overruled at all — false where the name itself decides (a summon whose spelling carries its
       * owner, a spell effect), so the pencil is not drawn rather than offering three wrong answers beside the one right
       * one. ClassificationReport.Row passes it through; IdentityVocabulary.CanOverrule is the rule.
       */
      public bool Overrulable { get; init; }

      /*
       * The Type options for THIS row, in the order the in-cell editor shows them. Built in Core
       * (IdentityVocabulary.TypeOptionsFor) by the same recognizers that hide a pencil, so what is trimmed is what the NAME decides and not
       * an opinion about which kinds are typeable: an eye offers NPC and nothing else, a summon whose spelling carries its master offers Pet
       * alone, a row whose verdict IS a spell shape keeps only its own answer. Mercenary is offered on any row the pencil appears on (it is
       * one vocabulary with the "Set … as" cascade; see IdentityVocabulary.TypeOptionsFor for why the older per-row refusal went).
       * Includes what the row already is, because the editor preselects that value; never offers Unknown, which is a click for the capture to
       * answer again rather than a word a cell may hold.
       */
      public IReadOnlyList<IdentityVocabulary.TypeOption> TypeChoices { get; init; } = IdentityVocabulary.TypeOptions;

      /*
       * IN-CELL EDITING (2026-11). The pencil puts a ComboBox IN THE CELL instead of over it. The shape this replaces — an editor
       * inside our own Popup, opened programmatically — could not work: the ComboBox's list is a second window outside that popup's
       * bounds, so a press on an option was an "outside" press, which closed the popup and destroyed the list mid-gesture. The
       * operator's own test says it plainly: click the icon then click an option = nothing; click the icon, press again inside the
       * cell and drag down into the list = it commits. That is mouse capture, not styling, and the only fix is to have ONE popup —
       * the ComboBox's own — with the control hosted by the grid cell.
       *
       * One cell edits at a time (NamesTable.EndAllCellEdits before opening another). EditingColumn names which of the three, so each
       * column's template can bind its own visibility; EditorItems is that editor's list, built per click, and EditorCurrentIndex is
       * what to preselect. All of it is per row, so a recycled cell cannot show somebody else's editor.
       */
      private string? _editingColumn;

      public string? EditingColumn
      {
        get => _editingColumn;
        set
        {
          if (_editingColumn == value) return;
          _editingColumn = value;
          Raise(nameof(EditingColumn));
          Raise(nameof(IsTypeEditing));
          Raise(nameof(IsClassEditing));
          Raise(nameof(IsOwnerEditing));
        }
      }

      public bool IsTypeEditing => EditingColumn == NamesTable.ColumnType;
      public bool IsClassEditing => EditingColumn == NamesTable.ColumnClass;
      public bool IsOwnerEditing => EditingColumn == NamesTable.ColumnOwner;

      private IReadOnlyList<string> _editorItems = Array.Empty<string>();

      public IReadOnlyList<string> EditorItems
      {
        get => _editorItems;
        set { _editorItems = value; Raise(nameof(EditorItems)); }
      }

      public int EditorCurrentIndex { get; set; } = -1;

      private void Raise(string name)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

      /// <summary>The WHY cell's tooltip: the proof in one line, plus a short flag when one changes what to do.</summary>
      public string Provenance { get; init; } = string.Empty;

      /*
       * The take-back's label, per row (2026-11). The honest outcome of a take-back is sometimes an IDENTICAL cell — the capture
       * re-earns exactly what memory said — and an operator who cannot tell "tried" from "did nothing" calls the click dead. So
       * the name itself reads "Recalculating…" until the pass this click asked for has landed and been shown (the timing law is
       * RecalcOverlay: a floor, then the pass, capped). The grid binds to DisplayText, not Name: the sort key stays the name,
       * so the list does not reshuffle while the word sits over it.
       */
      private bool _recalculating;

      public bool Recalculating
      {
        get => _recalculating;
        set
        {
          if (_recalculating == value) return;
          _recalculating = value;
          Raise(nameof(Recalculating));
          Raise(nameof(DisplayText));
        }
      }

      public string DisplayText => Recalculating ? "Recalculating…" : Name;

      public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    /*
     * One census at a time; an ask that arrives during one is OWED rather than lost; and no census may paint an answer older than the last
     * operator write (CensusRepaint). This replaces a bare `Interlocked.Exchange(ref _refreshInFlight, 1) == 1 → return`, which is how "the
     * dropdown did nothing the first time" happened: the verdict went into identity-overrides.txt, the repaint that would have shown it met a
     * busy flag and evaporated, and on a finished log nothing ever asks again — so the store and the screen disagreed until a second click, or
     * the take-back icon, happened to find the road clear.
     */
    private CensusRepaint _repaint = new();

    // The engine this pane follows, and the floor between automatic rebuilds (see EventsDerived). An explicit Refresh()
    // — menu, first show, after a write — never waits.
    private DeriveEngine? _followed;
    private long _lastAutoRefreshMs;
    private const long AutoRefreshFloorMs = 2000;

    // One in-cell editor at a time; the live row is cleared when it closes.
    private NameRow? _typeEditRow;
    private NameRow? _classEditRow;
    private NameRow? _ownerEditRow;

    /*
     * While a cell editor (the Type or Class popup) is open, NO census lands: the pane follows the derive, a verdict written from
     * another pane forces a pass immediately, and merging rows tears out the very cell the popup is anchored to — WPF closes the
     * popup, its close hook clears the row being edited, and the click already on its way writes nothing (2026-10-09: "after
     * changing a player to NPC from the damage summary i wasnt able to change the type or reset claim in the identity window").
     * The newest deferred census paints when the gesture ends.
     */
    internal readonly EditorHold Editors = new();

    // Header state feeds the globally ordered index; the vendor's page sort uses the same IdentityRowOrder comparison.
    private string _sortColumn = IdentityRowOrder.DefaultColumn;
    private bool _sortDescending;

    // Applied once, then only on a theme change: re-sizing on every Loaded would fight an operator who dragged a column.
    private bool _widthsApplied;

    /*
     * The take-back's in-flight click, one at a time: which row is labelled, since when, and whether the pass it asked for has
     * landed. The label comes down on the first of (pass landed, hard cap) once the floor is paid — RecalcOverlay decides; a
     * bulk load parks both derive lanes, so the cap is what keeps a row that is being re-read from reading as frozen.
     */
    private string? _recalcName;
    private long _recalcStartTick;
    private bool _recalcPassLanded;
    private System.Windows.Threading.DispatcherTimer? _recalcHardCap;
    private System.Windows.Threading.DispatcherTimer? _recalcFloor;

    public NamesTable()
    {
      InitializeComponent();

      InitializePaging();
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
      _paneReady = true;
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
      else Refresh();   // you just opened it: a fresh census lays the whole list out in the header's order
    }

    private void UnfollowSession()
    {
      AbandonView();
      _repaint = new CensusRepaint();
      Editors.Abandon();
      EndAllCellEdits();

      if (_followed is null) return;
      _followed.Derived -= EventsDerived;
      _followed = null;
    }

    private void EventsActiveChanged()
    {
      if (!Dispatcher.CheckAccess())
      {
        Dispatcher.BeginInvoke(EventsActiveChanged);
        return;
      }

      UnfollowSession();
      ClearRows();
      if (IsVisible) FollowSession();
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

      // A pass landed: that is what a take-back click was waiting for. (The census below is throttled at 2 s; the LABEL is not —
      // the click owed its answer as soon as the pass ran, and Reconcile already ran an unthrottled census beside the write.)
      if (_recalcName is not null)
      {
        _recalcPassLanded = true;
        TryHideRecalc();
      }

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
      AbandonView();
      Editors.Abandon();
      EndAllCellEdits();
      ForgetRecalc();
      _repaint = new CensusRepaint();
      ResetPaging();
    }

    /// <summary>Ask for a census; overlapping asks coalesce into one follow-up while the pane is visible.</summary>
    public void Refresh()
    {
      if (!Dispatcher.CheckAccess())
      {
        Dispatcher.BeginInvoke(Refresh);
        return;
      }

      var session = DeriveEngine.Active;
      if (!IsVisible || session is null) return;
      var repaint = _repaint;
      var epoch = _viewEpoch;
      var requestGeneration = session.ExplicitRequestGeneration;
      if (!repaint.TryBegin(out var stamp)) return;

      _ = Task.Run(() =>
      {
        ClassificationReport? census = null;
        try
        {
          census = session.BuildNameCensus();
        }
        catch (Exception ex)
        {
          Log.Error("Name census failed", ex);
        }

        Dispatcher.BeginInvoke(() =>
        {
          // An abandoned run owns its old gate, never the new capture's debt or run flag.
          if (epoch != _viewEpoch || !ReferenceEquals(repaint, _repaint)
            || !ReferenceEquals(session, DeriveEngine.Active)) return;

          var outcome = repaint.Complete(stamp);
          var requestsMoved = requestGeneration != session.ExplicitRequestGeneration;
          if (census is not null && repaint.IsFresh(stamp) && !requestsMoved)
          {
            StoreCensus(census.Rows, stamp, requestGeneration);
            RequestViewRebuild();
          }

          if (outcome == CensusRepaint.Outcome.Rerun || requestsMoved) Refresh();
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
      // The cell word only — NOT the dropdown. The owner list is a live question ("whose summon can this be right now") and this
      // mapping runs for every name in the census, twice a second on a live raid, so it builds no lists; the pencil asks when clicked.
      Owner = PetOwnership.DisplayOf(row.PetOwner),
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
     * nowhere: a per-row badge would paint half the list, and the paging strip carries only row counts.
     * "Not in this log" is gone on the same grounds — it read as an error on a row whose Type already says Unknown. A hover
     * is never empty: an unplaced name answers "Nothing Identified It".
     */
    internal static string ProvenanceFor(ClassificationReport.Row row)
    {
      /*
       * The hover's HEAD line: one short clause, and no newline anywhere in this method. Anything else rides BELOW it, appended by
       * ProvenanceFor from Row.OtherEvidence (docs/DesignNotes.md → "The cell carries the judgement, the hover carries the reasons");
       * the head alone answers "why does it say That", and a four-line head makes the eye work for the one clause that matters.
       *
       * The proof clause is whatever the deciding rule can say, and NOTHING where no rule says anything - which is the
       * same state the take-back click takes a name back to: unclaimed, rather than
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
     * One place answers "how wide is this column?", and both doors read it: `ApplyColumnWidths` walks the live grid and
     * asks per column, while `DesiredPaneWidth` — which ThemeConfig calls to size the right-hand identity strip before
     * the pane exists, so it cannot ask the grid — sums the same answers over the declared columns. The two numbers were
     * written next to each other with their own copies of the arithmetic and had drifted (one said ~771 px fixed, the
     * other ~385 px at 12 pt), which is how a column change could quietly desize the strip it feeds. Measured numbers are
     * what the law carries: ~771 px at 12 pt from the fixed buckets, ~385 px from the shared switch — the width has to
     * come from this file's own column arithmetic because a number written beside the dock markup drifts the day a column
     * changes and the last column ends up behind a horizontal scrollbar in a pane nobody can widen past the strip.
     */
    private static double ColumnWidth(string mappingName)
    {
      // The pencil ((and the take-back icon)) is EQIconStyle — a square of the current font size — plus the 8+8 margins every icon in these panes carries.
      var iconAllowance = ThemeConfig.CurrentFontSize + 16;
      return mappingName switch
      {
        "Name" => ThemeConfig.CurrentNameWidth + iconAllowance,               // the take-back icon pays for itself, like the pencils
        "Type" => TypeColumnWidth() + iconAllowance,
        "PlayerClass" => ThemeConfig.CurrentMediumWidth + ThemeConfig.CurrentShortestWidth,
        "Owner" => ThemeConfig.CurrentMediumWidth + ThemeConfig.CurrentShortestWidth + iconAllowance,
        "Why" => ThemeConfig.CurrentMediumWidth + ThemeConfig.CurrentShortestWidth,
        _ => 0.0,
      };
    }

    /// <summary>The grid's columns in the order the XAML declares them — what `DesiredPaneWidth` sums over.</summary>
    private static readonly string[] ColumnNames = { "Name", "Type", "PlayerClass", "Owner", "Why" };

    internal static double DesiredPaneWidth()
    {
      double columns = 0;
      foreach (var name in ColumnNames) columns += ColumnWidth(name);
      return columns + 18;   // 18: the vertical scrollbar that comes with a long list

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

      foreach (var column in namesGrid.Columns)
      {
        var width = ColumnWidth(column.MappingName);
        if (width > 0) column.Width = width;
      }
    }

    private void EventsThemeChanged(string _) => ApplyColumnWidths();

    /*
     * The Type dropdown: click the pencil in a row, the list opens over the cell, and picking an entry writes that one
     * name. A cell edit edits its cell — batching stays where it always was, in the fight grids' right-click menu, which
     * takes a multi-selection through the same ClassificationCommands; a pencil drawn on ONE row promises that row and
     * nothing else.
     */
    private void TypeEditMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
      if (sender is not ImageAwesome icon || icon.DataContext is not NameRow row) return;

      var choices = row.TypeChoices;
      var current = -1;
      for (var i = 0; i < choices.Count; i++) if (choices[i].Kind == row.Kind) current = i;
      Log.Debug($"identity cell: {DoorType} editor opened for {row.Name} with {choices.Count} options");
      BeginCellEdit(row, ColumnType, [.. choices.Select(o => o.Word)], current, FindCellEditor(icon));
    }

    private void ApplyTypePick(NameRow row, IdentityVocabulary.TypeOption option)
    {
      if (option is null)
      {
        // A popup that closes WITHOUT committing an item used to leave no trace: the press lands outside the list,
        // onClosed clears the selection, and this returns with nothing said - which is what "the pencil does nothing"
        // looks like from the outside. It is only interesting while a row is in hand, so a completed write (which
        // closes the popup first) stays quiet.
        Log.Debug($"identity cell: {DoorType} picked nothing");
        return;
      }

      /*
       * Three refusals before a write: no row (the click that opened the popup, or a popup already closed), the answer it
       * already gives (a no-op must not spend a derive pass or rewrite identity-overrides.txt), and a name whose own spelling
       * settles the kind. The last one is redundant with the list this popup was opened with — deliberately so: the guard
       * and the menu read the same recognizer, and if they ever drift the guard wins rather than writing what the pane
       * never offered.
       */
      if (option.Kind == row.Kind) { RefuseDebug(DoorType, $"{row.Name} already reads {option.Word}"); return; }
      if (!row.Overrulable) { Refuse(DoorType, $"{row.Name} offers no Type pencil (a summon's spelling or a spell name settles its kind)"); return; }
      if (!row.TypeChoices.Contains(option)) { Refuse(DoorType, $"{option.Word} is not in the list this popup was opened with for {row.Name}"); return; }

      /*
       * the take-back click takes back EVERYTHING this window remembers about the name, not only the line in
       * identity-overrides.txt: without the ledger entry going too, the row comes straight back on the next derive wearing
       * its remembered verdict, which is the opposite of what the click looks like it did. Setting a verdict drops the
       * prior as well — this capture's answer now outranks it, and IdentityPriorStore.Recall stops offering it, so the
       * "... in previous log" tooltip cannot survive the override that replaced it.
       *
       * What it does NOT do is erase the answer — the word is RECALCULATE, not "clear", for exactly that reason. This
       * capture's own lines are re-read by the next pass and they are untouched by forgetting: a name npcs.txt places, a ``X`s pet`` name,
       * a defender a pet-target spell hit, a name the raid healed twenty times over - all of those come back, same kind, same Why word.
       * Only memory-fed rows visibly move. An operator who wants to overrule what the capture says picks the KIND (Manual strength outranks
       * every rule, including ones the rules learn later); asking for no answer is not a thing this button can promise.
       */
      // One call, both halves: FORGET what this app believed (ledger row included) then ASSERT the operator's word.
      // The eviction is not a bonus — a verdict that only adds a claim leaves players.txt and petmapping.txt answering
      // underneath it, which is how a name kept behaving like a player after being told NPC.
      ClassificationCommands.ApplyVerdict(row.Name, option.Kind);

      // Names itself: this door writes silently in the stores (ApplyVerdict logs nothing on purpose), so a report of
      // "I picked Reset and nothing happened" has no other way to say whether the click arrived at all. The reset line says what it
      // removed AND that the capture answers again, because the honest outcome of that verb is sometimes "no visible change".
      Log.Info(option.Kind == IdentityKind.Unknown
        ? $"identity cell: reset {row.Name} - override, roster/legacy claims, older-log verdict and saved summon owner forgotten; this capture's own lines answer again"
        : $"identity cell: type of {row.Name} -> {IdentityVocabulary.TypeWord(option.Kind)}");

      Reconcile();
    }

    private void ClassEditMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
      if (sender is not ImageAwesome icon || icon.DataContext is not NameRow row || !row.ClassEditable) return;

      var classNames = MainActions.ClassList.OfType<string>().ToList();
      Log.Debug($"identity cell: {DoorClass} editor opened for {row.Name} with {classNames.Count} classes");
      BeginCellEdit(row, ColumnClass, classNames, classNames.IndexOf(row.PlayerClass ?? string.Empty), FindCellEditor(icon));
    }

    /*
     * This door PRINTS NOTHING when it works (2026-11: "can you remove that debug i listed as well? i want the log quiet again").
     *
     * The INFO line existed to answer one question — did the click reach the write, or did the write refuse? — and the fix to the cell made the
     * answer visible where it actually belongs: an accepted pick shows in the row, so a session no longer needs a log line to prove that
     * `class of Farzi -> Berserker` happened. What stays are the refusals, at DEBUG: they describe states that should not occur, they are off
     * unless settings.txt says Debug=True, and they are the only trace left when somebody reports a pick that did nothing.
     */
    private void ApplyClassPick(NameRow row, string className)
    {
      if (row.PlayerClass == className && !string.IsNullOrEmpty(className))
      {
        RefuseDebug(DoorClass, $"{row.Name} already reads {className}");
        return;
      }

      /*
       * The blank entry MainActions.ClassList leads with is not "picked nothing": it is this pane's way of taking a saved class BACK.
       * `SetDefaultPlayerClass` REMOVES the default for an empty word (it never stores one for readers to re-filter), and because this column
       * reads assigned-first the cell goes back to showing what THIS capture learned. Answering "picked nothing" is what made that entry look
       * dead, and answering twice was what made a real pick look dead too.
       */
      if (string.IsNullOrEmpty(className))
      {
        PlayerRegistry.Instance.SetDefaultPlayerClass(row.Name, string.Empty);
        Reconcile();
        return;
      }

      /*
       * The write ANSWERS, and the pane acts on it: `SetDefaultPlayerClass` validates the word itself (CombatRecordLookup.IsValidClassName), and
       * its result used to be thrown away while this method announced success — which is how a refusal and an acceptance looked identical in the
       * field report that started this (two `identity cell: class of Farzi -> …` INFO lines, nothing visible on screen; the write had landed and
       * the CELL was reading the other lane — PlayerRegistry.GetAssignedOrLastKnownPlayerClass). A refused word now spends no derive pass.
       */
      if (!PlayerRegistry.Instance.SetDefaultPlayerClass(row.Name, className))
      {
        Log.Debug($"identity cell: {DoorClass} refused for {row.Name}: '{className}' is not a class word this build accepts");
        return;
      }

      // Nothing printed: the row is the evidence (see this method's header).
      Reconcile();
    }

    /*
     * The Owner dropdown, same shape as the other two pencils. What differs is only what a pick MEANS: an owner is not a verdict about
     * this name but a pair (this summon belongs to that person), so it writes the ownership lane and nothing else — the row's Type stays
     * Pet, its class stays whatever it was. And the last entry takes the pair back without touching the kind, because "I do not know
     * whose this is" is not "this is not a pet" (PetOwnership.IsClear).
     */
    private void OwnerEditMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
      if (sender is not ImageAwesome icon || icon.DataContext is not NameRow row || !row.OwnerEditable) return;

      var owners = PetOwnership.Choices(row.Owner, IdentityLookup.OurPeopleNames());
      Log.Debug($"identity cell: {DoorOwner} editor opened for {row.Name} with {owners.Count} candidates");
      BeginCellEdit(row, ColumnOwner, [.. owners], owners.FindIndex(o => string.Equals(o, row.Owner, StringComparison.OrdinalIgnoreCase)), FindCellEditor(icon));
    }

    private void ApplyOwnerPick(NameRow row, string owner)
    {
      if (string.IsNullOrEmpty(owner))
      {
        Log.Debug($"identity cell: {DoorOwner} picked nothing");
        return;
      }

      if (!row.OwnerEditable) { Refuse(DoorOwner, $"{row.Name} no longer reads Pet, so it has no Owner pencil"); return; }

      // The click that opened the popup, and a pick of what the cell already says: both write nothing.
      if (string.Equals(owner, row.Owner, StringComparison.OrdinalIgnoreCase))
      {
        RefuseDebug(DoorOwner, $"{row.Name} already belongs to {owner}");
        return;
      }

      /*
       * Setting an owner goes through the SAME seam the summary panes' `Assign … as Pet of` uses — `PetAssignment.Assign` — rather than
       * poking `PlayerRegistry.AddPetToPlayer`: that call refuses to reassign a name the parser verified as a player (which is exactly what
       * an operator contradicts), leaves the KIND alone, and logs nothing. The seam forgets every earlier claim, asserts Pet, writes the
       * pair, records the owner as roster, refuses a pair already in effect by asking BOTH stores case-insensitively, and asks for the pass
       * that folds the summon's facts under its person (`+Pets`).
       *
       * The list no longer offers a take-back entry (2026-11: it is the take-back click now, and wider on purpose), but a value that names
       * nobody must still never be written AS an owner — a restored row or a stale pick feeding this handler is exactly the shape that guard
       * was bought for. Taking an owner back is a forget (live map AND ledger lane, Pet verdict standing); only a person is an assign.
       */
      if (PetOwnership.IsClear(owner))
      {
        PlayerRegistry.Instance.ForgetPetMapping(row.Name);
        Log.Info($"identity cell: owner of {row.Name} taken back (the Pet verdict stands)");
      }
      else PetAssignment.Assign(row.Name, owner);

      Reconcile();
    }

    /*
     * The three cell doors had no voice at all: their writes go into stores that log nothing, and a refusal was a bare `return`. So
     * "I picked it and nothing happened" could not be told apart from "the click never reached the handler" - not from the log, which is
     * the only instrument anyone has when a pane looks dead. A pick that lands is Info (one line per deliberate edit); a pick refused for
     * the ordinary reason (you chose what the cell already says) is Debug, because that gesture is normal and Info must stay readable.
     * The surprising refusals - no row, no pencil, an option the popup never offered - are Info: they should not happen at all.
     */
    private const string DoorType = "type pick";
    private const string DoorClass = "class pick";
    private const string DoorOwner = "owner pick";

    /* Every refusal prints why a click wrote nothing, because "the dropdown did nothing" was the field report and only a log line
     * can tell an intentional refusal from a dead handler. */
    private static void Refuse(string door, string why) => Log.Info($"identity cell: {door} ignored - {why}");
    private static void RefuseDebug(string door, string why) => Log.Debug($"identity cell: {door} ignored - {why}");

    // A pane hide or capture change closes editors before their cells can mean something else.
    /*
     * IN-CELL EDITORS. A pencil click puts a ComboBox in the clicked cell and opens its drop-down; the pick writes through the same
     * Apply* methods the old popup used, and the editor then disappears. Nothing else on screen moves.
     *
     * Why the editor lives in the cell rather than in a Popup of its own: with an outer Popup, the ComboBox's list is a SECOND window
     * outside that popup's bounds, so a press on an option counts as a press "outside" and dismisses the popup — destroying the list
     * mid-gesture, before anything commits. The operator's comparison is exact: click the icon then click an option writes nothing,
     * while pressing inside the cell and dragging down into the list does (mouse capture follows the press). One popup total is the
     * repair, and it is the ComboBox's own.
     */
    internal const string ColumnType = "Type";
    internal const string ColumnClass = "Class";
    internal const string ColumnOwner = "Owner";

    internal void BeginCellEdit(NameRow row, string column, IReadOnlyList<string> items, int currentIndex, ComboBox? editor = null)
    {
      EndAllCellEdits();          // one editor on screen at a time, and its census hold ends with it
      Editors.Open();

      switch (column)
      {
        case ColumnType: _typeEditRow = row; break;
        case ColumnClass: _classEditRow = row; break;
        default: _ownerEditRow = row; break;
      }

      row.EditorItems = items;
      row.EditorCurrentIndex = currentIndex;

      if (editor is not null)
      {
        // Track it and preselect NOW, while the value can still be announced as ours. The cell's own ComboBox may not be found at
        // click time (a row that has never been laid out), and then this simply waits for CellEditorVisibilityChanged.
        _editorBox = editor;
        editor.ItemsSource = items;
        Preselect(editor, currentIndex);
      }

      row.EditingColumn = column;   // the template's ComboBox becomes visible, and opens itself
    }

    /*
     * The preselect is the one SelectedIndex write this pane makes, and it has to be announced: SelectedIndex raises the same
     * SelectionChanged an operator's pick raises, and unbracketed that made opening an editor "pick" the value the cell already had.
     */
    private void Preselect(ComboBox box, int index)
    {
      if (index < 0 || index >= box.Items.Count && box.ItemsSource is null) return;

      _suppressSelection = true;
      try
      {
        box.SelectedIndex = index;
      }
      finally
      {
        _suppressSelection = false;
      }
    }

    /*
     * The drop-down opens on its own once the control is visible and laid out — same as any WPF data-edit idiom, and NOT the old
     * failure: there is no enclosing popup to dismiss here. Preselecting raises SelectionChanged like any other programmatic change;
     * that is harmless because every Apply* refuses a value the row already reads (Debug line, no write, no derive pass).
     */
    private void CellEditorVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
      if (sender is not ComboBox box) return;

      if (!box.IsVisible) return;   // silent: every realized cell in a long grid fires this on its way to invisible
      Log.Debug($"identity editor: shown for {Describe(box)}");

      OpenEditorDropDown(box, first: true);
    }

    /*
     * Open the list once the cell has laid the control out, and if the items have not arrived by then, ask one more time at a lower
     * priority rather than showing an empty drop-down (the app's ComboBox template draws a 95 px empty panel when HasItems is false,
     * which is what "a white box with nothing in it" is). Every Debug line prints the control's own answer — item count, selection,
     * size — because "the list is empty" and "the list is there but a click does not reach it" are different bugs and this pane has
     * already been fixed for the wrong one twice.
     */
    private void OpenEditorDropDown(ComboBox box, bool first)
    {
      box.Dispatcher.BeginInvoke(new System.Action(() =>
      {
        if (box.DataContext is not NameRow row || row.EditingColumn is null)
        {
          Log.Debug($"identity editor: open skipped (no editing row) on {Describe(box)}");
          return;
        }

        Log.Debug($"identity editor: open attempt first={first} {Describe(box)} itemsSource={(box.ItemsSource?.GetType().Name ?? "null")} " +
                  $"declared={row.EditorItems.Count} current={row.EditorCurrentIndex}");

        if (box.Items.Count == 0)
        {
          box.ItemsSource = null;
          box.ItemsSource = row.EditorItems;   // re-ask the binding rather than opening a panel with nothing in it
          if (first) OpenEditorDropDown(box, first: false);
          return;
        }

        _editorBox = box;
        Preselect(box, row.EditorCurrentIndex);
        box.Focus();
        box.IsDropDownOpen = true;
      }), System.Windows.Threading.DispatcherPriority.Input);
    }

    /*
     * The two things this file got wrong at once, both visible in the log: `box.SelectedIndex = EditorCurrentIndex` raises the SAME
     * SelectionChanged an operator's pick raises, so the handler closed the editor mid-open; and EndCellEdit collapsed a control whose
     * drop-down was still open, which leaves WPF's popup window standing on its own - the white, empty, click-deaf rectangle that only
     * died on an outside click (its owner had been collapsed out from under it, items cleared). So: the programmatic preselect is
     * announced-and-suppressed, and closing an editor closes its list first.
     */
    private ComboBox? _editorBox;
    private bool _suppressSelection;
    private bool _endingCellEdit;

    /* The cell's own editor, found by walking down from the cell the pencil sits in. Null is a normal answer for a row that has not
     * been laid out yet: the preselect then happens on IsVisibleChanged instead of at the click. */
    private static ComboBox? FindCellEditor(DependencyObject from)
    {
      if (UiElementUtil.FindGridCell(from) is not { } cell) return null;

      var found = new List<ComboBox>();
      CollectBoxes(cell, found);
      return found.Count == 1 ? found[0] : null;
    }

    private static void CollectBoxes(DependencyObject parent, List<ComboBox> into)
    {
      for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
      {
        var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
        if (child is ComboBox box) into.Add(box);
        else CollectBoxes(child, into);
      }
    }

    /* Names the control instance a Debug line came from, and what it holds — cells are realized and reused, so "the combo" is ambiguous. */
    private static string Describe(ComboBox box)
    {
      var name = box.DataContext is NameRow row ? row.Name : "no-row";
      return $"[{box.GetHashCode():X8} {name} items={box.Items.Count} hasItems={box.HasItems} " +
             $"sel={box.SelectedIndex} open={box.IsDropDownOpen} vis={box.IsVisible} {box.ActualWidth:0}x{box.ActualHeight:0}]";
    }


    /*
     * The editor ends when the list closes — picked, escaped, or clicked away from — and on a pane hide or capture change. One
     * Editors.Close() per open: EndCellEdit early-returns for a row that is not editing, so a stray path cannot park the census hold
     * (a parked count means the pane stops repainting, which is the bug EditorHold exists to prevent).
     */
    private void CellEditorDropDownClosed(object sender, System.EventArgs e)
    {
      if (sender is not ComboBox box || box.DataContext is not NameRow row) return;
      Log.Debug($"identity editor: drop-down closed {Describe(box)}");
      EndCellEdit(row);
    }

    private void CellEditorLostFocus(object sender, RoutedEventArgs e)
    {
      if (sender is not ComboBox box || box.DataContext is not NameRow row) return;

      // Deferred on purpose: focus passes through the drop-down on its way in, and an immediate end would snap the editor shut
      // before the operator ever sees a list.
      box.Dispatcher.BeginInvoke(new System.Action(() =>
      {
        if (!box.IsDropDownOpen && !box.IsKeyboardFocusWithin) EndCellEdit(row);
      }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void EndCellEdit(NameRow row)
    {
      if (row.EditingColumn is null || _endingCellEdit) return;
      _endingCellEdit = true;
      try
      {
        if (_editorBox is { } box)
        {
          // DropDownClosed is synchronous and re-enters this door. Close before hiding, but release the hold only once.
          box.IsDropDownOpen = false;
          _editorBox = null;
        }

        row.EditingColumn = null;
        row.EditorItems = Array.Empty<string>();
        row.EditorCurrentIndex = -1;
        _typeEditRow = null;
        _classEditRow = null;
        _ownerEditRow = null;
      }
      finally
      {
        _endingCellEdit = false;
      }
      Editors.Close();
    }

    private void EndAllCellEdits()
    {
      foreach (var row in _ordered)
        if (row.EditingColumn is not null) { EndCellEdit(row); return; }
    }

    private void TypeCellEditorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (_suppressSelection) return;   // this pane preselecting what the cell already says is not an operator pick
      if (sender is not ComboBox box || box.DataContext is not NameRow row || !row.IsTypeEditing) return;

      Log.Debug($"identity cell: type editor picked {box.SelectedItem ?? "<none>"} on {Describe(box)}");
      var index = box.SelectedIndex;
      if (index < 0 || index >= row.TypeChoices.Count) return;

      var option = row.TypeChoices[index];
      EndCellEdit(row);           // close BEFORE the write: a write reconciles and repaints rows, and an editor standing inside a
      ApplyTypePick(row, option); // cell that is about to be re-made would only be in the way
    }

    private void ClassCellEditorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (_suppressSelection) return;   // this pane preselecting what the cell already says is not an operator pick
      if (sender is not ComboBox box || box.DataContext is not NameRow row || !row.IsClassEditing) return;

      Log.Debug($"identity cell: class editor picked {box.SelectedItem ?? "<none>"} on {Describe(box)}");
      // An empty selected word takes the saved class back; no selected item writes nothing.
      if (box.SelectedItem is not string className) return;

      EndCellEdit(row);
      ApplyClassPick(row, className);
    }

    private void OwnerCellEditorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (_suppressSelection) return;   // this pane preselecting what the cell already says is not an operator pick
      if (sender is not ComboBox box || box.DataContext is not NameRow row || !row.IsOwnerEditing) return;

      Log.Debug($"identity cell: owner editor picked {box.SelectedItem ?? "<none>"} on {Describe(box)}");
      var owner = box.SelectedItem as string;
      if (string.IsNullOrEmpty(owner)) return;

      EndCellEdit(row);
      ApplyOwnerPick(row, owner);
    }

    private void Reconcile()
    {
      // Same debt IdentityVerdictMenu.Write records: the census below repaints from its own classification, but the fight list and
      // the boards need the pass — and that pass must not be declined as if it were traffic (UnaskedRefresh).
      UnaskedRefresh.OweGesture("identity edit");

      /*
       * Water mark FIRST, and deliberately before either ask: any census already building read the stores BEFORE this write, and after this
       * line nothing may paint its answer as the last word. Without it the pane converges on a race — an auto-refresh that started 200 ms
       * before the click finishes a moment later, and "the dropdown did nothing" is what that looks like from a chair.
       */
      _repaint.Invalidate();

      DeriveEngine.Active?.RederiveAsync();
      Refresh();
    }

    /*
     * the take-back click — the take-back's door since 2026-11 ("Reset" stopped being a dropdown entry because a list of
     * ANSWERS should not carry a verb whose honest outcome is often an identical cell; the click, beside the name it applies to,
     * is what an operator can actually see). It does what the old entry did at its widest: forgets EVERY stored belief about this
     * name — override, ledger row (verdict + roster bit + class + owner), verified-pet and every other registry claim, and the
     * stored FALLBACK class the old door never had. This capture's cast-learned class WINDOWS stay — kind is a static verdict but
     * class is live per-second evidence, and "what the current log thinks" includes the class this log learned — then lets only this
     * capture's own lines answer. ClassificationCommands.Recalculate is the whole of the forget; Reconcile owes the pass that re-runs
     * the rules and repaints everything off the result.
     */
    private void RecalcMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
      if (sender is not ImageAwesome icon || icon.DataContext is not NameRow row) return;

      var name = row.Name;

      // One take-back at a time: state is a single name, so a second click would leave the first row labelled forever —
      // nothing else can take it off once this field points somewhere else.
      if (_recalcName is not null) FindRow(_recalcName)?.Recalculating = false;

      _recalcName = name;
      _recalcStartTick = Environment.TickCount64;
      _recalcPassLanded = false;
      row.Recalculating = true;

      ClassificationCommands.Recalculate(name);
      Log.Info($"identity cell: {name} recalculated - stored beliefs forgotten, this capture answers again");
      Reconcile();

      // The cap is the other half of RecalcOverlay.ShouldHide: a pass parked behind a bulk load may not hold the row hostage.
      _recalcFloor?.Stop();
      _recalcFloor = null;
      _recalcHardCap?.Stop();
      _recalcHardCap = null;
      var remaining = Math.Max(0, RecalcOverlay.HardCapMs - (Environment.TickCount64 - _recalcStartTick));
      if (remaining == 0) TryHideRecalc();
      else _recalcHardCap = new System.Windows.Threading.DispatcherTimer(
                   TimeSpan.FromMilliseconds(remaining), System.Windows.Threading.DispatcherPriority.Background,
                   OnRecalcHardCapTick, Dispatcher);
    }

    private void OnRecalcHardCapTick(object? sender, EventArgs e)
    {
      _recalcHardCap?.Stop();
      TryHideRecalc();   // the cap alone cannot pay the floor; ShouldHide still checks it
    }

    /*
     * When the label comes down is RecalcOverlay's law: never before the floor, then at the first of pass-landed and cap.
     * A pass can land inside the floor — the pane's own census was just forced by this click — so declining is not terminal:
     * the remaining floor gets a timer, or the answer would sit under the label until the 5 s cap.
     */
    private void TryHideRecalc()
    {
      if (_recalcName is null) return;
      if (!RecalcOverlay.ShouldHide(_recalcStartTick, Environment.TickCount64, _recalcPassLanded))
      {
        // Armed once, and only while the floor is still unpaid — re-arming at its own tick would spin.
        if (_recalcFloor is null && Environment.TickCount64 - _recalcStartTick < RecalcOverlay.MinVisibleMs)
        {
          var waitMs = Math.Max(1, RecalcOverlay.MinVisibleMs - (Environment.TickCount64 - _recalcStartTick));
          _recalcFloor = new System.Windows.Threading.DispatcherTimer(
                           TimeSpan.FromMilliseconds(waitMs), System.Windows.Threading.DispatcherPriority.Background,
                           OnRecalcFloorTick, Dispatcher);
        }
        return;
      }

      var name = _recalcName;
      _recalcName = null;
      _recalcHardCap?.Stop();
      _recalcHardCap = null;
      _recalcFloor?.Stop();
      _recalcFloor = null;
      FindRow(name)?.Recalculating = false;
    }

    /// <summary>Drop an in-flight "Recalculating…": its name belongs to a capture that is no longer open.</summary>
    private void ForgetRecalc()
    {
      _recalcName = null;
      _recalcHardCap?.Stop();
      _recalcHardCap = null;
      _recalcFloor?.Stop();
      _recalcFloor = null;
    }

    private void OnRecalcFloorTick(object? sender, EventArgs e)
    {
      _recalcFloor?.Stop();
      _recalcFloor = null;
      TryHideRecalc();
    }

    /*
     * The three controls that used to sit right of the caption are gone, and their verbs with them rather than hidden:
     *
     *   "Refresh"       — the window follows the derive while it is visible (FollowSession), so nothing needs asking.
     *   "Not a player"  — DelVerdict plus a re-derive: the Type cell answers with a verdict and the take-back icon in the Name column takes
     *                     one back, both drawn on the row they apply to.
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
