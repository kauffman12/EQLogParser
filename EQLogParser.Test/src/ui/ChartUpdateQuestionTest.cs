using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser;

/*
 * The rule that decides whether a chart UPDATE is a question it already answered.
 *
 * Field measurement (whole night, Select All): two `DamageChart UPDATE` passes over 4,660,915 records — walk 1,540 ms and 1,078 ms, 97 % of each
 * — drawing the identical 5 lines / 36,163 points, with no stats build between them. Dropping the second one is the whole of B13; this file pins
 * what "the same question" means, because the failure mode of getting it wrong is a chart that quietly keeps showing an older drawing.
 *
 * Three shapes matter and they are easy to get wrong in opposite directions:
 *
 *   - **Too few terms** ⇒ a real change is skipped. A generation-only key would ignore a pane's row selection (the wrong chart); a
 *     selection-only key would ignore new records (a stale chart). Every term the pass reads has to be in here, which is why the terms are
 *     asserted one at a time rather than as "keys differ for some input".
 *   - **Too many terms** ⇒ merely a lost skip: the pass runs as it always did. Reordering a selection is in that class, and it is asserted
 *     because it is the one term whose inclusion is a deliberate allowance (panes hand rows in grid order, so it costs nothing in practice).
 *   - **Unknown content** ⇒ never reusable. An unstamped event is `null`, and a null is not equal to anything, including another null: "we do
 *     not know what this walks" must walk. That asymmetry is what makes a missing stamp a millisecond bug rather than a wrong-answer bug.
 *
 * Row identity is the NAME, not the object: panes build fresh `PlayerStats` wrappers around the same players constantly, and treating two equal
 * rows as different questions would leave the door open — the bug this exists to close — while treating two different players as the same one
 * draws the wrong chart. Hence both directions are asserted.
 */
[TestClass]
public class ChartUpdateQuestionTest
{
  private const string View = "By Player";
  private const int Top = 5;

  private static PlayerStats Person(string name) => new() { Name = name };

  private static DataPointEvent Event(long generation, params string[] selected)
  {
    var e = new DataPointEvent { Action = "UPDATE", DataGeneration = generation };
    foreach (var name in selected)
    {
      e.Selected.Add(Person(name));
    }

    return e;
  }

  [TestMethod]
  public void TwoEventsAskingTheSameQuestionKeyTheSame()
  {
    var first = ChartUpdateQuestion.KeyOf(Event(7, "Reisil", "Bryn"), View, Top);
    var second = ChartUpdateQuestion.KeyOf(Event(7, "Reisil", "Bryn"), View, Top);

    Assert.IsNotNull(first);
    Assert.AreEqual(first, second,
      "two UPDATEs over the same data with the same rows and controls must look identical - that equality is what keeps a second 4.6M-record walk from running");

    // Equal by NAME rather than by object identity: panes hand over fresh PlayerStats wrappers for the same people every time.
    Assert.IsTrue(ChartUpdateQuestion.IsRepeat(second, first, hasAppliedData: true),
      "a repeat question against data already drawn is the case this rule exists to catch");
  }

  [TestMethod]
  public void EveryTermThatChangesTheDrawingChangesTheKey()
  {
    var baseline = ChartUpdateQuestion.KeyOf(Event(7, "Reisil", "Bryn"), View, Top);

    // WHICH records.
    Assert.AreNotEqual(baseline, ChartUpdateQuestion.KeyOf(Event(8, "Reisil", "Bryn"), View, Top),
      "a build restamps, so new content must never be mistaken for what the chart already drew");

    // HOW to draw it -- the view option decides which records the walk keeps (its own ShouldSkipRecord) as well as the shape of the plot.
    Assert.AreNotEqual(baseline, ChartUpdateQuestion.KeyOf(Event(7, "Reisil", "Bryn"), "By Group", Top));
    Assert.AreNotEqual(baseline, ChartUpdateQuestion.KeyOf(Event(7, "Reisil", "Bryn"), View, 10));

    // WHICH rows: added, replaced, and the empty-versus-nobody case.
    Assert.AreNotEqual(baseline, ChartUpdateQuestion.KeyOf(Event(7, "Reisil", "Bryn", "Akira"), View, Top));
    Assert.AreNotEqual(baseline, ChartUpdateQuestion.KeyOf(Event(7, "Reisil", "Coas"), View, Top));
    Assert.AreNotEqual(baseline, ChartUpdateQuestion.KeyOf(Event(7), View, Top),
      "a chart asked about nobody is not the question it was asked about two raiders");

    // Group selection is its own term (the panes select group rows, and those change what gets plotted).
    var withGroup = Event(7, "Reisil", "Bryn");
    withGroup.SelectedGroups.Add(new GroupEntry { Name = "Group 1", GroupId = 1 });
    Assert.AreNotEqual(baseline, ChartUpdateQuestion.KeyOf(withGroup, View, Top));
  }

  [TestMethod]
  public void SelectionOrderIsATermAndOnlyCostsASkip()
  {
    var one = ChartUpdateQuestion.KeyOf(Event(7, "Reisil", "Bryn"), View, Top);
    var other = ChartUpdateQuestion.KeyOf(Event(7, "Bryn", "Reisil"), View, Top);

    Assert.AreNotEqual(one, other,
      "order participates; dragging a selection in the reverse direction asks an honest second question rather than being guessed at");
  }

  [TestMethod]
  public void AnUnstampedEventCannotBeKeyedAtAll()
  {
    Assert.AreEqual(-1, new DataPointEvent().DataGeneration, "an event built outside a stamped build is the unknown case by default");
    Assert.IsNull(ChartUpdateQuestion.KeyOf(new DataPointEvent(), View, Top),
      "unknown content gets no key: it must walk, so that forgetting to stamp costs milliseconds rather than showing a stale chart");
    Assert.IsNull(ChartUpdateQuestion.KeyOf(Event(0), View, Top), "generation 0 is 'never built', not a usable identity");

    // A null key is not equal to anything -- including the null of another unknown event.
    Assert.IsFalse(ChartUpdateQuestion.IsRepeat(null, null, hasAppliedData: true),
      "two unknowns are not the same question");
  }

  [TestMethod]
  public void AKeyMatchAgainstNothingDrawnIsNotARepeat()
  {
    var key = ChartUpdateQuestion.KeyOf(Event(7, "Reisil"), View, Top);

    Assert.IsFalse(ChartUpdateQuestion.IsRepeat(key, key, hasAppliedData: false),
      "the first UPDATE after a clear matches its own key but has drawn nothing - clearing the chart must clear the memory of what was answered");
  }

  [TestMethod]
  public void ANameContainingASeparatorCannotFuseTwoQuestions()
  {
    // Names come out of a log file. Without length-prefixed terms, ["a|b", "c"] and ["a", "b|c"] - and every other boundary trick - would key
    // identically, which is a wrong chart rather than a slow one.
    Assert.AreNotEqual(
      ChartUpdateQuestion.KeyOf(Event(7, "a|b", "c"), View, Top),
      ChartUpdateQuestion.KeyOf(Event(7, "a", "b|c"), View, Top));

    Assert.AreNotEqual(
      ChartUpdateQuestion.KeyOf(Event(7, "a\u0001b", "c"), View, Top),
      ChartUpdateQuestion.KeyOf(Event(7, "a", "b\u0001c"), View, Top));

    // And a longer name that starts like another one cannot be read as a prefix match.
    Assert.AreNotEqual(
      ChartUpdateQuestion.KeyOf(Event(7, "Bryn"), View, Top),
      ChartUpdateQuestion.KeyOf(Event(7, "Brynlyn"), View, Top));
  }

  [TestMethod]
  public void AnEmptySelectionAndNoSelectionAreTheSameQuestion()
  {
    var emptyList = new DataPointEvent { Action = "UPDATE", DataGeneration = 7 };
    var noList = new DataPointEvent { Action = "UPDATE", DataGeneration = 7 };

    Assert.AreEqual(ChartUpdateQuestion.KeyOf(emptyList, View, Top), ChartUpdateQuestion.KeyOf(noList, View, Top),
      "\"nobody selected\" is one state however the caller spelled it - two spellings would leave the expensive door open");
  }
}
