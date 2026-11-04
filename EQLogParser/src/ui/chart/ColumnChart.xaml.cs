using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;

namespace EQLogParser;

public partial class ColumnChart : IDocumentContent
{
  private readonly List<BenchmarkBar> _columns = [];
  private readonly List<string> _selectedClasses = [];
  private readonly DeferredBusyState _busyState = new();
  private bool _ready;
  private int _publication;
  private int _sessionId;

  public ColumnChart()
  {
    InitializeComponent();
    titleLabel.Content = Labels.NoData;
    SharedControls.PopulateClassesList(classesList, _selectedClasses);
    Loaded += ContentLoaded;
    ShowColumns();
  }

  public void HideContent()
  {
    _ready = false;
    _publication++;
    _busyState.Cancel();
    ThemeConfig.EventsThemeChanged -= EventsThemeChanged;
    CombatEvents.ActiveDataCleared -= EventsClearedActiveData;
    DeriveEngine.ActiveChanged -= CaptureChanged;
    StatsManager.EventsGenerationStatus -= EventsGenerationStatus;
    chartView.Deactivate();
    ClearColumns();
  }

  protected virtual IStatsBuilder StatsManager => DamageStatsManagerAdapter.Instance;
  protected virtual string ChartTitle => "Players vs Top Performer (Percent of Total Damage)";

  private async void CreateImageClick(object sender, RoutedEventArgs e)
    => await UiElementUtil.CreateImage(Dispatcher, chartView, titleLabel);

  private void ClassPreviewMouseDown(object sender, EventArgs e)
    => SharedControls.ClassPreviewMouseDown(classesList, sender);

  private void ClassSelectionChanged(object sender, EventArgs e)
  {
    if (chartView != null && SharedControls.ClassesListSelectedChanged(classesList, _selectedClasses)) ShowColumns();
  }

  private void EventsThemeChanged(string _) => PublishClassBrushes();

  private void CaptureChanged() => Dispatcher.InvokeAsync(() =>
  {
    if (_ready && _sessionId != (DeriveEngine.Active?.SessionId ?? 0)) Reset();
  });

  private void EventsClearedActiveData(bool _)
  {
    var owner = DeriveEngine.Active?.SessionId ?? 0;
    Dispatcher.InvokeAsync(() =>
    {
      if (_ready && _sessionId == owner) Reset();
    });
  }

  private void Reset()
  {
    _sessionId = DeriveEngine.Active?.SessionId ?? 0;
    _publication++;
    _busyState.Cancel();
    titleLabel.Content = Labels.NoData;
    ClearColumns();
  }

  private void ClearColumns()
  {
    _columns.Clear();
    ShowColumns();
  }

  private void EventsGenerationStatus(StatsGenerationEvent status)
    => Dispatcher.InvokeAsync(() => ApplyGenerationStatus(status));

  private async void ApplyGenerationStatus(StatsGenerationEvent status)
  {
    try
    {
      if (!_ready || status.SessionId != (DeriveEngine.Active?.SessionId ?? 0)) return;
      _sessionId = status.SessionId;
      var publication = ++_publication;
      _busyState.Cancel();
      switch (status.State)
      {
        case "COMPLETED":
          if (status.CombinedStats == null)
          {
            titleLabel.Content = Labels.NoData;
            ClearColumns();
            return;
          }
          var players = status.CombinedStats.StatsList.ToArray();
          // Only plain data enters the worker; colors, resources and every control read stay on the dispatcher.
          var columns = await Task.Run(() => BenchmarkSeries.Prepare(players, IdentityLookup.IsOneOfUs));
          if (!_ready || publication != _publication || status.SessionId != (DeriveEngine.Active?.SessionId ?? 0)) return;
          _columns.Clear();
          _columns.AddRange(columns);
          ShowColumns();
          titleLabel.Content = ChartTitle;
          break;
        case "STARTED":
          _busyState.Arm(() =>
          {
            titleLabel.Content = "Loading...";
            ClearColumns();
          }, "chart");
          break;
        case "NONPC":
        case "NODATA":
          titleLabel.Content = Labels.NoData;
          ClearColumns();
          break;
      }
    }
    catch (Exception ex)
    {
      ThreadBoundary.Note("benchmark chart build", ex);
    }
  }

  private void ShowColumns()
  {
    PublishClassBrushes();
    var visible = _columns.Where(column => _selectedClasses.Contains(column.ClassName)).ToArray();
    chartView.SetData(BarChartData.Benchmark(visible), false);
  }

  private void PublishClassBrushes()
  {
    foreach (var name in _columns.Select(column => column.ClassName).Distinct(StringComparer.Ordinal))
      Application.Current.Resources[BarChartData.ClassColorKey(name)] = UiUtil.GetBrush(EQDataStore.Instance.GetClassColor(name));
  }

  private void CopyCsvClick(object sender, RoutedEventArgs e)
  {
    try
    {
      var header = new List<string> { "Class", "Percent", "Name" };
      var rows = new List<List<object>>();
      foreach (var column in _columns)
        rows.Add([column.ClassName, column.Percent, column.HasPets ? $"{column.Name} +Pets" : column.Name]);
      Clipboard.SetDataObject(titleLabel.Content is string title
        ? TextUtils.BuildTsv(header, rows, title) : TextUtils.BuildTsv(header, rows));
    }
    catch (ExternalException ex)
    {
      ThreadBoundary.Note("benchmark clipboard", ex);
    }
  }

  private void ContentLoaded(object sender, RoutedEventArgs e)
  {
    if (VisualParent == null || _ready) return;
    _ready = true;
    chartView.Activate();
    _sessionId = DeriveEngine.Active?.SessionId ?? 0;
    ThemeConfig.EventsThemeChanged += EventsThemeChanged;
    CombatEvents.ActiveDataCleared += EventsClearedActiveData;
    DeriveEngine.ActiveChanged += CaptureChanged;
    StatsManager.EventsGenerationStatus += EventsGenerationStatus;
    if (StatsManager.GetLastStats() is { } stats && stats.SessionId == _sessionId)
    {
      EventsGenerationStatus(stats);
    }
    else
    {
      Task.Run(() => ThreadBoundary.Guard("benchmark initial stats request", () =>
        StatsManager.RebuildTotalStats(new GenerateStatsOptions { Source = "chart opened, no prior build" })));
    }
  }
}
