using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace EQLogParser
{
  // A refresh reads one snapshot's complete pool context, even when only selected lives are counted.
  // docs/DesignNotes.md → "NPC health is estimated from killed lives, and concurrent names pool".
  public partial class NpcCombatStatsViewer : IDocumentContent
  {
    private const string AllZones = "All Zones";
    private const string AllNames = "All NPCs";

    private readonly ObservableCollection<string> _zones = [];
    private readonly ObservableCollection<string> _names = [];
    private readonly NpcStatsRefresh _refresh = new();
    private readonly SummaryBuildGate _buildGate = new(static work =>
      Task.Run(() => ThreadBoundary.Guard("npc stats worker", work)));
    public ObservableCollection<NpcCombatRow> NpcStatsData { get; set; }
    private string _currentZone;
    private string _currentNpc;
    private bool _ready;

    private long _refreshGeneration;

    public NpcCombatStatsViewer()
    {
      InitializeComponent();
      SyncFusionUtil.ConfigureFrozenCellAnimations(dataGrid);
      NpcStatsData = [];
      zoneList.ItemsSource = _zones;
      npcList.ItemsSource = _names;
      DataContext = this;

      // default these columns to descending
      var desc = new[] { "AvgHp", "Deaths", "AvgMelee", "MaxMelee" };
      dataGrid.SortColumnsChanging += (s, e) => DataGridUtil.SortColumnsChanging(s, e, desc);
      dataGrid.SortColumnsChanged += (s, e) => DataGridUtil.SortColumnsChanged(s, e, desc);
    }

    public void HideContent()
    {
      _ready = false;
      _refresh.Reset();
      MainActions.EventsFightSelectionSettled -= SelectionChange;
      MainActions.EventsLogLoadingComplete -= LogLoadingComplete;
      MainActions.EventsFightSnapshotChanged -= FightSnapshotChanged;
      ThemeConfig.EventsThemeChanged -= EventsThemeChanged;
      Clear();
    }

    private void CopyCsvClick(object sender, RoutedEventArgs e) => DataGridUtil.CopyCsvFromTable(dataGrid, titleLabel.Content.ToString());
    private async void CreateImageClick(object sender, RoutedEventArgs e) => await DataGridUtil.CreateImageAsync(dataGrid, titleLabel);
    private void LogLoadingComplete(string file, bool open)
    {
      if (!open)
      {
        _refresh.Reset();
        Clear();
        return;
      }

      LoadCurrent();
    }

    private void FightSnapshotChanged(DerivedSnapshot snapshot)
    {
      if (!_ready) return;
      if (snapshot is null)
      {
        _refresh.Reset();
        _refresh.WaitFor(0);
        Clear();
        return;
      }

      if (snapshot.FromLiveSession && _refresh.Pending) Load();
    }

    private void RefreshClick(object sender, RoutedEventArgs e)
    {
      _refreshGeneration++;
      LoadCurrent();
    }

    private void EventsThemeChanged(string _) => DataGridUtil.RefreshTableColumns(dataGrid);
    private void UpdateTitle() => titleLabel.Content = NpcStatsData.Count == 0
      ? "No NPC Statistics Found"
      : NpcStatsData.Count + " NPC Entries Found";

    private void SelectionChange()
    {
      // Only a table scoped to the selection has to answer for the selection changing.
      if (fightOption.SelectedIndex == 1)
      {
        Load();
      }
    }

    private void LoadCurrent()
    {
      // Opening may have missed loading-complete; opening, completion and Refresh all owe a covering projection.
      _refresh.WaitFor(DeriveEngine.Active?.CapturedTotal ?? 0);
      Load();
    }

    private void Load()
    {
      if (!_ready) return;

      // The rows and the fact table they point into travel together; see FightTable.GetStatRows for why.
      var (snapshot, rows) = MainActions.GetNpcStatRows(fightOption.SelectedIndex == 1);
      if (snapshot?.Facts is null || !snapshot.FromLiveSession)
      {
        _refresh.WaitFor(0);
        Clear();
        return;
      }
      if (!_refresh.Covers(snapshot.FactCount)) return;
      if (rows.Count == 0)
      {
        Clear();
        _refresh.Reset();
        return;
      }

      var selected = fightOption.SelectedIndex == 1 ? rows : null;

      // ZoneRecord intervals enter the worker as plain data, never as a DependencyObject read.
      var zones = new NpcZoneTimeline(RecordsStore.Instance.GetAllZoning().Select(z => (z.Item1, z.Item2.Zone)));

      var key = NpcStatsBuildKey.For(snapshot.SessionId, FightTable.SelectionStamp(snapshot),
        snapshot.Facts.FactCount, snapshot.Heals?.HealCount ?? 0, zones.Count, selected,
        _refresh.Generation, _refreshGeneration);
      if (!_refresh.Ask(key, snapshot.FactCount)) return;

      try
      {
        _buildGate.Request(key, () => Build(snapshot, zones, selected, key));
      }
      catch (Exception ex)
      {
        ThreadBoundary.Note("npc stats schedule", ex);
      }
    }

    private bool Build(DerivedSnapshot snapshot, NpcZoneTimeline zones, IReadOnlyList<DerivedFight> selected, long key)
    {
      try
      {
        if (!Dispatcher.Invoke(() => CanPublish(snapshot, key) && _refresh.Begin(key, snapshot.FactCount))) return false;
        var input = DeriveEngine.Active?.PrepareNpcStats(snapshot, zones, selected);
        if (input is null)
        {
          Dispatcher.Invoke(() => RetryAfterProjectionMoved(snapshot, key));
          return false;
        }

        var built = NpcCombatStats.Build(input);
        return Dispatcher.Invoke(() =>
        {
          if (!CanPublish(snapshot, key)) return false;
          Publish(built, zoneList.SelectedItem as string, npcList.SelectedItem as string);
          return _refresh.Complete(key, snapshot.FactCount);
        });
      }
      catch (Exception ex)
      {
        // Neither a failed nor an obsolete build pays the read debt; the next applied snapshot can retry.
        ThreadBoundary.Note("npc stats build", ex);
        return false;
      }
    }

    private void RetryAfterProjectionMoved(DerivedSnapshot snapshot, long key)
    {
      if (!CanPublish(snapshot, key)) return;
      _refresh.Invalidate();
      _refresh.WaitFor(0);
      // If the replacement has not reached FightTable yet, its applied-snapshot event will retry. Never spin on the old list.
      var (current, _) = MainActions.GetNpcStatRows(false);
      if (!ReferenceEquals(current, snapshot)) Load();
    }

    private bool CanPublish(DerivedSnapshot snapshot, long key)
      => _ready && snapshot.FromLiveSession && _refresh.CanPublish(key, snapshot.FactCount);

    private void Publish(List<NpcCombatStatsRow> stats, string selectedZone, string selectedName)
    {
      _zones.Clear();
      _zones.Add(AllZones);
      _names.Clear();
      _names.Add(AllNames);

      var list = new List<NpcCombatRow>(stats.Count);
      foreach (var stat in stats)
      {
        list.Add(new NpcCombatRow
        {
          Zone = stat.Zone,
          Npc = stat.Npc,
          Deaths = stat.Deaths,
          AvgHp = stat.AvgHp,
          AvgMelee = stat.AvgMelee,
          MaxMelee = stat.MaxMelee
        });
      }

      AddSorted(_zones, stats.Select(s => s.Zone));
      AddSorted(_names, stats.Select(s => s.Npc));

      // Keep the reader's filter across a Refresh when the value still exists; otherwise fall back to showing everything.
      zoneList.SelectedIndex = _zones.IndexOf(selectedZone) is var z and > 0 ? z : 0;
      npcList.SelectedIndex = _names.IndexOf(selectedName) is var n and > 0 ? n : 0;
      UiUtil.UpdateObservable(list, NpcStatsData);

      if (dataGrid.View != null)
      {
        dataGrid.View.Filter = item => item is not NpcCombatRow row
          || (_currentZone == null || _currentZone.Equals(row.Zone))
          && (_currentNpc == null || _currentNpc.Equals(row.Npc));
        dataGrid.View.Refresh();
      }

      UpdateTitle();
    }

    private static void AddSorted(ObservableCollection<string> target, IEnumerable<string> values)
    {
      foreach (var value in values.Distinct().OrderBy(k => k, StringComparer.Create(new CultureInfo("en-US"), true)))
      {
        target.Add(value);
      }
    }

    private void Refresh()
    {
      dataGrid?.View?.RefreshFilter();
      UpdateTitle();
    }

    private void OptionsChanged(object sender, EventArgs e)
    {
      if (dataGrid is { View: not null })
      {
        _currentZone = zoneList.SelectedIndex > 0 && zoneList.SelectedItem is string zone ? zone : null;
        _currentNpc = npcList.SelectedIndex > 0 && npcList.SelectedItem is string name ? name : null;

        if (ReferenceEquals(sender, fightOption))
        {
          Load();
        }
        else
        {
          Refresh();
        }
      }
    }

    private void ContentLoaded(object sender, RoutedEventArgs e)
    {
      if (VisualParent != null && !_ready)
      {
        _ready = true;
        MainActions.EventsFightSelectionSettled += SelectionChange;
        MainActions.EventsLogLoadingComplete += LogLoadingComplete;
        MainActions.EventsFightSnapshotChanged += FightSnapshotChanged;
        ThemeConfig.EventsThemeChanged += EventsThemeChanged;
        LoadCurrent();
      }
    }

    private void Clear()
    {
      _refresh.Invalidate();
      NpcStatsData.Clear();
      _zones.Clear();
      _names.Clear();
      UpdateTitle();
    }

    private void AutoGeneratingColumn(object sender, AutoGeneratingColumnArgs e)
    {
      var mapping = e.Column.MappingName;
      if (mapping is "Zone" or "Npc")
      {
        e.Column.Width = mapping == "Zone" ? ThemeConfig.CurrentSpellWidth : ThemeConfig.CurrentNpcWidth;
        if (mapping == "Npc")
        {
          e.Column.HeaderText = "NPC";
        }

        return;
      }

      var column = new GridNumericColumn
      {
        MappingName = mapping,
        NumberDecimalDigits = 0,
        NumberGroupSizes = [3],
        TextAlignment = TextAlignment.Right,
        HeaderText = mapping switch
        {
          "Deaths" => "Deaths",
          "AvgHp" => "Est HP",
          "AvgMelee" => "Avg Melee",
          "MaxMelee" => "Max Melee",
          _ => e.Column.HeaderText
        }
      };

      if (mapping == "AvgHp")
      {
        column.AllowNullValue = true;
        column.NullText = "";
        column.ShowHeaderToolTip = true;
        column.HeaderToolTipTemplate = (DataTemplate)Application.Current.Resources["NpcCombatStatsHpToolTip"];
      }

      column.Width = DataGridUtil.CalculateMinGridHeaderWidth(column.HeaderText);
      e.Column = column;
    }
  }

  // Public properties are the grid's columns; unavailable HP remains null through sorting and export.
  public class NpcCombatRow
  {
    public string Zone { get; set; }
    public string Npc { get; set; }
    public int Deaths { get; set; }
    public long? AvgHp { get; set; }
    public long AvgMelee { get; set; }
    public long MaxMelee { get; set; }
  }
}
