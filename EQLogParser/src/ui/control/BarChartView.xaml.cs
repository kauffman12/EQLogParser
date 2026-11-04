using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace EQLogParser;

/// <summary>A fixed axis and native scrolling extent with only viewport-sized column visuals.</summary>
public partial class BarChartView : UserControl
{
  private readonly List<BarChartColumnVisual> _columns = [];
  private readonly Rectangle _baseline = new() { Height = 1 };
  private BarChartData _data = BarChartData.Frequency([]);
  private BarChartBounds _bounds;
  private double _stride = 100;
  private double _pendingOffset = double.NaN;
  private bool _ready;
  private bool _loaded;
  private bool _arranging;
  private bool _axisDirty = true;
  private int _version;

  public BarChartView()
  {
    InitializeComponent();
    _baseline.SetResourceReference(Shape.FillProperty, "ContentForeground");
    strip.Children.Add(_baseline);
    _ready = true;
    RefreshPresentation();
  }

  internal BarChartData Data => _data;
  internal int RealizedColumnCount => _columns.Count;
  internal double ColumnStride => _stride;

  internal void SetData(BarChartData data, bool resetOffset = true)
  {
    VerifyAccess();
    foreach (var column in _columns) column.CloseHover();
    _data = data;
    _version++;
    _axisDirty = true;
    RefreshPresentation(resetOffset);
  }

  internal void RefreshPresentation(bool resetOffset = false)
  {
    if (!_ready) return;
    var oldStride = _stride;
    var oldOffset = double.IsNaN(_pendingOffset) ? scroll.HorizontalOffset : _pendingOffset;
    FontSize = double.IsFinite(ThemeConfig.CurrentFontSize) && ThemeConfig.CurrentFontSize > 0
      ? ThemeConfig.CurrentFontSize : 12;
    FontFamily = new FontFamily(string.IsNullOrEmpty(ThemeConfig.CurrentFontFamily)
      ? "Segoe UI" : ThemeConfig.CurrentFontFamily);
    var width = 60.0;
    foreach (var sample in _data.WidthSamples()) width = Math.Max(width, Measure(sample));
    _stride = width + 24;
    scroll.ColumnStride = _stride;
    axisColumn.Width = new GridLength(Measure(_data.Axis.Maximum.ToString("N0", CultureInfo.CurrentCulture)) + 20);
    axisTitle.Text = _data.Title;
    emptyMessage.Text = _data.EmptyText;
    emptyMessage.Visibility = _data.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    _pendingOffset = resetOffset ? 0 : BarChartLayout.PreservePosition(oldOffset, oldStride, _stride,
      BarChartLayout.Extent(_data.Count, _stride), scroll.ViewportWidth);
    _axisDirty = true;
    UpdateGeometry();
  }

  internal void Activate()
  {
    if (!_loaded)
    {
      ThemeConfig.EventsThemeChanged += ThemeChanged;
      _loaded = true;
    }
    RefreshPresentation();
  }

  internal void Deactivate()
  {
    if (_loaded) ThemeConfig.EventsThemeChanged -= ThemeChanged;
    _loaded = false;
    foreach (var column in _columns) column.CloseHover();
  }

  protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
  {
    base.OnDpiChanged(oldDpi, newDpi);
    RefreshPresentation();
  }

  private void ViewLoaded(object sender, RoutedEventArgs e) => Activate();
  private void ViewUnloaded(object sender, RoutedEventArgs e) => Deactivate();

  private void ThemeChanged(string _) => RefreshPresentation();
  private void ViewSizeChanged(object sender, SizeChangedEventArgs e) => UpdateGeometry();

  private void ScrollChanged(object sender, ScrollChangedEventArgs e)
  {
    foreach (var column in _columns) column.CloseHover();
    UpdateGeometry();
  }

  private void UpdateGeometry()
  {
    if (!_ready || _arranging) return;
    _arranging = true;
    try
    {
      var extent = BarChartLayout.Extent(_data.Count, _stride);
      strip.Width = extent;
      var height = Math.Max(0, scroll.ViewportHeight);
      strip.Height = height;
      var bounds = BarChartLayout.Bounds(height, FontSize, _data.HasSecondary);
      if (_axisDirty || bounds != _bounds)
      {
        _bounds = bounds;
        DrawAxis();
        _axisDirty = false;
      }
      _baseline.Width = extent;
      _baseline.Visibility = _data.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
      Canvas.SetTop(_baseline, bounds.Bottom);

      // ScrollViewer publishes new extents on its next layout. Do not clamp a new question to the OLD extent.
      var extentReady = extent <= scroll.ViewportWidth
        ? scroll.ExtentWidth <= scroll.ViewportWidth + 0.5 : Math.Abs(scroll.ExtentWidth - extent) < 0.5;
      if (!double.IsNaN(_pendingOffset) && extentReady)
      {
        scroll.SetPresentationOffset(BarChartLayout.ClampOffset(_pendingOffset, extent, scroll.ViewportWidth));
        _pendingOffset = double.NaN;
      }
      var range = BarChartLayout.Visible(_data.Count, _stride, scroll.HorizontalOffset, scroll.ViewportWidth);
      while (_columns.Count > range.Count)
      {
        var last = _columns[^1];
        last.Release();
        strip.Children.Remove(last.Root);
        _columns.RemoveAt(_columns.Count - 1);
      }
      while (_columns.Count < range.Count)
      {
        var column = new BarChartColumnVisual();
        _columns.Add(column);
        strip.Children.Add(column.Root);
      }
      for (var i = 0; i < _columns.Count; i++)
      {
        var column = _columns[i];
        var index = range.First + i;
        if (column.Index != index || column.Version != _version) column.Bind(index, _version, _data.Column(index));
        column.Arrange(_stride, height, FontSize, FontFamily, bounds, _data.Axis.Maximum);
      }
    }
    finally
    {
      _arranging = false;
    }
  }

  private void DrawAxis()
  {
    axis.Children.Clear();
    if (_data.Count == 0) return;
    var width = axisColumn.Width.Value;
    var spine = new Rectangle { Width = 1, Height = _bounds.Height };
    spine.SetResourceReference(Shape.FillProperty, "ContentForeground");
    Canvas.SetLeft(spine, width - 1);
    Canvas.SetTop(spine, _bounds.Top);
    axis.Children.Add(spine);
    for (var value = 0L; value <= _data.Axis.Maximum; value += _data.Axis.Step)
    {
      var top = _bounds.Bottom - BarChartLayout.BarHeight(value, _data.Axis.Maximum, _bounds.Height);
      var label = new TextBlock
      {
        Text = value.ToString("N0", CultureInfo.CurrentCulture), Width = Math.Max(0, width - 10),
        TextAlignment = TextAlignment.Right, FontSize = FontSize, FontFamily = FontFamily
      };
      label.SetResourceReference(TextBlock.ForegroundProperty, "ContentForeground");
      Canvas.SetTop(label, Math.Max(0, top - (_bounds.LineHeight / 2)));
      axis.Children.Add(label);
      var tick = new Rectangle { Width = 6, Height = 1 };
      tick.SetResourceReference(Shape.FillProperty, "ContentForeground");
      Canvas.SetLeft(tick, width - 6);
      Canvas.SetTop(tick, top);
      axis.Children.Add(tick);
    }
  }

  private double Measure(string text)
    => new FormattedText(text ?? "", CultureInfo.CurrentCulture, FlowDirection,
      new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), FontSize,
      Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;
}
