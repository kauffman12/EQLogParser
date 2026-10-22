// The meter's column table and the geometry it costs. What breaks if these assertions go: an install reads a switch back
// under a different name than it was saved (a column that saves off and comes on), every existing install silently loses or
// gains a column because someone changed a default, or a hidden column still reserves its width and the names on the row
// disappear behind the numbers.

using System.Text.RegularExpressions;

namespace EQLogParser;

[TestClass]
public sealed class MeterColumnLayoutTest
{
  // The whole key vocabulary, exactly — every one of them an OverlayShow<What>Column key.
  private static readonly string[] Keys = ["OverlayShowPercentColumn", "OverlayShowDamageColumn", "OverlayShowDpsColumn", "OverlayShowRdpsColumn", "OverlayShowSdpsColumn"];

  // Every combination of the five switches, 0 (nothing) through 31 (% Damage DPS RDPS SDPS all on). The top mask is what a meter with
  // nothing hidden looks like, so a loop that stopped at RDPS's own bit would skip fifteen of them.
  private const int MaskCount = 32;

  private readonly string?[] _saved = new string?[Keys.Length];

  [TestInitialize]
  public void ParkTheColumnKeys()
  {
    for (var i = 0; i < Keys.Length; i++)
    {
      _saved[i] = ConfigUtil.GetSetting(Keys[i]);
      ConfigUtil.RemoveSetting(Keys[i]);
    }

  }

  [TestCleanup]
  public void PutTheColumnKeysBack()
  {
    for (var i = 0; i < Keys.Length; i++)
    {
      ConfigUtil.SetSetting(Keys[i], _saved[i]);
    }

  }

  [TestMethod]
  public void TheShippedTableIsDrawnOrderWithOneKeyAndDefaultEach()
  {
    Assert.HasCount(5, MeterColumns.All);
    CollectionAssert.AreEqual(new[] { "%", "Damage", "DPS", "RDPS", "SDPS" }, MeterColumns.All.Select(c => c.Header).ToArray());

    var bits = MeterColumns.All.Select(c => c.Bit).ToArray();
    Assert.HasCount(bits.Length, bits.Distinct().ToArray());
    Assert.HasCount(bits.Length, MeterColumns.All.Select(c => c.Key).Distinct().ToArray());

    // The whole key vocabulary, exactly. `Sec` has no key by decision, and another key would be another switch nobody asked for —
    // which is also why this compares the set rather than asking whether some key "mentions seconds".
    CollectionAssert.AreEqual(Keys, MeterColumns.All.Select(c => c.Key).ToArray());
  }

  /*
   * The table names the CELL each switchable column draws in, so the picker's order and the meter's layout are one answer rather than
   * two that happen to match today, and every header word on the meter — including the one cell no switch owns — is spelled here
   * instead of copied into XAML where it could be renamed on its own.
   */
  [TestMethod]
  public void TheTableNamesEveryCellItSwitchesAndSpellsEveryHeaderWord()
  {
    CollectionAssert.AreEqual(
      new[] { MeterSlot.Percent, MeterSlot.Damage, MeterSlot.Dps, MeterSlot.Rdps, MeterSlot.Sdps },
      MeterColumns.All.Select(c => c.Slot).ToArray());

    Assert.IsFalse(MeterColumns.All.Any(c => c.Slot == MeterSlot.Seconds), "elapsed seconds is not a switch and may not join the table");

    // Widths and insets are indexed by the slot's own ordinal, so inserting a cell anywhere but the end silently reorders the block.
    var drawnOrder = new[] { MeterSlot.Percent, MeterSlot.Damage, MeterSlot.Dps, MeterSlot.Rdps, MeterSlot.Sdps, MeterSlot.Seconds };
    for (var i = 0; i < drawnOrder.Length; i++)
    {
      Assert.AreEqual(i, (int)drawnOrder[i], $"{drawnOrder[i]} is no longer the {i + 1}th cell of the numeric block");
    }

    // And the block is EXACTLY these six cells. MeterColumnLayout names every one in its width and inset tables and refuses any other,
    // so this is the assertion that turns "somebody added a MeterSlot" into a named failure here instead of an exception on the first
    // meter open: a new cell arrives with a preset, an inset, a header word and expectations, all at once.
    CollectionAssert.AreEqual(drawnOrder, Enum.GetValues<MeterSlot>(),
      "the numeric block gained or lost a cell outside these expectations");

    Assert.AreEqual("%", MeterColumns.HeaderOf(MeterSlot.Percent));
    Assert.AreEqual("Damage", MeterColumns.HeaderOf(MeterSlot.Damage));
    Assert.AreEqual("DPS", MeterColumns.HeaderOf(MeterSlot.Dps));
    Assert.AreEqual("RDPS", MeterColumns.HeaderOf(MeterSlot.Rdps));
    Assert.AreEqual("SDPS", MeterColumns.HeaderOf(MeterSlot.Sdps));
    Assert.AreEqual("Sec", MeterColumns.HeaderOf(MeterSlot.Seconds));
  }

  /*
   * The dropdown gets to explain the one word that is not self-evident — RDPS is a rolling window while every other number on the row covers the whole
   * encounter, which is the first question after somebody switches the column on — and the meter's header cell keeps printing the bare word, because a row of
   * digits has no room for a parenthesis. Both halves still come from this table, so the picker cannot rename a column out of its own header or its settings key,
   * and the fold stays by bit. Exactly one qualifier is allowed: a parenthesised phrase on every item is the wall of text the bare list avoided.
   */
  [TestMethod]
  public void ThePickerExplainsRdpsWhileTheMeterKeepsTheBareWord()
  {
    CollectionAssert.AreEqual(new[] { "%", "Damage", "DPS", "RDPS (6s rolling)", "SDPS" },
      MeterColumns.All.Select(c => c.PickerLabel).ToArray());

    Assert.AreEqual(1, MeterColumns.All.Count(c => !string.IsNullOrEmpty(c.PickerNote)),
      "only a word that genuinely needs explaining carries one");

    foreach (var column in MeterColumns.All)
    {
      Assert.IsTrue(column.PickerLabel.StartsWith(column.Header, StringComparison.Ordinal),
        $"{column.Header}: the picker may not call a column something its own header does not say");
    }

    // And the qualifier stops at the dropdown: HeaderOf is what the meter draws.
    Assert.AreEqual("RDPS", MeterColumns.HeaderOf(MeterSlot.Rdps));
  }

  /*
   * A cell the table does not know about echoes itself instead of throwing: HeaderOf runs inside the meter's window constructor, so a
   * new MeterSlot added without a table row must cost one odd header word on a development screen, not the whole board. (The tests
   * above are what make that state unreachable in shipped code; this is what it degrades to.) The same law `HitLabels.Unmapped` follows.
   */
  [TestMethod]
  public void AnUnmappedCellEchoesItselfInsteadOfThrowingOnTheLayoutPath()
  {
    var unmapped = (MeterSlot)99;
    Assert.AreEqual("99", MeterColumns.HeaderOf(unmapped));
    Assert.IsFalse(string.IsNullOrWhiteSpace(MeterColumns.HeaderOf(unmapped)));
  }

  [TestMethod]
  public void NoKeysYetMeansTheMeterNobodyHasTouched()
  {
    Assert.AreEqual(MeterColumn.Damage | MeterColumn.Dps, MeterColumns.Load());
    Assert.AreEqual(MeterColumns.Default, MeterColumns.Load());
  }

  // A missing key is what every install predating a rate column has, so opt-in is what they all get: neither an extra rate nor the
  // rolling one can appear in front of somebody who never asked for it.
  [TestMethod]
  public void ExtraRateColumnsAreOptInAndPercentStaysOff()
  {
    Assert.IsFalse(MeterColumns.Includes(MeterColumns.Default, MeterColumn.Rdps));
    Assert.IsFalse(MeterColumns.Includes(MeterColumns.Default, MeterColumn.Sdps));
    Assert.IsFalse(MeterColumns.All.First(c => c.Bit == MeterColumn.Sdps).DefaultOn);
    Assert.IsFalse(MeterColumns.Includes(MeterColumns.Default, MeterColumn.Percent));
    Assert.IsFalse(MeterColumns.All.First(c => c.Bit == MeterColumn.Rdps).DefaultOn);
    Assert.IsFalse(MeterColumns.All.First(c => c.Bit == MeterColumn.Percent).DefaultOn);
    Assert.IsTrue(MeterColumns.All.First(c => c.Bit == MeterColumn.Damage).DefaultOn);
    Assert.IsTrue(MeterColumns.All.First(c => c.Bit == MeterColumn.Dps).DefaultOn);
  }

  [TestMethod]
  public void EveryMaskSurvivesSaveAndReload()
  {
    for (var value = 0; value < MaskCount; value++)
    {
      var shown = (MeterColumn)value;
      MeterColumns.Save(shown);
      Assert.AreEqual(shown, MeterColumns.Load(), $"mask {shown} did not come back as it was saved");
    }
  }

  // An unreadable value is answered per column, not by a blanket "missing means false" that would switch Damage and DPS off
  // on every row over one corrupt line.
  [TestMethod]
  public void AnUnreadableKeyFallsBackToItsOwnColumnsDefault()
  {
    foreach (var key in Keys)
    {
      ConfigUtil.SetSetting(key, "maybe");
    }

    Assert.AreEqual(MeterColumn.Damage | MeterColumn.Dps, MeterColumns.Load());
  }

  /*
   * One fold from "is this column on" to the mask, shared by the settings-file reader and the setup window's checkboxes — what keeps a previewed
   * tick and a saved tick meaning the same thing is that neither owns its own loop. Every mask has to be reachable from per-column answers, an
   * all-off answer must reach "draw nothing", and reading the keys back through this fold must equal Load().
   */
  [TestMethod]
  public void OneFoldTurnsPerColumnAnswersIntoTheMaskBothDoorsSpeak()
  {
    for (var value = 0; value < MaskCount; value++)
    {
      var wanted = (MeterColumn)value;
      Assert.AreEqual(wanted, MeterColumns.FromSwitches(c => MeterColumns.Includes(wanted, c.Bit)),
        $"answers {wanted} folded into something else");
    }

    Assert.AreEqual(MeterColumn.None, MeterColumns.FromSwitches(_ => false));
    Assert.AreEqual(MeterColumn.Percent | MeterColumn.Damage | MeterColumn.Dps | MeterColumn.Rdps | MeterColumn.Sdps,
      MeterColumns.FromSwitches(_ => true));

    MeterColumns.Save(MeterColumn.Damage | MeterColumn.Rdps);
    Assert.AreEqual(MeterColumns.Load(), MeterColumns.FromSwitches(c => ConfigUtil.IfSetOrElse(c.Key, c.DefaultOn)),
      "Load() and the picker's fold no longer answer the same question");
  }

  // Every column key wears the same shape, so a column switch cannot be spelled like the panel selector (`OverlayShowingDps` = damage or tanking) that already
  // lives beside it in settings.txt, and exactly one column carries a retired name — percent, the only one this table renamed.
  [TestMethod]
  public void EveryColumnKeyIsASuffixedColumnSwitch()
  {
    foreach (var key in Keys)
    {
      Assert.IsTrue(Regex.IsMatch(key, "^OverlayShow[A-Za-z]+Column$"), $"{key} is not an OverlayShow<What>Column key");
      Assert.AreNotEqual("OverlayShowingDps", key, "the panel selector's key may not double as a column switch");
    }
  }

  [TestMethod]
  public void TheMeasuredWidthsAreTheShippedOnes()
  {
    // docs/DesignNotes.md → "Six-second RDPS: per-life buckets beside the meter's own amounts".
    AssertBlock(10, 40, 50, 46);
    AssertBlock(12, 45, 60, 52);
    AssertBlock(14, 50, 70, 60);
    AssertBlock(16, 55, 75, 66);
  }

  /*
   * The shipped right insets, pinned as values rather than compared against themselves. Every one is a Windows measurement: negative
   * means the number ends past its own cell edge inside the neighbour's empty space, and the header reuses the same figure so a word
   * lands on its digits. A hidden column answers zero (`InsetOf` is gated on `Shows`), which is what keeps mini mode from leaving a hole.
   */
  [TestMethod]
  public void TheShippedRightInsetsAreTheMeasuredOnes()
  {
    const MeterColumn allOn = MeterColumn.Percent | MeterColumn.Damage | MeterColumn.Dps | MeterColumn.Rdps | MeterColumn.Sdps;
    var all = new MeterColumnLayout(12, allOn);

    Assert.AreEqual(-13, all.InsetOf(MeterSlot.Percent));
    Assert.AreEqual(-12, all.InsetOf(MeterSlot.Damage));
    Assert.AreEqual(-6, all.InsetOf(MeterSlot.Dps));
    Assert.AreEqual(-6, all.InsetOf(MeterSlot.Rdps));
    Assert.AreEqual(-6, all.InsetOf(MeterSlot.Sdps), "the two rate cells end at the same edge as DPS does");
    Assert.AreEqual(2, all.InsetOf(MeterSlot.Seconds));
  }

  [TestMethod]
  public void AHiddenColumnReservesNoWidthAndNoInset()
  {
    foreach (var font in new[] { 10, 12, 14, 16 })
    {
      for (var value = 0; value < MaskCount; value++)
      {
        var shown = (MeterColumn)value;
        var layout = new MeterColumnLayout(font, shown);

        foreach (MeterSlot slot in Enum.GetValues<MeterSlot>())
        {
          var expected = slot switch
          {
            MeterSlot.Percent => MeterColumns.Includes(shown, MeterColumn.Percent),
            MeterSlot.Damage => MeterColumns.Includes(shown, MeterColumn.Damage),
            MeterSlot.Dps => MeterColumns.Includes(shown, MeterColumn.Dps),
            MeterSlot.Rdps => MeterColumns.Includes(shown, MeterColumn.Rdps),
            MeterSlot.Sdps => MeterColumns.Includes(shown, MeterColumn.Sdps),
            _ => true, // duration is always drawn
          };

          Assert.AreEqual(expected, layout.Shows(slot), $"font {font}, mask {shown}, slot {slot}");
          if (!expected)
          {
            Assert.AreEqual(0, layout.WidthOf(slot), $"hidden {slot} still has width at {font}pt");
            Assert.AreEqual(0, layout.InsetOf(slot), $"hidden {slot} still pulls an inset at {font}pt");
          }
        }
      }
    }
  }

  [TestMethod]
  public void TheMinimumIsTheVisibleBlockPlusRoomForAName()
  {
    foreach (var font in new[] { 10, 12, 14, 16 })
    {
      for (var value = 0; value < MaskCount; value++)
      {
        var layout = new MeterColumnLayout(font, (MeterColumn)value);
        var slots = new[] { MeterSlot.Percent, MeterSlot.Damage, MeterSlot.Dps, MeterSlot.Rdps, MeterSlot.Sdps, MeterSlot.Seconds };
        Assert.AreEqual(slots.Sum(layout.WidthOf), layout.NumericWidth, $"font {font}, mask {(MeterColumn)value}");
        Assert.AreEqual(layout.NumericWidth + MeterColumnLayout.NameAreaMinWidthDip, layout.MinimumWindowWidth);
      }
    }
  }

  [TestMethod]
  public void OneRateColumnStillPrintsItsSecondsAndKeepsANameFloor()
  {
    var rdpsOnly = new MeterColumnLayout(12, MeterColumn.Rdps);
    Assert.AreEqual(0, rdpsOnly.WidthOf(MeterSlot.Damage));
    Assert.AreEqual(0, rdpsOnly.WidthOf(MeterSlot.Percent));
    Assert.AreEqual(52, rdpsOnly.WidthOf(MeterSlot.Rdps));
    Assert.AreEqual(0, rdpsOnly.WidthOf(MeterSlot.Sdps), "asking for one rate column must not hand over the other");
    Assert.AreEqual(45, rdpsOnly.WidthOf(MeterSlot.Seconds));
    Assert.AreEqual(97 + MeterColumnLayout.NameAreaMinWidthDip, rdpsOnly.MinimumWindowWidth);

    var dpsOnly = new MeterColumnLayout(12, MeterColumn.Dps);
    Assert.AreEqual(60, dpsOnly.WidthOf(MeterSlot.Dps));
    Assert.AreEqual(0, dpsOnly.WidthOf(MeterSlot.Rdps));

    // Every switch off is still a meter: duration and the name/icon allowance remain.
    var bare = new MeterColumnLayout(12, MeterColumn.None);
    Assert.AreEqual(45, bare.NumericWidth);
    Assert.IsTrue(bare.Shows(MeterSlot.Seconds));
    Assert.AreEqual(141, bare.MinimumWindowWidth);
  }

  // Switching a column off must not resize the ones that remain: each column keeps its own cell and its own inset, which is what
  // lets a header word and its digits stay together at any set of columns. The header's font does not move either.
  [TestMethod]
  public void AColumnGestureNeverResizesTheColumnsThatStay()
  {
    const MeterColumn all = MeterColumn.Percent | MeterColumn.Damage | MeterColumn.Dps | MeterColumn.Rdps | MeterColumn.Sdps;

    foreach (var font in new[] { 10, 12, 14, 16 })
    {
      var full = new MeterColumnLayout(font, all);
      Assert.AreEqual(font + 1, full.HeaderFontPt);

      foreach (var hidden in new[] { MeterColumn.Percent, MeterColumn.Damage, MeterColumn.Dps, MeterColumn.Rdps, MeterColumn.Sdps })
      {
        var layout = new MeterColumnLayout(font, all & ~hidden);
        foreach (MeterSlot slot in Enum.GetValues<MeterSlot>())
        {
          if (layout.Shows(slot))
          {
            Assert.AreEqual(full.WidthOf(slot), layout.WidthOf(slot), $"{slot} moved width when {hidden} was hidden at {font}pt");
            Assert.AreEqual(full.InsetOf(slot), layout.InsetOf(slot), $"{slot} moved inset when {hidden} was hidden at {font}pt");
          }
        }

        Assert.AreEqual(font + 1, layout.HeaderFontPt);
      }
    }
  }

  [TestMethod]
  public void TheLiveMinimumSeparatesTheToolbarFromEveryColumnMask()
  {
    const double chrome = 6; // outer border and the numeric grid's right margin
    foreach (var font in new[] { 10, 12, 14, 16 })
    {
      for (var value = 0; value < MaskCount; value++)
      {
        var layout = new MeterColumnLayout(font, (MeterColumn)value);
        foreach (var toolbarWidth in new[] { 70.0, 150.0, 225.5 })
        {
          var minimum = layout.MinimumWindowWidthFor(toolbarWidth, chrome);
          var numericLeft = minimum - chrome - layout.NumericWidth;
          Assert.IsTrue(numericLeft >= toolbarWidth + 4, $"font {font}, mask {value}: toolbar covers numbers");
          Assert.IsTrue(minimum >= layout.MinimumWindowWidth, "the header cannot spend the name allowance");
        }
      }
    }
  }

  [TestMethod]
  public void AnAbsentOrInvalidToolbarKeepsTheRowMinimum()
  {
    var layout = new MeterColumnLayout(12, MeterColumn.None);
    foreach (var width in new[] { 0, -1, double.NaN, double.PositiveInfinity })
    {
      Assert.AreEqual(layout.MinimumWindowWidth, layout.MinimumWindowWidthFor(width, 6));
    }

    Assert.AreEqual(layout.MinimumWindowWidth, layout.MinimumWindowWidthFor(150, double.NaN));
    Assert.AreEqual(layout.MinimumWindowWidth, layout.MinimumWindowWidthFor(150, -1));
    Assert.AreEqual(205, layout.MinimumWindowWidthFor(150, 6));
    Assert.AreEqual(280, layout.MinimumWindowWidthFor(225, 6), "a changed measurement must change the floor");
    Assert.AreEqual(205, layout.MinimumWindowWidthFor(150, 6), "the floor is not a cumulative width reserve");
  }

  // An unreadable font size lays out like the biggest shipped one rather than throwing on the UI path.
  [TestMethod]
  public void AnUnreadableFontSizeLaysOutLikeTheCeiling()
  {
    var bad = new MeterColumnLayout(11, MeterColumns.Default);
    var ceiling = new MeterColumnLayout(16, MeterColumns.Default);
    Assert.AreEqual(ceiling.NumericWidth, bad.NumericWidth);
  }

  private static void AssertBlock(int font, double narrow, double wide, double rolling)
  {
    var layout = new MeterColumnLayout(font, MeterColumn.Percent | MeterColumn.Damage | MeterColumn.Dps | MeterColumn.Rdps | MeterColumn.Sdps);
    Assert.AreEqual(narrow, layout.WidthOf(MeterSlot.Percent), $"{font}pt percent");
    Assert.AreEqual(wide, layout.WidthOf(MeterSlot.Damage), $"{font}pt damage");
    Assert.AreEqual(wide, layout.WidthOf(MeterSlot.Dps), $"{font}pt dps");
    Assert.AreEqual(rolling, layout.WidthOf(MeterSlot.Rdps), $"{font}pt rolling");
    Assert.AreEqual(rolling, layout.WidthOf(MeterSlot.Sdps), $"{font}pt raid-time rate");
    Assert.AreEqual(narrow, layout.WidthOf(MeterSlot.Seconds), $"{font}pt seconds");

    var expected = narrow * 2 + wide * 2 + rolling * 2;
    Assert.AreEqual(expected, layout.NumericWidth, $"{font}pt numeric block");
    Assert.AreEqual(expected + MeterColumnLayout.NameAreaMinWidthDip, layout.MinimumWindowWidth, $"{font}pt minimum");
  }
}
