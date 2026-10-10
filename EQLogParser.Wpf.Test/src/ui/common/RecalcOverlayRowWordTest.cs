using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser;

/*
 * The word under the calculator icon while its click runs (2026-11). Lives in this assembly because NameRow is nested in the
 * WPF control; nothing here is constructed, so no STA. The law: the cell says "Recalculating…" until the pass the click asked for
 * has landed and been shown — "if it ends up with the same result, at least the user knows it tried" — and the name stays the
 * SORT key underneath (a label that reshuffles a 4,000-row list twice a second is the flicker this pane's merge law exists to
 * prevent), so a census-rebuilt row starts unlabelled rather than inheriting one.
 */
[TestClass]
public sealed class RecalcOverlayRowWordTest
{
  [TestMethod]
  public void TheRowSaysRecalculatingOnlyWhileTheClickRuns()
  {
    var row = new NamesTable.NameRow { Name = "Probera", Kind = IdentityKind.Player };

    Assert.AreEqual("Probera", row.DisplayText, "an untouched row prints its name");
    Assert.IsFalse(row.Recalculating);

    row.Recalculating = true;
    Assert.AreEqual("Recalculating…", row.DisplayText, "the operator asked for exactly this: at least the user knows it tried");
    Assert.AreEqual("Probera", row.Name, "the word sits OVER the name, it is not the name — sort and find keep working on it");

    row.Recalculating = false;
    Assert.AreEqual("Probera", row.DisplayText, "and it comes back down, no residue");

    var fresh = new NamesTable.NameRow { Name = "Probera" };
    Assert.IsFalse(fresh.Recalculating, "the census replaces rows wholesale: a rebuilt row must not inherit the label of the one it replaced");
  }
}
