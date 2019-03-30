using System;
using System.Collections.Generic;
using System.Linq;
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
    private bool Connected = false;

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
    }

    private void DisplayPage(int count)
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
            for (int i=0; i<chatList.Count; i++)
            {
              var text = (i == (chatList.Count - 1)) ? chatList[i].Line : (chatList[i].Line + Environment.NewLine);
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
              chatScroller.ScrollToVerticalOffset(0.40 * chatScroller.ExtentHeight);
            }
          }, DispatcherPriority.Background);

          Dispatcher.InvokeAsync(() =>
          {
            if (!Connected)
            {
              chatScroller.ScrollChanged += Chat_ScrollChanged;
              Connected = true;
            }
          });

          Running = false;
        });
      }
    }

    private void Chat_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
      if (e.VerticalChange < 0 && e.VerticalOffset / e.ExtentHeight < 0.35)
      {
        DisplayPage(15);
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
        CurrentIterator?.Close();
        CurrentIterator = new ChatIterator(name);

        chatScroller.ScrollChanged -= Chat_ScrollChanged;
        Connected = false;

        DataManager.Instance.SetApplicationSetting("ChatSelectedPlayer", name);
        chatBox.Document.Blocks.Clear();
        DisplayPage(100);
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
