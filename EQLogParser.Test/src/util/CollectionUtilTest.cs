using EQLogParser;

namespace EQLogParserTest
{
  [TestClass]
  public class CollectionUtilTest
  {
    [TestMethod]
    [DataRow(new int[] { 10, 20, 30, 40, 50 }, 30, 2)]
    [DataRow(new int[] { 10, 20, 30, 40, 50 }, 25, ~2)]
    [DataRow(new int[] { 10, 20, 30 }, 5, ~0)]
    [DataRow(new int[] { 10, 20, 30 }, 35, ~3)]
    [DataRow(new int[] { }, 10, ~0)]
    [DataRow(new int[] { 42 }, 42, 0)]
    [DataRow(new int[] { 42 }, 10, ~0)]
    [DataRow(new int[] { 42 }, 100, ~1)]
    [DataRow(new int[] { 10, 20, 30 }, 10, 0)]
    [DataRow(new int[] { 10, 20, 30 }, 30, 2)]
    public void SearchReturnsAMatchOrTheComplementOfItsInsertionPoint(int[] items, int target, int expected)
    {
      Assert.AreEqual(expected, CollectionUtil.BinarySearch(items.ToList(), value => value.CompareTo(target)));
    }

    [TestMethod]
    public void ARepeatedValueMayResolveToAnyMatchingElement()
    {
      var values = new List<int> { 10, 20, 20, 20, 30 };
      var index = CollectionUtil.BinarySearch(values, value => value.CompareTo(20));
      Assert.IsTrue(index >= 1 && index <= 3);
    }

    [TestMethod]
    [DataRow("cherry", 2)]
    [DataRow("blueberry", ~2)]
    public void AnOrdinalStringComparerControlsTheSearch(string target, int expected)
    {
      var values = new List<string> { "apple", "banana", "cherry", "date", "elderberry" };
      Assert.AreEqual(expected,
        CollectionUtil.BinarySearch(values, value => string.Compare(value, target, StringComparison.Ordinal)));
    }

    [TestMethod]
    public void TheComparisonCanReadDescendingObjectKeys()
    {
      var values = new List<TestItem> { new(50), new(40), new(30), new(20), new(10) };
      Assert.AreEqual(2, CollectionUtil.BinarySearch(values, item => 30.CompareTo(item.Value)));
    }

    [TestMethod]
    [DataRow(10_000, 50_000, 5000)]
    [DataRow(10_000, 50_005, ~5001)]
    public void LargeInputsKeepTheSameSearchContract(int count, int target, int expected)
    {
      var values = Enumerable.Range(0, count).Select(i => i * 10).ToList();
      Assert.AreEqual(expected, CollectionUtil.BinarySearch(values, value => value.CompareTo(target)));
    }

    private sealed record TestItem(int Value);
  }
}
