using EQLogParser;
using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

#nullable enable annotations

namespace EQLogParser.Wpf.Test;

// Exercise preview-press, context-opening and keyboard wiring on a real grid.
// Group membership and additive selection arithmetic live in GroupSectionSelectTest.
[TestClass]
[TestCategory("WindowsIntegration")]
public sealed class FightTableGroupGestureTest
{
    private const double T0 = 1_000;
    private const int Groups = 5;
    private const int FightsPerGroup = 10;

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

  [TestMethod]
  [DataRow(System.Windows.Input.Key.LeftCtrl, System.Windows.Input.ModifierKeys.Control)]
  [DataRow(System.Windows.Input.Key.RightCtrl, System.Windows.Input.ModifierKeys.Control)]
  [DataRow(System.Windows.Input.Key.LeftShift, System.Windows.Input.ModifierKeys.Shift)]
  [DataRow(System.Windows.Input.Key.RightShift, System.Windows.Input.ModifierKeys.Shift)]
  public void HeldModifierKeyDownsKeepAddingGroupsAcrossRepeatedMenus(
    System.Windows.Input.Key key, System.Windows.Input.ModifierKeys modifiers)
  {
    var expected = new List<List<string>>();
    var observed = new List<List<string>>();
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
        var source = KeyboardSource(window);
        var groups = ((IEnumerable)grid.ItemsSource).Cast<DerivedFightRow>()
          .Where(row => !row.IsDivider).GroupBy(row => row.Fight.GroupId)
          .Select(group => group.ToList()).ToList();
        var keeps = new HashSet<DerivedFightRow>(groups[^1]);
        foreach (var row in keeps) grid.SelectedItems.Add(row);

        for (var gesture = 0; gesture < Groups * 2; gesture++)
        {
          var group = groups[gesture % Groups];
          grid.CurrentItem = group[0];
          RememberRightPress(table, modifiers);
          // A modifier key-down while the right button is held must not turn an additive press into a plain one.
          for (var repeat = 0; repeat < 3; repeat++)
          {
            grid.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
              source, Environment.TickCount, key)
            { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent, Source = grid });
          }

          Invoke(table, "GridContextMenuOpening", grid, ContextMenuArgs());
          // Closing and Click may arrive in either order; neither may change the mode of this menu.
          if (gesture % 2 == 0) Invoke(table, "GridContextMenuClosing", grid, ContextMenuArgs());
          Invoke(table, "SelectGroupClick", new object?[] { null, null });
          if (gesture % 2 != 0) Invoke(table, "GridContextMenuClosing", grid, ContextMenuArgs());
          keeps.UnionWith(group);
          expected.Add(keeps.Select(row => row.Name).ToList());
          observed.Add(SelectedNames(table));
        }

        // A completed Ctrl/Shift menu must not leak its mode into the next plain mouse gesture.
        grid.CurrentItem = groups[0][0];
        RememberRightPress(table, System.Windows.Input.ModifierKeys.None);
        Invoke(table, "GridContextMenuOpening", grid, ContextMenuArgs());
        Invoke(table, "SelectGroupClick", new object?[] { null, null });
        Invoke(table, "GridContextMenuClosing", grid, ContextMenuArgs());
        expected.Add(groups[0].Select(row => row.Name).ToList());
        observed.Add(SelectedNames(table));
      }
      finally
      {
        window.Close();
      }
    });

    Assert.HasCount(expected.Count, observed);
    for (var gesture = 0; gesture < expected.Count; gesture++)
    {
      CollectionAssert.AreEquivalent(expected[gesture], observed[gesture],
        $"gesture {gesture}: holding a modifier must keep earlier groups selected; the final plain gesture replaces them");
    }
  }

    // Keyboard navigation and keyboard-opened menus must invalidate any abandoned pointer press.
    [TestMethod]
    [DataRow(System.Windows.Input.Key.Down)]
    [DataRow(System.Windows.Input.Key.A)]
    [DataRow(System.Windows.Input.Key.Apps)]
    [DataRow(System.Windows.Input.Key.F10)]
    public void AKeyBeforeTheMenuClearsAModifierHeldOnAnEarlierPress(System.Windows.Input.Key key)
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
                var otherGroup = rows.Skip(2 * (FightsPerGroup + 1)).Take(FightsPerGroup).ToList();
                Assert.AreEqual(FightsPerGroup, otherGroup.Count);
                Assert.IsTrue(otherGroup.All(row => !row.IsDivider), "the setup must select fights, not the divider");
                foreach (var row in otherGroup) grid.SelectedItems.Add(row);
                Assert.AreEqual(FightsPerGroup, SelectedNames(table).Count, "the keyboard gesture starts with a full outside group");

                // Ctrl rode an earlier press whose menu never opened; then the keyboard takes over and opens one.
                RememberRightPress(table, System.Windows.Input.ModifierKeys.Control);
                grid.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                  KeyboardSource(window), Environment.TickCount, key)
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
          "a keyboard gesture must discard the previous mouse press's additive mode");
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

        Sectionizer.StampGroupIds(fights);
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
