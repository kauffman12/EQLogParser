namespace EQLogParser;

[TestClass]
public class OverlayPositionPersistenceTest : TempDirFixture
{
  [TestMethod]
  [DataRow(false, -3840L, 100L)]
  [DataRow(true, -3840L, 100L)]
  [DataRow(false, 100L, -1440L)]
  [DataRow(true, 100L, -1440L)]
  [DataRow(false, -3840L, -1440L)]
  [DataRow(true, -3840L, -1440L)]
  [DataRow(false, 12000L, 100L)]
  [DataRow(true, 12000L, 100L)]
  public async Task ATextOrTimerOverlayKeepsItsDesktopPositionAfterReopening(bool timer, long left, long top)
  {
    var path = Path.Combine(NewTempDir(), "overlay-position.db");
    string id;
    await using (var db = new TriggerStateDB(path, applyLegacyUpgrades: false))
    {
      var node = timer ? await db.GetDefaultTimerOverlay() : await db.GetDefaultTextOverlay();
      Assert.IsNotNull(node?.OverlayData);
      id = node.Id;
      node.OverlayData.Left = left;
      node.OverlayData.Top = top;
      await db.Update(node);
    }

    await using var reopened = new TriggerStateDB(path, applyLegacyUpgrades: false);
    var restored = await reopened.GetOverlayById(id);
    Assert.IsNotNull(restored?.OverlayData);
    Assert.AreEqual(left, restored.OverlayData.Left);
    Assert.AreEqual(top, restored.OverlayData.Top);
  }
}
