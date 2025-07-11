using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for ParsePreview.xaml
  /// </summary>
  public partial class ParsePreview
  {
    private readonly ObservableCollection<string> _availableParses = [];
    private readonly ConcurrentDictionary<string, ParseData> _parses = new();
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
      playerParseTextDoRank.IsChecked = ConfigUtil.IfSetOrElse("PlayerParseShowRank", true);
      playerParseTextDoTotals.IsChecked = ConfigUtil.IfSetOrElse("PlayerParseShowTotals", true);
      playerParseTextDoSpecials.IsChecked = ConfigUtil.IfSetOrElse("PlayerParseShowSpecials", true);
      playerParseTextDoTime.IsChecked = ConfigUtil.IfSetOrElse("PlayerParseShowTime", true);

      // this window is either hidden or visible and doesn't need to implement dispose
      DamageStatsManager.Instance.EventsGenerationStatus += EventsGenerationStatus;
      HealingStatsManager.Instance.EventsGenerationStatus += EventsGenerationStatus;
      TankingStatsManager.Instance.EventsGenerationStatus += EventsGenerationStatus;

      _titleTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 1000) };
      _titleTimer.Tick += (_, _) =>
      {
        _titleTimer.Stop();

        if (parseList.SelectedIndex > -1)
        {
          SetParseTextByType(parseList.SelectedItem as string);
        }
      };

      customParseTitle.Text = Resource.CUSTOM_PARSE_TITLE;
      customParseTitle.FontStyle = FontStyles.Italic;
      parseList.Focus();
      _initialized = true;
    }

    internal void CopyToEqClick(string type)
    {
      if (parseList.SelectedItem?.ToString() != type && _availableParses.Contains(type))
      {
        parseList.SelectedItem = type;
      }

      Clipboard.SetDataObject(playerParseTextBox.Text);
    }

    internal void AddParse(string type, CombinedStats combined, List<PlayerStats> selected = null, bool copy = false)
    {
      _parses[type] = new ParseData { CombinedStats = combined };

      if (selected != null)
      {
        _parses[type].Selected.AddRange(selected);
      }

      if (!_availableParses.Contains(type))
      {
        Dispatcher.InvokeAsync(() => _availableParses.Add(type));
      }

      TriggerParseUpdate(type, copy);
    }

    internal void UpdateParse(PlayerStatsSelectionChangedEventArgs data, bool hasTopParse, string label, string topLabel)
    {
      // change the update order based on whats displayed
      if (parseList.SelectedItem?.ToString() == topLabel)
      {
        UpdateParse(label, data.Selected);
        if (hasTopParse)
        {
          AddParse(topLabel, data.CurrentStats, data.Selected);
        }
      }
      else
      {
        if (hasTopParse)
        {
          AddParse(topLabel, data.CurrentStats, data.Selected);
        }

        UpdateParse(label, data.Selected);
      }
    }

    internal void UpdateParse(string type, List<PlayerStats> selected)
    {
      if (_parses.TryGetValue(type, out var value))
      {
        value.Selected.Clear();
        if (selected != null)
        {
          _parses[type].Selected.AddRange(selected);
        }

        TriggerParseUpdate(type);
      }
    }

    private void CopyToEqButtonClick(object sender = null, RoutedEventArgs e = null) => CopyToEqClick(parseList.SelectedItem?.ToString());

    private void EventsGenerationStatus(StatsGenerationEvent e)
    {
      switch (e.State)
      {
        case "COMPLETED":
        case "NONPC":
        case "NODATA":
          AddParse(e.Type, e.CombinedStats);
          break;
      }
    }

    private void SetParseTextByType(string type)
    {
      if (_parses.TryGetValue(type, out var value))
      {
        var combined = value.CombinedStats;
        var customTitle = customParseTitle.FontStyle == FontStyles.Italic ? null : customParseTitle.Text;
        var opts = GetSummaryOptions();
        var summary = SelectedParseBuilder.Build(type, combined, value.Selected, GetSummaryOptions(), customTitle);

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
        ShowTotals = playerParseTextDoTotals.IsChecked == true,
        ShowSpecial = playerParseTextDoSpecials.IsChecked == true,
        ShowTime = playerParseTextDoTime.IsChecked == true,
        RankPlayers = playerParseTextDoRank.IsChecked == true
      };
    }

    private void TriggerParseUpdate(string type, bool copy = false)
    {
      Dispatcher.InvokeAsync(() =>
      {
        if (parseList.SelectedItem?.ToString() == type)
        {
          SetParseTextByType(type);
        }
        else
        {
          parseList.SelectedItem = type;
        }

        if (copy)
        {
          CopyToEqButtonClick();
        }
      });
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
      if (parseList.SelectedIndex > -1)
      {
        SetParseTextByType(parseList.SelectedItem as string);
      }

      // don't call these until after init/load
      if (_initialized)
      {
        ConfigUtil.SetSetting("PlayerParseShowPetLabel", playerParseTextDoPetLabel.IsChecked == true);
        ConfigUtil.SetSetting("PlayerParseShowDPS", playerParseTextDoDPS.IsChecked == true);
        ConfigUtil.SetSetting("PlayerParseShowRank", playerParseTextDoRank.IsChecked == true);
        ConfigUtil.SetSetting("PlayerParseShowTotals", playerParseTextDoTotals.IsChecked == true);
        ConfigUtil.SetSetting("PlayerParseShowSpecials", playerParseTextDoSpecials.IsChecked == true);
        ConfigUtil.SetSetting("PlayerParseShowTime", playerParseTextDoTime.IsChecked == true);
      }
    }

    private void ParseListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (parseList.SelectedIndex > -1)
      {
        SetParseTextByType(parseList.SelectedItem as string);
      }
    }

    private void ParseFormatSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (parseList.SelectedIndex > -1)
      {
        SetParseTextByType(parseList.SelectedItem as string);
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

    private void CustomTitleGotFocus(object sender, RoutedEventArgs e)
    {
      if (customParseTitle.Text == Resource.CUSTOM_PARSE_TITLE)
      {
        customParseTitle.Text = "";
        customParseTitle.FontStyle = FontStyles.Normal;
      }
    }

    private void CustomTitleLostFocus(object sender, RoutedEventArgs e)
    {
      if (customParseTitle.Text.Length == 0)
      {
        customParseTitle.Text = Resource.CUSTOM_PARSE_TITLE;
        customParseTitle.FontStyle = FontStyles.Italic;
      }
    }

    private void CustomTitleKeyDown(object sender, KeyEventArgs e)
    {
      if (e.Key == Key.Escape)
      {
        customParseTitle.Text = Resource.CUSTOM_PARSE_TITLE;
        customParseTitle.FontStyle = FontStyles.Italic;
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
