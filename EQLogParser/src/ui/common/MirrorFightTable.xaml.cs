using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  // The derived-fight-list twin of FightTable: same grid, different data source (CombatMirror →
  // ClassificationRules → FightDeriver → Sectionizer). Read-only in this step; identity overrides
  // and cross-grid selection sync land on top of it later.
  public partial class MirrorFightTable
  {
    private readonly ObservableCollection<MirrorFightRow> _rows = [];

    private MirrorSession _session;
    private bool _currentShowBreaks;

    public MirrorFightTable()
    {
      InitializeComponent();

      mirrorGrid.ItemsSource = _rows;
      mirrorShowBreaks.IsChecked = _currentShowBreaks = ConfigUtil.IfSet("NpcShowInactivityBreaks", true);
      ApplyFilter();

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
          _rows.Clear();
          mirrorStatus.Text = "Mirror: no log";
        }
        else
        {
          mirrorStatus.Text = "Mirror: capturing...";
        }
      });
    }

    // Derivation completes on a background thread; rows swap on the dispatcher.
    private void OnDerived(MirrorSnapshot snapshot)
    {
      Dispatcher.InvokeAsync(() =>
      {
        _rows.Clear();
        foreach (var row in snapshot.Rows) _rows.Add(row);

        mirrorStatus.Text = $"Derived {snapshot.DerivedAt:HH:mm:ss} - {snapshot.FightCount} fights, " +
                            $"{snapshot.FactCount:N0} facts, {snapshot.ElapsedMs:F0} ms";
      });
    }

    private void Attach(MirrorSession session)
    {
      _session = session;
      if (session is not null) session.Derived += OnDerived;
      mirrorRederiveButton.IsEnabled = session is not null;
    }

    private void Detach()
    {
      if (_session is not null) _session.Derived -= OnDerived;
      _session = null;
    }

    private void MirrorGridItemsSourceChanged(object sender, Syncfusion.UI.Xaml.Grid.GridItemsSourceChangedEventArgs e) => ApplyFilter();

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
