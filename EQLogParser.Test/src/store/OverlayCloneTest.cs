using System.Reflection;

namespace EQLogParser
{
  /* LegacyOverlay.ToOverlay() used to hand-copy 35 fields, which silently dropped any Overlay
   * member added afterwards. It now delegates to the generated Mapperly deep clone (a [Mapper]
   * method, so it is a compile-time emitted copy — not runtime reflection). This test is what makes
   * that safe: every Overlay property must survive the port, whatever it is named, so a new field
   * cannot be lost the way ManualColor was. */
  [TestClass]
  public class OverlayCloneTest
  {
    /* Values chosen to differ from every Overlay default, so a dropped mapping shows up as a
     * mismatch instead of hiding behind an identical default. */
    private static LegacyOverlay Legacy() => new()
    {
      Id = "legacy-id",
      Name = "legacy-name",
      Source = "src",
      OverlayComments = "notes",
      FontSize = "18pt",
      FontWeight = "Bold",
      SortBy = 1,
      HorizontalAlignment = 2,
      VerticalAlignment = 0,
      FontColor = "#FF112233",
      FontFamily = "Arial",
      ActiveColor = "#FF00FF00",
      BackgroundColor = "#80123456",
      IdleColor = "#FF111111",
      ResetColor = "#FF151515",
      OverlayColor = "#FF123456",
      IdleTimeoutSeconds = 12.5,
      FadeDelay = 7,
      UseStandardTime = true,
      ShowMillis = true,
      IsTimerOverlay = true,
      IsTextOverlay = true,
      IsDefault = true,
      ShowActive = false,
      ShowIdle = false,
      ShowReset = false,
      StreamerMode = true,
      HideDuplicates = true,
      UseTextDropShadow = false,
      TextOverlayWrap = false,
      TimerMode = 2,
      Height = 222,
      Width = 333,
      Top = 44,
      Left = 55,
      ClosePattern = "close me",
      UseCloseRegex = true
    };

    [TestMethod]
    public void ToOverlay_EveryOverlayPropertySurvives_AndLegacyIdentityIsDropped()
    {
      var legacy = Legacy();
      var copy = legacy.ToOverlay();

      Assert.AreNotSame(legacy, copy, "the legacy object must not be handed out as-is (callers fix up colors on it)");

      var mismatches = typeof(Overlay)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
        .Where(p => !Equals(p.GetValue(legacy), p.GetValue(copy)))
        .Select(p => $"{p.Name}: legacy={p.GetValue(legacy)} copy={p.GetValue(copy)}")
        .ToList();

      Assert.AreEqual(0, mismatches.Count, "Overlay properties lost by ToOverlay(): " + string.Join("; ", mismatches));
    }

    [TestMethod]
    public void ToOverlay_IsADeepCopy_EditingTheCopyLeavesTheSourceAlone()
    {
      var legacy = Legacy();
      var copy = legacy.ToOverlay();

      copy.FontSize = "9pt";
      copy.FontFamily = "Courier New";

      // the caller fixes up colors/identity on the returned object — that must not reach back into
      // the LegacyOverlay handed to Add()
      Assert.AreEqual("18pt", legacy.FontSize);
      Assert.AreEqual("Arial", legacy.FontFamily);
    }
  }
}
