using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace EQLogParser;

/// <summary>One reusable column's bounded set of visuals; no statistics or capture references.</summary>
internal sealed class BarChartColumnVisual
{
  private readonly Rectangle _bar = new();
  private readonly TextBlock _above = Text();
  private readonly TextBlock _primary = Text();
  private readonly TextBlock _secondary = Text();
  private readonly TextBlock _group = Text();
  private readonly Canvas _groupBand = new() { ClipToBounds = true, IsHitTestVisible = false };
  private readonly ToolTip _tooltip = new();
  private double _value;

  internal Canvas Root { get; } = new() { ClipToBounds = true, Background = Brushes.Transparent };
  internal int Index { get; private set; } = -1;
  internal int Version { get; private set; } = -1;

  internal BarChartColumnVisual()
  {
    Root.ToolTip = _tooltip;
    Root.Children.Add(_bar);
    Root.Children.Add(_above);
    Root.Children.Add(_primary);
    Root.Children.Add(_secondary);
    Root.Children.Add(_groupBand);
    _groupBand.Children.Add(_group);
    _group.LayoutTransform = new RotateTransform(-90);
    _group.FontWeight = FontWeights.Bold;
  }

  internal void Bind(int index, int version, BarChartColumn column)
  {
    CloseHover();
    Index = index;
    Version = version;
    _value = column.Value;
    _above.Text = column.Above;
    _primary.Text = column.Primary;
    _secondary.Text = column.Secondary;
    _group.Text = column.Group;
    _tooltip.Content = column.Tooltip;
    _bar.SetResourceReference(Shape.FillProperty, column.ColorKey);
  }

  internal void Arrange(double stride, double height, double fontSize, FontFamily family,
    BarChartBounds bounds, long maximum)
  {
    Root.Width = stride;
    Root.Height = height;
    Canvas.SetLeft(Root, Index * stride);
    var barHeight = BarChartLayout.BarHeight(_value, maximum, bounds.Height);
    _bar.Width = stride * 0.55;
    _bar.Height = barHeight;
    Canvas.SetLeft(_bar, (stride - _bar.Width) / 2);
    Canvas.SetTop(_bar, bounds.Bottom - barHeight);
    PlaceText(_above, Math.Max(0, bounds.Bottom - barHeight - bounds.LineHeight), bounds.LineHeight);
    PlaceText(_primary, bounds.PrimaryTop, bounds.LineHeight);
    PlaceText(_secondary, bounds.SecondaryTop, bounds.LineHeight);
    _group.FontFamily = family;
    _group.FontSize = fontSize + 10;
    _group.Visibility = string.IsNullOrEmpty(_group.Text) || bounds.Height == 0
      ? Visibility.Collapsed : Visibility.Visible;
    _group.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
    Canvas.SetLeft(_group, Math.Max(0, (stride - _group.DesiredSize.Width) / 2));
    _groupBand.Width = stride;
    _groupBand.Height = bounds.Height;
    Canvas.SetTop(_groupBand, bounds.Top);
    Canvas.SetTop(_group, Math.Max(0, (bounds.Height - _group.DesiredSize.Height) / 2));

    void PlaceText(TextBlock text, double top, double lineHeight)
    {
      text.FontSize = fontSize;
      text.FontFamily = family;
      text.Width = Math.Max(0, stride - 8);
      text.Height = lineHeight;
      Canvas.SetLeft(text, 4);
      Canvas.SetTop(text, top);
    }
  }

  internal void CloseHover() => _tooltip.IsOpen = false;

  internal void Release()
  {
    CloseHover();
    _tooltip.Content = null;
    Root.ToolTip = null;
  }

  private static TextBlock Text()
  {
    var text = new TextBlock { TextAlignment = TextAlignment.Center, IsHitTestVisible = false };
    text.SetResourceReference(TextBlock.ForegroundProperty, "ContentForeground");
    return text;
  }
}
