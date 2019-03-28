using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for ChatViewer.xaml
  /// </summary>
  public partial class ChatViewer : UserControl
  {
    public ChatViewer()
    {
      InitializeComponent();

      Task.Run(() =>
      {
        StringBuilder builder = new StringBuilder();
        ChatManager.GetRecent().ForEach(chatLine => builder.AppendLine(chatLine.Line));
        Dispatcher.InvokeAsync(() =>
        {
          chatBox.AppendText(builder.ToString());
          chatScroller.ScrollToEnd();
        },
        DispatcherPriority.Background
        );
      });
    }
  }
}
