using System;
using System.Collections.Generic;
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
   * one for the live demo, Save raises Saved with the final copy for the overlay to write to settings.ini, and Cancel
   * asks the overlay to put everything back — the two labeled buttons are the only exits, keyboard included. It owns no canvas and no keys;
   * that is what keeps a second topmost window from becoming a second source of truth.
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

    /* The "show" dropdown's four categories, in the app's checkbox-in-a-combo pattern (the same template the breakdown
       column pickers use). They live here as fields because both directions need them: LoadFrom writes the checks and
       Snapshot reads them back, and the combo's title is re-summarised whenever the dropdown closes. */
    private readonly List<ComboBoxItemDetails> _showItems =
    [
      new(true, "damage in"),
      new(true, "damage out"),
      new(true, "healing"),
      new(true, "procs"),
    ];

    // The shipped picks, used only as the parse fallback when a combo somehow has nothing selected.
    private static readonly FctConfigState Defaults = new();

    private bool _loading;

    /* True once somebody drags this window by its title strip. Until then the overlay keeps it parked beside itself —
     * dragging the overlay across the screen should carry its settings panel along, but a panel the player moved on
     * purpose is theirs to keep. */
    internal bool MovedByUser { get; private set; }

    internal FctSettingsWindow()
    {
      /* The same ritual every other window in the app performs (QuickShareWindow, DamageOverlayWindow, the dialogs):
         SfSkinManager must stamp this window with the active theme before any content exists, or its ComboBoxes and
         numeric spinner draw as bare WPF instead of the skin everything else wears. */
      ThemeConfig.SetCurrentTheme(this);
      InitializeComponent();
      showCombo.ItemsSource = _showItems;

      /* The brushes and EQDescriptionSize come from application resources and swap themselves when the theme changes;
         what does not follow on its own is this window's own SfSkinManager stamp, so it gets re-stamped. The unsubscribe
         on Closed matters: the static theme event outlives every window that listens to it. */
      ThemeConfig.EventsThemeChanged += EventsThemeChanged;
      Closed += (_, _) => ThemeConfig.EventsThemeChanged -= EventsThemeChanged;
    }

    private void EventsThemeChanged(string _) => ThemeConfig.SetCurrentTheme(this);

    internal void LoadFrom(FctConfigState state)
    {
      _loading = true;

      SelectByTag(modeCombo, state.Fountain ? "fountain" : "split");

      /* Each mode reads its shape from its own combo, so both get selected — the hidden one to its mode's default,
       * which is also what a mode switch mid-configure will offer first. */
      SelectByTag(shapeCombo, !state.Fountain && state.Shape is FctMotionStyle.Straight ? "line" : "parabola");
      SelectByTag(sprayCombo, state.Fountain && state.Shape is FctMotionStyle.Hold ? "hold" : "spray");
      SelectByTag(healLaneCombo, FctRailLanes.Token(state.HealLane));
      SelectByTag(healDirCombo, Up(state.HealUp));
      SelectByTag(takenLaneCombo, FctRailLanes.Token(state.TakenLane));
      SelectByTag(inDirCombo, Up(state.TakenUp));
      SelectByTag(dealtLaneCombo, FctRailLanes.Token(state.DealtLane));
      SelectByTag(outDirCombo, Up(state.DealtUp));
      _showItems[0].IsChecked = state.ShowTaken;
      _showItems[1].IsChecked = state.ShowDealt;
      _showItems[2].IsChecked = state.ShowHeals;
      _showItems[3].IsChecked = state.ShowProcs;
      UpdateShowTitle();
      thresholdUpDown.Value = state.Threshold;
      SelectByTag(labelSideCombo, LabelName(state.LabelSide));
      sizeSlider.Value = FctScale.PercentOfSize(state.TextScale);
      critSlider.Value = FctScale.PercentOfSize(state.CritScale);
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
      var fountain = ComboTag(modeCombo) == "fountain";
      var state = new FctConfigState
      {
        Fountain = fountain,
        // the panels' words are line and settle; the engine's older names (Straight, Hold) ride in the Tags —
        // and settle is fountain's alone: a scroll that parks mid-column breaks the chain a column exists to be
        Shape = fountain
          ? ComboTag(sprayCombo) == "hold" ? FctMotionStyle.Hold : FctMotionStyle.Spray
          : ComboTag(shapeCombo) == "line" ? FctMotionStyle.Straight : FctMotionStyle.Parabola,
        HealLane = FctRailLanes.Parse(ComboTag(healLaneCombo), Defaults.HealLane),
        HealUp = ComboTag(healDirCombo) == "up",
        TakenLane = FctRailLanes.Parse(ComboTag(takenLaneCombo), Defaults.TakenLane),
        TakenUp = ComboTag(inDirCombo) == "up",
        DealtLane = FctRailLanes.Parse(ComboTag(dealtLaneCombo), Defaults.DealtLane),
        DealtUp = ComboTag(outDirCombo) == "up",
        Threshold = Math.Clamp(Math.Round(thresholdUpDown.Value ?? 0d), 0, FctOverlaySettings.ThresholdMax),
        ShowTaken = _showItems[0].IsChecked,
        ShowDealt = _showItems[1].IsChecked,
        ShowHeals = _showItems[2].IsChecked,
        ShowProcs = _showItems[3].IsChecked,
        LabelSide = ComboTag(labelSideCombo) switch
        {
          "left" => FctLabelSide.Left,
          "right" => FctLabelSide.Right,
          _ => FctLabelSide.Below,
        },
        TextScale = FctScale.SizeFromPercent((int)Math.Round(sizeSlider.Value)),
        CritScale = FctScale.SizeFromPercent((int)Math.Round(critSlider.Value)),
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

    /* The trigger grid's numeric editor: Value follows both typing and the arrows, committing as it goes — which the
       preview design already assumes, since nothing is real until Save. */
    private void ThresholdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
      if (_loading || !IsLoaded)
      {
        return;
      }

      PreviewChanged?.Invoke(Snapshot());
    }

    /* Closing the dropdown is the commit — title re-summarised, snapshot out. Checking a box inside an open dropdown
       previews nothing on purpose: mid-click states are not decisions. */
    private void ShowDropDownClosed(object sender, EventArgs e)
    {
      if (_loading)
      {
        return;
      }

      UpdateShowTitle();
      PreviewChanged?.Invoke(Snapshot());
    }

    /* The combo's closed face counts what is on, in the app's own words — the same summariser the column pickers use. */
    private void UpdateShowTitle() => UiElementUtil.SetComboBoxTitle(showCombo, "categories");

    /* Double-click returns a dial to the shipped middle; letting go of either restarts the demo cycle up top. */
    private void SliderReleased(object sender, MouseButtonEventArgs e)
    {
      if (e.ClickCount == 2)
      {
        if (sender == sizeSlider)
        {
          sizeSlider.Value = 0;                                  // the normal dial's middle is nothing
        }
        else if (sender == critSlider)
        {
          critSlider.Value = (FctScale.CritSizeDefault - 1) * 100; // and the crit dial's is its shipped +10 %
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
     * Fountain keeps the always-applies block untouched — shows, label, threshold and dials are statements about
     * numbers, not layout, and the engine never treated them otherwise — and thins the layout block to what it can
     * actually obey: healing's column (bands has none to hand out) and both side picks step off, and the shape picker
     * swaps lists instead of disappearing — fountain shapes are spray and settle, never a rail it would only degrade.
     * Collapsed, never disabled: a control that changes nothing in the mode you are in is what configure mode was
     * cleaned up to stop showing at all.
     */
    private void ApplyModeVisibility()
    {
      var fountain = ComboTag(modeCombo) == "fountain";

      shapeCombo.Visibility = fountain ? Visibility.Collapsed : Visibility.Visible;
      sprayCombo.Visibility = fountain ? Visibility.Visible : Visibility.Collapsed;
      healsTitle.Visibility = fountain ? Visibility.Collapsed : Visibility.Visible;
      healsRow.Visibility = fountain ? Visibility.Collapsed : Visibility.Visible;
      takenLaneCombo.Visibility = dealtLaneCombo.Visibility = fountain ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateReadouts()
    {
      sizeValue.Text = Signed((int)Math.Round(sizeSlider.Value));
      critValue.Text = Signed((int)Math.Round(critSlider.Value));
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

    private static string Up(bool up) => up ? "up" : "down";

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