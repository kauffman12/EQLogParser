using Syncfusion.Windows.Controls.Input;
using System;
using System.Collections.Generic;
using System.Globalization;
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

    private List<string> PlayerAutoCompleteList;
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
      fontFamily.ItemsSource = fontFamily.ItemsSource = Fonts.SystemFontFamilies.OrderBy(f => f.Source).ToList();
      textFilter.Text = EQLogParser.Resource.CHAT_TEXT_FILTER;
      startDate.DateTime = new DateTime(1999, 3, 16);
      endDate.DateTime = DateTime.Now;
      toFilter.Text = EQLogParser.Resource.CHAT_TO_FILTER;
      fromFilter.Text = EQLogParser.Resource.CHAT_FROM_FILTER;

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

      UpdateCurrentTextColor();

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
            foreach (var chatType in newIterator.TakeWhile(chatType => chatType.Text != tempChat.Text).Reverse())
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
                  newItem.Inlines.Add(new Run(chatType.Text));
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

      ChatManager.EventsUpdatePlayer += ChatManagerEventsUpdatePlayer;
      ChatManager.EventsNewChannels += ChatManagerEventsNewChannels;
      (Application.Current.MainWindow as MainWindow).EventsThemeChanged += EventsThemeChanged;
    }

    private void EventsThemeChanged(object sender, string e) => UpdateCurrentTextColor();
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

    private void UpdateCurrentTextColor()
    {
      var defaultColor = (Color)Application.Current.Resources["ContentForeground.Color"];

      try
      {
        var colorSetting = "ChatFontFgColor" + MainWindow.CurrentTheme;
        var fgColor = ConfigUtil.GetSetting(colorSetting, TextFormatUtils.GetHexString(defaultColor));
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

                  var text = chatList[i].Text;

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
                  chatScroller.ScrollChanged += ChatScrollChanged;
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
      var items = new List<ComboBoxItemDetails>
      {
        new ComboBoxItemDetails { Text = EQLogParser.Resource.SELECT_ALL },
        new ComboBoxItemDetails { Text = EQLogParser.Resource.UNSELECT_ALL }
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

    private void ChangeSearch(bool force = false)
    {
      if (players.SelectedItem is string name && name.Length > 0 && !name.StartsWith("No ", StringComparison.Ordinal))
      {
        var channelList = GetSelectedChannels(out bool changed);
        string text = (textFilter.Text.Length != 0 && textFilter.Text != EQLogParser.Resource.CHAT_TEXT_FILTER) ? textFilter.Text : null;
        string to = (toFilter.Text.Length != 0 && toFilter.Text != EQLogParser.Resource.CHAT_TO_FILTER) ? toFilter.Text : null;
        string from = (fromFilter.Text.Length != 0 && fromFilter.Text != EQLogParser.Resource.CHAT_FROM_FILTER) ? fromFilter.Text : null;
        double startDateValue = GetStartDate();
        double endDateValue = GetEndDate();
        if (force || changed || LastPlayerSelection != name || LastTextFilter != text || LastToFilter != to || LastFromFilter != from ||
          LastStartDate != startDateValue || LastEndDate != endDateValue)
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

          chatScroller.ScrollChanged -= ChatScrollChanged;
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

    private void ChatScrollChanged(object sender, ScrollChangedEventArgs e)
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

    private void ChatKeyDown(object sender, KeyEventArgs e)
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

    private void PlayerChanged(object sender, SelectionChangedEventArgs e)
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

    private void ChannelsDropDownClosed(object sender, EventArgs e)
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

        selected.SelectedText = string.Format(CultureInfo.CurrentCulture, "{0} {1}", count, EQLogParser.Resource.CHANNELS_SELECTED);
        channels.SelectedIndex = -1;
        channels.SelectedItem = selected;
      }

      if (Ready)
      {
        ChangeSearch();
      }
    }

    private void FontFgColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
      chatBox.Foreground = new SolidColorBrush(colorPicker.Color);
      var colorSetting = "ChatFontFgColor" + MainWindow.CurrentTheme;
      ConfigUtil.SetSetting(colorSetting, TextFormatUtils.GetHexString(colorPicker.Color));
    }

    private void FontSizeChanged(object sender, SelectionChangedEventArgs e)
    {
      if (fontSize.SelectedItem != null)
      {
        chatBox.FontSize = (double)fontSize.SelectedItem;
        ConfigUtil.SetSetting("ChatFontSize", fontSize.SelectedItem.ToString());
      }
    }

    private void FontFamilyChanged(object sender, SelectionChangedEventArgs e)
    {
      if (fontFamily.SelectedItem != null)
      {
        var family = fontFamily.SelectedItem as FontFamily;
        chatBox.FontFamily = family;
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
        chatBox.Focus();
      }
      else if (filter is SfTextBoxExt filterExt)
      {
        filterExt.AutoCompleteSource = PlayerAutoCompleteList;
      }
    }

    private void FilterGotFocus(TextBox filter, string text)
    {
      if (filter.Text == text)
      {
        filter.Text = "";
        filter.FontStyle = FontStyles.Normal;
      }
    }

    private static void FilterLostFocus(TextBox filter, string text)
    {
      if (filter.Text.Length == 0)
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

    private void ChatMouseWheel(object sender, MouseWheelEventArgs e)
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

    private void SelectedDatesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ChangeSearch();

    private double GetEndDate()
    {
      double result = 0;

      if (endDate.DateTime != null)
      {
        result = endDate.DateTime.Value.Ticks / TimeSpan.FromSeconds(1).Ticks;
      }

      return result;
    }

    private double GetStartDate()
    {
      double result = 0;

      if (startDate.DateTime != null)
      {
        result = startDate.DateTime.Value.Ticks / TimeSpan.FromSeconds(1).Ticks;
      }

      return result;
    }

    #region IDisposable Support
    private bool disposedValue = false; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        (Application.Current.MainWindow as MainWindow).EventsThemeChanged -= EventsThemeChanged;
        ChatManager.EventsUpdatePlayer -= ChatManagerEventsUpdatePlayer;
        ChatManager.EventsNewChannels -= ChatManagerEventsNewChannels;

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
