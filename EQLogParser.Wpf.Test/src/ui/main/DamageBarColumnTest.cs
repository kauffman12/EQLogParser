using System.Windows;

using EQLogParser;

namespace EQLogParser.Wpf.Test;

/*
 * The wiring between MeterColumnLayout and a real DamageBar. Core answers zero for a switched-off column and that is headless-tested
 * (MeterColumnLayoutTest); what only this assembly can look at is whether the bar actually hands those numbers to its own
 * ColumnDefinitions, TextBlocks and margins — which is where a forgotten assignment or a leftover per-column margin would live, and a
 * control that reports zero while still reserving a cell is exactly the bug the column switches were built to kill.
 *
 * Nothing here judges pixels across fonts: alignment of header words over digits, DPI and thin-bar sight stay Windows gates under
 * "Damage meter column switches, checked on Windows" in docs/ReleaseChecklist.md. This assembly compiles everywhere and runs only there.
 */
[TestClass]
public sealed class DamageBarColumnTest
{
  private const double RowHeight = 24;
  private const double WindowWidth = 600;

  // The ranking-bar height a 12pt normal-style meter asks for (DamageOverlayWindow.UpdateMiniBars writes this key). Stating it here
  // means the bar's measured height is OUR number; with the key missing the row would report whatever the vendor control defaults to
  // and the assertion at the end of HidingEverythingStillLeavesAWorkingRow would prove nothing.
  private const double BarHeight = 21;

  /*
   * A pooled row ships collapsed and a collapsed element is never measured, so its children keep zero ActualWidth no matter what the
   * columns say; every body opens the bar and gives it a pass before reading anything.
   *
   * The application keys the bar's XAML reads by DynamicResource are seeded HERE, in the caller's own `Sta.Run` body, not in a
   * `[TestInitialize]`: `Sta.Run` gives every call its own thread, so resources prepared on another thread's Application are not what
   * this bar resolves (that mis-seed silently measured every text width at WPF's default 12 pt and the ranking bar at the vendor's own
   * height — the two things these tests exist to pin). The row font goes in for the same reason: a width measured at some other size
   * is not this bar's width. The class icon's `DamageOverlayImageSize` is deliberately left unset — no assertion here reads the icon.
   */
  private static DamageBar LaidOut(int fontPt, MeterColumn shown, bool miniBars, bool miniFirst)
  {
    var app = Application.Current ?? new Application();
    app.Resources["DamageOverlayBarHeight"] = BarHeight;
    app.Resources["DamageOverlayFontSize"] = (double)fontPt;

    var bar = new DamageBar("DamageOverlayDamageBrush", "DamageOverlayProgressBrush", true);

    if (miniFirst)
    {
      bar.SetMiniBars(miniBars);
    }

    bar.ApplyColumns(new MeterColumnLayout(fontPt, shown));

    if (!miniFirst)
    {
      bar.SetMiniBars(miniBars);
    }

    bar.Visibility = Visibility.Visible;
    bar.Measure(new Size(WindowWidth, RowHeight));
    bar.Arrange(new Rect(new Size(WindowWidth, RowHeight)));
    bar.UpdateLayout();
    return bar;
  }

  [TestMethod]
  public void ASwitchedOffColumnCostsNothingOnTheRealBar()
  {
    const MeterColumn shown = MeterColumn.Damage | MeterColumn.Dps;
    var layout = new MeterColumnLayout(12, shown);

    double percentCell = -1, rdpsCell = -1, damageCell = -1, secCell = -1, blockUsed = -1;
    double percentText = -1, rdpsText = -1, damageText = -1, secText = -1, nameText = -1;
    var percentHidden = false;
    var rdpsHidden = false;

    Sta.Run(() =>
    {
      var bar = LaidOut(12, shown, miniBars: false, miniFirst: false);

      percentCell = bar.colPercent.ActualWidth;
      rdpsCell = bar.colRdps.ActualWidth;
      damageCell = bar.colDamage.ActualWidth;
      secCell = bar.colSec.ActualWidth;
      blockUsed = bar.colPercent.ActualWidth + bar.colDamage.ActualWidth + bar.colDps.ActualWidth
        + bar.colRdps.ActualWidth + bar.colSec.ActualWidth;

      percentHidden = bar.percent.Visibility == Visibility.Collapsed;
      rdpsHidden = bar.rdps.Visibility == Visibility.Collapsed;
      percentText = bar.percent.ActualWidth;
      rdpsText = bar.rdps.ActualWidth;
      damageText = bar.damage.ActualWidth;
      secText = bar.time.ActualWidth;
      nameText = bar.player.ActualWidth;
    });

    Assert.AreEqual(0, percentCell, "a hidden % column still cost cell width on the row");
    Assert.AreEqual(0, rdpsCell, "a hidden RDPS column still cost cell width on the row");
    Assert.IsTrue(percentHidden);
    Assert.IsTrue(rdpsHidden);
    Assert.AreEqual(0, percentText, "hidden text still took room");
    Assert.AreEqual(0, rdpsText, "hidden text still took room");

    Assert.AreEqual(layout.WidthOf(MeterSlot.Damage), damageCell);
    Assert.AreEqual(layout.WidthOf(MeterSlot.Seconds), secCell);
    Assert.AreEqual(layout.NumericWidth, blockUsed, "the numeric block costs more than the visible columns");

    Assert.IsTrue(damageText > 0);
    Assert.IsTrue(secText > 0);
    Assert.IsTrue(nameText > 0, "hiding columns must not take the name away");
  }

  // The right inset is what lines a header word up with its digits, so a hidden column may keep none of it — and neither bar style
  // (normal or thin) may add one back. Both gesture orders are covered because the window uses one and the bar pool sees the other.
  [TestMethod]
  public void TextInsetsFollowTheLayoutInBothGestureOrders()
  {
    const MeterColumn shown = MeterColumn.Percent | MeterColumn.Damage | MeterColumn.Dps | MeterColumn.Rdps;

    foreach (var miniBars in new[] { false, true })
    {
      foreach (var hidden in new MeterColumn[] { MeterColumn.None, MeterColumn.Rdps, MeterColumn.Percent })
      {
        var layout = new MeterColumnLayout(12, shown & ~hidden);

        foreach (var miniFirst in new[] { false, true })
        {
          double percent = 0, damage = 0, dps = 0, rdps = 0, sec = 0;

          Sta.Run(() =>
          {
            var bar = LaidOut(12, shown & ~hidden, miniBars, miniFirst);
            percent = bar.percent.Margin.Right;
            damage = bar.damage.Margin.Right;
            dps = bar.dps.Margin.Right;
            rdps = bar.rdps.Margin.Right;
            sec = bar.time.Margin.Right;
          });

          var expected = new[]
          {
            layout.InsetOf(MeterSlot.Percent), layout.InsetOf(MeterSlot.Damage), layout.InsetOf(MeterSlot.Dps),
            layout.InsetOf(MeterSlot.Rdps), layout.InsetOf(MeterSlot.Seconds),
          };
          var actual = new[] { percent, damage, dps, rdps, sec };

          CollectionAssert.AreEqual(expected, actual,
            $"mini={miniBars} miniFirst={miniFirst} hidden={hidden}: a right inset does not match the shared layout");
        }
      }
    }
  }

  // Every switch off is still a meter: duration, name and ranking bar keep their room, and the block shrinks to seconds alone.
  [TestMethod]
  public void HidingEverythingStillLeavesAWorkingRow()
  {
    var layout = new MeterColumnLayout(12, MeterColumn.None);

    double blockUsed = -1, secCell = -1, secText = -1, nameText = -1, barHeight = -1;

    Sta.Run(() =>
    {
      var bar = LaidOut(12, MeterColumn.None, miniBars: false, miniFirst: false);
      blockUsed = bar.colPercent.ActualWidth + bar.colDamage.ActualWidth + bar.colDps.ActualWidth
        + bar.colRdps.ActualWidth + bar.colSec.ActualWidth;
      secCell = bar.colSec.ActualWidth;
      secText = bar.time.ActualWidth;
      nameText = bar.player.ActualWidth;
      barHeight = bar.progress.ActualHeight;
    });

    Assert.AreEqual(layout.NumericWidth, blockUsed);
    Assert.AreEqual(45, secCell);
    Assert.IsTrue(secText > 0);
    Assert.IsTrue(nameText > 0);

    // The height travels through a DynamicResource, so a live measurement proves the ranking bar stayed wired while its neighbours
    // disappeared — and it may not outgrow what the window asked for, which would cover the header above the first row.
    Assert.IsTrue(barHeight is > 0 and <= BarHeight, $"the ranking bar measured {barHeight} against the {BarHeight} DIP it was given");
  }
}
