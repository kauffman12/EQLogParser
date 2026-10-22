using System.Globalization;

namespace EQLogParser;

[TestClass]
public class LegacyIdentityNumberTest
{
  [TestMethod]
  [DataRow("en-US")]
  [DataRow("de-DE")]
  [DataRow("fr-FR")]
  public void LegacyStampsKeepTheirFractionAndNeverFollowTheLocale(string culture)
  {
    var original = CultureInfo.CurrentCulture;
    try
    {
      CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
      Assert.IsTrue(PlayerRegistry.TryReadPetMapLine("Fluffy", "Kizant| 63865000000.5 ",
        out var owner, out var petStamp));
      Assert.AreEqual("Kizant", owner);
      Assert.AreEqual(63865000000.5, petStamp);

      Assert.IsTrue(PlayerRegistry.TryReadRosterLine("Kizant= 63865000000.5 ,Wizard",
        out var name, out var rosterStamp, out var className));
      Assert.AreEqual("Kizant", name);
      Assert.AreEqual(63865000000.5, rosterStamp);
      Assert.AreEqual("Wizard", className);
    }
    finally
    {
      CultureInfo.CurrentCulture = original;
    }
  }

  [TestMethod]
  [DataRow("")]
  [DataRow("broken")]
  [DataRow("NaN")]
  [DataRow("Infinity")]
  [DataRow("18446744073709551616")]
  public void AnUnreadableStampKeepsTheNameAsAnUndatedStatement(string token)
  {
    Assert.IsTrue(PlayerRegistry.TryReadPetMapLine("Fluffy", "Kizant|" + token,
      out var owner, out var petStamp));
    Assert.AreEqual("Kizant", owner);
    Assert.AreEqual(0, petStamp);

    Assert.IsTrue(PlayerRegistry.TryReadRosterLine("Kizant=" + token + ",Wizard",
      out var name, out var rosterStamp, out var className));
    Assert.AreEqual("Kizant", name);
    Assert.AreEqual(0, rosterStamp);
    Assert.AreEqual("Wizard", className);
  }
}
