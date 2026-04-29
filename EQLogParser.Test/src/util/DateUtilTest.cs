using EQLogParser;

namespace EQLogParserTest
{
  [TestClass]
  public class DateUtilTest
  {
    #region ToDouble / FromDouble Tests

    [TestMethod]
    public void TestToDouble_FromDateTime()
    {
      var dateTime = new DateTime(55, 1, 15, 14, 30, 45);
      var result = DateUtil.ToDotNetSeconds(dateTime);

      Assert.AreEqual(1705329045, result, 0.001);
    }

    [TestMethod]
    public void TestToDouble_FromTicks()
    {
      var ticks = 1705329045L * TimeSpan.TicksPerSecond;
      var result = DateUtil.TicksToDotNetSeconds(ticks);

      Assert.AreEqual(1705329045.0, result, 0.001);
    }

    [TestMethod]
    public void TestFromDouble_ToDateTime()
    {
      var seconds = 1705329045.0;
      var result = DateUtil.FromDotNetSeconds(seconds);

      Assert.AreEqual(55, result.Year);
      Assert.AreEqual(1, result.Month);
      Assert.AreEqual(15, result.Day);
      Assert.AreEqual(14, result.Hour);
      Assert.AreEqual(30, result.Minute);
      Assert.AreEqual(45, result.Second);
    }

    [TestMethod]
    public void TestToFromDouble_RoundTrip()
    {
      var original = new DateTime(2024, 6, 15, 10, 20, 30);
      var seconds = DateUtil.ToDotNetSeconds(original);
      var roundTrip = DateUtil.FromDotNetSeconds(seconds);

      Assert.AreEqual(original.Ticks, roundTrip.Ticks);
    }

    #endregion

    #region FormatSimpleDate Tests

    [TestMethod]
    public void TestFormatSimpleDate_ZeroSeconds()
    {
      var result = DateUtil.FormatDotNetDateSeconds(0);
      Assert.AreEqual("Jan 01 00:00:00", result);
    }

    [TestMethod]
    public void TestFormatSimpleDate_PositiveSeconds()
    {
      var result = DateUtil.FormatDotNetDateSeconds(3661);
      Assert.AreEqual("Jan 01 01:01:01", result);
    }

    [TestMethod]
    public void TestFormatSimpleDate_LargeSeconds()
    {
      var result = DateUtil.FormatDotNetDateSeconds(86400);
      Assert.AreEqual("Jan 02 00:00:00", result);
    }

    #endregion

    #region FormatSeconds Tests

    [TestMethod]
    public void TestFormatSeconds_Zero()
    {
      var result = DateUtil.FormatDotNetTimeSeconds(0);
      Assert.AreEqual("00:00:00", result);
    }

    [TestMethod]
    public void TestFormatSeconds_OneHour()
    {
      var result = DateUtil.FormatDotNetTimeSeconds(3600);
      Assert.AreEqual("01:00:00", result);
    }

    [TestMethod]
    public void TestFormatSeconds_OneDay()
    {
      var result = DateUtil.FormatDotNetTimeSeconds(86400);
      Assert.AreEqual("00:00:00", result);
    }

    #endregion

    #region FormatTicks Tests

    [TestMethod]
    public void TestFormatTicks_SecondsMs_Zero()
    {
      var result = DateUtil.FormatTicks(0, DateUtil.TimeFormat.SecondsMs);
      Assert.AreEqual("00.000", result);
    }

    [TestMethod]
    public void TestFormatTicks_SecondsMs_OneSecond()
    {
      var ticks = TimeSpan.FromSeconds(1).Ticks;
      var result = DateUtil.FormatTicks(ticks, DateUtil.TimeFormat.SecondsMs);
      Assert.AreEqual("01.000", result);
    }

    [TestMethod]
    public void TestFormatTicks_SecondsMs_OneMillisecond()
    {
      var ticks = TimeSpan.FromMilliseconds(1).Ticks;
      var result = DateUtil.FormatTicks(ticks, DateUtil.TimeFormat.SecondsMs);
      Assert.AreEqual("00.001", result);
    }

    [TestMethod]
    public void TestFormatTicks_HMSCompact_Zero()
    {
      var result = DateUtil.FormatTicks(0, DateUtil.TimeFormat.HMSCompact);
      Assert.AreEqual("00:00", result);
    }

    [TestMethod]
    public void TestFormatTicks_HMSCompact_OneMinute()
    {
      var ticks = TimeSpan.FromMinutes(1).Ticks;
      var result = DateUtil.FormatTicks(ticks, DateUtil.TimeFormat.HMSCompact);
      Assert.AreEqual("01:00", result);
    }

    [TestMethod]
    public void TestFormatTicks_HMSCompact_OneHour()
    {
      var ticks = TimeSpan.FromHours(1).Ticks;
      var result = DateUtil.FormatTicks(ticks, DateUtil.TimeFormat.HMSCompact);
      Assert.AreEqual("01:00:00", result);
    }

    [TestMethod]
    public void TestFormatTicks_HMSMsCompact_Zero()
    {
      var result = DateUtil.FormatTicks(0, DateUtil.TimeFormat.HMSMsCompact);
      Assert.AreEqual("00:00.000", result);
    }

    [TestMethod]
    public void TestFormatTicks_HMSMsCompact_MinutesAndMilliseconds()
    {
      var ticks = TimeSpan.FromMinutes(1).Add(TimeSpan.FromMilliseconds(123)).Ticks;
      var result = DateUtil.FormatTicks(ticks, DateUtil.TimeFormat.HMSMsCompact);
      Assert.AreEqual("01:00.123", result);
    }

    [TestMethod]
    public void TestFormatTicks_NegativeTicks_TreatedAsZero()
    {
      var result = DateUtil.FormatTicks(-100, DateUtil.TimeFormat.HMSCompact);
      Assert.AreEqual("00:00", result);
    }

    #endregion

    #region FormatGeneralTime Tests

    [TestMethod]
    public void TestFormatGeneralTime_ZeroSeconds()
    {
      var result = DateUtil.FormatGeneralTime(0);
      Assert.AreEqual("", result);
    }

    [TestMethod]
    public void TestFormatGeneralTime_SecondsNoShow()
    {
      var result = DateUtil.FormatGeneralTime(30);
      Assert.AreEqual("", result);
    }

    [TestMethod]
    public void TestFormatGeneralTime_SecondsWithShow()
    {
      var result = DateUtil.FormatGeneralTime(30, showSeconds: true);
      Assert.AreEqual("30 seconds", result);
    }

    [TestMethod]
    public void TestFormatGeneralTime_OneMinute()
    {
      var result = DateUtil.FormatGeneralTime(60);
      Assert.AreEqual("1 minute", result);
    }

    [TestMethod]
    public void TestFormatGeneralTime_MultipleMinutes()
    {
      var result = DateUtil.FormatGeneralTime(120);
      Assert.AreEqual("2 minutes", result);
    }

    [TestMethod]
    public void TestFormatGeneralTime_OneHour()
    {
      var result = DateUtil.FormatGeneralTime(3600);
      Assert.AreEqual("1 hour", result);
    }

    [TestMethod]
    public void TestFormatGeneralTime_MultipleHours()
    {
      var result = DateUtil.FormatGeneralTime(7200);
      Assert.AreEqual("2 hours", result);
    }

    [TestMethod]
    public void TestFormatGeneralTime_OneDay()
    {
      var result = DateUtil.FormatGeneralTime(86400);
      Assert.AreEqual("1 day", result);
    }

    [TestMethod]
    public void TestFormatGeneralTime_MultipleDays()
    {
      var result = DateUtil.FormatGeneralTime(172800);
      Assert.AreEqual("2 days", result);
    }

    #endregion

    #region SimpleTimeToSeconds Tests

    [TestMethod]
    public void TestSimpleTimeToSeconds_Empty()
    {
      var result = DateUtil.SimpleTimeToSeconds("");
      Assert.AreEqual(0u, result);
    }

    [TestMethod]
    public void TestSimpleTimeToSeconds_Null()
    {
      var result = DateUtil.SimpleTimeToSeconds(null!);
      Assert.AreEqual(0u, result);
    }

    [TestMethod]
    public void TestSimpleTimeToSeconds_SecondsOnly()
    {
      var result = DateUtil.SimpleTimeToSeconds("30");
      Assert.AreEqual(30u, result);
    }

    [TestMethod]
    public void TestSimpleTimeToSeconds_MinutesSeconds()
    {
      var result = DateUtil.SimpleTimeToSeconds("1:30");
      Assert.AreEqual(90u, result);
    }

    [TestMethod]
    public void TestSimpleTimeToSeconds_HoursMinutesSeconds()
    {
      var result = DateUtil.SimpleTimeToSeconds("1:2:30");
      Assert.AreEqual(3750u, result);
    }

    [TestMethod]
    public void TestSimpleTimeToSeconds_ZeroPadded()
    {
      var result = DateUtil.SimpleTimeToSeconds("01:05:30");
      Assert.AreEqual(3930u, result);
    }

    [TestMethod]
    public void TestSimpleTimeToSeconds_InvalidMinutesOver59()
    {
      var result = DateUtil.SimpleTimeToSeconds("1:60:30");
      Assert.AreEqual(0u, result);
    }

    [TestMethod]
    public void TestSimpleTimeToSeconds_InvalidSecondsOver59()
    {
      var result = DateUtil.SimpleTimeToSeconds("1:30:60");
      Assert.AreEqual(0u, result);
    }

    [TestMethod]
    public void TestSimpleTimeToSeconds_InvalidHoursOver23()
    {
      var result = DateUtil.SimpleTimeToSeconds("24:30:30");
      Assert.AreEqual(0u, result);
    }

    [TestMethod]
    public void TestSimpleTimeToSeconds_InvalidFormat_TooManyParts()
    {
      var result = DateUtil.SimpleTimeToSeconds("1:30:50:20");
      Assert.AreEqual(0u, result);
    }

    [TestMethod]
    public void TestSimpleTimeToSeconds_InvalidFormat_NonNumeric()
    {
      var result = DateUtil.SimpleTimeToSeconds("abc");
      Assert.AreEqual(0u, result);
    }

    #endregion

    #region CustomDateTimeParser Tests

    [TestMethod]
    public void TestCustomDateTimeParser_January()
    {
      var result = DateUtil.CustomDateTimeParser("MMM dd HH:mm:ss yyyy", "Jan 15 14:30:45 2024");
      Assert.AreEqual(1, result.Month);
      Assert.AreEqual(15, result.Day);
      Assert.AreEqual(2024, result.Year);
    }

    [TestMethod]
    public void TestCustomDateTimeParser_February()
    {
      var result = DateUtil.CustomDateTimeParser("MMM dd HH:mm:ss yyyy", "Feb 28 10:00:00 2024");
      Assert.AreEqual(2, result.Month);
    }

    [TestMethod]
    public void TestCustomDateTimeParser_March()
    {
      var result = DateUtil.CustomDateTimeParser("MMM dd HH:mm:ss yyyy", "Mar 01 08:15:30 2024");
      Assert.AreEqual(3, result.Month);
    }

    [TestMethod]
    public void TestCustomDateTimeParser_April()
    {
      var result = DateUtil.CustomDateTimeParser("MMM dd HH:mm:ss yyyy", "Apr 30 23:59:59 2024");
      Assert.AreEqual(4, result.Month);
    }

    [TestMethod]
    public void TestCustomDateTimeParser_May()
    {
      var result = DateUtil.CustomDateTimeParser("MMM dd HH:mm:ss yyyy", "May 15 12:00:00 2024");
      Assert.AreEqual(5, result.Month);
    }

    [TestMethod]
    public void TestCustomDateTimeParser_June()
    {
      var result = DateUtil.CustomDateTimeParser("MMM dd HH:mm:ss yyyy", "Jun 20 16:45:00 2024");
      Assert.AreEqual(6, result.Month);
    }

    [TestMethod]
    public void TestCustomDateTimeParser_July()
    {
      var result = DateUtil.CustomDateTimeParser("MMM dd HH:mm:ss yyyy", "Jul 04 10:30:00 2024");
      Assert.AreEqual(7, result.Month);
    }

    [TestMethod]
    public void TestCustomDateTimeParser_August()
    {
      var result = DateUtil.CustomDateTimeParser("MMM dd HH:mm:ss yyyy", "Aug 15 14:20:30 2024");
      Assert.AreEqual(8, result.Month);
    }

    [TestMethod]
    public void TestCustomDateTimeParser_September()
    {
      var result = DateUtil.CustomDateTimeParser("MMM dd HH:mm:ss yyyy", "Sep 01 09:00:00 2024");
      Assert.AreEqual(9, result.Month);
    }

    [TestMethod]
    public void TestCustomDateTimeParser_October()
    {
      var result = DateUtil.CustomDateTimeParser("MMM dd HH:mm:ss yyyy", "Oct 31 18:30:45 2024");
      Assert.AreEqual(10, result.Month);
    }

    [TestMethod]
    public void TestCustomDateTimeParser_November()
    {
      var result = DateUtil.CustomDateTimeParser("MMM dd HH:mm:ss yyyy", "Nov 11 11:11:11 2024");
      Assert.AreEqual(11, result.Month);
    }

    [TestMethod]
    public void TestCustomDateTimeParser_December()
    {
      var result = DateUtil.CustomDateTimeParser("MMM dd HH:mm:ss yyyy", "Dec 25 20:00:00 2024");
      Assert.AreEqual(12, result.Month);
    }

    [TestMethod]
    public void TestCustomDateTimeParser_InvalidMonth()
    {
      var result = DateUtil.CustomDateTimeParser("MMM dd HH:mm:ss yyyy", "Xyz 15 14:30:45 2024");
      Assert.AreEqual<DateTime>(DateTime.MinValue, result);
    }

    [TestMethod]
    public void TestCustomDateTimeParser_InvalidDay()
    {
      var result = DateUtil.CustomDateTimeParser("MMM dd HH:mm:ss yyyy", "Jan 00 14:30:45 2024");
      Assert.AreEqual<DateTime>(DateTime.MinValue, result);
    }

    [TestMethod]
    public void TestCustomDateTimeParser_InvalidHour()
    {
      var result = DateUtil.CustomDateTimeParser("MMM dd HH:mm:ss yyyy", "Jan 15 25:30:45 2024");
      Assert.AreEqual<DateTime>(DateTime.MinValue, result);
    }

    [TestMethod]
    public void TestCustomDateTimeParser_InvalidMinute()
    {
      var result = DateUtil.CustomDateTimeParser("MMM dd HH:mm:ss yyyy", "Jan 15 14:60:45 2024");
      Assert.AreEqual<DateTime>(DateTime.MinValue, result);
    }

    [TestMethod]
    public void TestCustomDateTimeParser_InvalidSecond()
    {
      var result = DateUtil.CustomDateTimeParser("MMM dd HH:mm:ss yyyy", "Jan 15 14:30:60 2024");
      Assert.AreEqual<DateTime>(DateTime.MinValue, result);
    }

    [TestMethod]
    public void TestCustomDateTimeParser_StringTooShort()
    {
      var result = DateUtil.CustomDateTimeParser("MMM dd HH:mm:ss yyyy", "Jan 15");
      Assert.AreEqual<DateTime>(DateTime.MinValue, result);
    }

    #endregion
  }
}
