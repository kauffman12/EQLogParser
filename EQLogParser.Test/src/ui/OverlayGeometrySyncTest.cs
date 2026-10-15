namespace EQLogParser;

[TestClass]
public class OverlayGeometrySyncTest
{
  [TestMethod]
  [DataRow(-3840L)]
  [DataRow(-1440L)]
  [DataRow(0L)]
  [DataRow(12000L)]
  [DataRow((long)int.MinValue)]
  [DataRow((long)int.MaxValue)]
  public void ThePositionEditorRangePreservesSignedAndWideDesktopCoordinates(long coordinate)
  {
    Assert.AreEqual(coordinate, Math.Clamp(coordinate,
      (long)OverlayGeometrySync.MinCoordinate, OverlayGeometrySync.MaxCoordinate));
  }

  [TestMethod]
  public void APreviewSaveUpdatesTheMatchingModelWithoutLosingOtherPendingEdits()
  {
    var editable = new Overlay { Left = 100, Top = 200, FontSize = "24pt", OverlayComments = "pending edit" };
    var updated = Saved("selected", -3840, -1440);

    Assert.IsTrue(OverlayGeometrySync.Apply("selected", editable, updated));

    Assert.AreEqual((-3840L, -1440L, 450L, 150L),
      (editable.Left, editable.Top, editable.Width, editable.Height));
    Assert.AreEqual("24pt", editable.FontSize);
    Assert.AreEqual("pending edit", editable.OverlayComments);
    Assert.AreEqual("16pt", updated.OverlayData.FontSize, "sync must not write unsaved edits into the stored row");
  }

  [TestMethod]
  [DataRow("another")]
  [DataRow("")]
  [DataRow(null)]
  public void AnUpdateCannotMoveADifferentOrUnselectedOverlay(string? selectedId)
  {
    var editable = new Overlay { Left = -3000, Top = -800, Width = 600, Height = 200 };

    Assert.IsFalse(OverlayGeometrySync.Apply(selectedId, editable, Saved("updated", 100, 200)));

    Assert.AreEqual((-3000L, -800L, 600L, 200L),
      (editable.Left, editable.Top, editable.Width, editable.Height));
  }

  [TestMethod]
  public void ATriggerUpdateCannotOverwriteOverlayGeometry()
  {
    var editable = new Overlay { Left = -3000, Top = -800 };
    var updated = new TriggerNode { Id = "selected", TriggerData = new Trigger() };

    Assert.IsFalse(OverlayGeometrySync.Apply("selected", editable, updated));
    Assert.AreEqual((-3000L, -800L), (editable.Left, editable.Top));
  }

  private static TriggerNode Saved(string id, long left, long top) => new()
  {
    Id = id,
    OverlayData = new Overlay
    {
      Left = left, Top = top, Width = 450, Height = 150, FontSize = "16pt"
    }
  };
}
