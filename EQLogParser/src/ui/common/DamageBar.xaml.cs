using Syncfusion.UI.Xaml.ProgressBar;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  /// <summary>Interaction logic for DamageBar.xaml</summary>
  public partial class DamageBar
  {
    /*
     * Every colour on this row travels as a KEY into the application resource dictionary (Application.Current.Resources — the app's one
     * style dictionary, see docs/DesignNotes.md → "Style state lives in the application resource dictionary"), never as a brush or a colour:
     * a DynamicResource lookup follows the key when the theme or the meter's own colour changes, which is how a bar repaints without anyone
     * holding a window reference. Hence *Key on every name here — "DamageOverlayProgressBrush" is a dictionary key, not a colour, and this
     * row resolves it against whichever value the meter published last.
     */
    private readonly string _defaultProgressKey;
    private MeterColumnLayout _layout;
    private string _activeColorKey;
    private string _lastClassName;
    private bool _iconInitialized;
    private bool _miniBars;

    public DamageBar(string nameForegroundKey, string progressColorKey, bool showClassIcon)
    {
      InitializeComponent();

      _activeColorKey = _defaultProgressKey = progressColorKey;
      player.SetResourceReference(TextBlock.ForegroundProperty, nameForegroundKey);
      progress.SetResourceReference(ProgressBarBase.ProgressColorProperty, progressColorKey);
      classImage.Visibility = showClassIcon ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Draw the numeric block from the meter's shared layout: width, text and inset per column.</summary>
    internal void ApplyColumns(MeterColumnLayout layout)
    {
      _layout = layout;

      colPercent.Width = new GridLength(layout.WidthOf(MeterSlot.Percent));
      colDamage.Width = new GridLength(layout.WidthOf(MeterSlot.Damage));
      colDps.Width = new GridLength(layout.WidthOf(MeterSlot.Dps));
      colRdps.Width = new GridLength(layout.WidthOf(MeterSlot.Rdps));
      colSec.Width = new GridLength(layout.WidthOf(MeterSlot.Seconds));

      percent.Visibility = VisibilityFor(layout, MeterSlot.Percent);
      damage.Visibility = VisibilityFor(layout, MeterSlot.Damage);
      dps.Visibility = VisibilityFor(layout, MeterSlot.Dps);
      rdps.Visibility = VisibilityFor(layout, MeterSlot.Rdps);
      time.Visibility = VisibilityFor(layout, MeterSlot.Seconds);

      ApplyTextMargins();
    }

    internal void SetMiniBars(bool miniBars)
    {
      _miniBars = miniBars;
      if (miniBars)
      {
        progress.Margin = new Thickness(0, 16, 0, 0);
        classImage.Margin = new Thickness(0, 0, 0, 4);
        player.Margin = new Thickness(4, 1, 0, 2);
      }
      else
      {
        progress.Margin = new Thickness(0, 0, 0, 0);
        classImage.Margin = new Thickness(0, 2, 0, 4);
        player.Margin = new Thickness(4, 0, 0, 1);
      }

      ApplyTextMargins();
    }

    internal void Update(string playerName, string className, string percentValue, string damageValue, string dpsValue,
      string rdpsValue, string timeValue, double barPercent, string colorOverrideKey = null)
    {
      if (colorOverrideKey != null && _activeColorKey != colorOverrideKey)
      {
        progress.SetResourceReference(ProgressBarBase.ProgressColorProperty, colorOverrideKey);
        _activeColorKey = colorOverrideKey;
      }
      else if (colorOverrideKey == null && _activeColorKey != _defaultProgressKey)
      {
        progress.SetResourceReference(ProgressBarBase.ProgressColorProperty, _defaultProgressKey);
        _activeColorKey = _defaultProgressKey;
      }

      if (Visibility != Visibility.Visible)
      {
        Visibility = Visibility.Visible;
      }

      if (!_iconInitialized || _lastClassName != className)
      {
        classImage.Source = UiElementUtil.CreateAppIcon(PlayerRegistry.GetPlayerIconPath(className));
        _lastClassName = className;
        _iconInitialized = true;
      }

      player.Text = playerName;
      damage.Text = damageValue;
      dps.Text = dpsValue;
      rdps.Text = rdpsValue;
      percent.Text = percentValue;
      time.Text = timeValue;
      progress.Progress = barPercent;
    }

    // The numeric text's own margins: the vertical pair comes from the bar style, the right inset from the shared layout so a
    // word and its digits end at the same edge. Markup margins stand until the window lays the block out for the first time.
    private void ApplyTextMargins()
    {
      if (_layout is null)
      {
        return;
      }

      var top = _miniBars ? 1.0 : 0.0;
      var bottom = _miniBars ? 2.0 : 1.0;

      percent.Margin = new Thickness(0, top, _layout.InsetOf(MeterSlot.Percent), bottom);
      damage.Margin = new Thickness(0, top, _layout.InsetOf(MeterSlot.Damage), bottom);
      dps.Margin = new Thickness(0, top, _layout.InsetOf(MeterSlot.Dps), bottom);
      rdps.Margin = new Thickness(0, top, _layout.InsetOf(MeterSlot.Rdps), bottom);
      time.Margin = new Thickness(0, top, _layout.InsetOf(MeterSlot.Seconds), bottom);
    }

    private static Visibility VisibilityFor(MeterColumnLayout layout, MeterSlot slot) =>
      layout.Shows(slot) ? Visibility.Visible : Visibility.Collapsed;
  }
}
