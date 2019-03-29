using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for ChatViewer.xaml
  /// </summary>
  public partial class ChatViewer : UserControl
  {
    private ChatIterator CurrentIterator = null;
    private bool Ready = false;
    private static bool Running = false;

    public ChatViewer()
    {
      InitializeComponent();

      CurrentIterator = new ChatIterator(DataManager.Instance.PlayerName);
      DisplayPage(100);
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
            Paragraph para = new Paragraph
            {
              FontSize = 14,
              Margin = new System.Windows.Thickness(0),
              Foreground = new SolidColorBrush(Colors.Orange)
            };

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
            if (!Ready)
            {
              Ready = true;
              chatScroller.ScrollChanged += Chat_ScrollChanged;
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
      if (e.Key == System.Windows.Input.Key.PageDown)
      {
        var offset = Math.Min(chatScroller.ExtentHeight, chatScroller.VerticalOffset + chatScroller.ViewportHeight);
        chatScroller.ScrollToVerticalOffset(offset);
      }
      else if (e.Key == System.Windows.Input.Key.PageUp)
      {
        var offset = Math.Max(0, chatScroller.VerticalOffset - chatScroller.ViewportHeight);
        chatScroller.ScrollToVerticalOffset(offset);
      }
    }
  }
}
