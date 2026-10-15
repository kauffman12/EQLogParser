using EQLogParser;
using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace EQLogParser.Wpf.Test;

/*
 * Field report: with a large selection, "unselect group" on a fight works the first time, but right-click -> select group
 * -> unselect group on the SAME row then clears the WHOLE list instead of taking that group.
 *
 * This test drives the real pane and the real SfDataGrid through exactly that sequence - Select All, then the group
 * gesture twice with a re-select in between, each right-click replaying what GridContextMenuOpening does (name the row
 * under the pointer and point the grid's cursor at it) - and asks for the selection after every step.
 *
 *
 * The sequence passed while the field fault was live, which is what localised the fault OUTSIDE this pane: SfDataGrid's
 * own press handling collapses an extended selection to the clicked row the moment the button goes down (no button guard
 * on the press path - decompiled 33.1.49), before any menu or method runs. Scripted gestures cannot reach a press
 * handler, so this test guards the arithmetic behind the menu items; RightPressOnUnselectedRow_LeavesTheOperatorsSelectionAlone
 * pins the theft itself and the pane's remember-and-restore answer.
 *
 * Windows only: it is the one place an actual grid may be poked, and the answer must hold in a shown window with real
 * rows arranged, not in a windowless control.
 */
[TestClass]
public sealed class FightTableGroupGestureTest
{
    private const double T0 = 1_000;
    private const int Groups = 5;
    private const int FightsPerGroup = 10;

    [TestMethod]
    public void UnselectSelectUnselect_TakesOnlyTheClickedGroup()
    {
        var afterStep2 = new List<string>();
        var afterStep3 = new List<string>();
        var afterStep4 = new List<string>();
        var expected = new List<string>();
        var everyFight = new List<string>();
        string? failure = null;

        Sta.Run(() =>
        {
            try
            {
                var app = Application.Current ?? new Application();
                app.Resources["CustomCheckBoxTemplate"] ??= new ControlTemplate(typeof(CheckBox));
                app.Resources["TemplateToolTip"] ??= new DataTemplate();
                app.Resources["EQIconStyle"] ??= new Style(typeof(System.Windows.Controls.Image));

                var table = Paint(Snapshot());
                var window = new Window { Content = table, Width = 640, Height = 480 };
                window.Show();
                Flush();

                var rows = ((IEnumerable)table.fightGrid.ItemsSource).Cast<DerivedFightRow>().ToList();
                everyFight.AddRange(rows.Where(r => !r.IsDivider).Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal));

                // Group two is the second block of ten (index 10 is the divider after group one).
                var target = rows[12];
                var groupTwo = rows.Skip(11).Take(FightsPerGroup).Select(r => r.Name).ToHashSet();
                expected.AddRange(rows.Where(r => !r.IsDivider && !groupTwo.Contains(r.Name))
                                      .Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal));

                // Step 1: select all. A later group assertion cannot diagnose a selection that never formed.
                Invoke(table, "SelectAllClick", new object?[] { null, null });
                CollectionAssert.AreEquivalent(everyFight, SelectedNames(table),
                    "Select All did not select every fight before the group gesture");

                // Steps 2-4: the group gesture on the same row, with an ADDITIVE re-select in the middle — the modifier held on
                // the press (RememberRightPress below), which is the only way this sequence puts the rest of the list back.
                RightClickOn(table, target);
                CollectionAssert.AreEquivalent(everyFight, SelectedNames(table),
                    "aiming the menu at the target changed the selection before Unselect Group ran");
                Invoke(table, "RemoveGroupFromSelection");
                afterStep2.AddRange(SelectedNames(table));

                RememberRightPress(table, System.Windows.Input.ModifierKeys.Control);
                RightClickOn(table, target);
                Invoke(table, "ApplyGroupSelection", true);
                afterStep3.AddRange(SelectedNames(table));

                RightClickOn(table, target);
                Invoke(table, "RemoveGroupFromSelection");
                afterStep4.AddRange(SelectedNames(table));

                window.Close();
            }
            catch (Exception ex)
            {
                failure = ex.ToString();
            }
        });

        Assert.IsNull(failure, "the gesture sequence must run on a shown grid");
        CollectionAssert.AreEqual(expected, afterStep2, "step 2: unselect group takes the clicked group only");
        CollectionAssert.AreEqual(everyFight, afterStep3, "step 3: an additive select group puts the whole list back");
        CollectionAssert.AreEqual(expected, afterStep4, "step 4: the second unselect must take the clicked group, not the list");
    }

    /*
     * The modifier rule itself (2026-10-14, operator: "select group should deselect everything but that group, unless i am holding
     * control or shift when right-clicking"). Two panes of one grid, same starting selection across two other groups:
     *
     *   plain press  -> Select Group leaves ONLY the clicked encounter (this is what changed; it used to add);
     *   Ctrl press   -> Select Group adds it, so several groups can be assembled on purpose.
     *
     * The press goes in through RememberRightPress, the same method GridPreviewRightButtonDown calls with Keyboard.Modifiers:
     * real modifier state cannot be scripted, and the alternative — reading the flag inside the handler — would leave the actual
     * rule untested. RightClickOn is the opening that follows it, because a menu item reads the mode the OPENING stamped.
     * That a Ctrl press ALSO stops the pane restoring its remembered selection is pinned by
     * RightPressOnUnselectedRow_LeavesTheOperatorsSelectionAlone's neighbours in this file; what matters here is that the same
     * menu item answers differently, and only from the press.
     */
    [TestMethod]
    [DataRow(System.Windows.Input.ModifierKeys.None, false)]
    [DataRow(System.Windows.Input.ModifierKeys.Control, true)]
    [DataRow(System.Windows.Input.ModifierKeys.Shift, true)]
    public void OnlyAModifiedPressKeepsTheOtherGroupsSelected(System.Windows.Input.ModifierKeys modifiers, bool additive)
    {
        var after = new List<string>();
        var groupTwo = new List<string>();
        var outside = new List<string>();
        string? failure = null;

        Sta.Run(() =>
        {
            try
            {
                var app = Application.Current ?? new Application();
                app.Resources["CustomCheckBoxTemplate"] ??= new ControlTemplate(typeof(CheckBox));
                app.Resources["TemplateToolTip"] ??= new DataTemplate();
                app.Resources["EQIconStyle"] ??= new Style(typeof(System.Windows.Controls.Image));

                var table = Paint(Snapshot());
                var window = new Window { Content = table, Width = 640, Height = 480 };
                window.Show();
                Flush();

                var rows = ((IEnumerable)table.fightGrid.ItemsSource).Cast<DerivedFightRow>().ToList();
                groupTwo.AddRange(rows.Skip(11).Take(FightsPerGroup).Select(r => r.Name));

                // A working selection over two OTHER groups, then the pointer aims at group two.
                foreach (var row in rows.Skip(1).Take(FightsPerGroup).Concat(rows.Skip(21).Take(FightsPerGroup)))
                    table.fightGrid.SelectedItems.Add(row);
                outside.AddRange(table.fightGrid.SelectedItems.Cast<DerivedFightRow>().Select(r => r.Name));

                RememberRightPress(table, modifiers);
                RightClickOn(table, rows[12]);
                Invoke(table, "SelectGroupClick", new object?[] { null, null });

                after.AddRange(SelectedNames(table));
                window.Close();
            }
            catch (Exception ex)
            {
                failure = ex.ToString();
            }
        });

        Assert.IsNull(failure, "the gesture must run on a shown grid");
        if (additive)
        {
            CollectionAssert.AreEquivalent(groupTwo.Concat(outside).ToList(), after,
              $"{modifiers} on the press means the group joins what was selected");
        }
        else
        {
            CollectionAssert.AreEquivalent(groupTwo, after,
              "a plain press means this group and nothing else — the rest of the selection is deselected");
        }
    }

    /*
     * The field fault itself, pinned. Report: with a big selection standing, right-clicking an UNSELECTED fight "unselects
     * everything" as soon as the button goes down; Select Group then puts back only that group, and Unselect Group leaves
     * nothing. The control owns the theft (decompiled 33.1.49): CellRenderer.ProcessMouseDown raises PointerOperation.Pressed
     * for any button, and GridSelectionController.ProcessSelection under Extended mode without Ctrl does
     * RemoveSelection(everything) + AddSelection(clicked). The pane answers by remembering the selection on
     * PreviewMouseRightButtonDown - which tunnels ahead of the control's bubbling MouseDown - and putting it back at
     * ContextMenuOpening, before the menu items, the boards or any gesture read what survived the button.
     *
     * Scripted gestures cannot make a real CellRenderer run, so the collapse is applied here exactly as ProcessSelection
     * applies it; the preview press goes through the real XAML wiring as a routed event. If a control upgrade ever stops
     * collapsing on right-press, the restore degrades to a no-op and this test still passes - it pins the pane's promise,
     * not the vendor's habit.
     */
    [TestMethod]
    public void RightPressOnUnselectedRow_LeavesTheOperatorsSelectionAlone()
    {
        var protectedRows = new List<DerivedFightRow>();
        var afterOpening = new List<DerivedFightRow>();
        var afterUnselect = new List<DerivedFightRow>();
        DerivedFightRow? clicked = null;
        string? failure = null;

        Sta.Run(() =>
        {
            try
            {
                var app = Application.Current ?? new Application();
                app.Resources["CustomCheckBoxTemplate"] ??= new ControlTemplate(typeof(CheckBox));
                app.Resources["TemplateToolTip"] ??= new DataTemplate();
                app.Resources["EQIconStyle"] ??= new Style(typeof(System.Windows.Controls.Image));

                var table = Paint(Snapshot());
                var window = new Window { Content = table, Width = 640, Height = 480 };
                window.Show();
                Flush();

                var grid = table.fightGrid;
                var rows = ((IEnumerable)grid.ItemsSource).Cast<DerivedFightRow>().ToList();

                // The operator's working set: group five selected. The pointer then aims at group one - an
                // UNSELECTED fight, the exact condition the report named.
                protectedRows.AddRange(rows.Skip(44).Take(FightsPerGroup));
                foreach (var row in protectedRows) grid.SelectedItems.Add(row);
                var target = rows[2];
                clicked = target;
                Assert.IsFalse(grid.SelectedItems.Contains(target), "precondition: the right-click lands on an unselected row");

                // 1. The press tunnels down; the pane remembers the selection before anything can spend it.
                grid.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Right)
                {
                    RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent,
                    Source = grid,
                });

                // 2. The control's collapse, replayed as ProcessSelection applies it: everything gone, clicked row alone.
                grid.SelectedItems.Clear();
                grid.SelectedItems.Add(target);
                Assert.AreEqual(1, grid.SelectedItems.Count, "precondition: the press left exactly the clicked row");

                // 3. The menu opens - and finds the operator's selection, not the button's.
                Invoke(table, "GridContextMenuOpening", grid, ContextMenuArgs());
                afterOpening.AddRange(grid.SelectedItems.Cast<DerivedFightRow>());

                // 4. And the menu item the report was about: unselecting a group nobody had selected spends nothing.
                RightClickOn(table, target);
                Invoke(table, "RemoveGroupFromSelection");
                afterUnselect.AddRange(grid.SelectedItems.Cast<DerivedFightRow>());

                window.Close();
            }
            catch (Exception ex)
            {
                failure = ex.ToString();
            }
        });

        Assert.IsNull(failure, "the gesture sequence must run on a shown grid");
        CollectionAssert.AreEquivalent(protectedRows, afterOpening,
            "a plain right-press on an unselected row replaced the operator's selection; the opening must put it back");
        Assert.IsTrue(clicked is not null && !afterOpening.Contains(clicked), "the clicked row joins only when a gesture says so");
        CollectionAssert.AreEquivalent(protectedRows, afterUnselect,
            "unselect group over an unselected group must not spend the selection that was");
    }

  [TestMethod]
  [DataRow(System.Windows.Input.Key.Down)]
  [DataRow(System.Windows.Input.Key.A)]
  [DataRow(System.Windows.Input.Key.Apps)]
  [DataRow(System.Windows.Input.Key.F10)]
  public void AKeyboardGestureInvalidatesAnAbandonedRightPress(System.Windows.Input.Key key)
  {
    var expected = new List<DerivedFightRow>();
    var afterOpening = new List<DerivedFightRow>();
    Sta.Run(() =>
    {
      var app = Application.Current ?? new Application();
      app.Resources["CustomCheckBoxTemplate"] ??= new ControlTemplate(typeof(CheckBox));
      app.Resources["TemplateToolTip"] ??= new DataTemplate();
      app.Resources["EQIconStyle"] ??= new Style(typeof(System.Windows.Controls.Image));
      var table = Paint(Snapshot());
      var window = new Window { Content = table, Width = 640, Height = 480 };
      window.Show();
      try
      {
        Flush();
        var grid = table.fightGrid;
        var rows = ((IEnumerable)grid.ItemsSource).Cast<DerivedFightRow>().Where(r => !r.IsDivider).ToList();
        grid.SelectedItems.Add(rows[0]);
        grid.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
          System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Right)
        { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent, Source = grid });
        // No menu opened. A later key press tunnels through the real XAML wiring.
        var source = KeyboardSource(window);
        grid.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
          source, Environment.TickCount, key)
        { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent, Source = grid });
        // Replay the keyboard's new selection, then open its menu.
        expected.AddRange(rows.Skip(20).Take(3));
        grid.SelectedItems.Clear();
        foreach (var row in expected) grid.SelectedItems.Add(row);
        Invoke(table, "GridContextMenuOpening", grid, ContextMenuArgs());
        afterOpening.AddRange(grid.SelectedItems.Cast<DerivedFightRow>());
      }
      finally
      {
        window.Close();
      }
    });
    CollectionAssert.AreEquivalent(expected, afterOpening,
      "the keyboard's selection must not be overwritten by the abandoned mouse press");
  }

    /*
     * Shift+F10 must not buy additive mode. The pane reads the modifier off a POINTER press, and a keyboard-opened menu carries
     * Shift in its own keystroke — so any key press ends the gesture memory, exactly as it already ends the remembered selection
     * (AKeyboardGestureInvalidatesAnAbandonedRightPress). Without this, every Shift+F10 menu in the pane would quietly mean "add".
     */
    [TestMethod]
    public void AKeyBeforeTheMenuClearsAModifierHeldOnAnEarlierPress()
    {
        var after = new List<string>();
        var groupTwo = new List<string>();
        string? failure = null;

        Sta.Run(() =>
        {
            try
            {
                var app = Application.Current ?? new Application();
                app.Resources["CustomCheckBoxTemplate"] ??= new ControlTemplate(typeof(CheckBox));
                app.Resources["TemplateToolTip"] ??= new DataTemplate();
                app.Resources["EQIconStyle"] ??= new Style(typeof(System.Windows.Controls.Image));

                var table = Paint(Snapshot());
                var window = new Window { Content = table, Width = 640, Height = 480 };
                window.Show();
                Flush();

                var grid = table.fightGrid;
                var rows = ((IEnumerable)grid.ItemsSource).Cast<DerivedFightRow>().ToList();
                groupTwo.AddRange(rows.Skip(11).Take(FightsPerGroup).Select(r => r.Name));
                foreach (var row in rows.Skip(21).Take(FightsPerGroup)) grid.SelectedItems.Add(row);

                // Ctrl rode an earlier press whose menu never opened; then the keyboard takes over and opens one.
                RememberRightPress(table, System.Windows.Input.ModifierKeys.Control);
                grid.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                  KeyboardSource(window), Environment.TickCount, System.Windows.Input.Key.F10)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent, Source = grid });

                RightClickOn(table, rows[12]);
                Invoke(table, "SelectGroupClick", new object?[] { null, null });
                after.AddRange(SelectedNames(table));
                window.Close();
            }
            catch (Exception ex)
            {
                failure = ex.ToString();
            }
        });

        Assert.IsNull(failure, "the gesture must run on a shown grid");
        CollectionAssert.AreEquivalent(groupTwo, after,
          "Shift+F10 carries Shift without anybody meaning 'add this group'; the menu it opens is a plain gesture");
    }

    // Show/Flush does not guarantee FromVisual can see the HWND on this test STA. The key event needs
    // the window's input source even though the routed event itself is raised directly on the grid.
    private static HwndSource KeyboardSource(Window window) =>
      HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle())
      ?? throw new InvalidOperationException("The test window could not create an input source");

    // ContextMenuEventArgs has NO reachable constructor (not even non-public), so it is allocated without running
    // one. That is safe here because the opening handler reads exactly one thing off it - OriginalSource, a plain
    // field which comes back null, the same answer a keyboard-opened menu gives; this test names its row through
    // RightClickOn instead.
    private static object ContextMenuArgs() =>
      System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ContextMenuEventArgs));

    /*
     * What GridContextMenuOpening does: carry the press's modifier into the menu (_menuAdds from _rightPressAdds, copied - not
     * re-derived from the test's own modifier argument, so a key press in between still clears it the way production does), and
     * name the row under the pointer. A test that wants an open menu calls this; the item handlers read what it wrote.
     */
    private static void RightClickOn(FightTable table, DerivedFightRow row)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var adds = typeof(FightTable).GetField("_rightPressAdds", flags)!;
        typeof(FightTable).GetField("_menuAdds", flags)!.SetValue(table, adds.GetValue(table));
        typeof(FightTable).GetField("_menuRow", flags)!.SetValue(table, row);
        if (!row.IsDivider) table.fightGrid.CurrentItem = row;
        Flush();
    }

    // The press's modifier, named by the test rather than read off real hardware: this is the seam
    // GridPreviewRightButtonDown feeds Keyboard.Modifiers into, and the only way to ask what a Ctrl press does.
    private static void RememberRightPress(FightTable table, System.Windows.Input.ModifierKeys modifiers)
    {
        typeof(FightTable).GetMethod("RememberRightPress", BindingFlags.Instance | BindingFlags.NonPublic)!
          .Invoke(table, new object[] { modifiers });
        Flush();
    }

    private static void Invoke(FightTable table, string method, params object?[] args)
    {
        typeof(FightTable).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
          .Invoke(table, args);
        Flush();
    }

    // The grid's own view of the selection. Sorted: SelectedItems enumerates in whatever order the last selection
    // operation touched the rows (the comment on GetSelectedFights says so), and these comparisons are about SETS.
    private static List<string> SelectedNames(FightTable table) =>
      table.fightGrid.SelectedItems.Cast<DerivedFightRow>()
        .Where(r => !r.IsDivider && r.Fight is not null)
        .Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

    private static void Flush() => Dispatcher.CurrentDispatcher.Invoke(new Action(() => { }), DispatcherPriority.Normal);

    // Five groups of ten: two-second gaps inside a group, a half-hour gap between groups (the divider threshold is 120 s).
    private static DerivedSnapshot Snapshot()
    {
        var fights = new List<DerivedFight>();
        var id = 0;
        for (var g = 0; g < Groups; g++)
        {
            for (var i = 0; i < FightsPerGroup; i++)
            {
                var begin = T0 + g * 3600 + i * 2;
                fights.Add(new DerivedFight
                {
                    Id = ++id,
                    Name = $"F{g}x{i}",
                    BeginTime = begin,
                    LastTime = begin + 1,
                    DamageTotal = 100,
                    DamageHits = 1,
                    DamageByOwner = 100,
                });
            }
        }

        return DerivedFightRows.Build(fights, new EntityTimeline(), 0, new DamageFactTable(8), new FightFactIndex());
    }

    private static FightTable Paint(DerivedSnapshot snapshot)
    {
        var table = new FightTable();
        table.fightShowBreaks.IsChecked = true;
        table.fightShowTanking.IsChecked = true;
        table.OnDerived(snapshot);
        Flush();
        return table;
    }
}
