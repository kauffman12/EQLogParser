using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser;

public partial class HitFreqChart : UserControl, IDisposable
{
  private const string CritHittype = "Critical";
  private const string NonCritHittype = "Non-Critical";
  private Dictionary<string, List<HitFrequencyChoice>> _playerData = [];
  private readonly List<string> _minFreqs =
    ["Any Frequency", "Frequency > 1", "Frequency > 2", "Frequency > 3", "Frequency > 4", "Frequency > 5"];
  private List<HitFrequencyBar> _columns = [];
  private bool _paneReady;
  private bool _updatingSelectors;
  private bool _disposedValue;
  private int _sessionId;

  public HitFreqChart()
  {
    InitializeComponent();
    minFreqList.ItemsSource = _minFreqs;
    minFreqList.SelectedIndex = 0;
    _sessionId = DeriveEngine.Active?.SessionId ?? 0;
    DeriveEngine.ActiveChanged += CaptureChanged;
    CombatEvents.ActiveDataCleared += ActiveDataCleared;
    _paneReady = true;
  }

  public void Dispose()
  {
    Dispose(true);
    GC.SuppressFinalize(this);
  }

  internal void Update(PlayerStats playerStats, CombinedStats combined)
  {
    if (_disposedValue) return;
    _sessionId = DeriveEngine.Active?.SessionId ?? 0;
    _playerData = HitFrequencySeries.ForPlayers(playerStats, combined);
    PublishPlayers();
  }

  protected virtual void Dispose(bool disposing)
  {
    if (_disposedValue) return;
    _disposedValue = true;
    DeriveEngine.ActiveChanged -= CaptureChanged;
    CombatEvents.ActiveDataCleared -= ActiveDataCleared;
    chartView.Deactivate();
    _playerData.Clear();
    _columns.Clear();
    chartView.SetData(BarChartData.Frequency([]));
  }

  private void CaptureChanged() => Dispatcher.InvokeAsync(() =>
  {
    if (!_disposedValue && _sessionId != (DeriveEngine.Active?.SessionId ?? 0)) Clear();
  });

  private void ActiveDataCleared(bool _)
  {
    var owner = DeriveEngine.Active?.SessionId ?? 0;
    Dispatcher.InvokeAsync(() =>
    {
      if (!_disposedValue && _sessionId == owner) Clear();
    });
  }

  private void Clear()
  {
    _sessionId = DeriveEngine.Active?.SessionId ?? 0;
    _playerData = [];
    PublishPlayers();
  }

  private void PublishPlayers()
  {
    _updatingSelectors = true;
    try
    {
      playerList.ItemsSource = _playerData.Keys.ToList();
      playerList.SelectedIndex = playerList.Items.Count > 0 ? 0 : -1;
    }
    finally
    {
      _updatingSelectors = false;
    }
    UpdatePlayerChoices();
  }

  private async void CreateImageClick(object sender, RoutedEventArgs e)
    => await UiElementUtil.CreateImage(Dispatcher, chartView);

  private void ListSelectionChanged(object sender, SelectionChangedEventArgs e) => UserSelectionChanged();

  private void CopyCsvClick(object sender, RoutedEventArgs e)
  {
    var header = new List<string> { "Hit Value", "Frequency", "Difference" };
    var rows = new List<List<object>>();
    foreach (var column in _columns) rows.Add([column.Amount, column.Frequency, column.Difference]);
    UiUtil.SetClipboardText(TextUtils.BuildTsv(header, rows));
  }

  private void UserSelectionChanged()
  {
    if (!_paneReady || _updatingSelectors || _disposedValue) return;
    _columns = [];
    if (playerList.SelectedItem is string player && _playerData.TryGetValue(player, out var data)
      && hitTypeList.SelectedItem is HitFrequencyChoice choice && data.Contains(choice)
      && critTypeList.SelectedItem is string critType)
    {
      _columns = HitFrequencySeries.Filter(choice.Values(critType == CritHittype), Math.Max(0, minFreqList.SelectedIndex));
    }
    chartView.SetData(BarChartData.Frequency(_columns));
  }

  private void CritTypeListSelectionChanged(object sender, SelectionChangedEventArgs e)
  {
    if (!_paneReady || _updatingSelectors) return;
    _updatingSelectors = true;
    try
    {
      UpdateSelectedHitTypes(critTypeList.SelectedItem is string type && type == NonCritHittype);
    }
    finally
    {
      _updatingSelectors = false;
    }
    UserSelectionChanged();
  }

  private void PlayerListSelectionChanged(object sender, SelectionChangedEventArgs e)
  {
    if (!_paneReady || _updatingSelectors) return;
    UpdatePlayerChoices();
  }

  private void UpdatePlayerChoices()
  {
    _updatingSelectors = true;
    try
    {
      var critTypes = new List<string>();
      if (playerList.SelectedItem is string player && _playerData.TryGetValue(player, out var data))
      {
        if (data.Any(choice => choice.Critical.Count > 0)) critTypes.Add(CritHittype);
        if (data.Any(choice => choice.NonCritical.Count > 0)) critTypes.Add(NonCritHittype);
      }
      critTypeList.ItemsSource = critTypes;
      critTypeList.SelectedIndex = critTypes.Count > 0 ? critTypes.Count - 1 : -1;
      UpdateSelectedHitTypes(critTypeList.SelectedItem is string type && type == NonCritHittype);
    }
    finally
    {
      _updatingSelectors = false;
    }
    UserSelectionChanged();
  }

  private void UpdateSelectedHitTypes(bool useNonCrit)
  {
    var selected = hitTypeList.SelectedItem is HitFrequencyChoice current ? current : null;
    var choices = new List<HitFrequencyChoice>();
    if (playerList.SelectedItem is string player && _playerData.TryGetValue(player, out var data))
    {
      choices = data.Where(choice => choice.Values(!useNonCrit).Count > 0)
        .OrderBy(choice => choice.Label).ToList();
    }
    hitTypeList.ItemsSource = choices;
    hitTypeList.SelectedItem = (selected is not null ? choices.Find(choice => choice.Key == selected.Key) : null)
      ?? choices.FirstOrDefault();
  }
}
