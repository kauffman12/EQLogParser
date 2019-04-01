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

    private const string TEXT_FILTER_DEFAULT = "Keyword Search";
    private static List<double> FontSizeList = new List<double>() { 10, 12, 14, 16, 18, 20, 22, 24 };
    private static List<ColorItem> ColorItems;
    private static bool Running = false;

    private DispatcherTimer TextFilterTimer;
    private ChatIterator CurrentIterator = null;
    private string LastChannelSelection = null;
    private string LastPlayerSelection = null;
    private string LastTextFilter = null;
    private bool Connected = false;
    private bool Ready = false;

    public ChatViewer()
    {
      InitializeComponent();

      ColorItems = typeof(Colors).GetProperties().
        Select(prop => new ColorItem() { Brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(prop.Name)), Name = prop.Name }).
        OrderBy(item => item.Name).ToList();

      textFilter.Text = TEXT_FILTER_DEFAULT;
      fontSize.ItemsSource = FontSizeList;
      fontColor.ItemsSource = ColorItems;

      var playerList = ChatManager.GetArchivedPlayers();
      if (playerList.Count > 0)
      {
        players.Items.Clear();
        players.ItemsSource = playerList;

        string player = DataManager.Instance.GetApplicationSetting("ChatSelectedPlayer");
        players.SelectedIndex = (player != null && playerList.IndexOf(player) > -1) ? playerList.IndexOf(player) : 0;
      }

      string color = DataManager.Instance.GetApplicationSetting("ChatFontColor");
      if (color != null)
      {
        fontColor.SelectedItem = ColorItems.Find(item => item.Name == color);
      }

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

      TextFilterTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 500) };
      TextFilterTimer.Tick += (sender, e) =>
      {
        TextFilterTimer.Stop();
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
            var page = CurrentIterator.Take(count);

            Dispatcher.InvokeAsync(() =>
            {
              Paragraph para = new Paragraph { Margin = new Thickness(0) };

              var chatList = page.Reverse().ToList();
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

                    para.Inlines.Add(new Run { Text = second, Foreground = chatBox.Background, Background = chatBox.Foreground });

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

            }, DispatcherPriority.Background);

            Running = false;
          }
          catch (Exception ex)
          {
            LOG.Debug(ex);
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
        if (changed || LastPlayerSelection != name || LastTextFilter != text)
        {
          LastPlayerSelection = name;
          LastTextFilter = text;
          CurrentIterator?.Close();
          CurrentIterator = new ChatIterator(name, channelList, text);

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

    private void FontColor_Changed(object sender, SelectionChangedEventArgs e)
    {
      if (fontColor.SelectedItem != null)
      {
        var item = fontColor.SelectedItem as ColorItem;

        List<Run> highlighted = new List<Run>();
        foreach (var block in chatBox.Document.Blocks)
        {
          if (block is Paragraph para)
          {
            foreach (var inline in para.Inlines)
            {
              if (inline.Foreground != null && inline.Foreground != chatBox.Foreground)
              {
                highlighted.Add(inline as Run);
              }
            }
          }
        }

        chatBox.Foreground = item.Brush;
        DataManager.Instance.SetApplicationSetting("ChatFontColor", item.Name);

        highlighted.ForEach(run =>
        {
          run.Foreground = chatBox.Background;
          run.Background = chatBox.Foreground;
        });
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

    private void TextFilter_TextChange(object sender, TextChangedEventArgs e)
    {
      TextFilterTimer?.Stop();
      TextFilterTimer?.Start();
    }
  }
}
