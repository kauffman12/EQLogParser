using System;
using EQLogParser;

namespace EQLogParser.Wpf.Test;

/*
 * The key MainWindow hands SummaryBuildGate for one board announcement: every input the three builders read, so that
 * "already building this" and "the newest question" are answered without materializing twice.
 *
 * Only a static method is reachable here — MainWindow cannot be constructed headlessly — but that is the whole rule, and it
 * needs no dispatcher or STA lane: class loading registers dependency properties, it never touches a window. The law this
 * exists for is the session term (2026-11): the key used to be built from rows, stamp and tank filter alone, so closing one
 * capture and opening another could ask a question the gate had ALREADY BUILT — an empty selection over two captures is that
 * exact case, since both have no ids and both start at stamp 0 — and the fresh session's boards would be answered "SkippedSame"
 * and stay blank until something else moved.
 *
 * The other half of that defect lives in BuildBoards rather than in the key — a build whose capture has been DISPOSED must not
 * paint, because Dispose drops the snapshot and BuildSummaryInput reads a null snapshot as "clear every board", so a stale build
 * blanks what the new capture already drew. MainWindow returns without touching a builder in that case (comparing `session`
 * against its own session id); no test can construct MainWindow, so that sentence is the guard's only record, which is why the
 * abandoned build logs its own Debug line.
 */
[TestClass]
public class BoardKeyTest
{
  [TestMethod]
  public void TheSameQuestionAsksTheSameKey()
  {
    var one = MainWindow.SummaryKeyFor(3, [], 77, 0);
    Assert.AreEqual(one, MainWindow.SummaryKeyFor(3, [], 77, 0), "a repeated announcement must be recognised as the same question");
  }

  [TestMethod]
  public void ANewCaptureAsksADifferentKeyEvenWithNothingSelected()
  {
    var closed = MainWindow.SummaryKeyFor(3, [], 0, 0);
    var opened = MainWindow.SummaryKeyFor(4, [], 0, 0);
    Assert.AreNotEqual(closed, opened,
      "the same-looking question on the NEXT capture is a different question: it has never been answered");

    // The everyday shape of it — Clear All / Open Monitor re-opens with an empty selection at stamp 0.
    Assert.AreNotEqual(MainWindow.SummaryKeyFor(1, [], 0, 0), MainWindow.SummaryKeyFor(2, [], 0, 0));
  }

  [TestMethod]
  public void EveryInputTheBuildersReadMovesTheKey()
  {
    var baseKey = MainWindow.SummaryKeyFor(3, [new DerivedFight { Id = 1 }, new DerivedFight { Id = 2 }], 77, 0);

    Assert.AreNotEqual(baseKey, MainWindow.SummaryKeyFor(3, [new DerivedFight { Id = 1 }], 77, 0), "one row less is a different board");
    Assert.AreNotEqual(baseKey, MainWindow.SummaryKeyFor(3, [new DerivedFight { Id = 2 }, new DerivedFight { Id = 1 }], 77, 0),
      "the builders walk the selection in list order, so order is part of the question");
    Assert.AreNotEqual(baseKey, MainWindow.SummaryKeyFor(3, [new DerivedFight { Id = 1 }, new DerivedFight { Id = 2 }], 78, 0),
      "content moved under the selection: the rows are the same, their answer is not");
    Assert.AreNotEqual(baseKey, MainWindow.SummaryKeyFor(3, [new DerivedFight { Id = 1 }, new DerivedFight { Id = 2 }], 77, 2),
      "the tanking board's damage-type filter changes what three of the columns contain");
  }

  [TestMethod]
  public void TheKeyIsNeverTheReservedZero()
  {
    // SummaryBuildGate reserves 0 for "never built", so a computed 0 must be moved off it rather than treated as unanswered forever.
    for (var session = 0; session < 4; session++)
      for (long stamp = 0; stamp < 3; stamp++)
        for (var tank = 0; tank < 3; tank++)
          Assert.AreNotEqual(0, MainWindow.SummaryKeyFor(session, [], stamp, tank),
            $"session {session}, stamp {stamp}, tank filter {tank} hashed to the reserved value");

    Assert.AreNotEqual(0, MainWindow.SummaryKeyFor(0, [new DerivedFight { Id = 0 }], 0, 0));
  }

  [TestMethod]
  public void AnEmptySelectionIsAQuestionRatherThanNoQuestion()
  {
    // An empty selection means "blank the boards", so it must be keyable (and skippable) like any other answer.
    Assert.AreEqual(MainWindow.SummaryKeyFor(9, [], 5, 1), MainWindow.SummaryKeyFor(9, [], 5, 1));
    Assert.AreNotEqual(MainWindow.SummaryKeyFor(9, [], 5, 1), MainWindow.SummaryKeyFor(9, [new DerivedFight { Id = 4 }], 5, 1));
  }
}
