using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EQLogParser
{
  /*
   * The FCT overlay's settings, in a window of their own. Configure mode used to lay every control on a strip inside the
   * overlay itself, which worked until the controls had opinions about the same pixels as the numbers being judged —
   * especially in a small window, where the row wrapped into the demo it existed to preview. This window takes the whole
   * property list (mode, shape, the three category placements, what shows, the threshold, and the two dials), stacks it
   * vertically with the name beside each control like every settings panel everybody already knows, and lets the overlay
   * be nothing but numbers while somebody decides.
   *
   * It is deliberately dumb: LoadFrom hands it a snapshot to display, every change raises PreviewChanged with a fresh
   * one for the live demo, Save raises Saved with the final copy for the overlay to write to settings.ini, and Cancel —
   * button, Esc or the close mark, all three the same gesture — asks the overlay to put everything back. It owns no
   * canvas and no keys; that is what keeps a second topmost window from becoming a second source of truth.
   */
  public sealed partial class FctSettingsWindow : Window
  {
    /* All three carry a full snapshot; the overlay never has to ask this window anything later. */
    internal event Action<FctConfigState> PreviewChanged;
    internal event Action<FctConfigState> Saved;
    internal event Action Cancelled;

    /* Raised when a dial is released, so the overlay can restart the demo cycle: what was just set should be on screen
     * in the next second, not whenever the loop happens to come round. */
    internal event Action DialReleased;

    private bool _loading;

    /* True once somebody drags this window by its title strip. Until then the overlay keeps it parked beside itself —
     * dragging the overlay across the screen should carry its settings panel along, but a panel the player moved on
     * purpose is theirs to keep. */
    internal bool MovedByUser { get; private set; }

    internal FctSettingsWindow()
    {
      InitializeComponent();
    }

    internal void LoadFrom(FctConfigState state)
    {
      _loading = true;

      SelectByTag(modeCombo, state.Fountain ? "fountain" : "split");
      SelectByTag(shapeCombo, FctOverlaySettings.Name(state.Shape));
      SelectByTag(healSideCombo, Side(state.HealSide));
      SelectByTag(healDirCombo, Up(state.HealUp));
      SelectByTag(takenSideCombo, Side(state.TakenSide));
      SelectByTag(inDirCombo, Up(state.TakenUp));
      SelectByTag(dealtSideCombo, Side(state.DealtSide));
      SelectByTag(outDirCombo, Up(state.DealtUp));
      showDealtCheck.IsChecked = state.ShowDealt;
      showTakenCheck.IsChecked = state.ShowTaken;
      showHealsCheck.IsChecked = state.ShowHeals;
      SelectByTag(thresholdCombo, ((int)state.Threshold).ToString(CultureInfo.InvariantCulture));
      SelectByTag(labelSideCombo, LabelName(state.LabelSide));
      sizeSlider.Value = FctScale.PercentOfSize(state.TextScale);
      speedSlider.Value = FctScale.PercentOfSpeed(state.Speed);
      sampleCheck.IsChecked = state.SampleData;

      _loading = false;

      /* One pass with the guard down: mode visibility, the legend sentence and the dial readouts all derive from the
         controls, so they get computed exactly the way a user change would compute them. */
      ApplyModeVisibility();
      UpdateReadouts();
      PreviewChanged?.Invoke(Snapshot());
    }

    internal void SetStats(string stats) => statsText.Text = stats;

    private FctConfigState Snapshot()
    {
      var state = new FctConfigState
      {
        Fountain = ComboTag(modeCombo) == "fountain",
        Shape = ComboTag(shapeCombo) == "straight" ? FctMotionStyle.Straight : FctMotionStyle.Parabola,
        HealSide = ComboTag(healSideCombo) == "right" ? FctRegionSide.Right : FctRegionSide.Left,
        HealUp = ComboTag(healDirCombo) == "up",
        TakenSide = ComboTag(takenSideCombo) == "right" ? FctRegionSide.Right : FctRegionSide.Left,
        TakenUp = ComboTag(inDirCombo) == "up",
        DealtSide = ComboTag(dealtSideCombo) == "right" ? FctRegionSide.Right : FctRegionSide.Left,
        DealtUp = ComboTag(outDirCombo) == "up",
        Threshold = double.TryParse(ComboTag(thresholdCombo), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : 0,
        ShowDealt = showDealtCheck.IsChecked == true,
        ShowTaken = showTakenCheck.IsChecked == true,
        ShowHeals = showHealsCheck.IsChecked == true,
        LabelSide = ComboTag(labelSideCombo) switch
        {
          "left" => FctLabelSide.Left,
          "right" => FctLabelSide.Right,
          _ => FctLabelSide.Below,
        },
        TextScale = FctScale.SizeFromPercent((int)Math.Round(sizeSlider.Value)),
        Speed = FctScale.SpeedFromPercent((int)Math.Round(speedSlider.Value)),
        SampleData = sampleCheck.IsChecked == true,
      };

      return state;
    }

    /*
     * One handler for every pick. The guard matters twice over: LoadFrom writes the controls with it up so restoring a
     * snapshot cannot announce itself as an edit, and WPF raises SelectionChanged during initialisation of the very
     * first layout too — before anything exists to preview.
     */
    private void ControlChanged(object sender, RoutedEventArgs e)
    {
      if (_loading || !IsLoaded)
      {
        return;
      }

      if (sender == modeCombo)
      {
        ApplyModeVisibility();
      }

      UpdateLegend();
      PreviewChanged?.Invoke(Snapshot());
    }

    private void ScaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
      if (_loading || !IsLoaded)
      {
        return;
      }

      UpdateReadouts();
      PreviewChanged?.Invoke(Snapshot());
    }

    /* Double-click returns a dial to the shipped middle; letting go of either restarts the demo cycle up top. */
    private void SliderReleased(object sender, MouseButtonEventArgs e)
    {
      if (e.ClickCount == 2)
      {
        if (sender == sizeSlider)
        {
          sizeSlider.Value = FctScale.SpeedPercentDefault;
        }
        else if (sender == speedSlider)
        {
          speedSlider.Value = FctScale.SpeedPercentDefault;
        }

        UpdateReadouts();
        PreviewChanged?.Invoke(Snapshot());
      }

      DialReleased?.Invoke();
    }

    private void SaveClick(object sender, RoutedEventArgs e) => Saved?.Invoke(Snapshot());

    private void CancelClick(object sender, RoutedEventArgs e) => Cancelled?.Invoke();

    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
      if (e.Key == Key.Escape)
      {
        Cancelled?.Invoke();
      }
    }

    private void HeaderDrag(object sender, MouseButtonEventArgs e)
    {
      if (e.ButtonState != MouseButtonState.Pressed)
      {
        return;
      }

      MovedByUser = true;
      try
      {
        DragMove();
      }
      catch (InvalidOperationException)
      {
        // DragMove throws when the press was already swallowed (double-click); nothing to move, nothing to say.
      }
    }

    /*
     * Fountain shows exactly three things — two travel directions among these controls, plus shows and the dials lower
     * down — because "the UI isn't complex" was the whole point of the mode. Shape (spray is not a choice), healing's
     * column (bands has none to hand out) and the threshold step off; the damage rows stay but shed their side picks.
     * Collapsed, never disabled: a control that changes nothing in the mode you are in is what configure mode was
     * cleaned up to stop showing at all.
     */
    private void ApplyModeVisibility()
    {
      var fountain = ComboTag(modeCombo) == "fountain";

      shapeTitle.Visibility = fountain ? Visibility.Collapsed : Visibility.Visible;
      shapeCombo.Visibility = fountain ? Visibility.Collapsed : Visibility.Visible;
      healsTitle.Visibility = fountain ? Visibility.Collapsed : Visibility.Visible;
      healsRow.Visibility = fountain ? Visibility.Collapsed : Visibility.Visible;
      takenSideCombo.Visibility = dealtSideCombo.Visibility = fountain ? Visibility.Collapsed : Visibility.Visible;
      thresholdTitle.Visibility = thresholdCombo.Visibility = fountain ? Visibility.Collapsed : Visibility.Visible;
    }

    /* The legend reads straight off the picks: split names each category's column, fountain's columns are fixed so its
     * story is which way each stream runs. */
    private void UpdateLegend()
    {
      if (ComboTag(modeCombo) == "fountain")
      {
        legendText.Text = $"on you {(ComboTag(inDirCombo) == "up" ? "↑" : "↓")}   mine {(ComboTag(outDirCombo) == "up" ? "↑" : "↓")}";
        return;
      }

      legendText.Text = $"heals {SideArrow(healSideCombo)}   on me {SideArrow(takenSideCombo)}   mine {SideArrow(dealtSideCombo)}";
    }

    private void UpdateReadouts()
    {
      sizeValue.Text = Signed((int)Math.Round(sizeSlider.Value));
      speedValue.Text = Signed((int)Math.Round(speedSlider.Value));
    }

    private static string Signed(int percent) => percent == 0 ? "—" : $"{percent:+0;-0}%";

    private static string LabelName(FctLabelSide side) =>
      side switch
      {
        FctLabelSide.Left => "left",
        FctLabelSide.Right => "right",
        _ => "below",
      };

    private static string Side(FctRegionSide side) => side is FctRegionSide.Right ? "right" : "left";

    private static string Up(bool up) => up ? "up" : "down";

    private static string SideArrow(ComboBox combo) => ComboTag(combo) == "right" ? "→" : "←";

    private static string ComboTag(ComboBox combo) => combo.SelectedItem is ComboBoxItem item ? item.Tag as string : null;

    /* A combo's items name themselves with Tag; an unknown stored value lands on the first item rather than an empty box. */
    private static void SelectByTag(ComboBox combo, string tag)
    {
      for (var i = 0; i < combo.Items.Count; i++)
      {
        if (combo.Items[i] is ComboBoxItem item && item.Tag as string == tag)
        {
          combo.SelectedIndex = i;
          return;
        }
      }

      combo.SelectedIndex = 0;
    }
  }
}
