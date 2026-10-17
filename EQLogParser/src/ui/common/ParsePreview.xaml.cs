using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for ParsePreview.xaml
  /// </summary>
  public partial class ParsePreview : IDisposable
  {
    private readonly ObservableCollection<string> _availableParses = [];
    // Display state is dispatcher-owned; only in-flight build tickets cross threads.
    private readonly Dictionary<string, ParseData> _parses = new();
    private readonly ConcurrentDictionary<string, Publication> _buildTickets = new();
    private long _publicationEpoch;
    private int _disposed;
    private sealed record Publication(long Epoch, int SessionId);
    private readonly bool _initialized;
    private readonly DispatcherTimer _titleTimer;

    public ParsePreview()
    {
      InitializeComponent();

      parseList.ItemsSource = _availableParses;
      parseList.SelectedIndex = -1;

      parseFormat.ItemsSource = new List<string>() { "Inline", "List" };
      var format = ConfigUtil.GetSetting("PlayerParseFormat");
      parseFormat.SelectedIndex = (string.IsNullOrEmpty(format) || format != "List") ? 0 : 1;

      playerParseTextDoPetLabel.IsChecked = ConfigUtil.IfSetOrElse("PlayerParseShowPetLabel", true);
      playerParseTextDoDPS.IsChecked = ConfigUtil.IfSetOrElse("PlayerParseShowDPS", true);
      playerParseTextDoTime.IsChecked = ConfigUtil.IfSetOrElse("PlayerParseShowTime", true);
      playerParseTextDoRaidTime.IsChecked = ConfigUtil.IfSetOrElse("PlayerParseShowRaidTime", true);
      playerParseTextDoTotals.IsChecked = ConfigUtil.IfSetOrElse("PlayerParseShowTotals", true);
      playerParseTextDoRank.IsChecked = ConfigUtil.IfSetOrElse("PlayerParseShowRank", true);
      playerParseTextDoSpecials.IsChecked = ConfigUtil.IfSetOrElse("PlayerParseShowSpecials", true);

      // The host's one pane lives for the app; short-lived hosts can detach through Dispose.
      DamageStatsBuilder.Instance.EventsGenerationStatus += EventsGenerationStatus;
      HealingStatsBuilder.Instance.EventsGenerationStatus += EventsGenerationStatus;
      TankingStatsBuilder.Instance.EventsGenerationStatus += EventsGenerationStatus;

      // Clear invalidates queued publications immediately, including those made while this pane is hidden.
      CombatEvents.ActiveDataCleared += ActiveDataCleared;

      _titleTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 1000) };
      _titleTimer.Tick += (_, _) =>
      {
        _titleTimer.Stop();

        if (parseList.SelectedIndex > -1 && parseList.SelectedItem is string type)
        {
          SetParseTextByType(type);
        }
      };

      UiElementUtil.SetWatermark(customParseTitle, Resource.CUSTOM_PARSE_TITLE);
      parseList.Focus();
      _initialized = true;
    }

    public void Dispose()
    {
      if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
      DamageStatsBuilder.Instance.EventsGenerationStatus -= EventsGenerationStatus;
      HealingStatsBuilder.Instance.EventsGenerationStatus -= EventsGenerationStatus;
      TankingStatsBuilder.Instance.EventsGenerationStatus -= EventsGenerationStatus;
      CombatEvents.ActiveDataCleared -= ActiveDataCleared;
      ClearParses();
      _buildTickets.Clear();
    }

    internal void CopyToEqClick(string type)
    {
      if (parseList.SelectedItem?.ToString() != type && _availableParses.Contains(type))
      {
        parseList.SelectedItem = type;
      }

      UiUtil.SetClipboardText(playerParseTextBox.Text);
    }

    internal void AddParse(string type, CombinedStats combined, List<PlayerStats> selected = null, bool copy = false)
    {
      var ticket = PublicationNow();
      var selection = selected is null ? new List<PlayerStats>() : new List<PlayerStats>(selected);
      DispatchPublication(ticket, () => PublishParse(type, combined, selection, copy));
    }

    internal void UpdateParse(PlayerStatsSelectionChangedEventArgs data, bool hasTopParse, string label, string topLabel)
    {
      var ticket = PublicationNow();
      var selection = data.Selected is null ? new List<PlayerStats>() : new List<PlayerStats>(data.Selected);
      var combined = data.CurrentStats;
      DispatchPublication(ticket, () =>
      {
        // Keep the selected parse last, so the refresh does not switch away from what was being read.
        if (parseList.SelectedItem?.ToString() == topLabel)
        {
          UpdateSelection(label, selection);
          if (hasTopParse) PublishParse(topLabel, combined, selection, false);
        }
        else
        {
          if (hasTopParse) PublishParse(topLabel, combined, selection, false);
          UpdateSelection(label, selection);
        }
      });
    }

    internal void UpdateParse(string type, List<PlayerStats> selected)
    {
      var ticket = PublicationNow();
      var selection = selected is null ? new List<PlayerStats>() : new List<PlayerStats>(selected);
      DispatchPublication(ticket, () => UpdateSelection(type, selection));
    }

    internal void ClearParses()
    {
      var epoch = Interlocked.Increment(ref _publicationEpoch);
      void Clear()
      {
        if (epoch != Interlocked.Read(ref _publicationEpoch)) return;
        _titleTimer?.Stop();
        _parses.Clear();
        _availableParses.Clear();
        parseList.SelectedIndex = -1;
        playerParseTextBox.Text = string.Empty;
      }

      if (Dispatcher.CheckAccess()) Clear();
      else _ = Dispatcher.InvokeAsync(Clear);
    }

    internal void EventsGenerationStatus(StatsGenerationEvent e)
    {
      if (Volatile.Read(ref _disposed) != 0) return;
      if (e.State == "STARTED")
      {
        // Each singleton builder serializes its start/answer pair. A clear must not restamp its old answer.
        _buildTickets[e.Type] = new Publication(Interlocked.Read(ref _publicationEpoch), e.SessionId);
      }
      else if (e.State is "COMPLETED" or "NONPC" or "NODATA"
        && _buildTickets.TryRemove(e.Type, out var ticket))
      {
        var type = e.Type;
        var combined = e.CombinedStats;
        DispatchPublication(ticket, () => PublishParse(type, combined, [], false));
      }
    }

    private void ActiveDataCleared(bool _) => ClearParses();

    private Publication PublicationNow()
      => new(Interlocked.Read(ref _publicationEpoch), DeriveEngine.Active?.SessionId ?? 0);

    private void DispatchPublication(Publication ticket, Action publish)
    {
      void Apply()
      {
        if (Volatile.Read(ref _disposed) == 0 && ticket == PublicationNow()) publish();
      }

      if (Dispatcher.CheckAccess()) Apply();
      else _ = Dispatcher.InvokeAsync(Apply);
    }

    private void PublishParse(string type, CombinedStats combined, List<PlayerStats> selected, bool copy)
    {
      var value = new ParseData { CombinedStats = combined };
      value.Selected.AddRange(selected);
      _parses[type] = value;
      if (!_availableParses.Contains(type)) _availableParses.Add(type);
      TriggerParseUpdate(type, copy);
    }

    private void UpdateSelection(string type, List<PlayerStats> selected)
    {
      if (!_parses.TryGetValue(type, out var value)) return;
      value.Selected.Clear();
      value.Selected.AddRange(selected);
      TriggerParseUpdate(type);
    }

    private void CopyToEqButtonClick(object sender = null, RoutedEventArgs e = null) => CopyToEqClick(parseList.SelectedItem?.ToString());

    internal string CustomTitle => customParseTitle.FontStyle == FontStyles.Italic ? null : customParseTitle.Text;

    private void SetParseTextByType(string type)
    {
      if (_parses.TryGetValue(type, out var value))
      {
        var combined = value.CombinedStats;
        var customTitle = customParseTitle.FontStyle == FontStyles.Italic ? null : customParseTitle.Text;
        var summary = StatsFormatter.Build(type, combined, value.Selected, GetSummaryOptions(), customTitle);

        if (summary != null)
        {
          playerParseTextBox.Text = summary.Title + summary.RankedPlayers;
        }

        playerParseTextBox.SelectAll();
      }
    }

    private SummaryOptions GetSummaryOptions()
    {
      return new SummaryOptions
      {
        ListView = parseFormat.SelectedIndex == 1,
        ShowPetLabel = playerParseTextDoPetLabel.IsChecked == true,
        ShowDps = playerParseTextDoDPS.IsChecked == true,
        ShowTime = playerParseTextDoTime.IsChecked == true,
        ShowRaidTime = playerParseTextDoRaidTime.IsChecked == true,
        ShowTotals = playerParseTextDoTotals.IsChecked == true,
        RankPlayers = playerParseTextDoRank.IsChecked == true,
        ShowSpecial = playerParseTextDoSpecials.IsChecked == true
      };
    }

    private void TriggerParseUpdate(string type, bool copy = false)
    {
      if (parseList.SelectedItem?.ToString() == type) SetParseTextByType(type);
      else parseList.SelectedItem = type;

      if (copy) CopyToEqButtonClick();
    }

    private void PlayerParseTextBoxTextChanged(object sender, TextChangedEventArgs e)
    {
      if (string.IsNullOrEmpty(playerParseTextBox.Text) || playerParseTextBox.Text == Resource.SHARE_DPS_SELECTED)
      {
        sharePlayerParseLabel.Text = Resource.SHARE_DPS_SELECTED;
        sharePlayerParseLabel.SetResourceReference(TextBlock.ForegroundProperty, "ContentForeground");
        sharePlayerParseWarningLabel.Text = string.Format(CultureInfo.CurrentCulture, "{0}/{1}", playerParseTextBox.Text.Length, 509);
        sharePlayerParseWarningLabel.Visibility = Visibility.Hidden;
      }
      else if (playerParseTextBox.Text.Length > 509)
      {
        sharePlayerParseLabel.Text = Resource.SHARE_DPS_TOO_BIG;
        sharePlayerParseLabel.SetResourceReference(TextBlock.ForegroundProperty, "EQWarnForegroundBrush");
        sharePlayerParseWarningLabel.Text = $"{playerParseTextBox.Text.Length}/509";
        sharePlayerParseWarningLabel.SetResourceReference(TextBlock.ForegroundProperty, "EQWarnForegroundBrush");
        sharePlayerParseWarningLabel.Visibility = Visibility.Visible;
      }
      else if (playerParseTextBox.Text.Length > 0 && playerParseTextBox.Text != Resource.SHARE_DPS_SELECTED)
      {
        if (parseList.SelectedItem is string selected && _parses.TryGetValue(selected, out var data))
        {
          var count = data.Selected?.Count > 0 ? data.Selected?.Count : 0;
          var players = count == 1 ? "Player" : "Players";
          sharePlayerParseLabel.Text = $"{count} {players} Selected";
        }

        sharePlayerParseLabel.SetResourceReference(TextBlock.ForegroundProperty, "ContentForeground");
        sharePlayerParseWarningLabel.Text = playerParseTextBox.Text.Length + " / " + 509;
        sharePlayerParseWarningLabel.SetResourceReference(TextBlock.ForegroundProperty, "ContentForeground");
        sharePlayerParseWarningLabel.Visibility = Visibility.Visible;
      }
    }

    private void PlayerParseTextCheckChange(object sender, RoutedEventArgs e)
    {
      if (parseList.SelectedIndex > -1 && parseList.SelectedItem is string type2)
      {
        SetParseTextByType(type2);
      }

      // don't call these until after init/load
      if (_initialized)
      {
        ConfigUtil.SetSetting("PlayerParseShowPetLabel", playerParseTextDoPetLabel.IsChecked == true);
        ConfigUtil.SetSetting("PlayerParseShowDPS", playerParseTextDoDPS.IsChecked == true);
        ConfigUtil.SetSetting("PlayerParseShowTime", playerParseTextDoTime.IsChecked == true);
        ConfigUtil.SetSetting("PlayerParseShowRaidTime", playerParseTextDoRaidTime.IsChecked == true);
        ConfigUtil.SetSetting("PlayerParseShowTotals", playerParseTextDoTotals.IsChecked == true);
        ConfigUtil.SetSetting("PlayerParseShowRank", playerParseTextDoRank.IsChecked == true);
        ConfigUtil.SetSetting("PlayerParseShowSpecials", playerParseTextDoSpecials.IsChecked == true);
      }
    }

    private void ParseListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (parseList.SelectedIndex > -1 && parseList.SelectedItem is string type3)
      {
        SetParseTextByType(type3);
      }
    }

    private void ParseFormatSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (parseList.SelectedIndex > -1 && parseList.SelectedItem is string type4)
      {
        SetParseTextByType(type4);
      }

      if (_initialized)
      {
        if (parseFormat.SelectedIndex > -1)
        {
          ConfigUtil.SetSetting("PlayerParseFormat", parseFormat.SelectedValue.ToString());
        }
      }
    }

    private void PlayerParseTextMouseEnter(object sender, MouseEventArgs e)
    {
      if (!playerParseTextBox.IsFocused)
      {
        playerParseTextBox.Focus();
      }
    }


    private void CustomTitleKeyDown(object sender, KeyEventArgs e)
    {
      if (e.Key == Key.Escape)
      {
        UiElementUtil.RestoreWatermark(customParseTitle, Resource.CUSTOM_PARSE_TITLE);
        parseList.Focus();
      }
    }

    private void CustomTitleTextChanged(object sender, TextChangedEventArgs e)
    {
      _titleTimer.Stop();
      _titleTimer.Start();
    }
  }
}
