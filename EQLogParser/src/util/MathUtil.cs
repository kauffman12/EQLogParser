
using System;
using System.Windows;

namespace EQLogParser
{
  internal class MathUtil
  {
    // Helper method to calculate the distance between two points
    internal static double GetDistance(Point p1, Point p2)
    {
      return Math.Sqrt(Math.Pow(p1.X - p2.X, 2) + Math.Pow(p1.Y - p2.Y, 2));
    }
  }
}
