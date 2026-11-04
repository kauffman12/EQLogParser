using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EQLogParser;

/// <summary>Native horizontal scrolling without ScrollViewer's vertical-wheel consumption at a boundary.</summary>
public sealed class BarChartScrollViewer : ScrollViewer
{
  private double _wheelTarget = double.NaN;

  internal double ColumnStride { get; set; } = 100;

  internal void SetPresentationOffset(double offset)
  {
    _wheelTarget = double.NaN;
    ScrollToHorizontalOffset(offset);
  }

  protected override void OnMouseWheel(MouseWheelEventArgs e)
  {
    // ScrollTo is queued by the native viewer. Accumulate bursts against the owed position, not the last painted one.
    var start = double.IsNaN(_wheelTarget) ? HorizontalOffset : _wheelTarget;
    var offset = BarChartLayout.WheelOffset(start, e.Delta, SystemParameters.WheelScrollLines,
      ColumnStride, ViewportWidth, ExtentWidth);
    if (Math.Abs(offset - start) < 0.001) return;
    _wheelTarget = offset;
    ScrollToHorizontalOffset(offset);
    e.Handled = true;
  }

  protected override void OnScrollChanged(ScrollChangedEventArgs e)
  {
    if (e.ExtentWidthChange != 0 || e.ViewportWidthChange != 0 || Math.Abs(HorizontalOffset - _wheelTarget) < 0.001)
      _wheelTarget = double.NaN;
    base.OnScrollChanged(e);
  }

  protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
  {
    _wheelTarget = double.NaN;
    base.OnPreviewMouseDown(e);
  }

  protected override void OnPreviewKeyDown(KeyEventArgs e)
  {
    _wheelTarget = double.NaN;
    base.OnPreviewKeyDown(e);
  }
}
