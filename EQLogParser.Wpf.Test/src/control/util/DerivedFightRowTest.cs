using System.ComponentModel;

namespace EQLogParser.Wpf.Test;

/*
 * The row class behind the derived fight list must notify the way WPF listens.
 *
 * Why this is a law and not a detail: WPF's binding engine decides whether to subscribe by checking that the bound
 * item IMPLEMENTS INotifyPropertyChanged — it never discovers a bare `PropertyChanged` event by name, so a class
 * that declares the event without the interface fails SILENTLY. The realized rows keep whatever they were painted
 * with; only containers realized AFTER the change (scrolled into view, re-virtualized) ever show it. That is
 * exactly the bug this pins: the search mark appeared to freeze — Enter cycled correctly through the greedlings,
 * ScrollInView no-oped on visible ones as it should, and green appeared only on a row scrolled to from off-screen
 * (reported 2026-10-02; DerivedFightRow declared the event but did not implement the interface).
 */
[TestClass]
public class DerivedFightRowTest
{
  [TestMethod]
  public void TheBoundRowImplementsTheInterfaceWpfListensFor()
  {
    var row = new DerivedFightRow();
    Assert.IsInstanceOfType(row, typeof(INotifyPropertyChanged),
      "a row with a mutable bound property (IsSearchResult) must implement INotifyPropertyChanged, not just declare the event");
  }

  [TestMethod]
  public void TheSearchMarkRaisesOneEventPerRealChange()
  {
    var row = new DerivedFightRow();
    var fired = 0;
    string? changed = null;
    ((INotifyPropertyChanged)row).PropertyChanged += (_, e) => { fired++; changed = e.PropertyName; };

    row.IsSearchResult = true;
    Assert.AreEqual(1, fired);
    Assert.AreEqual(nameof(row.IsSearchResult), changed);

    row.IsSearchResult = true;   // same value: no event - the setter's equality guard is what keeps
    Assert.AreEqual(1, fired);   // ClearSearchMark from the next walk from re-painting every pass

    row.IsSearchResult = false;  // and the clear must notify too, or the old mark outlives the search
    Assert.AreEqual(2, fired);
  }
}
