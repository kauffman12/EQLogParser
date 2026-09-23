namespace EQLogParser.Wpf.Test
{
  /// <summary>
  /// The rows dropdown's effect on the timeline's row order, and specifically the promise the whole layout
  /// flow rests on: "missing from a saved layout" means off-but-available, so ticking a row back on has to
  /// work from an order list that no longer holds it — and clearing every row has to stay cleared.
  /// </summary>
  [TestClass]
  public class TimelineRowsTest
  {
    private static ComboBoxItemDetails Row(string key, bool on) => new(on, key);

    [TestMethod]
    public void Apply_KeepsTheOrderOfRowsThatStayOn()
    {
      var order = new List<string> { "Spirit of Bear", "Eyes of the Wolf", "Spirit of Aquibai" };

      var changed = TimelineRows.Apply(order, new List<ComboBoxItemDetails>
      {
        Row("Spirit of Bear", true),
        Row("Eyes of the Wolf", true),
        Row("Spirit of Aquibai", true)
      });

      Assert.IsFalse(changed, "nothing was ticked, so there is nothing to redraw");
      CollectionAssert.AreEqual(new List<string> { "Spirit of Bear", "Eyes of the Wolf", "Spirit of Aquibai" }, order);
    }

    [TestMethod]
    public void Apply_UnTickedRowDropsOutOfTheOrder()
    {
      var order = new List<string> { "Spirit of Bear", "Eyes of the Wolf", "Spirit of Aquibai" };

      var changed = TimelineRows.Apply(order, new List<ComboBoxItemDetails>
      {
        Row("Spirit of Bear", true),
        Row("Eyes of the Wolf", false),
        Row("Spirit of Aquibai", true)
      });

      Assert.IsTrue(changed);
      CollectionAssert.AreEqual(new List<string> { "Spirit of Bear", "Spirit of Aquibai" }, order);
    }

    [TestMethod]
    public void Apply_RowTickedBackOnLandsAtTheEnd()
    {
      // The agreed rule: it comes back at the bottom and gets dragged home, because nothing remembers the
      // slot it had before the ✕ took it out.
      var order = new List<string> { "Spirit of Bear", "Spirit of Aquibai" };

      var changed = TimelineRows.Apply(order, new List<ComboBoxItemDetails>
      {
        Row("Spirit of Bear", true),
        Row("Spirit of Aquibai", true),
        Row("Eyes of the Wolf", true)
      });

      Assert.IsTrue(changed);
      CollectionAssert.AreEqual(new List<string> { "Spirit of Bear", "Spirit of Aquibai", "Eyes of the Wolf" }, order);
    }

    [TestMethod]
    public void Apply_AddsASpellTheLayoutNeverKnewAbout()
    {
      // A layout saved before this fight produced the spell: the saved rows stay on, the new one is available
      // from the fight. This is the case that used to have no way back at all.
      var order = new List<string> { "Clarity" };

      var changed = TimelineRows.Apply(order, new List<ComboBoxItemDetails>
      {
        Row("Clarity", true),
        Row("Blessing of Neriad", true)
      });

      Assert.IsTrue(changed);
      CollectionAssert.AreEqual(new List<string> { "Clarity", "Blessing of Neriad" }, order);
    }

    [TestMethod]
    public void Apply_UnselectAllEmptiesTheOrderAndStaysEmpty()
    {
      var order = new List<string> { "Clarity", "Spirit of Bear" };
      var allOff = new List<ComboBoxItemDetails> { Row("Clarity", false), Row("Spirit of Bear", false) };

      Assert.IsTrue(TimelineRows.Apply(order, allOff));
      Assert.AreEqual(0, order.Count, "an empty row list is a choice, not an unset one");

      // Applying the same thing again changes nothing. This is why Display() cannot read Count == 0 as
      // "nobody seeded me yet" — it would resurrect every row on the next redraw.
      Assert.IsFalse(TimelineRows.Apply(order, allOff));
      Assert.AreEqual(0, order.Count);
    }

    [TestMethod]
    public void Apply_SelectAllAddsTheMissingRowsInDropdownOrder()
    {
      // BuildRows lists the rows that are off alphabetically after the ones on, so Select All appends in that order.
      var order = new List<string> { "Clarity" };

      TimelineRows.Apply(order, new List<ComboBoxItemDetails>
      {
        Row("Clarity", true),
        Row("Blessing of Neriad", true),
        Row("Spirit of Bear", true)
      });

      CollectionAssert.AreEqual(new List<string> { "Clarity", "Blessing of Neriad", "Spirit of Bear" }, order);
    }

    [TestMethod]
    public void Apply_LeavesRowsTheFightDoesntHaveAlone()
    {
      // A layout can name a spell this fight never produced; the dropdown does not offer it, so applying
      // touches nothing rather than inventing a row with no data behind it.
      var order = new List<string> { "Clarity", "Spell The Fight Never Saw" };

      Assert.IsFalse(TimelineRows.Apply(order, new List<ComboBoxItemDetails> { Row("Clarity", true) }));
      CollectionAssert.AreEqual(new List<string> { "Clarity", "Spell The Fight Never Saw" }, order);
    }
  }
}
