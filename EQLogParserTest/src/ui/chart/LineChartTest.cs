using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EQLogParser;

namespace EQLogParserTest
{
  [TestClass]
  public class LineChartTest
  {
    private static MethodInfo _populateRollingMethod;

    [ClassInitialize]
    public static void ClassInitialize(TestContext context)
    {
      var lineChartType = typeof(LineChart);
      _populateRollingMethod = lineChartType.GetMethod(
        "PopulateRolling",
        BindingFlags.NonPublic | BindingFlags.Static);

      if (_populateRollingMethod == null)
      {
        throw new InvalidOperationException("Could not find PopulateRolling method via reflection");
      }
    }

    private static void RunPopulateRolling(Dictionary<string, List<DataPoint>> data)
    {
      _populateRollingMethod.Invoke(null, new object[] { data });
    }

    private static DataPoint CreatePoint(double currentTime, long totalPerSecond)
    {
      return new DataPoint
      {
        CurrentTime = currentTime,
        TotalPerSecond = totalPerSecond
      };
    }

    [TestMethod]
    public void TestPopulateRolling_EmptyData()
    {
      var data = new Dictionary<string, List<DataPoint>>
      {
        { "test", new List<DataPoint>() }
      };

      RunPopulateRolling(data);

      Assert.AreEqual(0, data["test"].Count);
    }

    [TestMethod]
    public void TestPopulateRolling_SinglePoint()
    {
      var points = new List<DataPoint> { CreatePoint(0, 100) };
      var data = new Dictionary<string, List<DataPoint>> { { "test", points } };

      RunPopulateRolling(data);

      Assert.AreEqual(100, points[0].RollingTotal);
      Assert.AreEqual(100, points[0].RollingDps);
    }

    [TestMethod]
    public void TestPopulateRolling_TwoPointsWithinWindow()
    {
      var points = new List<DataPoint>
      {
        CreatePoint(0, 100),
        CreatePoint(1, 200)
      };
      var data = new Dictionary<string, List<DataPoint>> { { "test", points } };

      RunPopulateRolling(data);

      Assert.AreEqual(100, points[0].RollingTotal);
      Assert.AreEqual(100, points[0].RollingDps);
      Assert.AreEqual(300, points[1].RollingTotal);
      Assert.AreEqual(150, points[1].RollingDps);
    }

    [TestMethod]
      public void TestPopulateRolling_PointsOutsideWindow()
    {
      var points = new List<DataPoint>
      {
        CreatePoint(0, 100),
        CreatePoint(3, 200),
        CreatePoint(6, 300)
      };
      var data = new Dictionary<string, List<DataPoint>> { { "test", points } };

      RunPopulateRolling(data);

      Assert.AreEqual(100, points[0].RollingTotal);
      Assert.AreEqual(100, points[0].RollingDps);
      Assert.AreEqual(200, points[1].RollingTotal);
      Assert.AreEqual(200, points[1].RollingDps);
      Assert.AreEqual(300, points[2].RollingTotal);
      Assert.AreEqual(300, points[2].RollingDps);
    }

    [TestMethod]
    public void TestPopulateRolling_SlidingWindow()
    {
      var points = new List<DataPoint>
      {
        CreatePoint(0, 100),
        CreatePoint(1, 100),
        CreatePoint(2, 100),
        CreatePoint(4, 200),
        CreatePoint(5, 200)
      };
      var data = new Dictionary<string, List<DataPoint>> { { "test", points } };

      RunPopulateRolling(data);

      Assert.AreEqual(100, points[0].RollingTotal);
      Assert.AreEqual(100, points[0].RollingDps);
      Assert.AreEqual(200, points[1].RollingTotal);
      Assert.AreEqual(100, points[1].RollingDps);
      Assert.AreEqual(300, points[2].RollingTotal);
      Assert.AreEqual(100, points[2].RollingDps);
      Assert.AreEqual(300, points[3].RollingTotal);
      Assert.AreEqual(150, points[3].RollingDps);
      Assert.AreEqual(400, points[4].RollingTotal);
      Assert.AreEqual(200, points[4].RollingDps);
    }

    [TestMethod]
    public void TestPopulateRolling_ZeroValues()
    {
      var points = new List<DataPoint>
      {
        CreatePoint(0, 0),
        CreatePoint(1, 0),
        CreatePoint(2, 0)
      };
      var data = new Dictionary<string, List<DataPoint>> { { "test", points } };

      RunPopulateRolling(data);

      Assert.AreEqual(0, points[0].RollingTotal);
      Assert.AreEqual(0, points[0].RollingDps);
      Assert.AreEqual(0, points[1].RollingTotal);
      Assert.AreEqual(0, points[1].RollingDps);
      Assert.AreEqual(0, points[2].RollingTotal);
      Assert.AreEqual(0, points[2].RollingDps);
    }

    [TestMethod]
    public void TestPopulateRolling_IncreasingValues()
    {
      var points = new List<DataPoint>
      {
        CreatePoint(0, 10),
        CreatePoint(1, 20),
        CreatePoint(2, 30),
        CreatePoint(3, 40),
        CreatePoint(4, 50)
      };
      var data = new Dictionary<string, List<DataPoint>> { { "test", points } };

      RunPopulateRolling(data);

      Assert.AreEqual(10, points[0].RollingTotal);
      Assert.AreEqual(10, points[0].RollingDps);
      Assert.AreEqual(30, points[1].RollingTotal);
      Assert.AreEqual(15, points[1].RollingDps);
      Assert.AreEqual(60, points[2].RollingTotal);
      Assert.AreEqual(20, points[2].RollingDps);
      Assert.AreEqual(100, points[3].RollingTotal);
      Assert.AreEqual(25, points[3].RollingDps);
      Assert.AreEqual(150, points[4].RollingTotal);
      Assert.AreEqual(30, points[4].RollingDps);
    }

    [TestMethod]
    public void TestPopulateRolling_DecreasingValues()
    {
      var points = new List<DataPoint>
      {
        CreatePoint(0, 50),
        CreatePoint(1, 40),
        CreatePoint(2, 30),
        CreatePoint(3, 20),
        CreatePoint(4, 10)
      };
      var data = new Dictionary<string, List<DataPoint>> { { "test", points } };

      RunPopulateRolling(data);

      Assert.AreEqual(50, points[0].RollingTotal);
      Assert.AreEqual(50, points[0].RollingDps);
      Assert.AreEqual(90, points[1].RollingTotal);
      Assert.AreEqual(45, points[1].RollingDps);
      Assert.AreEqual(120, points[2].RollingTotal);
      Assert.AreEqual(40, points[2].RollingDps);
      Assert.AreEqual(120, points[3].RollingTotal);
      Assert.AreEqual(30, points[3].RollingDps);
      Assert.AreEqual(100, points[4].RollingTotal);
      Assert.AreEqual(25, points[4].RollingDps);
    }

    [TestMethod]
    public void TestPopulateRolling_MultipleSeries()
    {
      var series1 = new List<DataPoint>
      {
        CreatePoint(0, 100),
        CreatePoint(1, 200)
      };
      var series2 = new List<DataPoint>
      {
        CreatePoint(0, 50),
        CreatePoint(1, 150)
      };
      var data = new Dictionary<string, List<DataPoint>>
      {
        { "series1", series1 },
        { "series2", series2 }
      };

      RunPopulateRolling(data);

      Assert.AreEqual(100, series1[0].RollingTotal);
      Assert.AreEqual(300, series1[1].RollingTotal);
      Assert.AreEqual(50, series2[0].RollingTotal);
      Assert.AreEqual(200, series2[1].RollingTotal);
    }

    [TestMethod]
    public void TestPopulateRolling_WindowBoundaryExactlyFive()
    {
      var points = new List<DataPoint>
      {
        CreatePoint(0, 100),
        CreatePoint(5, 200)
      };
      var data = new Dictionary<string, List<DataPoint>> { { "test", points } };

      RunPopulateRolling(data);

      Assert.AreEqual(100, points[0].RollingTotal);
      Assert.AreEqual(100, points[0].RollingDps);
      Assert.AreEqual(200, points[1].RollingTotal);
      Assert.AreEqual(200, points[1].RollingDps);
    }

    [TestMethod]
    public void TestPopulateRolling_WindowBoundaryJustOverFive()
    {
      var points = new List<DataPoint>
      {
        CreatePoint(0, 100),
        CreatePoint(5, 200),
        CreatePoint(6, 300)
      };
      var data = new Dictionary<string, List<DataPoint>> { { "test", points } };

      RunPopulateRolling(data);

      Assert.AreEqual(100, points[0].RollingTotal);
      Assert.AreEqual(100, points[0].RollingDps);
      Assert.AreEqual(200, points[1].RollingTotal);
      Assert.AreEqual(200, points[1].RollingDps);
      Assert.AreEqual(500, points[2].RollingTotal);
      Assert.AreEqual(250, points[2].RollingDps);
    }

    [TestMethod]
    public void TestPopulateRolling_LargeDataSet()
    {
      var count = 10000;
      var points = new List<DataPoint>();
      for (var i = 0; i < count; i++)
      {
        points.Add(CreatePoint(i, 100));
      }
      var data = new Dictionary<string, List<DataPoint>> { { "test", points } };

      RunPopulateRolling(data);

      var lastPoint = points[count - 1];
      Assert.AreEqual(500, lastPoint.RollingTotal);
      Assert.AreEqual(100, lastPoint.RollingDps);
    }

    [TestMethod]
    public void TestPopulateRolling_GapsInTime()
    {
      var points = new List<DataPoint>
      {
        CreatePoint(0, 100),
        CreatePoint(1, 100),
        CreatePoint(10, 100),
        CreatePoint(11, 100),
        CreatePoint(20, 100)
      };
      var data = new Dictionary<string, List<DataPoint>> { { "test", points } };

      RunPopulateRolling(data);

      Assert.AreEqual(100, points[0].RollingTotal);
      Assert.AreEqual(200, points[1].RollingTotal);
      Assert.AreEqual(100, points[2].RollingTotal);
      Assert.AreEqual(200, points[3].RollingTotal);
      Assert.AreEqual(100, points[4].RollingTotal);
    }

    [TestMethod]
    public void TestPopulateRolling_VeryLargeValues()
    {
      var points = new List<DataPoint>
      {
        CreatePoint(0, long.MaxValue / 2),
        CreatePoint(1, long.MaxValue / 4)
      };
      var data = new Dictionary<string, List<DataPoint>> { { "test", points } };

      try
      {
        RunPopulateRolling(data);
        Assert.Fail("Should have thrown on overflow");
      }
      catch (TargetInvocationException)
      {
      }
    }

    [TestMethod]
    public void TestPopulateRolling_NegativeValues()
    {
      var points = new List<DataPoint>
      {
        CreatePoint(0, -100),
        CreatePoint(1, 200),
        CreatePoint(2, -50)
      };
      var data = new Dictionary<string, List<DataPoint>> { { "test", points } };

      RunPopulateRolling(data);

      Assert.AreEqual(-100, points[0].RollingTotal);
      Assert.AreEqual(100, points[1].RollingTotal);
      Assert.AreEqual(50, points[2].RollingTotal);
    }

    [TestMethod]
    public void TestPopulateRolling_AllSameTime()
    {
      var points = new List<DataPoint>
      {
        CreatePoint(0, 100),
        CreatePoint(0, 200),
        CreatePoint(0, 300)
      };
      var data = new Dictionary<string, List<DataPoint>> { { "test", points } };

      RunPopulateRolling(data);

      Assert.AreEqual(100, points[0].RollingTotal);
      Assert.AreEqual(300, points[1].RollingTotal);
      Assert.AreEqual(600, points[2].RollingTotal);
    }

    [TestMethod]
    public void TestPopulateRolling_RealisticDpsPattern()
    {
      var points = new List<DataPoint>
      {
        CreatePoint(0, 0),
        CreatePoint(1, 2500),
        CreatePoint(2, 3000),
        CreatePoint(3, 2800),
        CreatePoint(4, 3200),
        CreatePoint(5, 0),
        CreatePoint(6, 2000),
        CreatePoint(7, 2500)
      };
      var data = new Dictionary<string, List<DataPoint>> { { "test", points } };

      RunPopulateRolling(data);

      Assert.AreEqual(0, points[0].RollingTotal);
      Assert.AreEqual(2500, points[1].RollingTotal);
      Assert.AreEqual(5500, points[2].RollingTotal);
      Assert.AreEqual(8300, points[3].RollingTotal);
      Assert.AreEqual(11500, points[4].RollingTotal);
      Assert.AreEqual(3200, points[5].RollingTotal);
      Assert.AreEqual(5200, points[6].RollingTotal);
      Assert.AreEqual(7700, points[7].RollingTotal);
    }
  }
}
