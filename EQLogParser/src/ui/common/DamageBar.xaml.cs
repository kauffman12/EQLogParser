using Syncfusion.UI.Xaml.ProgressBar;
using System.Windows.Controls;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for DamageBar.xaml
  /// </summary>
  public partial class DamageBar : UserControl
  {
    public DamageBar(string foregroundColor, string progressColor, bool showClassIcon)
    {
      InitializeComponent();

      player.SetResourceReference(TextBlock.ForegroundProperty, foregroundColor);
      progress.SetResourceReference(SfLinearProgressBar.ProgressColorProperty, progressColor);
      classImage.Visibility = showClassIcon ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    internal void SetMiniBars(bool miniBars)
    {
      if (miniBars)
      {
        this.progress.Margin = new System.Windows.Thickness(0, 16, 0, 0);
        this.classImage.Margin = new System.Windows.Thickness(0, 0, 0, 4);
        this.player.Margin = new System.Windows.Thickness(4, 1, 0, 2);
        this.damage.Margin = new System.Windows.Thickness(0, 1, -12, 2);
        this.dps.Margin = new System.Windows.Thickness(0, 1, -2, 2);
        this.time.Margin = new System.Windows.Thickness(0, 1, 2, 2);
      }
      else
      {
        this.progress.Margin = new System.Windows.Thickness(0, 0, 0, 0);
        this.classImage.Margin = new System.Windows.Thickness(0, 2, 0, 4);
        this.player.Margin = new System.Windows.Thickness(4, 0, 0, 1);
        this.damage.Margin = new System.Windows.Thickness(0, 0, -12, 1);
        this.dps.Margin = new System.Windows.Thickness(0, 0, -2, 1);
        this.time.Margin = new System.Windows.Thickness(0, 0, 2, 1);
      }
    }

    internal void Update(string origName, string player, string damage, string dps, string time, double barPercent)
    {
      if (Visibility != System.Windows.Visibility.Visible)
      {
        Visibility = System.Windows.Visibility.Visible;
      }

      classImage.Source = PlayerManager.Instance.GetPlayerIcon(origName);
      this.player.Text = player;
      this.damage.Text = damage;
      this.dps.Text = dps;
      this.time.Text = time;
      progress.Progress = barPercent;
    }
  }
}
