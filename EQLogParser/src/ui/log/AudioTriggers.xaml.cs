using System.ComponentModel;
using System.Windows.Controls;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for AudioTriggers.xaml
  /// </summary>
  public partial class AudioTriggers : UserControl
  {
    public AudioTriggers()
    {
      InitializeComponent();
    }

    private void sfTreeView_ItemDropping(object sender, Syncfusion.UI.Xaml.TreeView.TreeViewItemDroppingEventArgs e)
    {
      if (e.TargetNode != null && e.TargetNode.ParentNode == null)
      {
        e.Handled = true;
      }
    }
  }
}
