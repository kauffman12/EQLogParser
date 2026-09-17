using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Syncfusion.Windows.Shared;

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

    /* Raised as (left, top, width, height) when a position field commits anything — typing or spinning. The overlay applies it
     * to its own window at once: these fields are the mouse's job with numbers instead of a drag, so a change is live like
     * every other control here, and Cancelled is how it walks back (the overlay restores where configure found the window). */
    internal event Action<double, double, double, double> GeometryEdited;

    /*
     * The "show" dropdown, in the app's checkbox-in-a-combo pattern (the same template the breakdown column pickers use):
     * nine rows of numbers and eight event words sharing one combo rather than earning one each, because they answer one
     * question — "what may draw" — and two dropdowns holding half of it each would be panel furniture with an opinion of its
     * own. What shows is alphabetical, because this is a lookup list: the earlier arrangement by narrative (categories, then
     * quiet defensive words, then the loud pair) read like a fight and made finding a word a memory test.
     *
     * It is built from
       FctShowList rather than from a field per entry: seventeen checkboxes named by hand meant two blocks of seventeen
       assignments that had to be kept in step with each other, with the ini keys, and with whatever the list actually held.
       _showItems is what the combo displays (in the table's order); _showCheck maps an entry back to its checkbox for the
       two passes that fill it in and read it out, which is also why nothing here needs to know whether an entry is a row or
       a word — FctShowList.Entry answers that. */
    private readonly List<ComboBoxItemDetails> _showItems = [];
    private readonly Dictionary<FctShowList.Entry, ComboBoxItemDetails> _showCheck = new();

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
      foreach (var entry in FctShowList.All)
      {
        var item = new ComboBoxItemDetails(true, entry.Label);
        _showItems.Add(item);
        _showCheck[entry] = item;
      }

      showCombo.ItemsSource = _showItems;

      /* Both size dials snap to the same list of positions: one per reachable percent, unevenly spaced so that 0 % — the dash — falls at the exact middle of
         the track while the -50 % floor and the +110 % ceiling both stay within reach (FctScale.DialExtent). Setting Ticks also re-snaps whatever Value the
         markup left behind, which is why the crit dial's shipped +10 % is parked right after it rather than in the XAML: a thumb sitting off centre with the
         panel still closed would be the same lie in the other direction. All of this runs before the window is loaded, so none of it previews. */
      var sizeTicks = new DoubleCollection(FctScale.SizeDialTicks());
      sizeSlider.Ticks = sizeTicks;
      critSlider.Ticks = sizeTicks;
      critSlider.Value = FctScale.DialFromSizePercent(FctScale.CritSizePercentDefault);

      /* The position fields' reach is the virtual screen — every monitor at once, and negative left/top included, because a
         display left of or above the primary is legal geometry, not an error. Sizes floor at what the layout can draw in and
         cap at the desktop itself. Values arrive from the overlay through UpdateFromWindow; the XAML ships none, because there
         is no right default for another machine's desktop. */
      heightUpDown.MinValue = FctResize.MinHeight;
      heightUpDown.MaxValue = SystemParameters.VirtualScreenHeight;
      widthUpDown.MinValue = FctResize.MinWidth;
      widthUpDown.MaxValue = SystemParameters.VirtualScreenWidth;
      leftUpDown.MinValue = SystemParameters.VirtualScreenLeft;
      leftUpDown.MaxValue = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
      topUpDown.MinValue = SystemParameters.VirtualScreenTop;
      topUpDown.MaxValue = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;

      /* The brushes and EQDescriptionSize come from application resources and swap themselves when the theme changes;
         what does not follow on its own is this window's own SfSkinManager stamp, so it gets re-stamped. The unsubscribe
         on Closed matters: the static theme event outlives every window that listens to it. */
      ThemeConfig.EventsThemeChanged += EventsThemeChanged;
      Closed += (_, _) => ThemeConfig.EventsThemeChanged -= EventsThemeChanged;
    }

    private void EventsThemeChanged(string _) => ThemeConfig.SetCurrentTheme(this);

    /* The window's own bounds, written into the position fields without announcing an edit: the overlay calls it when configure
       opens and after every move or resize (mouse or field), which is what keeps two editors of one rectangle honest. Filling
       rides under the loading guard for the same reason LoadFrom does — a load is not an edit. */
    internal void UpdateFromWindow(double left, double top, double width, double height)
    {
      _loading = true;
      leftUpDown.Value = left;
      topUpDown.Value = top;
      widthUpDown.Value = width;
      heightUpDown.Value = height;
      _loading = false;
    }

    /* Hands the panel a snapshot to display. raisePreview is false when the caller is only filling the controls in as part of
       its own apply — entering or leaving configure mode — because that pass would otherwise answer itself with a preview of
       state the caller has already put on the canvas, and restart the demo the caller is about to stop. A user edit raises. */
    internal void LoadFrom(FctConfigState state, bool raisePreview = true)
    {
      _loading = true;

      SelectByTag(modeCombo, state.Fountain ? "fountain" : "split");

      /* Each mode reads its shape from its own combo, so both get selected — the hidden one to its mode's default,
       * which is also what a mode switch mid-configure will offer first. */
      SelectByTag(shapeCombo, !state.Fountain && state.Shape is FctMotionStyle.Straight ? "line" : "arc");
      SelectByTag(sprayCombo, state.Fountain && state.Shape is FctMotionStyle.Freeze ? "freeze" : "spray");
      SelectByTag(healLaneCombo, FctRailLanes.Token(state.HealLane));
      SelectByTag(healDirCombo, Up(state.HealUp));
      SelectByTag(takenLaneCombo, FctRailLanes.Token(state.TakenLane));
      SelectByTag(inDirCombo, Up(state.TakenUp));
      SelectByTag(dealtLaneCombo, FctRailLanes.Token(state.DealtLane));
      SelectByTag(outDirCombo, Up(state.DealtUp));
      /* One pass over the table fills every switch, which is what keeps this honest: the list the player sees, the values
         written back at Snapshot and the keys in settings.ini are all the same seventeen entries (FctShowList), so a row can
         appear in the dropdown without existing in three other places — or, worse, exist here and be ignored. */
      foreach (var entry in FctShowList.All)
      {
        _showCheck[entry].IsChecked = entry.Get(state);
      }
      UpdateShowTitle();
      thresholdUpDown.Value = state.Threshold;
      gutterUpDown.Value = state.Gutter;
      SelectByTag(labelSideCombo, LabelName(state.LabelSide));
      /* Percent is what a snapshot and settings.ini speak; the thumb speaks DialExtent, so every fill and every readout crosses through the same two
         conversions. Crit goes through PercentOfSize too — one scale for both size dials, only their shipped middles differ. */
      sizeSlider.Value = FctScale.DialFromSizePercent(FctScale.PercentOfSize(state.TextScale));
      critSlider.Value = FctScale.DialFromSizePercent(FctScale.PercentOfSize(state.CritScale));
      speedSlider.Value = FctScale.PercentOfSpeed(state.Speed);
      sampleCheck.IsChecked = state.SampleData;

      /* The seven hues, filled in the section's order (see ColorPickers). Setting a picker raises its event like a click
         would, so the whole fill rides under _loading — a load is not an edit, and a storm of previews from one open
         would be noise. */
      var hues = new[]
      {
        state.ColorDamageDealt, state.ColorDamageTaken, state.ColorHealing, state.ColorCrit,
        state.ColorSpecial, state.ColorWords, state.ColorSource,
      };
      var pickers = ColorPickers();
      for (var i = 0; i < pickers.Length; i++)
      {
        pickers[i].Color = ToMedia(hues[i]);
      }

      _loading = false;

      /* One pass with the guard down: mode visibility, the legend sentence and the dial readouts all derive from the
         controls, so they get computed exactly the way a user change would compute them. */
      ApplyModeVisibility();
      UpdateReadouts();
      RefreshLaneChoices();
      if (raisePreview)
      {
        PreviewChanged?.Invoke(Snapshot());
      }
    }

    internal void SetStats(string stats) => statsText.Text = stats;

    private FctConfigState Snapshot()
    {
      var fountain = ComboTag(modeCombo) == "fountain";
      var state = new FctConfigState
      {
        Fountain = fountain,
        // the Tags carry the same words the panel shows and settings.ini saves, so one vocabulary spans dropdown, file and engine
        // (Straight excepted: the player reads "line"). Freezing is fountain's alone — a scroll that parks mid-column breaks the chain
        Shape = fountain
          ? ComboTag(sprayCombo) == "freeze" ? FctMotionStyle.Freeze : FctMotionStyle.Spray
          : ComboTag(shapeCombo) == "line" ? FctMotionStyle.Straight : FctMotionStyle.Arc,
        HealLane = FctRailLanes.Parse(ComboTag(healLaneCombo), Defaults.HealLane),
        HealUp = ComboTag(healDirCombo) == "up",
        TakenLane = FctRailLanes.Parse(ComboTag(takenLaneCombo), Defaults.TakenLane),
        TakenUp = ComboTag(inDirCombo) == "up",
        DealtLane = FctRailLanes.Parse(ComboTag(dealtLaneCombo), Defaults.DealtLane),
        DealtUp = ComboTag(outDirCombo) == "up",
        Threshold = Math.Clamp(Math.Round(thresholdUpDown.Value ?? 0d), 0, FctOverlaySettings.ThresholdMax),
        Gutter = FctOverlaySettings.ClampGutter(gutterUpDown.Value ?? FctStage.CenterGutter),
        LabelSide = ComboTag(labelSideCombo) switch
        {
          "left" => FctLabelSide.Left,
          "right" => FctLabelSide.Right,
          "none" => FctLabelSide.None,
          _ => FctLabelSide.Below,
        },
        TextScale = FctScale.SizeFromPercent(FctScale.SizePercentFromDial(sizeSlider.Value)),
        CritScale = FctScale.SizeFromPercent(FctScale.SizePercentFromDial(critSlider.Value)), // same percent rule as the text dial; only its middle differs
        Speed = FctScale.SpeedFromPercent((int)Math.Round(speedSlider.Value)),
        SampleData = sampleCheck.IsChecked == true,
        ColorDamageDealt = ToInt(dealtColor.Color),
        ColorDamageTaken = ToInt(takenColor.Color),
        ColorHealing = ToInt(healsColor.Color),
        ColorCrit = ToInt(critColor.Color),
        ColorSpecial = ToInt(specialColor.Color),
        ColorWords = ToInt(wordsColor.Color),
        ColorSource = ToInt(labelsColor.Color),
      };

      /* Rows and words alike: the same table, read in the same order it was filled. */
      foreach (var entry in FctShowList.All)
      {
        entry.Set(state, _showCheck[entry].IsChecked);
      }

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

      var state = Snapshot();

      /* Flipping a direction dial can strand the column it was pointed at: two categories, one queue, opposite ways is not a
         thing a rail can do (FctConveyor). The state resolves it and the controls follow the resolution, so what previews —
         and what Save will write — is the arrangement the overlay actually runs rather than the one that was typed. */
      if (state.ResolveLaneConflicts() > 0)
      {
        LoadFrom(state, raisePreview: false);
      }

      RefreshLaneChoices();
      PreviewChanged?.Invoke(state);
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

    /* Same contract as every other control: the typed gutter previews staged — guide outlines and columns move live —
       and nothing reaches settings.ini until Save. */
    private void GutterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
      if (_loading || !IsLoaded)
      {
        return;
      }

      PreviewChanged?.Invoke(Snapshot());
    }

    /* One handler for all four position fields: a move is one rectangle, and raising per field would send the overlay three
       disagreeing rectangles out of a two-field edit before it agrees with itself. An emptied field is no rectangle at all, so a
       null in any of the four says wait — the next real edit raises all four again. */
    private void GeometryFieldChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
      if (_loading || !IsLoaded ||
          leftUpDown.Value is not { } left || topUpDown.Value is not { } top ||
          widthUpDown.Value is not { } width || heightUpDown.Value is not { } height)
      {
        return;
      }

      GeometryEdited?.Invoke(left, top, width, height);
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
    private void UpdateShowTitle() => UiElementUtil.SetComboBoxTitle(showCombo, "kinds");

    /* Double-click returns a dial to the shipped middle; letting go of either restarts the demo cycle up top. */
    private void SliderReleased(object sender, MouseButtonEventArgs e)
    {
      if (e.ClickCount == 2)
      {
        if (sender == sizeSlider)
        {
          sizeSlider.Value = FctScale.DialFromSizePercent(0);      // the text dial's middle is nothing, and now sits where it looks like it should
        }
        else if (sender == critSlider)
        {
          critSlider.Value = FctScale.DialFromSizePercent(FctScale.CritSizePercentDefault); // and the crit dial's is its shipped +10 %, a step right of centre
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
     * swaps lists instead of disappearing — fountain shapes are spray and freeze, never a rail it would only degrade.
     * Collapsed, never disabled: a control that changes nothing in the mode you are in is what configure mode was
     * cleaned up to stop showing at all.
     */
    private void ApplyModeVisibility()
    {
      var fountain = ComboTag(modeCombo) == "fountain";

      shapeCombo.Visibility = fountain ? Visibility.Collapsed : Visibility.Visible;
      sprayCombo.Visibility = fountain ? Visibility.Visible : Visibility.Collapsed;

      /* Fountain has no columns to hand out, so the lane pickers step off — but the DIRECTION dials stay, healing's
         included: a fountain gets to say whether heals rise while damage falls (the genre's classic look) or sink.
         Hiding the whole heals row had quietly tied healing to the damage-in dial, which is not a sentence anybody
         meant to write when they moved "damage in". */
      takenLaneCombo.Visibility = dealtLaneCombo.Visibility = healLaneCombo.Visibility = fountain ? Visibility.Collapsed : Visibility.Visible;

      /* The gutter IS split's geometry, so it steps off with the lane pickers: fountain clears its middle by sending
         both sides away from it, and there is no band left to size. */
      gutterTitle.Visibility = gutterUpDown.Visibility = fountain ? Visibility.Collapsed : Visibility.Visible;
    }

    /*
     * One train per column: a category may join a column whose traffic travels its way, and may never book one it would meet
     * head-on, so those choices are taken out of the dropdowns rather than explained afterwards. Sharing a column with traffic
     * going the same way stays on offer — "heals in the same column as my damage, both rising" is a real preference and one
     * queue handles it exactly. A hand-written settings.ini can still arrive in conflict; ResolveLaneConflicts moves somebody
     * before any geometry is built (FctConfigState.BuildLayout).
     */
    private void RefreshLaneChoices()
    {
      var (healLane, healUp) = (FctRailLanes.Parse(ComboTag(healLaneCombo), Defaults.HealLane), ComboTag(healDirCombo) == "up");
      var (takenLane, takenUp) = (FctRailLanes.Parse(ComboTag(takenLaneCombo), Defaults.TakenLane), ComboTag(inDirCombo) == "up");
      var (dealtLane, dealtUp) = (FctRailLanes.Parse(ComboTag(dealtLaneCombo), Defaults.DealtLane), ComboTag(outDirCombo) == "up");

      MarkLaneItems(healLaneCombo, healUp, takenLane, takenUp, dealtLane, dealtUp);
      MarkLaneItems(takenLaneCombo, takenUp, healLane, healUp, dealtLane, dealtUp);
      MarkLaneItems(dealtLaneCombo, dealtUp, healLane, healUp, takenLane, takenUp);
    }

    private static void MarkLaneItems(ComboBox combo, bool up, FctRailLane other1, bool up1, FctRailLane other2, bool up2)
    {
      foreach (var entry in combo.Items)
      {
        if (entry is not ComboBoxItem item)
        {
          continue;
        }

        /* Whatever is already selected stays selectable: greying out a box's own value would advertise a choice this panel is
           not making. A category switched off owns no column and conflicts with nothing (FctRailLane.None), which
           LaneAvailable already answers true for, as it does for the "none" item itself. */
        item.IsEnabled = ReferenceEquals(item, combo.SelectedItem) ||
          FctConfigState.LaneAvailable(FctRailLanes.Parse(item.Tag as string, FctRailLane.None), up, other1, up1, other2, up2);
      }
    }

    private void UpdateReadouts()
    {
      sizeValue.Text = Signed(FctScale.SizePercentFromDial(sizeSlider.Value));
      critValue.Text = Signed(FctScale.SizePercentFromDial(critSlider.Value));
      speedValue.Text = Signed((int)Math.Round(speedSlider.Value));
    }

    private static string Signed(int percent) => percent == 0 ? "—" : $"{percent:+0;-0}%";

    private static string LabelName(FctLabelSide side) =>
      side switch
      {
        FctLabelSide.Left => "left",
        FctLabelSide.Right => "right",
        FctLabelSide.None => "none",
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

    /* ----------------------------------------- the colors tab ------------------------------------------ */

    /* The seven pickers in the section's order, matched to the state's hue order: one table so a colour cannot join the
     * XAML and be forgotten at load or save — that promise does not need a per-row reset button to be worth keeping.
     * (The WPF Color <-> 0xAARRGGBB int translation lives at the bottom; the engine and settings.ini only ever speak
     * ints. Reaching a shipped value needs no arrow either: Cancel puts every hue back, and the pickers remember recent
     * colours across opens on their own.) */
    private ColorPicker[] ColorPickers() =>
    [
      dealtColor, takenColor, healsColor, critColor, specialColor, wordsColor, labelsColor,
    ];

    /* A picked colour is a staged edit like any other: straight into the preview, which the overlay applies to its palette
       dial — so the NEXT spawned number wears it and nothing already mid-flight gets tugged, the same promise the size dial
       makes. The picker keeps its own recent-colours memory across opens; only Save writes ini. */
    private void ColourPicked(object sender, SelectedColorChangedEventArgs e)
    {
      if (_loading || !IsLoaded)
      {
        return;
      }

      PreviewChanged?.Invoke(Snapshot());
    }

    /* One int is the whole colour everywhere the engine speaks — Skia draws it, settings.ini stores it, tests assert it. */
    private static Color ToMedia(int argb) =>
      Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private static int ToInt(Color c) => unchecked((c.A << 24) | (c.R << 16) | (c.G << 8) | c.B);
  }
}