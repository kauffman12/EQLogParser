﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for ParsePreview.xaml
  /// </summary>
  public partial class ParsePreview : UserControl
  {
    private static readonly SolidColorBrush BRIGHT_BRUSH = Application.Current.Resources["hoverForegroundBrush"] as SolidColorBrush;
    private static readonly SolidColorBrush WARNING_BRUSH = Application.Current.Resources["warnBackgroundBrush"] as SolidColorBrush;
    private readonly ObservableCollection<string> AvailableParses = new ObservableCollection<string>();
    private readonly ConcurrentDictionary<string, ParseData> Parses = new ConcurrentDictionary<string, ParseData>();
    private readonly bool initialized = false;
    private readonly DispatcherTimer TitleTimer;

    public ParsePreview()
    {
      InitializeComponent();

      parseList.ItemsSource = AvailableParses;
      parseList.SelectedIndex = -1;

      playerParseTextDoPetLabel.IsChecked = ConfigUtil.IfSetOrElse("PlayerParseShowPetLabel", true);
      playerParseTextDoDPS.IsChecked = ConfigUtil.IfSetOrElse("PlayerParseShowDPS", true);
      playerParseTextDoRank.IsChecked = ConfigUtil.IfSetOrElse("PlayerParseShowRank", true);
      playerParseTextDoTotals.IsChecked = ConfigUtil.IfSetOrElse("PlayerParseShowTotals", true);
      playerParseTextDoSpecials.IsChecked = ConfigUtil.IfSetOrElse("PlayerParseShowSpecials", true);
      playerParseTextDoTime.IsChecked = ConfigUtil.IfSetOrElse("PlayerParseShowTime", true);

      // this window is either hidden or visible and doesn't need to implement dispose
      DamageStatsManager.Instance.EventsGenerationStatus += Instance_EventsGenerationStatus;
      HealingStatsManager.Instance.EventsGenerationStatus += Instance_EventsGenerationStatus;
      TankingStatsManager.Instance.EventsGenerationStatus += Instance_EventsGenerationStatus;

      TitleTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 1000) };
      TitleTimer.Tick += (sender, e) =>
      {
        TitleTimer.Stop();

        if (parseList.SelectedIndex > -1)
        {
          SetParseTextByType(parseList.SelectedItem as string);
        }
      };

      customParseTitle.Text = Properties.Resources.CUSTOM_PARSE_TITLE;
      customParseTitle.FontStyle = FontStyles.Italic;
      parseList.Focus();
      initialized = true;
    }

    internal void CopyToEQClick(string type)
    {
      if (parseList.SelectedItem?.ToString() != type && AvailableParses.Contains(type))
      {
        parseList.SelectedItem = type;
      }

      Clipboard.SetDataObject(playerParseTextBox.Text);
    }

    internal void AddParse(string type, ISummaryBuilder builder, CombinedStats combined, List<PlayerStats> selected = null, bool copy = false)
    {
      Parses[type] = new ParseData() { Builder = builder, CombinedStats = combined };

      if (selected != null)
      {
        Parses[type].Selected.AddRange(selected);
      }

      if (!AvailableParses.Contains(type))
      {
        Dispatcher.InvokeAsync(() => AvailableParses.Add(type));
      }

      TriggerParseUpdate(type, copy);
    }

    internal void UpdateParse(PlayerStatsSelectionChangedEventArgs data, ISummaryBuilder builder, bool hasTopParse, string label, string topLabel)
    {
      // change the update order based on whats displayed
      if (parseList.SelectedItem?.ToString() == topLabel)
      {
        UpdateParse(label, data.Selected);
        if (hasTopParse)
        {
          AddParse(topLabel, builder, data.CurrentStats, data.Selected);
        }
      }
      else
      {
        if (hasTopParse)
        {
          AddParse(topLabel, builder, data.CurrentStats, data.Selected);
        }

        UpdateParse(label, data.Selected);
      }
    }

    internal void UpdateParse(string type, List<PlayerStats> selected)
    {
      if (Parses.ContainsKey(type))
      {
        Parses[type].Selected.Clear();
        if (selected != null)
        {
          Parses[type].Selected.AddRange(selected);
        }

        TriggerParseUpdate(type);
      }
    }

    private void CopyToEQButtonClick(object sender = null, RoutedEventArgs e = null)
    {
      CopyToEQClick(parseList.SelectedItem?.ToString());
    }

    private void Instance_EventsGenerationStatus(object sender, StatsGenerationEvent e)
    {
      switch (e.State)
      {
        case "COMPLETED":
        case "NONPC":
        case "NODATA":
          AddParse(e.Type, sender as ISummaryBuilder, e.CombinedStats);
          break;
      }
    }

    private void SetParseTextByType(string type)
    {
      if (Parses.ContainsKey(type))
      {
        var combined = Parses[type].CombinedStats;
        var customTitle = customParseTitle.FontStyle == FontStyles.Italic ? null : customParseTitle.Text;
        var summary = Parses[type].Builder?.BuildSummary(type, combined, Parses[type].Selected, playerParseTextDoPetLabel.IsChecked.Value,
          playerParseTextDoDPS.IsChecked.Value, playerParseTextDoTotals.IsChecked.Value, playerParseTextDoRank.IsChecked.Value, playerParseTextDoSpecials.IsChecked.Value,
          playerParseTextDoTime.IsChecked.Value, customTitle);
        playerParseTextBox.Text = summary.Title + summary.RankedPlayers;
        playerParseTextBox.SelectAll();
      }
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
          CopyToEQButtonClick();
        }
      });
    }

    private void PlayerParseTextBoxTextChanged(object sender, TextChangedEventArgs e)
    {
      if (string.IsNullOrEmpty(playerParseTextBox.Text) || playerParseTextBox.Text == Properties.Resources.SHARE_DPS_SELECTED)
      {
        copyToEQButton.IsEnabled = false;
        sharePlayerParseLabel.Text = Properties.Resources.SHARE_DPS_SELECTED;
        sharePlayerParseLabel.Foreground = BRIGHT_BRUSH;
        sharePlayerParseWarningLabel.Text = string.Format(CultureInfo.CurrentCulture, "{0}/{1}", playerParseTextBox.Text.Length, 509);
        sharePlayerParseWarningLabel.Visibility = Visibility.Hidden;
      }
      else if (playerParseTextBox.Text.Length > 509)
      {
        copyToEQButton.IsEnabled = false;
        sharePlayerParseLabel.Text = Properties.Resources.SHARE_DPS_TOO_BIG;
        sharePlayerParseLabel.Foreground = WARNING_BRUSH;
        sharePlayerParseWarningLabel.Text = string.Format("{0}/{1}", playerParseTextBox.Text.Length, 509);
        sharePlayerParseWarningLabel.Foreground = WARNING_BRUSH;
        sharePlayerParseWarningLabel.Visibility = Visibility.Visible;
      }
      else if (playerParseTextBox.Text.Length > 0 && playerParseTextBox.Text != Properties.Resources.SHARE_DPS_SELECTED)
      {
        copyToEQButton.IsEnabled = true;
        if (parseList.SelectedItem != null && Parses.TryGetValue(parseList.SelectedItem as string, out ParseData data))
        {
          var count = data.Selected?.Count > 0 ? data.Selected?.Count : 0;
          string players = count == 1 ? "Player" : "Players";
          sharePlayerParseLabel.Text = string.Format("{0} {1} Selected", count, players);
        }

        sharePlayerParseLabel.Foreground = BRIGHT_BRUSH;
        sharePlayerParseWarningLabel.Text = playerParseTextBox.Text.Length + " / " + 509;
        sharePlayerParseWarningLabel.Foreground = BRIGHT_BRUSH;
        sharePlayerParseWarningLabel.Visibility = Visibility.Visible;
      }
    }

    private void PlayerParseTextCheckChange(object sender, RoutedEventArgs e)
    {
      if (parseList.SelectedIndex > -1)
      {
        SetParseTextByType(parseList.SelectedItem as string);
      }

      // dont call these until after init/load
      if (initialized)
      {
        ConfigUtil.SetSetting("PlayerParseShowPetLabel", playerParseTextDoPetLabel.IsChecked.Value.ToString(CultureInfo.CurrentCulture));
        ConfigUtil.SetSetting("PlayerParseShowDPS", playerParseTextDoDPS.IsChecked.Value.ToString(CultureInfo.CurrentCulture));
        ConfigUtil.SetSetting("PlayerParseShowRank", playerParseTextDoRank.IsChecked.Value.ToString(CultureInfo.CurrentCulture));
        ConfigUtil.SetSetting("PlayerParseShowTotals", playerParseTextDoTotals.IsChecked.Value.ToString(CultureInfo.CurrentCulture));
        ConfigUtil.SetSetting("PlayerParseShowSpecials", playerParseTextDoSpecials.IsChecked.Value.ToString(CultureInfo.CurrentCulture));
        ConfigUtil.SetSetting("PlayerParseShowTime", playerParseTextDoTime.IsChecked.Value.ToString(CultureInfo.CurrentCulture));
      }
    }

    private void ParseListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (parseList.SelectedIndex > -1)
      {
        SetParseTextByType(parseList.SelectedItem as string);
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
      if (customParseTitle.Text == Properties.Resources.CUSTOM_PARSE_TITLE)
      {
        customParseTitle.Text = "";
        customParseTitle.FontStyle = FontStyles.Normal;
      }
    }

    private void CustomTitleLostFocus(object sender, RoutedEventArgs e)
    {
      if (customParseTitle.Text.Length == 0)
      {
        customParseTitle.Text = Properties.Resources.CUSTOM_PARSE_TITLE;
        customParseTitle.FontStyle = FontStyles.Italic;
      }
    }

    private void CustomTitleKeyDown(object sender, KeyEventArgs e)
    {
      if (e.Key == Key.Escape)
      {
        customParseTitle.Text = Properties.Resources.CUSTOM_PARSE_TITLE;
        customParseTitle.FontStyle = FontStyles.Italic;
        parseList.Focus();
      }
    }

    private void CustomTitleTextChanged(object sender, TextChangedEventArgs e)
    {
      TitleTimer.Stop();
      TitleTimer.Start();
    }
  }
}
