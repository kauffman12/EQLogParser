using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using EQLogParser;

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

                // Step 1: select all.
                Invoke(table, "SelectAllClick", new object[] { null!, null! });

                // Steps 2-4: the group gesture on the same row, with a re-select in the middle.
                RightClickOn(table, target);
                Invoke(table, "SelectGroup", false);
                afterStep2.AddRange(SelectedNames(table));

                RightClickOn(table, target);
                Invoke(table, "SelectGroup", true);
                afterStep3.AddRange(SelectedNames(table));

                RightClickOn(table, target);
                Invoke(table, "SelectGroup", false);
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
        CollectionAssert.AreEqual(everyFight, afterStep3, "step 3: select group puts the whole list back");
        CollectionAssert.AreEqual(expected, afterStep4, "step 4: the second unselect must take the clicked group, not the list");
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
                Invoke(table, "SelectGroup", false);
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

    // ContextMenuEventArgs has no public constructor; the opening handler reads it like any mouse event's source,
    // and an unfilled instance says "no row under the pointer" - which this test supplies through RightClickOn.
    private static object ContextMenuArgs() => Activator.CreateInstance(typeof(ContextMenuEventArgs), nonPublic: true)!;

    // What GridContextMenuOpening does with a row under the pointer: name it and point the cursor at it.
    private static void RightClickOn(FightTable table, DerivedFightRow row)
    {
        typeof(FightTable).GetField("_menuRow", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(table, row);
        if (!row.IsDivider) table.fightGrid.CurrentItem = row;
        Flush();
    }

    private static void Invoke(FightTable table, string method, params object[] args)
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
