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
    private static List<double> FontSizeList = new List<double>() { 10, 12, 14, 16, 18, 20, 22, 24 };
    private static List<ColorItem> ColorItems;
    private static bool Running = false;

    private ChatIterator CurrentIterator = null;
    private string LastChannelSelection = null;
    private string LastPlayerSelection = null;
    private bool Connected = false;
    private bool Ready = false;

    public ChatViewer()
    {
      InitializeComponent();

      ColorItems = typeof(Colors).GetProperties().
        Select(prop => new ColorItem() { Brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(prop.Name)), Name = prop.Name }).
        OrderBy(item => item.Name).ToList();

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

      List<CheckedItem> items = new List<CheckedItem>();
      DataManager.Instance.GetChannels().ForEach(chan =>
      {
        items.Add(new CheckedItem { Text = chan, IsChecked = true });
      });

      if (items.Count > 0)
      {
        items[0].SelectedText = items.Count + " Selected Channels";
        channels.ItemsSource = items;
        channels.SelectedItem = items[0];
      }

      Ready = true;
      ChangeSearch();
    }

    private void DisplayPage(int count, bool adjustScroll = false)
    {
      if (!Running)
      {
        Running = true;

        Task.Run(() =>
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
                var text = (i == (chatList.Count - 1)) ? chatList[i] : (chatList[i] + Environment.NewLine);
                para.Inlines.Add(new Run { Text = text });
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
        });
      }
    }

    private List<string> GetSelectedChannels(out bool changed)
    {
      changed = false;
      List<string> selected = new List<string>();

      StringBuilder builder = new StringBuilder();
      foreach(var item in channels.Items)
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
        if (changed || LastPlayerSelection != name)
        {
          LastPlayerSelection = name;
          CurrentIterator?.Close();
          CurrentIterator = new ChatIterator(name, channelList);

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
      if (Ready)
      {
        if (players.SelectedItem is string name && name != "" && !name.StartsWith("No "))
        {
          DataManager.Instance.SetApplicationSetting("ChatSelectedPlayer", name);
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

        var selected = channels.SelectedItem as CheckedItem;
        if (selected == null)
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
        chatBox.Foreground = item.Brush;
        DataManager.Instance.SetApplicationSetting("ChatFontColor", item.Name);
      }
    }

    private void FontSize_Changed(object sender, SelectionChangedEventArgs e)
    {
      if (fontSize.SelectedItem != null)
      {
        chatBox.FontSize = (double) fontSize.SelectedItem;
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
