using Syncfusion.UI.Xaml.ProgressBar;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for DamageBar.xaml
  /// </summary>
  public partial class DamageBar
  {
    private readonly string _defaultColor;
    private string _lastColor;

    public DamageBar(string foregroundColor, string progressColor, bool showClassIcon)
    {
      InitializeComponent();

      _lastColor = _defaultColor = progressColor;
      player.SetResourceReference(TextBlock.ForegroundProperty, foregroundColor);
      progress.SetResourceReference(ProgressBarBase.ProgressColorProperty, progressColor);
      classImage.Visibility = showClassIcon ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void SetMiniBars(bool miniBars)
    {
      if (miniBars)
      {
        progress.Margin = new Thickness(0, 16, 0, 0);
        classImage.Margin = new Thickness(0, 0, 0, 4);
        player.Margin = new Thickness(4, 1, 0, 2);
        damage.Margin = new Thickness(0, 1, -12, 2);
        dps.Margin = new Thickness(0, 1, -2, 2);
        time.Margin = new Thickness(0, 1, 2, 2);
      }
      else
      {
        progress.Margin = new Thickness(0, 0, 0, 0);
        classImage.Margin = new Thickness(0, 2, 0, 4);
        player.Margin = new Thickness(4, 0, 0, 1);
        damage.Margin = new Thickness(0, 0, -12, 1);
        dps.Margin = new Thickness(0, 0, -2, 1);
        time.Margin = new Thickness(0, 0, 2, 1);
      }
    }

    internal void Update(string origName, string playerName, string damageValue, string dpsValue,
      string timeValue, double barPercent, string colorOverride = null)
    {
      if (colorOverride != null && _lastColor != colorOverride)
      {
        progress.SetResourceReference(ProgressBarBase.ProgressColorProperty, colorOverride);
        _lastColor = colorOverride;
      }
      else if (colorOverride == null && _lastColor != _defaultColor)
      {
        progress.SetResourceReference(ProgressBarBase.ProgressColorProperty, _defaultColor);
        _lastColor = _defaultColor;
      }

      if (Visibility != Visibility.Visible)
      {
        Visibility = Visibility.Visible;
      }

      classImage.Source = PlayerManager.Instance.GetPlayerIcon(origName);
      player.Text = playerName;
      damage.Text = damageValue;
      dps.Text = dpsValue;
      time.Text = timeValue;
      progress.Progress = barPercent;
    }
  }
}
