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

    private const string END_DATE_DEFAULT = "End Date";
    private const string START_DATE_DEFAULT = "Start Date";
    private const string TEXT_FILTER_DEFAULT = "Message Search";
    private const string FROM_FILTER_DEFAULT = "From";
    private const string TO_FILTER_DEFAULT = "To";

    private static List<double> FontSizeList = new List<double>() { 10, 12, 14, 16, 18, 20, 22, 24 };
    private static List<ColorItem> ColorItems;
    private static bool Running = false;

    private List<string> PlayerAutoCompleteList = new List<string>();
    private Paragraph MainParagraph;
    private DispatcherTimer FilterTimer;
    private DispatcherTimer RefreshTimer;
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
    private bool Ready = false;

    public ChatViewer()
    {
      InitializeComponent();

      ColorItems = typeof(Colors).GetProperties().
        Where(prop => prop.Name != "Transparent").
        Select(prop => new ColorItem() { Brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(prop.Name)), Name = prop.Name }).
        OrderBy(item => item.Name).ToList();

      ColorItem DefaultForeground = new ColorItem { Name = "Default", Brush = new SolidColorBrush(Colors.White) };

      fontSize.ItemsSource = FontSizeList;
      startDate.Text = START_DATE_DEFAULT;
      endDate.Text = END_DATE_DEFAULT;
      textFilter.Text = TEXT_FILTER_DEFAULT;

      toFilter.DataContext = new AutoCompleteText() { Text = TO_FILTER_DEFAULT, Items = PlayerAutoCompleteList };
      fromFilter.DataContext = new AutoCompleteText() { Text = FROM_FILTER_DEFAULT, Items = PlayerAutoCompleteList };

      var fgList = new List<ColorItem>(ColorItems);
      fgList.Insert(0, DefaultForeground);
      fontFgColor.ItemsSource = fgList;

      string fgColor = DataManager.Instance.GetApplicationSetting("ChatFontFgColor");
      fgColor = fgColor ?? "Default";
      fontFgColor.SelectedItem = fgList.Find(item => item.Name == fgColor);

      string family = DataManager.Instance.GetApplicationSetting("ChatFontFamily");
      if (family != null)
      {
        fontFamily.SelectedItem = new FontFamily(family);
      }
      else
      {
        fontFamily.SelectedItem = chatBox.FontFamily;
      }

      string size = DataManager.Instance.GetApplicationSetting("ChatFontSize");
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
              }, DispatcherPriority.Background);
            }

            Dispatcher.Invoke(() =>
            {
              RefreshTimer.Stop();
            });
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
        }, DispatcherPriority.Background);
    }

    private void ChatManager_EventsUpdatePlayer(object sender, string player)
    {
      LoadPlayers(player);
    }

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

    private void LoadChannels(string player)
    {
      List<ChannelDetails> items = new List<ChannelDetails>();
      int selectedCount = 0;
      ChatManager.GetChannels(player).ForEach(chan =>
      {
        selectedCount += chan.IsChecked ? 1 : 0;
        items.Add(chan);
      });

      items[0].SelectedText = selectedCount + " Selected Channels";
      channels.ItemsSource = items;
      channels.SelectedItem = items[0];
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

              string player = DataManager.Instance.GetApplicationSetting("ChatSelectedPlayer");
              if (player == null && DataManager.Instance.PlayerName != null && DataManager.Instance.ServerName != null)
              {
                player = DataManager.Instance.PlayerName + "." + DataManager.Instance.ServerName;
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
        }, DispatcherPriority.Background);
    }

    private List<string> GetSelectedChannels(out bool changed)
    {
      changed = false;
      List<string> selected = new List<string>();

      StringBuilder builder = new StringBuilder();
      foreach (var item in channels.Items)
      {
        if (item is ChannelDetails checkedItem && checkedItem.IsChecked)
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
        string text = (textFilter.Text.Length != 0 && textFilter.Text != TEXT_FILTER_DEFAULT) ? textFilter.Text : null;
        string to = (toFilter.Text.Length != 0 && toFilter.Text != TO_FILTER_DEFAULT) ? toFilter.Text : null;
        string from = (fromFilter.Text.Length != 0 && fromFilter.Text != FROM_FILTER_DEFAULT) ? fromFilter.Text : null;
        double startDate = GetStartDate();
        double endDate = GetEndDate();
        if (refresh || changed || LastPlayerSelection != name || LastTextFilter != text || LastToFilter != to || LastFromFilter != from || LastStartDate != startDate || LastEndDate != endDate)
        {
          CurrentChatFilter = new ChatFilter(name, channelList, startDate, endDate, to, from, text);
          CurrentIterator?.Close();
          CurrentIterator = new ChatIterator(name, CurrentChatFilter);
          CurrentLineCount = 0;
          RefreshTimer.Stop();
          LastPlayerSelection = name;
          LastTextFilter = text;
          LastToFilter = to;
          LastFromFilter = from;
          LastStartDate = startDate;
          LastEndDate = endDate;

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
        DataManager.Instance.SetApplicationSetting("ChatSelectedPlayer", name);

        if (Ready)
        {
          ChangeSearch();
        }
      }
    }

    private void Channels_DropDownClosed(object sender, EventArgs e)
    {
      if (channels.Items.Count > 0)
      {
        int count = 0;
        foreach (var item in channels.Items)
        {
          var checkedItem = item as ChannelDetails;
          if (checkedItem.IsChecked)
          {
            count++;
          }
        }

        if (!(channels.SelectedItem is ChannelDetails selected))
        {
          selected = channels.Items[0] as ChannelDetails;
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
        DataManager.Instance.SetApplicationSetting("ChatFontFgColor", item.Name);
      }
    }

    private void FontSize_Changed(object sender, SelectionChangedEventArgs e)
    {
      if (fontSize.SelectedItem != null)
      {
        chatBox.FontSize = (double)fontSize.SelectedItem;
        DataManager.Instance.SetApplicationSetting("ChatFontSize", fontSize.SelectedItem.ToString());
      }
    }

    private void FontFamily_Changed(object sender, SelectionChangedEventArgs e)
    {
      if (fontFamily.SelectedItem != null)
      {
        var family = fontFamily.SelectedItem as FontFamily;
        chatBox.FontFamily = family;
        DataManager.Instance.SetApplicationSetting("ChatFontFamily", family.ToString());
      }
    }

    private void Filter_KeyDown(TextBox filter, string text, KeyEventArgs e)
    {
      if (e.Key == Key.Escape)
      {
        if (filter.DataContext is AutoCompleteText context && context.Items.Count > 0)
        {
          context.Items = new List<string>();
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
      Filter_KeyDown(toFilter, TO_FILTER_DEFAULT, e);
    }
    private void FromFilter_KeyDown(object sender, KeyEventArgs e)
    {
      Filter_KeyDown(fromFilter, FROM_FILTER_DEFAULT, e);
    }

    private void TextFilter_KeyDown(object sender, KeyEventArgs e)
    {
      Filter_KeyDown(textFilter, TEXT_FILTER_DEFAULT, e);
    }

    private void Filter_GotFocus(TextBox filter, string text)
    {
      if (filter.Text == text)
      {
        if (filter.DataContext is AutoCompleteText context)
        {
          context.Items = PlayerAutoCompleteList;
          filter.DataContext = null;
          filter.DataContext = context;
        }

        filter.Text = "";
        filter.FontStyle = FontStyles.Normal;
      }
    }

    private void ToFilter_GotFocus(object sender, RoutedEventArgs e) => Filter_GotFocus(toFilter, TO_FILTER_DEFAULT);

    private void FromFilter_GotFocus(object sender, RoutedEventArgs e) => Filter_GotFocus(fromFilter, FROM_FILTER_DEFAULT);

    private void TextFilter_GotFocus(object sender, RoutedEventArgs e) => Filter_GotFocus(textFilter, TEXT_FILTER_DEFAULT);

    private void Filter_LostFocus(TextBox filter, string text)
    {
      if (filter.Text.Length == 0)
      {
        if (filter.DataContext is AutoCompleteText context && context.Items.Count > 0)
        {
          context.Items = new List<string>();
          filter.DataContext = null;
          filter.DataContext = context;
        }

        filter.Text = text;
        filter.FontStyle = FontStyles.Italic;
      }
    }

    private void ToFilter_LostFocus(object sender, RoutedEventArgs e)
    {
      Filter_LostFocus(toFilter, TO_FILTER_DEFAULT);
    }

    private void FromFilter_LostFocus(object sender, RoutedEventArgs e)
    {
      Filter_LostFocus(fromFilter, FROM_FILTER_DEFAULT);
    }

    private void TextFilter_LostFocus(object sender, RoutedEventArgs e)
    {
      Filter_LostFocus(textFilter, TEXT_FILTER_DEFAULT);
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

    private void StartDate_MouseClick(object sender, MouseButtonEventArgs e)
    {
      DateTimeMouseClick(startDate, e);
    }

    private void EndDate_MouseClick(object sender, MouseButtonEventArgs e)
    {
      DateTimeMouseClick(endDate, e);
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
        startDate.Text = START_DATE_DEFAULT;
        startDate.FontStyle = FontStyles.Italic;
      }
      else if (endDate == sender)
      {
        endDate.Text = END_DATE_DEFAULT;
        endDate.FontStyle = FontStyles.Italic;
      }
    }
    private void Refresh_MouseClick(object sender, MouseButtonEventArgs e)
    {
      ChangeSearch(true);
    }

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

        RefreshTimer.Stop();
        FilterTimer.Stop();
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
