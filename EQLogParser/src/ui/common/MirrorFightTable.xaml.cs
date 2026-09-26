using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using EQLogParser.Mirror;

namespace EQLogParser
{
  // The derived-fight-list twin of FightTable: same grid, different data source (CombatMirror →
  // ClassificationRules → FightDeriver → Sectionizer). Selection is the one interactive seam today:
  // it feeds the damage summary from derived facts (see DerivedSelectionChanged). Identity overrides
  // and cross-grid selection sync land on top of it later.
  public partial class MirrorFightTable
  {
    /*
     * Raised when the selection settles, with the derived fights behind the selected rows (empty when
     * nothing is selected — an empty selection means "show no data", same as the legacy list).
     *
     * One-way on purpose, and damage-only: the point of this step is to be able to select the same fight
     * in both lists and read two answers from two engines. Feeding every legacy viewer would make that
     * comparison impossible to reason about, since whichever list was clicked last would own the boards.
     */
    internal event Action<IReadOnlyList<DerivedFight>> DerivedSelectionChanged;

    // Selection settles on a short pause rather than per click, and much more cheaply than the 750 ms the
    // legacy table waits: nothing is recomputed here, the click only says which fights are wanted. Long
    // enough that dragging a range across a thousand rows fires once, short enough to feel immediate.
    private const int SelectionSettleMs = 350;

    private ObservableCollection<MirrorFightRow> _rows = [];
    private readonly DispatcherTimer _selectionTimer;

    // What was last announced, as fight ids. Two jobs: a stale snapshot's rows cannot be re-announced as
    // if they were new, and a grid that re-raises SelectionChanged with the same selection (or with none,
    // when an ItemsSource swap lands) must not clear stats nobody changed.
    private List<int> _announcedIds = [];

    private MirrorSession _session;
    private bool _currentShowBreaks;

    public MirrorFightTable()
    {
      InitializeComponent();

      mirrorGrid.ItemsSource = _rows;
      mirrorShowBreaks.IsChecked = _currentShowBreaks = ConfigUtil.IfSet("NpcShowInactivityBreaks", true);
      ApplyFilter();

      _selectionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SelectionSettleMs) };
      _selectionTimer.Tick += (_, _) =>
      {
        _selectionTimer.Stop();
        AnnounceSelection();
      };

      MirrorSession.ActiveChanged += OnActiveChanged;
      Attach(MirrorSession.Active);
    }

    private void OnActiveChanged()
    {
      Dispatcher.InvokeAsync(() =>
      {
        Detach();
        Attach(MirrorSession.Active);
        if (MirrorSession.Active is null)
        {
          _selectionTimer.Stop();
          _announcedIds = [];
          _rows.Clear();
          mirrorStatus.Text = "Mirror: no log";
        }
        else
        {
          mirrorStatus.Text = "Mirror: capturing...";
        }
      });
    }

    // Derivation completes on a background thread; rows swap on the dispatcher. Big logs derive
    // thousands of fights — a whole-collection ItemsSource swap re-lays-out once instead of
    // signalling every row insert, and ItemsSourceChanged reapplies the divider filter.
    private void OnDerived(MirrorSnapshot snapshot)
    {
      Dispatcher.InvokeAsync(() =>
      {
        // The rows the last announcement pointed at no longer exist: forget them rather than let the swap's
        // selection reset look like a change and clear a summary nobody touched. What is on that board now
        // came from the previous pass, and stays there until the next click.
        _selectionTimer.Stop();
        _announcedIds = [];

        _rows = new ObservableCollection<MirrorFightRow>(snapshot.Rows);
        mirrorGrid.ItemsSource = _rows;

        mirrorStatus.Text = $"Derived {snapshot.DerivedAt:HH:mm:ss} - {snapshot.FightCount} fights, " +
                            $"{snapshot.FactCount:N0} facts, {snapshot.ElapsedMs:F0} ms";
      });
    }

    private void OnDeriveFailed(string message)
    {
      Dispatcher.InvokeAsync(() => mirrorStatus.Text = $"Derive failed: {message}");
    }

    // Capture heartbeat (dispatcher thread already). Only fires while no snapshot covers the
    // newest facts, so it never stomps a fresh "Derived …" line.
    private void OnCapturing(long total)
    {
      mirrorStatus.Text = $"Mirror: capturing... {total:N0} captured";
    }

    private void Attach(MirrorSession session)
    {
      _session = session;
      if (session is not null)
      {
        session.Derived += OnDerived;
        session.DeriveFailed += OnDeriveFailed;
        session.Capturing += OnCapturing;
      }
      mirrorRederiveButton.IsEnabled = session is not null;
    }

    private void Detach()
    {
      if (_session is not null)
      {
        _session.Derived -= OnDerived;
        _session.DeriveFailed -= OnDeriveFailed;
        _session.Capturing -= OnCapturing;
      }
      _session = null;
    }

    private void MirrorGridItemsSourceChanged(object sender, Syncfusion.UI.Xaml.Grid.GridItemsSourceChangedEventArgs e) => ApplyFilter();

    private void MirrorSelectionChanged(object sender, GridSelectionChangedEventArgs e)
    {
      // Restart the pause on every click so a dragged range announces once, at the end.
      _selectionTimer.Stop();
      _selectionTimer.Start();
    }

    internal IReadOnlyList<DerivedFight> GetSelectedFights()
    {
      if (mirrorGrid?.SelectedItems is not { } items) return [];

      var selected = new List<DerivedFight>();
      foreach (var item in items)
      {
        // Divider rows are gaps, not fights — they carry no DerivedFight and select nothing.
        if (item is MirrorFightRow { IsDivider: false } row && row.Fight is { } fight) selected.Add(fight);
      }

      return selected;
    }

    private void AnnounceSelection()
    {
      var selected = GetSelectedFights();
      var ids = new List<int>(selected.Count);
      foreach (var fight in selected) ids.Add(fight.Id);

      if (SameIds(ids, _announcedIds)) return;

      _announcedIds = ids;
      DerivedSelectionChanged?.Invoke(selected);

      mirrorStatus.Text = selected.Count == 0
        ? "Selection cleared"
        : $"Damage summary from derived facts: {selected.Count} fight{(selected.Count == 1 ? "" : "s")}";
    }

    private static bool SameIds(List<int> a, List<int> b)
    {
      if (a.Count != b.Count) return false;
      for (var i = 0; i < a.Count; i++)
      {
        if (a[i] != b[i]) return false;
      }

      return true;
    }

    private void RederiveClick(object sender, RoutedEventArgs e) => _session?.RederiveAsync();

    private void ShowBreakChanged(object sender, RoutedEventArgs e)
    {
      if (mirrorShowBreaks.IsChecked.HasValue && mirrorShowBreaks.IsChecked != _currentShowBreaks)
      {
        _currentShowBreaks = mirrorShowBreaks.IsChecked == true;
        ConfigUtil.SetSetting("NpcShowInactivityBreaks", _currentShowBreaks);
        ApplyFilter();
      }
    }

    // Same shape as FightTable: one always-on predicate, toggled by flag, refreshed via the view.
    private void ApplyFilter()
    {
      if (mirrorGrid?.View == null) return;
      mirrorGrid.View.Filter = item => !(_currentShowBreaks is false && ((MirrorFightRow)item).IsDivider);
      mirrorGrid.View.RefreshFilter();
    }
  }
}
