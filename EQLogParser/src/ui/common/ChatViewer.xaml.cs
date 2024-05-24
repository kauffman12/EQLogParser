using log4net;
using Syncfusion.Windows.Controls.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for ChatViewer.xaml
  /// </summary>
  public partial class ChatViewer : IDocumentContent
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly List<double> FontSizeList = [10, 12, 14, 16, 18, 20, 22, 24];

    private const int PageSize = 200;
    private List<string> _playerAutoCompleteList;
    private readonly DispatcherTimer _filterTimer;
    private ChatFilter _currentChatFilter;
    private ChatIterator _currentIterator;
    private IInputElement _lastFocused;
    private string _lastChannelSelection;
    private string _lastPlayerSelection;
    private string _lastTextFilter;
    private string _lastToFilter;
    private string _lastFromFilter;
    private double _lastStartDate;
    private double _lastEndDate;
    private bool _ready;

    public ChatViewer()
    {
      InitializeComponent();
      textFilter.Text = Resource.CHAT_TEXT_FILTER;
      startDate.DateTime = new DateTime(1999, 3, 16);
      endDate.DateTime = DateTime.Now;
      toFilter.Text = Resource.CHAT_TO_FILTER;
      fromFilter.Text = Resource.CHAT_FROM_FILTER;

      var allFonts = UiElementUtil.GetSystemFontFamilies();
      fontFamily.ItemsSource = allFonts;
      var family = ConfigUtil.GetSetting("ChatFontFamily") ?? chatBox.FontFamily?.Source;
      if (allFonts.FirstOrDefault(item => item.Source == family) is { } found)
      {
        fontFamily.SelectedItem = found;
      }

      fontSize.ItemsSource = FontSizeList;
      var size = ConfigUtil.GetSettingAsDouble("ChatFontSize");
      if (size > 0)
      {
        fontSize.SelectedItem = size;
      }
      else
      {
        fontSize.SelectedValue = chatBox.FontSize;
      }

      UpdateCurrentTextColor();
      _filterTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 500) };
      _filterTimer.Tick += (_, _) =>
      {
        _filterTimer.Stop();
        ChangeSearch();
      };

      MainActions.EventsThemeChanged += EventsThemeChanged;
    }

    private void EventsThemeChanged(string _) => UpdateCurrentTextColor();
    private void RefreshClick(object sender, RoutedEventArgs e) => ChangeSearch(true);
    private void ChatManagerEventsUpdatePlayer(string player) => LoadPlayers(player);
    private void ToFilterLostFocus(object sender, RoutedEventArgs e) => FilterLostFocus(toFilter, Resource.CHAT_TO_FILTER);
    private void FromFilterLostFocus(object sender, RoutedEventArgs e) => FilterLostFocus(fromFilter, Resource.CHAT_FROM_FILTER);
    private void TextFilterLostFocus(object sender, RoutedEventArgs e) => FilterLostFocus(textFilter, Resource.CHAT_TEXT_FILTER);
    private void ToFilterKeyDown(object sender, KeyEventArgs e) => FilterKeyDown(toFilter, Resource.CHAT_TO_FILTER, e);
    private void FromFilterKeyDown(object sender, KeyEventArgs e) => FilterKeyDown(fromFilter, Resource.CHAT_FROM_FILTER, e);
    private void TextFilterKeyDown(object sender, KeyEventArgs e) => FilterKeyDown(textFilter, Resource.CHAT_TEXT_FILTER, e);
    private void ToFilterGotFocus(object sender, RoutedEventArgs e) => FilterGotFocus(toFilter, Resource.CHAT_TO_FILTER);
    private void FromFilterGotFocus(object sender, RoutedEventArgs e) => FilterGotFocus(fromFilter, Resource.CHAT_FROM_FILTER);
    private void TextFilterGotFocus(object sender, RoutedEventArgs e) => FilterGotFocus(textFilter, Resource.CHAT_TEXT_FILTER);
    private void SelectedDatesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ChangeSearch();
    private long GetEndDate() => (endDate.DateTime != null) ? endDate.DateTime.Value.Ticks / TimeSpan.TicksPerSecond : 0;
    private long GetStartDate() => (startDate.DateTime != null) ? startDate.DateTime.Value.Ticks / TimeSpan.TicksPerSecond : 0;

    private void UpdateCurrentTextColor()
    {
      var defaultColor = (Color)Application.Current.Resources["ContentForeground.Color"]!;
      try
      {
        var colorSetting = "ChatFontFgColor" + MainActions.CurrentTheme;
        var fgColor = ConfigUtil.GetSetting(colorSetting, defaultColor.ToString());
        colorPicker.Color = (Color)ColorConverter.ConvertFromString(fgColor)!;
      }
      catch (FormatException)
      {
        colorPicker.Color = defaultColor;
      }
    }

    private void ChatManagerEventsNewChannels(List<string> e)
    {
      _ = Dispatcher.InvokeAsync(() =>
        {
          if (players.SelectedValue is string player)
          {
            LoadChannels(player);
          }
        }, DispatcherPriority.DataBind);
    }

    private void DisplayPage(int count)
    {
      var chatList = _currentIterator.Take(count).Select(chat => chat.Text).ToList();
      chatList.Reverse();

      if (chatList.Count > 0)
      {
        var text = string.Join("\n", chatList);

        if (!string.IsNullOrEmpty(chatBox.Text))
        {
          text += "\n";
        }

        chatBox.Text = text + chatBox.Text;
      }

      statusCount.Text = chatBox.Lines.Count + " Lines";
    }

    private void LoadChannels(string playerAndServer)
    {
      var items = new List<ComboBoxItemDetails>
      {
        new() { Text = Resource.SELECT_ALL },
        new() { Text = Resource.UNSELECT_ALL }
      };

      var count = 0;
      ChatManager.Instance.GetChannels(playerAndServer).ForEach(chan =>
      {
        count += chan.IsChecked ? 1 : 0;
        items.Add(chan);
      });

      channels.ItemsSource = items;
      UiElementUtil.SetComboBoxTitle(channels, count, Resource.CHANNELS_SELECTED, true);
    }

    private void LoadPlayers(string updatedPlayer = null)
    {
      Dispatcher.InvokeAsync(() =>
      {
        var orig = players.ItemsSource as List<string>;
        if (updatedPlayer == null || orig?.Contains(updatedPlayer) == false)
        {
          var playerList = ChatManager.GetArchivedPlayers();
          if (playerList.Count > 0)
          {
            players.ItemsSource = playerList;
            var player = ConfigUtil.GetSetting("ChatSelectedPlayer");
            if (string.IsNullOrEmpty(player))
            {
              if (!string.IsNullOrEmpty(ConfigUtil.PlayerName) && !string.IsNullOrEmpty(ConfigUtil.ServerName))
              {
                player = ConfigUtil.PlayerName + "." + ConfigUtil.ServerName;
              }
            }

            if (playerList.IndexOf(player) is var index and > -1)
            {
              players.SelectedIndex = index;
            }
          }
          else
          {
            players.ItemsSource = new List<string> { "No Chat Data" };
          }
        }
      });
    }

    private List<string> GetSelectedChannels(out bool changed)
    {
      changed = false;
      var selected = new List<string>();

      var builder = new StringBuilder();
      for (var i = 2; i < channels.Items.Count; i++)
      {
        if (channels.Items[i] is ComboBoxItemDetails { IsChecked: true } checkedItem)
        {
          selected.Add(checkedItem.Text);
          builder.Append(checkedItem.Text);
        }
      }

      var updated = builder.ToString();
      if (_lastChannelSelection != updated)
      {
        _lastChannelSelection = updated;
        changed = true;
      }

      return selected;
    }

    private void ChangeSearch(bool force = false)
    {
      try
      {
        if (players.SelectedItem is string name && !string.IsNullOrEmpty(name) && !name.StartsWith("No ", StringComparison.Ordinal))
        {
          var channelList = GetSelectedChannels(out var changed);
          var text = (textFilter.Text.Length != 0 && textFilter.Text != Resource.CHAT_TEXT_FILTER) ? textFilter.Text : null;
          var to = (toFilter.Text.Length != 0 && toFilter.Text != Resource.CHAT_TO_FILTER) ? toFilter.Text : null;
          var from = (fromFilter.Text.Length != 0 && fromFilter.Text != Resource.CHAT_FROM_FILTER) ? fromFilter.Text : null;
          var startDateValue = GetStartDate();
          var endDateValue = GetEndDate();
          if (force || changed || _lastPlayerSelection != name || _lastTextFilter != text || _lastToFilter != to || _lastFromFilter != from ||
            !_lastStartDate.Equals(startDateValue) || !_lastEndDate.Equals(endDateValue))
          {
            _currentChatFilter = new ChatFilter(name, channelList, startDateValue, endDateValue, to, from, text);
            _currentIterator?.Close();
            _currentIterator = new ChatIterator(name, _currentChatFilter);
            _lastPlayerSelection = name;
            _lastTextFilter = text;
            _lastToFilter = to;
            _lastFromFilter = from;
            _lastStartDate = startDateValue;
            _lastEndDate = endDateValue;

            if (changed)
            {
              ChatManager.SaveSelectedChannels(name, channelList);
            }

            chatBox.Text = "";
            _lastFocused = Keyboard.FocusedElement;
            DisplayPage(PageSize);
          }
        }
      }
      catch (Exception e)
      {
        Log.Error(e);
      }
    }

    private void ChatScrollChanged(object sender, ScrollChangedEventArgs e)
    {
      if (e.OriginalSource is ScrollViewer viewer)
      {
        if (e.VerticalChange < 0 && e.VerticalOffset < 800)
        {
          if (_ready)
          {
            DisplayPage(PageSize);
          }
        }
        else if (e.VerticalChange == 0 && chatBox?.Text != null && chatBox.Lines.Count > PageSize && e.VerticalOffset < 800)
        {
          viewer.ScrollToVerticalOffset(4500);
        }
      }
    }

    private void ChatTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
      if (chatBox is { Text: not null, Lines.Count: <= PageSize })
      {
        Task.Delay(250).ContinueWith(_ => Dispatcher.InvokeAsync(() =>
        {
          chatBox.GoToLine(chatBox.Lines.Count);
          _lastFocused?.Focus();
        }));
      }
    }

    private void ChatPreviewKeyDown(object sender, KeyEventArgs e)
    {
      // ignore these keys that open the save/options window
      if (e.Key == Key.O && (e.KeyboardDevice.Modifiers & ModifierKeys.Control) != 0)
      {
        e.Handled = true;
      }

      if (e.Key == Key.S && (e.KeyboardDevice.Modifiers & ModifierKeys.Control) != 0)
      {
        e.Handled = true;
      }
    }

    private void ChatMouseWheel(object sender, MouseWheelEventArgs e)
    {
      if ((Keyboard.Modifiers & ModifierKeys.Control) > 0)
      {
        if (e.Delta < 0 && fontSize?.SelectedIndex > 0)
        {
          fontSize.SelectedIndex--;
          e.Handled = true;
        }
        else if (e.Delta > 0 && fontSize?.SelectedIndex < (fontSize?.Items.Count - 1))
        {
          fontSize.SelectedIndex++;
          e.Handled = true;
        }
      }
    }

    private void ChannelPreviewMouseDown(object sender, EventArgs e)
    {
      if (sender is ComboBoxItem { Content: ComboBoxItemDetails details })
      {
        if (details.Text == "Select All" && !details.IsChecked)
        {
          details.IsChecked = true;
          if (channels.Items.Count > 1 && channels.Items[1] is ComboBoxItemDetails unselect)
          {
            unselect.IsChecked = false;
          }

          for (var i = 2; i < channels.Items.Count; i++)
          {
            if (channels.Items[i] is ComboBoxItemDetails item)
            {
              item.IsChecked = true;
            }
          }

          channels.Items.Refresh();
        }
        else if (details.Text == "Select All" && details.IsChecked)
        {
          details.IsChecked = true;
          channels.Items.Refresh();
        }
        else if (details.Text == "Unselect All" && !details.IsChecked)
        {
          details.IsChecked = true;
          if (channels.Items.Count > 0 && channels.Items[0] is ComboBoxItemDetails select)
          {
            select.IsChecked = false;
          }

          for (var i = 2; i < channels.Items.Count; i++)
          {
            if (channels.Items[i] is ComboBoxItemDetails item)
            {
              item.IsChecked = false;
            }
          }

          channels.Items.Refresh();
        }
        else if (details.Text == "Unselect All" && details.IsChecked)
        {
          details.IsChecked = true;
          channels.Items.Refresh();
        }
        else if (details.IsChecked)
        {
          if (channels.Items.Count > 0 && channels.Items[0] is ComboBoxItemDetails select && select.IsChecked)
          {
            select.IsChecked = false;
            details.IsChecked = false;
            channels.Items.Refresh();
          }
        }
        else if (!details.IsChecked)
        {
          if (channels.Items.Count > 1 && channels.Items[1] is ComboBoxItemDetails unselect && unselect.IsChecked)
          {
            unselect.IsChecked = false;
            details.IsChecked = true;
            channels.Items.Refresh();
          }
        }
      }
    }

    private void ChannelsDropDownClosed(object sender, EventArgs e)
    {
      if (channels?.Items.Count > 0)
      {
        var count = 0;
        for (var i = 2; i < channels.Items.Count; i++)
        {
          if (channels.Items[i] is ComboBoxItemDetails checkedItem && checkedItem.IsChecked)
          {
            count++;
          }
        }

        UiElementUtil.SetComboBoxTitle(channels, count, Resource.CHANNELS_SELECTED, true);
      }

      if (_ready)
      {
        ChangeSearch();
      }
    }

    private void FontFgColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
      if (chatBox != null)
      {
        chatBox.Foreground = new SolidColorBrush(colorPicker.Color);
        var colorSetting = "ChatFontFgColor" + MainActions.CurrentTheme;
        ConfigUtil.SetSetting(colorSetting, colorPicker.Color.ToString());
      }
    }

    private void FontSizeChanged(object sender, SelectionChangedEventArgs e)
    {
      if (fontSize?.SelectedItem != null && chatBox != null)
      {
        Application.Current.Resources["EQChatFontSize"] = (double)fontSize.SelectedItem;
        ConfigUtil.SetSetting("ChatFontSize", fontSize.SelectedItem.ToString());
      }
    }

    private void FontFamilyChanged(object sender, SelectionChangedEventArgs e)
    {
      if (fontFamily?.SelectedItem != null && chatBox != null && fontFamily.SelectedItem is FontFamily family)
      {
        Application.Current.Resources["EQChatFontFamily"] = family;
        ConfigUtil.SetSetting("ChatFontFamily", family.ToString());
      }
    }

    private void FilterKeyDown(TextBox filter, string text, KeyEventArgs e)
    {
      if (e.Key == Key.Escape)
      {
        if (filter is SfTextBoxExt filterExt)
        {
          filterExt.AutoCompleteSource = null;
        }

        filter.Text = text;
        filter.FontStyle = FontStyles.Italic;
        chatBox?.Focus();
      }
      else if (filter is SfTextBoxExt filterExt)
      {
        filterExt.AutoCompleteSource = _playerAutoCompleteList;
      }
    }

    private static void FilterGotFocus(TextBox filter, string text)
    {
      if (filter != null && filter.Text == text)
      {
        filter.Text = "";
        filter.FontStyle = FontStyles.Normal;
      }
    }

    private static void FilterLostFocus(TextBox filter, string text)
    {
      if (filter?.Text.Length == 0)
      {
        if (filter is SfTextBoxExt filterExt)
        {
          filterExt.AutoCompleteSource = null;
        }

        filter.Text = text;
        filter.FontStyle = FontStyles.Italic;
      }
    }

    private void FilterTextChanged(object sender, TextChangedEventArgs e)
    {
      _filterTimer?.Stop();
      _filterTimer?.Start();
    }

    // fix for edit control crashing if empty
    private void WindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
      if (e.OriginalSource is ScrollViewer && chatBox?.Lines?.Count == 0)
      {
        e.Handled = true;
      }
    }

    private void PlayerChanged(object sender, SelectionChangedEventArgs e)
    {
      if (players.SelectedItem is string { Length: > 0 } name && !name.StartsWith("No ", StringComparison.Ordinal))
      {
        LoadChannels(players.SelectedItem as string);
        _playerAutoCompleteList = ChatManager.GetPlayers(name);
        ConfigUtil.SetSetting("ChatSelectedPlayer", name);

        if (_ready)
        {
          ChangeSearch();
        }
      }
    }

    private void ChatViewerLoaded(object sender, RoutedEventArgs e)
    {
      if (VisualParent != null && !_ready)
      {
        ChatManager.Instance.EventsUpdatePlayer += ChatManagerEventsUpdatePlayer;
        ChatManager.Instance.EventsNewChannels += ChatManagerEventsNewChannels;
        LoadPlayers();
        ChangeSearch();
        _ready = true;
      }
    }

    public void HideContent()
    {
      ChatManager.Instance.EventsUpdatePlayer -= ChatManagerEventsUpdatePlayer;
      ChatManager.Instance.EventsNewChannels -= ChatManagerEventsNewChannels;
      _ready = false;
    }
  }
}
