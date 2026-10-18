using EQLogParser;

#nullable enable annotations

namespace EQLogParserTest
{
  [TestClass]
  public class DateUtilTest
  {
    [TestMethod]
    public void EpochSecondsAndTicksUseTheDotNetEpoch()
    {
      var date = new DateTime(55, 1, 15, 14, 30, 45);
      Assert.AreEqual(1705329045.0, DateUtil.ToDotNetSeconds(date), 0.001);
      Assert.AreEqual(1705329045.0, DateUtil.TicksToDotNetSeconds(date.Ticks), 0.001);
      Assert.AreEqual(date, DateUtil.FromDotNetSeconds(1705329045.0));
      var modern = new DateTime(2024, 6, 15, 10, 20, 30);
      Assert.AreEqual(modern.Ticks, DateUtil.FromDotNetSeconds(DateUtil.ToDotNetSeconds(modern)).Ticks);
    }

    [TestMethod]
    [DataRow(0.0, "Jan 01 00:00:00")]
    [DataRow(3661.0, "Jan 01 01:01:01")]
    [DataRow(86400.0, "Jan 02 00:00:00")]
    public void DatesFormatFromEpochSeconds(double seconds, string expected)
    {
      Assert.AreEqual(expected, DateUtil.FormatDotNetDateSeconds(seconds));
    }

    [TestMethod]
    [DataRow(0.0, "00:00:00")]
    [DataRow(3600.0, "01:00:00")]
    [DataRow(86400.0, "00:00:00")]
    public void TimeOfDayWrapsAtMidnight(double seconds, string expected)
    {
      Assert.AreEqual(expected, DateUtil.FormatDotNetTimeSeconds(seconds));
    }

    [TestMethod]
    [DataRow(0L, (int)DateUtil.TimeFormat.SecondsMs, "00.000")]
    [DataRow(10000000L, (int)DateUtil.TimeFormat.SecondsMs, "01.000")]
    [DataRow(10000L, (int)DateUtil.TimeFormat.SecondsMs, "00.001")]
    [DataRow(0L, (int)DateUtil.TimeFormat.HMSCompact, "00:00")]
    [DataRow(600000000L, (int)DateUtil.TimeFormat.HMSCompact, "01:00")]
    [DataRow(36000000000L, (int)DateUtil.TimeFormat.HMSCompact, "01:00:00")]
    [DataRow(0L, (int)DateUtil.TimeFormat.HMSMsCompact, "00:00.000")]
    [DataRow(601230000L, (int)DateUtil.TimeFormat.HMSMsCompact, "01:00.123")]
    [DataRow(-100L, (int)DateUtil.TimeFormat.HMSCompact, "00:00")]
    public void TicksFormatAtTheRequestedPrecision(long ticks, int format, string expected)
    {
      Assert.AreEqual(expected, DateUtil.FormatTicks(ticks, (DateUtil.TimeFormat)format));
    }

    [TestMethod]
    [DataRow(0.0, false, "")]
    [DataRow(30.0, false, "")]
    [DataRow(30.0, true, "30 seconds")]
    [DataRow(60.0, false, "1 minute")]
    [DataRow(120.0, false, "2 minutes")]
    [DataRow(3600.0, false, "1 hour")]
    [DataRow(7200.0, false, "2 hours")]
    [DataRow(86400.0, false, "1 day")]
    [DataRow(172800.0, false, "2 days")]
    public void GeneralTimeUsesWholeUnitsAndOptionalSeconds(double seconds, bool showSeconds, string expected)
    {
      Assert.AreEqual(expected, DateUtil.FormatGeneralTime(seconds, showSeconds));
    }

    [TestMethod]
    [DataRow("", 0u)]
    [DataRow(null, 0u)]
    [DataRow("30", 30u)]
    [DataRow("1:30", 90u)]
    [DataRow("1:2:30", 3750u)]
    [DataRow("01:05:30", 3930u)]
    [DataRow("1:60:30", 0u)]
    [DataRow("1:30:60", 0u)]
    [DataRow("24:30:30", 0u)]
    [DataRow("1:30:50:20", 0u)]
    [DataRow("abc", 0u)]
    public void SimpleClockTokensValidateTheirComponents(string? token, uint expected)
    {
      Assert.AreEqual(expected, DateUtil.SimpleTimeToSeconds(token));
    }

    [TestMethod]
    [DataRow("Jan 15 14:30:45 2024", 1, 15, 14, 30, 45)]
    [DataRow("Feb 28 10:00:00 2024", 2, 28, 10, 0, 0)]
    [DataRow("Mar 01 08:15:30 2024", 3, 1, 8, 15, 30)]
    [DataRow("Apr 30 23:59:59 2024", 4, 30, 23, 59, 59)]
    [DataRow("May 15 12:00:00 2024", 5, 15, 12, 0, 0)]
    [DataRow("Jun 20 16:45:00 2024", 6, 20, 16, 45, 0)]
    [DataRow("Jul 04 10:30:00 2024", 7, 4, 10, 30, 0)]
    [DataRow("Aug 15 14:20:30 2024", 8, 15, 14, 20, 30)]
    [DataRow("Sep 01 09:00:00 2024", 9, 1, 9, 0, 0)]
    [DataRow("Oct 31 18:30:45 2024", 10, 31, 18, 30, 45)]
    [DataRow("Nov 11 11:11:11 2024", 11, 11, 11, 11, 11)]
    [DataRow("Dec 25 20:00:00 2024", 12, 25, 20, 0, 0)]
    public void LogDatesKeepEveryCalendarAndClockComponent(string token, int month, int day, int hour, int minute, int second)
    {
      Assert.AreEqual(new DateTime(2024, month, day, hour, minute, second),
        DateUtil.CustomDateTimeParser("MMM dd HH:mm:ss yyyy", token));
    }

    [TestMethod]
    [DataRow("Xyz 15 14:30:45 2024")]
    [DataRow("Jan 00 14:30:45 2024")]
    [DataRow("Jan 15 25:30:45 2024")]
    [DataRow("Jan 15 14:60:45 2024")]
    [DataRow("Jan 15 14:30:60 2024")]
    [DataRow("Jan 15")]
    public void InvalidLogDatesReturnTheSentinel(string token)
    {
      Assert.AreEqual(DateTime.MinValue, DateUtil.CustomDateTimeParser("MMM dd HH:mm:ss yyyy", token));
    }
  }
}
