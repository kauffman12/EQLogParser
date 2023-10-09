using Syncfusion.Windows.Controls.Input;
using System;
using System.Collections.Generic;
using System.Linq;
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
  public partial class ChatViewer : UserControl, IDisposable
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly List<double> FontSizeList = new List<double>() { 10, 12, 14, 16, 18, 20, 22, 24 };

    private const int PAGE_SIZE = 200;
    private List<string> PlayerAutoCompleteList;
    private readonly DispatcherTimer FilterTimer;
    private ChatFilter CurrentChatFilter = null;
    private ChatIterator CurrentIterator = null;
    private IInputElement LastFocused = null;
    private string LastChannelSelection = null;
    private string LastPlayerSelection = null;
    private string LastTextFilter = null;
    private string LastToFilter = null;
    private string LastFromFilter = null;
    private double LastStartDate = 0;
    private double LastEndDate = 0;
    private readonly bool Ready = false;

    public ChatViewer()
    {
      InitializeComponent();
      textFilter.Text = EQLogParser.Resource.CHAT_TEXT_FILTER;
      startDate.DateTime = new DateTime(1999, 3, 16);
      endDate.DateTime = DateTime.Now;
      toFilter.Text = EQLogParser.Resource.CHAT_TO_FILTER;
      fromFilter.Text = EQLogParser.Resource.CHAT_FROM_FILTER;

      var allFonts = UIElementUtil.GetSystemFontFamilies();
      fontFamily.ItemsSource = allFonts;
      var family = ConfigUtil.GetSetting("ChatFontFamily") ?? chatBox.FontFamily?.Source;
      if (allFonts.FirstOrDefault(item => item.Source == family) is FontFamily found)
      {
        fontFamily.SelectedItem = found;
      }

      fontSize.ItemsSource = FontSizeList;
      var size = ConfigUtil.GetSetting("ChatFontSize");
      if (size != null && double.TryParse(size, out var dsize))
      {
        fontSize.SelectedItem = dsize;
      }
      else
      {
        fontSize.SelectedValue = chatBox.FontSize;
      }

      UpdateCurrentTextColor();
      FilterTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 500) };
      FilterTimer.Tick += (sender, e) =>
      {
        FilterTimer.Stop();
        ChangeSearch();
      };

      LoadPlayers();
      Ready = true;
      ChatManager.Instance.EventsUpdatePlayer += ChatManagerEventsUpdatePlayer;
      ChatManager.Instance.EventsNewChannels += ChatManagerEventsNewChannels;
      (Application.Current.MainWindow as MainWindow).EventsThemeChanged += EventsThemeChanged;
      ChangeSearch();
    }

    private void EventsThemeChanged(string _) => UpdateCurrentTextColor();
    private void RefreshClick(object sender, RoutedEventArgs e) => ChangeSearch(true);
    private void ChatManagerEventsUpdatePlayer(object sender, string player) => LoadPlayers(player);
    private void ToFilterLostFocus(object sender, RoutedEventArgs e) => FilterLostFocus(toFilter, EQLogParser.Resource.CHAT_TO_FILTER);
    private void FromFilterLostFocus(object sender, RoutedEventArgs e) => FilterLostFocus(fromFilter, EQLogParser.Resource.CHAT_FROM_FILTER);
    private void TextFilterLostFocus(object sender, RoutedEventArgs e) => FilterLostFocus(textFilter, EQLogParser.Resource.CHAT_TEXT_FILTER);
    private void ToFilterKeyDown(object sender, KeyEventArgs e) => FilterKeyDown(toFilter, EQLogParser.Resource.CHAT_TO_FILTER, e);
    private void FromFilterKeyDown(object sender, KeyEventArgs e) => FilterKeyDown(fromFilter, EQLogParser.Resource.CHAT_FROM_FILTER, e);
    private void TextFilterKeyDown(object sender, KeyEventArgs e) => FilterKeyDown(textFilter, EQLogParser.Resource.CHAT_TEXT_FILTER, e);
    private void ToFilterGotFocus(object sender, RoutedEventArgs e) => FilterGotFocus(toFilter, EQLogParser.Resource.CHAT_TO_FILTER);
    private void FromFilterGotFocus(object sender, RoutedEventArgs e) => FilterGotFocus(fromFilter, EQLogParser.Resource.CHAT_FROM_FILTER);
    private void TextFilterGotFocus(object sender, RoutedEventArgs e) => FilterGotFocus(textFilter, EQLogParser.Resource.CHAT_TEXT_FILTER);
    private void SelectedDatesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ChangeSearch();
    private double GetEndDate() => (endDate.DateTime != null) ? endDate.DateTime.Value.Ticks / TimeSpan.FromSeconds(1).Ticks : 0;
    private double GetStartDate() => (startDate.DateTime != null) ? startDate.DateTime.Value.Ticks / TimeSpan.FromSeconds(1).Ticks : 0;

    private void UpdateCurrentTextColor()
    {
      var defaultColor = (Color)Application.Current.Resources["ContentForeground.Color"];
      try
      {
        var colorSetting = "ChatFontFgColor" + MainWindow.CurrentTheme;
        var fgColor = ConfigUtil.GetSetting(colorSetting, defaultColor.ToString());
        colorPicker.Color = (Color)ColorConverter.ConvertFromString(fgColor);
      }
      catch (FormatException)
      {
        colorPicker.Color = defaultColor;
      }
    }

    private void ChatManagerEventsNewChannels(object sender, List<string> e)
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
      var chatList = CurrentIterator.Take(count).Select(chat => chat.Text).ToList();
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
        new ComboBoxItemDetails { Text = EQLogParser.Resource.SELECT_ALL },
        new ComboBoxItemDetails { Text = EQLogParser.Resource.UNSELECT_ALL }
      };

      var count = 0;
      ChatManager.Instance.GetChannels(playerAndServer).ForEach(chan =>
      {
        count += chan.IsChecked ? 1 : 0;
        items.Add(chan);
      });

      channels.ItemsSource = items;
      UIElementUtil.SetComboBoxTitle(channels, count, EQLogParser.Resource.CHANNELS_SELECTED, true);
    }

    private void LoadPlayers(string updatedPlayer = null)
    {
      if (updatedPlayer == null || !players.Items.Contains(updatedPlayer))
      {
        var playerList = ChatManager.Instance.GetArchivedPlayers();
        if (playerList.Count > 0)
        {
          players.Items.Clear();
          players.ItemsSource = playerList;

          var player = ConfigUtil.GetSetting("ChatSelectedPlayer");
          if (string.IsNullOrEmpty(player))
          {
            if (!string.IsNullOrEmpty(ConfigUtil.PlayerName) && !string.IsNullOrEmpty(ConfigUtil.ServerName))
            {
              player = ConfigUtil.PlayerName + "." + ConfigUtil.ServerName;
            }
          }

          if (playerList.IndexOf(player) is int index && index > -1)
          {
            players.SelectedIndex = index;
          }
        }
      }
    }

    private List<string> GetSelectedChannels(out bool changed)
    {
      changed = false;
      var selected = new List<string>();

      var builder = new StringBuilder();
      for (var i = 2; i < channels.Items.Count; i++)
      {
        if (channels.Items[i] is ComboBoxItemDetails checkedItem && checkedItem.IsChecked)
        {
          selected.Add(checkedItem.Text);
          builder.Append(checkedItem.Text);
        }
      }

      var updated = builder.ToString();
      if (LastChannelSelection != updated)
      {
        LastChannelSelection = updated;
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
          var text = (textFilter.Text.Length != 0 && textFilter.Text != EQLogParser.Resource.CHAT_TEXT_FILTER) ? textFilter.Text : null;
          var to = (toFilter.Text.Length != 0 && toFilter.Text != EQLogParser.Resource.CHAT_TO_FILTER) ? toFilter.Text : null;
          var from = (fromFilter.Text.Length != 0 && fromFilter.Text != EQLogParser.Resource.CHAT_FROM_FILTER) ? fromFilter.Text : null;
          var startDateValue = GetStartDate();
          var endDateValue = GetEndDate();
          if (force || changed || LastPlayerSelection != name || LastTextFilter != text || LastToFilter != to || LastFromFilter != from ||
            LastStartDate != startDateValue || LastEndDate != endDateValue)
          {
            CurrentChatFilter = new ChatFilter(name, channelList, startDateValue, endDateValue, to, from, text);
            CurrentIterator?.Close();
            CurrentIterator = new ChatIterator(name, CurrentChatFilter);
            LastPlayerSelection = name;
            LastTextFilter = text;
            LastToFilter = to;
            LastFromFilter = from;
            LastStartDate = startDateValue;
            LastEndDate = endDateValue;

            if (changed)
            {
              ChatManager.Instance.SaveSelectedChannels(name, channelList);
            }

            chatBox.Text = "";
            LastFocused = Keyboard.FocusedElement;
            DisplayPage(PAGE_SIZE);
          }
        }
      }
      catch (Exception e)
      {
        LOG.Error(e);
      }
    }

    private void ChatScrollChanged(object sender, ScrollChangedEventArgs e)
    {
      if (e.OriginalSource is ScrollViewer viewer)
      {
        if (e.VerticalChange < 0 && e.VerticalOffset < 800)
        {
          if (Ready)
          {
            DisplayPage(PAGE_SIZE);
          }
        }
        else if (e.VerticalChange == 0 && chatBox?.Text != null && chatBox.Lines.Count > PAGE_SIZE && e.VerticalOffset < 800)
        {
          viewer.ScrollToVerticalOffset(4500);
        }
      }
    }

    private void ChatTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
      if (chatBox?.Text != null && chatBox.Lines.Count <= PAGE_SIZE)
      {
        Task.Delay(100).ContinueWith(task => Dispatcher.InvokeAsync(() =>
        {
          chatBox.GoToLine(chatBox.Lines.Count);
          LastFocused?.Focus();
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
      var item = sender as ComboBoxItem;
      if (item.Content is ComboBoxItemDetails details)
      {
        if (details.Text == "Select All" && !details.IsChecked)
        {
          details.IsChecked = true;
          var unselect = channels.Items[1] as ComboBoxItemDetails;
          unselect.IsChecked = false;

          for (var i = 2; i < channels.Items.Count; i++)
          {
            (channels.Items[i] as ComboBoxItemDetails).IsChecked = true;
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
          var select = channels.Items[0] as ComboBoxItemDetails;
          select.IsChecked = false;

          for (var i = 2; i < channels.Items.Count; i++)
          {
            (channels.Items[i] as ComboBoxItemDetails).IsChecked = false;
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
          var select = channels.Items[0] as ComboBoxItemDetails;
          if (select.IsChecked)
          {
            select.IsChecked = false;
            details.IsChecked = false;
            channels.Items.Refresh();
          }
        }
        else if (!details.IsChecked)
        {
          var unselect = channels.Items[1] as ComboBoxItemDetails;
          if (unselect.IsChecked)
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
          var checkedItem = channels.Items[i] as ComboBoxItemDetails;
          if (checkedItem.IsChecked)
          {
            count++;
          }
        }

        UIElementUtil.SetComboBoxTitle(channels, count, EQLogParser.Resource.CHANNELS_SELECTED, true);
      }

      if (Ready)
      {
        ChangeSearch();
      }
    }

    private void FontFgColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
      if (chatBox != null)
      {
        chatBox.Foreground = new SolidColorBrush(colorPicker.Color);
        var colorSetting = "ChatFontFgColor" + MainWindow.CurrentTheme;
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
      if (fontFamily?.SelectedItem != null && chatBox != null)
      {
        var family = fontFamily.SelectedItem as FontFamily;
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
        filterExt.AutoCompleteSource = PlayerAutoCompleteList;
      }
    }

    private void FilterGotFocus(TextBox filter, string text)
    {
      if (filter?.Text == text)
      {
        filter.Text = "";
        filter.FontStyle = FontStyles.Normal;
      }
    }

    private static void FilterLostFocus(TextBox filter, string text)
    {
      if (filter?.Text?.Length == 0)
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
      FilterTimer?.Stop();
      FilterTimer?.Start();
    }

    private void WindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
      if (e.OriginalSource is ScrollViewer)
      {
        e.Handled = true;
      }
    }

    private void PlayerChanged(object sender, SelectionChangedEventArgs e)
    {
      if (players.SelectedItem is string name && name.Length > 0 && !name.StartsWith("No ", StringComparison.Ordinal))
      {
        LoadChannels(players.SelectedItem as string);
        PlayerAutoCompleteList = ChatManager.Instance.GetPlayers(name);
        ConfigUtil.SetSetting("ChatSelectedPlayer", name);

        if (Ready)
        {
          ChangeSearch();
        }
      }
    }

    #region IDisposable Support
    private bool disposedValue = false; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        (Application.Current.MainWindow as MainWindow).EventsThemeChanged -= EventsThemeChanged;
        ChatManager.Instance.EventsUpdatePlayer -= ChatManagerEventsUpdatePlayer;
        ChatManager.Instance.EventsNewChannels -= ChatManagerEventsNewChannels;

        chatBox?.Dispose();
        FilterTimer?.Stop();
        disposedValue = true;
      }
    }

    // This code added to correctly implement the disposable pattern.
    public void Dispose()
    {
      // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
      Dispose(true);
      // TODO: uncomment the following line if the finalizer is overridden above.
      GC.SuppressFinalize(this);
    }
    #endregion
  }
}
