using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
    private static bool Running = false;

    private List<string> PlayerAutoCompleteList = new List<string>();
    private Paragraph MainParagraph;
    private readonly DispatcherTimer FilterTimer;
    private readonly DispatcherTimer RefreshTimer;
    private ChatFilter CurrentChatFilter = null;
    private ChatIterator CurrentIterator = null;
    private ChatType FirstChat = null;
    private int CurrentLineCount = 0;
    private string LastChannelSelection = null;
    private string LastPlayerSelection = null;
    private string LastTextFilter = null;
    private string LastToFilter = null;
    private string LastFromFilter = null;
    private double LastStartDate = 0;
    private double LastEndDate = 0;
    private bool Connected = false;
    private readonly bool Ready = false;

    public ChatViewer()
    {
      InitializeComponent();

      fontSize.ItemsSource = FontSizeList;
      startDate.Text = Properties.Resources.CHAT_START_DATE;
      endDate.Text = Properties.Resources.CHAT_END_DATE;
      textFilter.Text = Properties.Resources.CHAT_TEXT_FILTER;

      var context = new AutoCompleteText() { Text = Properties.Resources.CHAT_TO_FILTER };
      context.Items.AddRange(PlayerAutoCompleteList);
      toFilter.DataContext = context;

      context = new AutoCompleteText() { Text = Properties.Resources.CHAT_FROM_FILTER };
      context.Items.AddRange(PlayerAutoCompleteList);
      fromFilter.DataContext = context;

      string fgColor = ConfigUtil.GetSetting("ChatFontFgColor");
      if (fontFgColor.ItemsSource is List<ColorItem> colors)
      {
        fontFgColor.SelectedItem = (colors.Find(item => item.Name == fgColor) is ColorItem found) ? found : colors.Find(item => item.Name == "#ffffff");
      }

      string family = ConfigUtil.GetSetting("ChatFontFamily");
      fontFamily.SelectedItem = (family != null) ? new FontFamily(family) : chatBox.FontFamily;

      string size = ConfigUtil.GetSetting("ChatFontSize");
      if (size != null && double.TryParse(size, out double dsize))
      {
        fontSize.SelectedItem = dsize;
      }
      else
      {
        fontSize.SelectedValue = chatBox.FontSize;
      }

      FilterTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 500) };
      FilterTimer.Tick += (sender, e) =>
      {
        FilterTimer.Stop();
        ChangeSearch();
      };

      RefreshTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 1) };
      RefreshTimer.Tick += (sender, e) =>
      {
        ChatIterator newIterator = new ChatIterator(players.SelectedValue as string, CurrentChatFilter);
        var tempChat = FirstChat;
        var tempFilter = CurrentChatFilter;

        if (tempChat != null)
        {
          Task.Run(() =>
          {
            foreach (var chatType in newIterator.TakeWhile(chatType => chatType.Line != tempChat.Line).Reverse())
            {
              Dispatcher.Invoke(() =>
              {
                // make sure user didnt start new search
                if (tempFilter == CurrentChatFilter && RefreshTimer.IsEnabled && tempFilter.PastLiveFilter(chatType))
                {
                  if (chatBox.Document.Blocks.Count == 0)
                  {
                    MainParagraph = new Paragraph { Margin = new Thickness(0, 0, 0, 0), Padding = new Thickness(4, 0, 0, 4) };
                    chatBox.Document.Blocks.Add(MainParagraph);
                    MainParagraph.Inlines.Add(new Run());
                  }

                  var newItem = new Span(new Run(Environment.NewLine));
                  newItem.Inlines.Add(new Run(chatType.Line));
                  MainParagraph.Inlines.InsertAfter(MainParagraph.Inlines.LastInline, newItem);
                  statusCount.Text = ++CurrentLineCount + " Lines";

                  FirstChat = chatType;
                }
              }, DispatcherPriority.DataBind);
            }

            Dispatcher.Invoke(() => RefreshTimer.Stop());
          });
        }
      };

      LoadPlayers();

      Ready = true;
      ChangeSearch();

      ChatManager.EventsUpdatePlayer += ChatManager_EventsUpdatePlayer;
      ChatManager.EventsNewChannels += ChatManager_EventsNewChannels;
    }

    private void ChatManager_EventsNewChannels(object sender, List<string> e)
    {
      _ = Dispatcher.InvokeAsync(() =>
        {
          if (players.SelectedValue is string player)
          {
            LoadChannels(player);
          }
        }, DispatcherPriority.DataBind);
    }

    private void ChatManager_EventsUpdatePlayer(object sender, string player) => LoadPlayers(player);

    private void DisplayPage(int count)
    {
      if (!Running)
      {
        Running = true;
        Task.Delay(10).ContinueWith(task =>
        {
          var chatList = CurrentIterator.Take(count).ToList();
          if (chatList.Count > 0)
          {
            Dispatcher.Invoke(() =>
            {
              try
              {
                bool needScroll = false;
                if (chatBox.Document.Blocks.Count == 0)
                {
                  MainParagraph = new Paragraph { Margin = new Thickness(0, 0, 0, 0), Padding = new Thickness(4, 0, 0, 4) };
                  chatBox.Document.Blocks.Add(MainParagraph);
                  MainParagraph.Inlines.Add(new Run());
                  needScroll = true;
                }

                for (int i = 0; i < chatList.Count; i++)
                {
                  if (needScroll && i == 0)
                  {
                    FirstChat = chatList[i];
                  }

                  var text = chatList[i].Line;

                  Span span = new Span();
                  if (LastTextFilter != null && chatList[i].KeywordStart > -1)
                  {
                    var first = text.Substring(0, chatList[i].KeywordStart);
                    var second = text.Substring(chatList[i].KeywordStart, LastTextFilter.Length);
                    var last = text.Substring(chatList[i].KeywordStart + LastTextFilter.Length);

                    if (first.Length > 0)
                    {
                      span.Inlines.Add(new Run(first));
                    }

                    span.Inlines.Add(new Run { Text = second, FontStyle = FontStyles.Italic, FontWeight = FontWeights.Bold });

                    if (last.Length > 0)
                    {
                      span.Inlines.Add(new Run(last));
                    }
                  }
                  else
                  {
                    span.Inlines.Add(new Run(text));
                  }

                  MainParagraph.Inlines.InsertAfter(MainParagraph.Inlines.FirstInline, span);

                  if (i != 0)
                  {
                    span.Inlines.Add(Environment.NewLine);
                  }

                  CurrentLineCount++;
                }

                if (needScroll)
                {
                  chatScroller.ScrollToEnd();
                }

                if (!Connected)
                {
                  chatScroller.ScrollChanged += Chat_ScrollChanged;
                  Connected = true;
                }

                statusCount.Text = CurrentLineCount + " Lines";
              }
              catch (Exception ex2)
              {
                LOG.Error(ex2);
                throw;
              }
              finally
              {
                Running = false;
              }

            }, DispatcherPriority.Normal);
          }
          else
          {
            Running = false;
          }
        }, TaskScheduler.Default);
      }
    }

    private void LoadChannels(string playerAndServer)
    {
      List<ComboBoxItemDetails> items = new List<ComboBoxItemDetails>
      {
        new ComboBoxItemDetails { Text = Properties.Resources.SELECT_ALL },
        new ComboBoxItemDetails { Text = Properties.Resources.UNSELECT_ALL }
      };

      int selectedCount = 0;
      ChatManager.GetChannels(playerAndServer).ForEach(chan =>
      {
        selectedCount += chan.IsChecked ? 1 : 0;
        items.Add(chan);
      });

      channels.ItemsSource = items;

      if (items.Count > 0)
      {
        items[0].SelectedText = selectedCount + " Channels Selected";
        channels.SelectedItem = items[0];
      }
    }

    private void LoadPlayers(string updatedPlayer = null)
    {
      _ = Dispatcher.InvokeAsync(() =>
        {
          if (updatedPlayer == null || (updatedPlayer != null && !players.Items.Contains(updatedPlayer)))
          {
            var playerList = ChatManager.GetArchivedPlayers();
            if (playerList.Count > 0)
            {
              if (players.ItemsSource == null)
              {
                players.Items.Clear();
              }

              players.ItemsSource = playerList;

              string player = ConfigUtil.GetSetting("ChatSelectedPlayer");
              if (string.IsNullOrEmpty(player) && !string.IsNullOrEmpty(ConfigUtil.PlayerName) && !string.IsNullOrEmpty(ConfigUtil.ServerName))
              {
                player = ConfigUtil.PlayerName + "." + ConfigUtil.ServerName;
              }

              players.SelectedIndex = (player != null && playerList.IndexOf(player) > -1) ? playerList.IndexOf(player) : 0;
            }
          }
          else
          {
            if (!RefreshTimer.IsEnabled)
            {
              RefreshTimer.Start();
            }
          }
        }, DispatcherPriority.DataBind);
    }

    private List<string> GetSelectedChannels(out bool changed)
    {
      changed = false;
      List<string> selected = new List<string>();

      StringBuilder builder = new StringBuilder();
      for (int i = 2; i < channels.Items.Count; i++)
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

    private void ChangeSearch(bool refresh = false)
    {
      if (players.SelectedItem is string name && name.Length > 0 && !name.StartsWith("No ", StringComparison.Ordinal))
      {
        var channelList = GetSelectedChannels(out bool changed);
        string text = (textFilter.Text.Length != 0 && textFilter.Text != Properties.Resources.CHAT_TEXT_FILTER) ? textFilter.Text : null;
        string to = (toFilter.Text.Length != 0 && toFilter.Text != Properties.Resources.CHAT_TO_FILTER) ? toFilter.Text : null;
        string from = (fromFilter.Text.Length != 0 && fromFilter.Text != Properties.Resources.CHAT_FROM_FILTER) ? fromFilter.Text : null;
        double startDateValue = GetStartDate();
        double endDateValue = GetEndDate();
        if (refresh || changed || LastPlayerSelection != name || LastTextFilter != text || LastToFilter != to || LastFromFilter != from || LastStartDate != startDateValue || LastEndDate != endDateValue)
        {
          CurrentChatFilter = new ChatFilter(name, channelList, startDateValue, endDateValue, to, from, text);
          CurrentIterator?.Close();
          CurrentIterator = new ChatIterator(name, CurrentChatFilter);
          CurrentLineCount = 0;
          RefreshTimer.Stop();
          LastPlayerSelection = name;
          LastTextFilter = text;
          LastToFilter = to;
          LastFromFilter = from;
          LastStartDate = startDateValue;
          LastEndDate = endDateValue;

          chatScroller.ScrollChanged -= Chat_ScrollChanged;
          Connected = false;

          if (changed)
          {
            ChatManager.SaveSelectedChannels(name, channelList);
          }

          chatBox.Document.Blocks.Clear();
          DisplayPage(100);
        }
      }
    }

    private void Chat_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
      if (e.VerticalChange < 0 && e.VerticalOffset / e.ExtentHeight <= 0.10)
      {
        int pageSize = (int)(100 * chatBox.ExtentHeight / 5000);
        pageSize = pageSize > 250 ? 250 : pageSize;
        DisplayPage(pageSize);
      }

      if (chatScroller.VerticalOffset >= 0 && e.ViewportHeightChange > 0)
      {
        chatScroller.ScrollToVerticalOffset(chatScroller.VerticalOffset + e.ViewportHeightChange);
      }
    }

    private void Chat_KeyDown(object sender, KeyEventArgs e)
    {
      if (e.Key == Key.PageDown)
      {
        var offset = Math.Min(chatScroller.ExtentHeight, chatScroller.VerticalOffset + chatScroller.ViewportHeight);
        chatScroller.ScrollToVerticalOffset(offset);
      }
      else if (e.Key == Key.PageUp)
      {
        var offset = Math.Max(0, chatScroller.VerticalOffset - chatScroller.ViewportHeight);
        chatScroller.ScrollToVerticalOffset(offset);
      }
    }

    private void Player_Changed(object sender, SelectionChangedEventArgs e)
    {
      if (players.SelectedItem is string name && name.Length > 0 && !name.StartsWith("No ", StringComparison.Ordinal))
      {
        LoadChannels(players.SelectedItem as string);
        PlayerAutoCompleteList = ChatManager.GetPlayers(name);
        ConfigUtil.SetSetting("ChatSelectedPlayer", name);

        if (Ready)
        {
          ChangeSearch();
        }
      }
    }

    private void Channel_PreviewMouseDown(object sender, EventArgs e)
    {
      var item = sender as ComboBoxItem;
      if (item.Content is ComboBoxItemDetails details)
      {
        if (details.Text == "Select All" && !details.IsChecked)
        {
          details.IsChecked = true;
          var unselect = channels.Items[1] as ComboBoxItemDetails;
          unselect.IsChecked = false;

          for (int i = 2; i < channels.Items.Count; i++)
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

          for (int i = 2; i < channels.Items.Count; i++)
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

    private void Channels_DropDownClosed(object sender, EventArgs e)
    {
      if (channels.Items.Count > 0)
      {
        int count = 0;
        for (int i = 2; i < channels.Items.Count; i++)
        {
          var checkedItem = channels.Items[i] as ComboBoxItemDetails;
          if (checkedItem.IsChecked)
          {
            count++;
          }
        }

        if (!(channels.SelectedItem is ComboBoxItemDetails selected))
        {
          selected = channels.Items[2] as ComboBoxItemDetails;
        }

        selected.SelectedText = count + " Channels Selected";
        channels.SelectedIndex = -1;
        channels.SelectedItem = selected;
      }

      if (Ready)
      {
        ChangeSearch();
      }
    }

    private void FontFgColor_Changed(object sender, SelectionChangedEventArgs e)
    {
      if (fontFgColor.SelectedItem != null)
      {
        var item = fontFgColor.SelectedItem as ColorItem;
        chatBox.Foreground = item.Brush;
        ConfigUtil.SetSetting("ChatFontFgColor", item.Name);
      }
    }

    private void FontSize_Changed(object sender, SelectionChangedEventArgs e)
    {
      if (fontSize.SelectedItem != null)
      {
        chatBox.FontSize = (double)fontSize.SelectedItem;
        ConfigUtil.SetSetting("ChatFontSize", fontSize.SelectedItem.ToString());
      }
    }

    private void FontFamily_Changed(object sender, SelectionChangedEventArgs e)
    {
      if (fontFamily.SelectedItem != null)
      {
        var family = fontFamily.SelectedItem as FontFamily;
        chatBox.FontFamily = family;
        ConfigUtil.SetSetting("ChatFontFamily", family.ToString());
      }
    }

    private void Filter_KeyDown(TextBox filter, string text, KeyEventArgs e)
    {
      if (e.Key == Key.Escape)
      {
        if (filter.DataContext is AutoCompleteText context && context.Items.Count > 0)
        {
          context.Items.Clear();
          filter.DataContext = null;
          filter.DataContext = context;
        }

        filter.Text = text;
        filter.FontStyle = FontStyles.Italic;
        chatBox.Focus();
      }
    }

    private void ToFilter_KeyDown(object sender, KeyEventArgs e)
    {
      Filter_KeyDown(toFilter, Properties.Resources.CHAT_TO_FILTER, e);
    }
    private void FromFilter_KeyDown(object sender, KeyEventArgs e)
    {
      Filter_KeyDown(fromFilter, Properties.Resources.CHAT_FROM_FILTER, e);
    }

    private void TextFilter_KeyDown(object sender, KeyEventArgs e)
    {
      Filter_KeyDown(textFilter, Properties.Resources.CHAT_TEXT_FILTER, e);
    }

    private void Filter_GotFocus(TextBox filter, string text)
    {
      if (filter.Text == text)
      {
        if (filter.DataContext is AutoCompleteText context)
        {
          context.Items.Clear();
          context.Items.AddRange(PlayerAutoCompleteList);
          filter.DataContext = null;
          filter.DataContext = context;
        }

        filter.Text = "";
        filter.FontStyle = FontStyles.Normal;
      }
    }

    private void ToFilter_GotFocus(object sender, RoutedEventArgs e) => Filter_GotFocus(toFilter, Properties.Resources.CHAT_TO_FILTER);

    private void FromFilter_GotFocus(object sender, RoutedEventArgs e) => Filter_GotFocus(fromFilter, Properties.Resources.CHAT_FROM_FILTER);

    private void TextFilter_GotFocus(object sender, RoutedEventArgs e) => Filter_GotFocus(textFilter, Properties.Resources.CHAT_TEXT_FILTER);

    private void Filter_LostFocus(TextBox filter, string text)
    {
      if (filter.Text.Length == 0)
      {
        if (filter.DataContext is AutoCompleteText context && context.Items.Count > 0)
        {
          context.Items.Clear();
          filter.DataContext = null;
          filter.DataContext = context;
        }

        filter.Text = text;
        filter.FontStyle = FontStyles.Italic;
      }
    }

    private void Filter_TextChanged(object sender, TextChangedEventArgs e)
    {
      FilterTimer?.Stop();
      FilterTimer?.Start();
    }

    private void Chat_MouseWheel(object sender, MouseWheelEventArgs e)
    {
      if ((Keyboard.Modifiers & ModifierKeys.Control) > 0)
      {
        if (e.Delta < 0 && fontSize.SelectedIndex > 0)
        {
          fontSize.SelectedIndex--;
        }
        else if (e.Delta > 0 && fontSize.SelectedIndex < (fontSize.Items.Count - 1))
        {
          fontSize.SelectedIndex++;
        }
      }
    }

    private void Calendar_SelectedDatesChanged(object s, SelectionChangedEventArgs e)
    {
      if (calendarPopup.PlacementTarget is TextBox target)
      {
        target.Text = calendar.SelectedDate?.ToShortDateString();
        target.FontStyle = FontStyles.Normal;
        calendarPopup.IsOpen = false;
      }
    }

    private double GetEndDate()
    {
      double result = 0;

      if (DateTime.TryParse(endDate.Text, out DateTime value))
      {
        result = value.Ticks / TimeSpan.FromSeconds(1).Ticks;
      }

      return result;
    }

    private double GetStartDate()
    {
      double result = 0;

      if (DateTime.TryParse(startDate.Text, out DateTime value))
      {
        result = value.Ticks / TimeSpan.FromSeconds(1).Ticks;
      }

      return result;
    }

    private void DateTimeMouseClick(TextBox box, MouseButtonEventArgs e)
    {
      if (calendarPopup.IsOpen && calendarPopup.PlacementTarget == box)
      {
        calendarPopup.IsOpen = false;
      }
      else if (e.ClickCount == 1 && e.ChangedButton == MouseButton.Left)
      {
        calendarPopup.PlacementTarget = null;

        if (DateTime.TryParse(box.Text, out DateTime result))
        {
          calendar.SelectedDates.Clear();
          calendar.SelectedDates.Add(result);
          calendar.DisplayDate = result;
        }

        calendarPopup.PlacementTarget = box;
        calendarPopup.IsOpen = true;
      }
    }

    private void DateChooser_KeyDown(object sender, KeyEventArgs e)
    {
      if (e.Key == Key.Escape)
      {
        ResetDateText(sender);
        chatBox.Focus();
      }
      else if (e.Key == Key.Enter)
      {
        chatBox.Focus();
      }
    }

    private void DateChooser_GotFocus(object sender, RoutedEventArgs e)
    {
      var text = (sender as TextBox)?.Text;
      if (text.Contains("Date"))
      {
        (sender as TextBox).Text = "";
      }
    }

    private void DateChooser_LostFocus(object sender, RoutedEventArgs e)
    {
      var text = (sender as TextBox)?.Text;
      if (!DateTime.TryParse(text, out _))
      {
        ResetDateText(sender);
      }
    }

    private void ResetDateText(object sender)
    {
      if (startDate == sender)
      {
        startDate.Text = Properties.Resources.CHAT_START_DATE;
        startDate.FontStyle = FontStyles.Italic;
      }
      else if (endDate == sender)
      {
        endDate.Text = Properties.Resources.CHAT_END_DATE;
        endDate.FontStyle = FontStyles.Italic;
      }
    }

    private void ToFilter_LostFocus(object sender, RoutedEventArgs e) => Filter_LostFocus(toFilter, Properties.Resources.CHAT_TO_FILTER);
    private void FromFilter_LostFocus(object sender, RoutedEventArgs e) => Filter_LostFocus(fromFilter, Properties.Resources.CHAT_FROM_FILTER);
    private void TextFilter_LostFocus(object sender, RoutedEventArgs e) => Filter_LostFocus(textFilter, Properties.Resources.CHAT_TEXT_FILTER);
    private void StartDate_MouseClick(object sender, MouseButtonEventArgs e) => DateTimeMouseClick(startDate, e);
    private void EndDate_MouseClick(object sender, MouseButtonEventArgs e) => DateTimeMouseClick(endDate, e);
    private void Refresh_MouseClick(object sender, MouseButtonEventArgs e) => ChangeSearch(true);


    #region IDisposable Support
    private bool disposedValue = false; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        if (disposing)
        {
          // TODO: dispose managed state (managed objects).
        }

        ChatManager.EventsUpdatePlayer -= ChatManager_EventsUpdatePlayer;
        ChatManager.EventsNewChannels -= ChatManager_EventsNewChannels;

        RefreshTimer?.Stop();
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
