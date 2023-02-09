using Syncfusion.Windows.Shared;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for DocumentViewer.xaml
  /// </summary>
  public partial class DocumentViewer : ChromelessWindow
  {
    public DocumentViewer(string path)
    {
      MainActions.SetTheme(this, MainWindow.CurrentTheme);
      InitializeComponent();
      rtfBox.LoadAsync(path);
    }

    private void ChromelessWindowClosed(object sender, System.EventArgs e)
    {
      rtfBox?.Dispose();
    }
  }
}
