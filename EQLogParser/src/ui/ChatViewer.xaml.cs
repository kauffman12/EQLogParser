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
  public partial class ChatViewer : UserControl
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private const string TEXT_FILTER_DEFAULT = "Message Search";
    private const string FROM_FILTER_DEFAULT = "From";

    private static List<double> FontSizeList = new List<double>() { 10, 12, 14, 16, 18, 20, 22, 24 };
    private static List<ColorItem> ColorItems;
    private static bool Running = false;

    private DispatcherTimer FilterTimer;
    private ChatIterator CurrentIterator = null;
    private string LastChannelSelection = null;
    private string LastPlayerSelection = null;
    private string LastTextFilter = null;
    private string LastFromFilter = null;
    private bool Connected = false;
    private bool Ready = false;

    public ChatViewer()
    {
      InitializeComponent();

      ColorItems = typeof(Colors).GetProperties().
        Where(prop => prop.Name != "Transparent").
        Select(prop => new ColorItem() { Brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(prop.Name)), Name = prop.Name }).
        OrderBy(item => item.Name).ToList();

      ColorItem DefaultBackground = new ColorItem { Name = "Default", Brush = new SolidColorBrush(Color.FromRgb(32, 32, 32)) };
      ColorItem DefaultForeground = new ColorItem { Name = "Default", Brush = new SolidColorBrush(Colors.White) };

      textFilter.Text = TEXT_FILTER_DEFAULT;
      fromFilter.Text = FROM_FILTER_DEFAULT;
      fontSize.ItemsSource = FontSizeList;

      var fgList = new List<ColorItem>(ColorItems);
      fgList.Insert(0, DefaultForeground);
      fontFgColor.ItemsSource = fgList;

      var bgList = new List<ColorItem>(ColorItems);
      bgList.Insert(0, DefaultBackground);
      fontBgColor.ItemsSource = bgList;

      var playerList = ChatManager.GetArchivedPlayers();
      if (playerList.Count > 0)
      {
        players.Items.Clear();
        players.ItemsSource = playerList;

        string player = DataManager.Instance.GetApplicationSetting("ChatSelectedPlayer");
        players.SelectedIndex = (player != null && playerList.IndexOf(player) > -1) ? playerList.IndexOf(player) : 0;
      }

      string fgColor = DataManager.Instance.GetApplicationSetting("ChatFontFgColor");
      fgColor = fgColor ?? "Default";
      fontFgColor.SelectedItem = fgList.Find(item => item.Name == fgColor);

      string bgColor = DataManager.Instance.GetApplicationSetting("ChatFontBgColor");
      bgColor = bgColor ?? "Default";
      fontBgColor.SelectedItem = bgList.Find(item => item.Name == bgColor);

      string family = DataManager.Instance.GetApplicationSetting("ChatFontFamily");
      if (family != null)
      {
        fontFamily.SelectedItem = new FontFamily(family);
      }

      string size = DataManager.Instance.GetApplicationSetting("ChatFontSize");
      if (size != null && double.TryParse(size, out double dsize))
      {
        fontSize.SelectedItem = dsize;
      }

      FilterTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 500) };
      FilterTimer.Tick += (sender, e) =>
      {
        FilterTimer.Stop();
        ChangeSearch();
      };

      Ready = true;
      ChangeSearch();
    }

    private void DisplayPage(int count, bool adjustScroll = false)
    {
      if (!Running)
      {
        Running = true;

        Task.Delay(200).ContinueWith(task =>
        {
          try
          {
            var chatList = CurrentIterator.Take(count).Reverse().ToList();

            Dispatcher.InvokeAsync(() =>
            {
              try
              {
                Paragraph para = new Paragraph { Margin = new Thickness(0, 0, 0, 0), Padding = new Thickness(4, 0, 0, 0) };

                if (chatList.Count > 0)
                {
                  for (int i = 0; i < chatList.Count; i++)
                  {
                    var text = chatList[i].Line;

                    if (LastTextFilter != null && chatList[i].KeywordStart > -1)
                    {
                      var first = text.Substring(0, chatList[i].KeywordStart);
                      var second = text.Substring(chatList[i].KeywordStart, LastTextFilter.Length);
                      var last = text.Substring(chatList[i].KeywordStart + LastTextFilter.Length);

                      if (first.Length > 0)
                      {
                        para.Inlines.Add(new Run(first));
                      }

                      para.Inlines.Add(new Run { Text = second, FontStyle = FontStyles.Italic, FontWeight = FontWeights.Bold });

                      if (last.Length > 0)
                      {
                        para.Inlines.Add(new Run(last));
                      }
                    }
                    else
                    {
                      para.Inlines.Add(new Run(text));
                    }

                    if (i < chatList.Count - 1)
                    {
                      para.Inlines.Add(new Run(Environment.NewLine));
                    }
                  }

                  var blocks = (chatBox.Document as FlowDocument).Blocks;
                  if (blocks.Count == 0)
                  {
                    blocks.Add(para);
                    chatScroller.ScrollToEnd();
                    para.Padding = new Thickness(4, 0, 0, 4);
                  }
                  else
                  {
                    blocks.InsertBefore(blocks.FirstBlock, para);

                    if (adjustScroll)
                    {
                      chatScroller.ScrollToVerticalOffset(0.40 * chatScroller.ExtentHeight);
                    }
                  }
                }

                if (!Connected)
                {
                  chatScroller.ScrollChanged += Chat_ScrollChanged;
                  Connected = true;
                }
              }
              catch (Exception ex2)
              {
                LOG.Error(ex2);
              }
              finally
              {
                Running = false;
              }
  
            }, DispatcherPriority.Background);
          }
          catch (Exception ex)
          {
            LOG.Error(ex);
          }
        });
      }
    }

    private List<string> GetSelectedChannels(out bool changed)
    {
      changed = false;
      List<string> selected = new List<string>();

      StringBuilder builder = new StringBuilder();
      foreach (var item in channels.Items)
      {
        if (item is CheckedItem checkedItem && checkedItem.IsChecked)
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

    private void ChangeSearch()
    {
      if (players.SelectedItem is string name && name != "" && !name.StartsWith("No "))
      {
        var channelList = GetSelectedChannels(out bool changed);
        string text = (textFilter.Text != "" && textFilter.Text != TEXT_FILTER_DEFAULT) ? textFilter.Text : null;
        string from = (fromFilter.Text != "" && fromFilter.Text != FROM_FILTER_DEFAULT) ? fromFilter.Text : null;
        if (changed || LastPlayerSelection != name || LastTextFilter != text || LastFromFilter != from)
        {
          LastPlayerSelection = name;
          LastTextFilter = text;
          LastFromFilter = from;
          CurrentIterator?.Close();
          CurrentIterator = new ChatIterator(name, channelList, from, text);

          chatScroller.ScrollChanged -= Chat_ScrollChanged;
          Connected = false;

          chatBox.Document.Blocks.Clear();
          DisplayPage(100);
        }
      }
    }

    private void Chat_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
      if (e.VerticalChange < 0 && e.VerticalOffset / e.ExtentHeight < 0.35)
      {
        DisplayPage(15, e.VerticalChange < 0);
      }
    }

    private void Chat_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
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
      if (players.SelectedItem is string name && name != "" && !name.StartsWith("No "))
      {
        List<CheckedItem> items = new List<CheckedItem>();
        ChatManager.GetChannels(players.SelectedItem as string).ForEach(chan =>
        {
          items.Add(new CheckedItem { Text = chan, IsChecked = true });
        });

        if (items.Count > 0)
        {
          items[0].SelectedText = items.Count + " Selected Channels";
          channels.ItemsSource = items;
          channels.SelectedItem = items[0];
        }

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
          var checkedItem = item as CheckedItem;
          if (checkedItem.IsChecked)
          {
            count++;
          }
        }

        if (!(channels.SelectedItem is CheckedItem selected))
        {
          selected = channels.Items[0] as CheckedItem;
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

    private void FontBgColor_Changed(object sender, SelectionChangedEventArgs e)
    {
      if (fontBgColor.SelectedItem != null)
      {
        var item = fontBgColor.SelectedItem as ColorItem;

        chatBox.Background = item.Brush;
        DataManager.Instance.SetApplicationSetting("ChatFontBgColor", item.Name);
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

    private void FromFilter_KeyDown(object sender, KeyEventArgs e)
    {
      if (e.Key == Key.Escape)
      {
        fromFilter.Text = FROM_FILTER_DEFAULT;
        fromFilter.FontStyle = FontStyles.Italic;
        chatBox.Focus();
      }
    }

    private void FromFilter_GotFocus(object sender, RoutedEventArgs e)
    {
      if (fromFilter.Text == FROM_FILTER_DEFAULT)
      {
        fromFilter.Text = "";
        fromFilter.FontStyle = FontStyles.Normal;
      }
    }

    private void FromFilter_LostFocus(object sender, RoutedEventArgs e)
    {
      if (fromFilter.Text == "")
      {
        fromFilter.Text = FROM_FILTER_DEFAULT;
        fromFilter.FontStyle = FontStyles.Italic;
      }
    }

    private void TextFilter_KeyDown(object sender, KeyEventArgs e)
    {
      if (e.Key == Key.Escape)
      {
        textFilter.Text = TEXT_FILTER_DEFAULT;
        textFilter.FontStyle = FontStyles.Italic;
        chatBox.Focus();
      }
    }

    private void TextFilter_GotFocus(object sender, RoutedEventArgs e)
    {
      if (textFilter.Text == TEXT_FILTER_DEFAULT)
      {
        textFilter.Text = "";
        textFilter.FontStyle = FontStyles.Normal;
      }
    }

    private void TextFilter_LostFocus(object sender, RoutedEventArgs e)
    {
      if (textFilter.Text == "")
      {
        textFilter.Text = TEXT_FILTER_DEFAULT;
        textFilter.FontStyle = FontStyles.Italic;
      }
    }

    private void Filter_TextChange(object sender, TextChangedEventArgs e)
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
  }
}
