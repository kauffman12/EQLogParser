using System;
using System.Collections.Generic;
using System.Linq;

using EQLogParser;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParserTests.src.control
{
  /*
   * RowPatch turns one displayed row list into the next while keeping surviving row INSTANCES — that is what lets a grid keep its
   * selection and scroll position instead of rebuilding every row and re-finding the selection by key. The three laws these tests
   * hold: untouched rows are reported as untouched (the "only when changed" the old equality gate never delivered), a patch never
   * reorders anything, and anything it cannot express honestly comes back null so the caller rebuilds wholesale.
   */
  [TestClass]
  public class RowPatchTest
  {
    // A stand-in for a grid row: a stable key, a mutable displayed value, and reference identity that matters.
    private sealed class Row(string key, string text)
    {
      public string Key { get; } = key;
      public string Text { get; set; } = text;
    }

    private static string KeyOf(Row row) => row.Key;

    private static bool SameContent(Row a, Row b) => a.Text == b.Text;

    private static void CopyContent(Row target, Row source) => target.Text = source.Text;

    private static List<Row> Rows(params (string key, string text)[] rows) =>
      rows.Select(r => new Row(r.key, r.text)).ToList();

    // The churn cap these tests use: generous enough that the interesting cases plan, small enough that one test can trip it.
    private const int Cap = 10;

    [TestMethod]
    public void ALikeForLikeList_ReportsZeroChurn()
    {
      var current = Rows(("a", "1"), ("b", "2"), ("c", "3"));
      var next = Rows(("a", "1"), ("b", "2"), ("c", "3"));

      var plan = RowPatch.Build(current, next, KeyOf, SameContent, Cap);

      Assert.IsNotNull(plan);
      Assert.AreEqual(0, plan.Churn);
      Assert.AreEqual(3, plan.Unchanged);
    }

    [TestMethod]
    public void ApplyingALikeForLikeList_LeavesTheSameInstancesInOrder()
    {
      var current = Rows(("a", "1"), ("b", "2"), ("c", "3"));
      var next = Rows(("a", "1"), ("b", "2"), ("c", "3"));
      var before = current.ToList();

      RowPatch.Apply(current, RowPatch.Build(current, next, KeyOf, SameContent, Cap)!, CopyContent);

      CollectionAssert.AreEqual(before, current);   // same objects, same order
      Assert.AreEqual("1", current[0].Text);
      Assert.AreEqual("3", current[2].Text);
    }

    [TestMethod]
    public void OneRowChanged_OneUpdateAndEveryInstanceKept()
    {
      var current = Rows(("a", "1"), ("b", "2"), ("c", "3"));
      var next = Rows(("a", "1"), ("b", "99"), ("c", "3"));
      var kept = current[1];

      var plan = RowPatch.Build(current, next, KeyOf, SameContent, Cap);
      Assert.IsNotNull(plan);
      Assert.AreEqual(1, plan.Updates.Count);
      Assert.AreEqual(0, plan.Removals.Count);
      Assert.AreEqual(0, plan.InsertedCount);
      Assert.AreEqual(2, plan.Unchanged);

      RowPatch.Apply(current, plan, CopyContent);

      Assert.AreEqual(3, current.Count);
      Assert.AreSame(kept, current[1]);          // the row that changed is still THE SAME OBJECT
      Assert.AreEqual("99", kept.Text);          // with the new content copied in
      Assert.AreEqual("99", next[1].Text);       // and the incoming row untouched by the copy
    }

    [TestMethod]
    public void SeveralRowsChanged_AllOfThemLand()
    {
      var current = Rows(("a", "1"), ("b", "2"), ("c", "3"), ("d", "4"));
      var next = Rows(("a", "7"), ("b", "2"), ("c", "8"), ("d", "9"));

      var plan = RowPatch.Build(current, next, KeyOf, SameContent, Cap);
      Assert.IsNotNull(plan);
      Assert.AreEqual(3, plan.Updates.Count);
      Assert.AreEqual(1, plan.Unchanged);

      RowPatch.Apply(current, plan, CopyContent);
      CollectionAssert.AreEqual(new[] { "7", "2", "8", "9" }, current.Select(r => r.Text).ToList());
    }

    [TestMethod]
    public void AppendedRows_LandAtTheEnd()
    {
      var current = Rows(("a", "1"), ("b", "2"));
      var next = Rows(("a", "1"), ("b", "2"), ("c", "3"), ("d", "4"));

      var plan = RowPatch.Build(current, next, KeyOf, SameContent, Cap);
      Assert.IsNotNull(plan);
      Assert.AreEqual(2, plan.InsertedCount);

      RowPatch.Apply(current, plan, CopyContent);
      CollectionAssert.AreEqual(new[] { "a", "b", "c", "d" }, current.Select(r => r.Key).ToList());
    }

    [TestMethod]
    public void ARowInsertedInTheMiddle_GoesInTheMiddle()
    {
      var current = Rows(("a", "1"), ("z", "2"));
      var next = Rows(("a", "1"), ("m", "50"), ("z", "2"));

      var plan = RowPatch.Build(current, next, KeyOf, SameContent, Cap);
      Assert.IsNotNull(plan);
      Assert.AreEqual(1, plan.InsertedCount);

      RowPatch.Apply(current, plan, CopyContent);
      CollectionAssert.AreEqual(new[] { "a", "m", "z" }, current.Select(r => r.Key).ToList());
    }

    [TestMethod]
    public void ARowMissingFromTheNewList_Leaves()
    {
      var current = Rows(("a", "1"), ("b", "2"), ("c", "3"));
      var next = Rows(("a", "1"), ("c", "3"));

      var plan = RowPatch.Build(current, next, KeyOf, SameContent, Cap);
      Assert.IsNotNull(plan);
      Assert.AreEqual(1, plan.Removals.Count);

      RowPatch.Apply(current, plan, CopyContent);
      CollectionAssert.AreEqual(new[] { "a", "c" }, current.Select(r => r.Key).ToList());
    }

    [TestMethod]
    public void InsertionsRemovalsAndEditsAtOnce_EndAsTheNewListExactly()
    {
      var current = Rows(("a", "1"), ("gone", "2"), ("b", "3"), ("c", "4"), ("d", "5"));
      var next = Rows(("new1", "9"), ("a", "1"), ("b", "33"), ("new2", "8"), ("d", "5"));

      var plan = RowPatch.Build(current, next, KeyOf, SameContent, Cap);
      Assert.IsNotNull(plan);
      Assert.AreEqual(2, plan.Removals.Count);       // gone, c
      Assert.AreEqual(2, plan.InsertedCount);        // new1, new2
      Assert.AreEqual(1, plan.Updates.Count);        // b
      Assert.AreEqual(2, plan.Unchanged);            // a, d

      RowPatch.Apply(current, plan, CopyContent);

      CollectionAssert.AreEqual(new[] { "new1", "a", "b", "new2", "d" }, current.Select(r => r.Key).ToList());
      CollectionAssert.AreEqual(new[] { "9", "1", "33", "8", "5" }, current.Select(r => r.Text).ToList());
    }

    [TestMethod]
    public void AnEmptyNewList_EmptiesTheOldOne()
    {
      var current = Rows(("a", "1"), ("b", "2"));
      var plan = RowPatch.Build(current, [], KeyOf, SameContent, Cap);
      Assert.IsNotNull(plan);

      RowPatch.Apply(current, plan, CopyContent);
      Assert.AreEqual(0, current.Count);
    }

    [TestMethod]
    public void AnEmptyOldList_TakesTheWholeNewOne()
    {
      var current = new List<Row>();
      var next = Rows(("a", "1"), ("b", "2"));

      var plan = RowPatch.Build(current, next, KeyOf, SameContent, Cap);   // churn 2 of cap 10: allowed
      Assert.IsNotNull(plan);

      RowPatch.Apply(current, plan, CopyContent);
      CollectionAssert.AreEqual(new[] { "a", "b" }, current.Select(r => r.Key).ToList());
    }

    [TestMethod]
    public void AReorderedRow_IsNotAPatch()
    {
      // Same keys, different relative order: copying content in place would shuffle words under a list that did not move.
      var current = Rows(("a", "1"), ("b", "2"), ("c", "3"));
      var next = Rows(("c", "3"), ("a", "1"), ("b", "2"));

      Assert.IsNull(RowPatch.Build(current, next, KeyOf, SameContent, Cap));
      CollectionAssert.AreEqual(new[] { "a", "b", "c" }, current.Select(r => r.Key).ToList());  // untouched
    }

    [TestMethod]
    public void AReorderWithTheOldFirstRowGone_IsStillRefused()
    {
      var current = Rows(("a", "1"), ("b", "2"), ("c", "3"));
      var next = Rows(("c", "3"), ("b", "2"));     // a gone, and b/c swapped

      Assert.IsNull(RowPatch.Build(current, next, KeyOf, SameContent, Cap));
    }

    [TestMethod]
    public void AKeyThatAppearsTwice_IsNotAPatch()
    {
      var current = Rows(("a", "1"), ("b", "2"));
      Assert.IsNull(RowPatch.Build(current, Rows(("a", "1"), ("a", "2")), KeyOf, SameContent, Cap));

      var duplicateCurrent = Rows(("a", "1"), ("a", "2"));
      Assert.IsNull(RowPatch.Build(duplicateCurrent, Rows(("a", "1"), ("b", "2")), KeyOf, SameContent, Cap));
    }

    [TestMethod]
    public void ChurnBeyondTheCap_IsRefusedAndChangesNothing()
    {
      var current = Rows(("a", "1"), ("b", "2"), ("c", "3"), ("d", "4"));
      var next = Rows(("w", "9"), ("x", "9"), ("y", "9"), ("z", "9"));

      Assert.IsNull(RowPatch.Build(current, next, KeyOf, SameContent, maxChurn: 3));
      CollectionAssert.AreEqual(new[] { "a", "b", "c", "d" }, current.Select(r => r.Key).ToList());

      // The same patch is fine when the cap admits it — the refusal is about size, not shape.
      Assert.IsNotNull(RowPatch.Build(current, next, KeyOf, SameContent, maxChurn: 8));
    }

    [TestMethod]
    public void UnchangedCountsTheRowsNobodyTouched_AndIsNotChurn()
    {
      var current = Rows(("a", "1"), ("b", "2"), ("c", "3"), ("d", "4"), ("e", "5"));
      var next = Rows(("a", "1"), ("b", "9"), ("c", "3"), ("x", "0"), ("e", "5"));   // d leaves, x arrives

      var plan = RowPatch.Build(current, next, KeyOf, SameContent, Cap);
      Assert.IsNotNull(plan);
      Assert.AreEqual(3, plan.Unchanged);          // a, c, e
      Assert.AreEqual(1, plan.Updates.Count);      // b
      Assert.AreEqual(1, plan.Removals.Count);     // d
      Assert.AreEqual(1, plan.InsertedCount);      // x
      Assert.AreEqual(3, plan.Churn);              // 1 + 1 + 1
    }

    [TestMethod]
    public void TheCapIsComparedAgainstChurn()
    {
      var current = Rows(("a", "1"), ("b", "2"));
      var next = Rows(("a", "1"), ("b", "2"), ("c", "3"));

      Assert.IsNull(RowPatch.Build(current, next, KeyOf, SameContent, maxChurn: 0));
      Assert.IsNotNull(RowPatch.Build(current, next, KeyOf, SameContent, maxChurn: 1));
    }

    [TestMethod]
    public void ALargeListWithAFewChangedRows_PlansAndApplies()
    {
      // The shape of a live raid pass: hundreds of rows, a handful moved. Churn must stay small enough to patch.
      var current = Rows(Enumerable.Range(0, 800).Select(i => ($"k{i}", i % 7 == 0 ? "old" : "same")).ToArray());
      var next = Enumerable.Range(0, 800)
                           .Select(i => new Row($"k{i}", i % 7 == 0 ? "new" : "same")).ToList();

      var plan = RowPatch.Build(current, next, (Func<Row, string>) KeyOf, SameContent, maxChurn: 200);
      Assert.IsNotNull(plan);
      Assert.AreEqual(800 / 7 + 1, plan.Updates.Count);          // every 7th row
      Assert.AreEqual(0, plan.Removals.Count + plan.InsertedCount);

      RowPatch.Apply(current, plan, CopyContent);
      Assert.IsTrue(current.All(r => r.Text == "new" || int.TryParse(r.Key[1..], out var n) && n % 7 != 0));
      CollectionAssert.AreEqual(next.Select(r => r.Key).ToList(), current.Select(r => r.Key).ToList());
    }
  }
}
