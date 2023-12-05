using Syncfusion.Windows.PropertyGrid;
using Syncfusion.Windows.Shared;
using System;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace EQLogParser
{
  internal class DurationEditor : BaseTypeEditor
  {
    private TimeSpanEdit _theTimeSpan;
    private readonly int _min;

    public DurationEditor()
    {
      _min = 0;
    }

    public DurationEditor(int min)
    {
      _min = min;
    }

    public override void Attach(PropertyViewItem property, PropertyItem info)
    {
      var binding = new Binding("Value")
      {
        Mode = info.CanWrite ? BindingMode.TwoWay : BindingMode.OneWay,
        Source = info,
        ValidatesOnExceptions = true,
        ValidatesOnDataErrors = true
      };

      BindingOperations.SetBinding(_theTimeSpan, TimeSpanEdit.ValueProperty, binding);
    }

    public override object Create(PropertyInfo propertyInfo) => Create();
    public override object Create(PropertyDescriptor descriotor) => Create();

    private object Create()
    {
      var timeSpan = new TimeSpanEdit
      {
        IncrementOnScrolling = false,
        MinValue = new TimeSpan(0, 0, _min),
        MaxValue = new TimeSpan(23, 59, 59),
        Format = "hh : mm : ss",
        Margin = new Thickness(0, 2, 0, 2)
      };

      _theTimeSpan = timeSpan;
      timeSpan.GotFocus += TimeSpanGotFocus;
      timeSpan.LostFocus += TimeSpanLostFocus;
      timeSpan.PreviewMouseWheel += TimeSpanPreviewMouseWheel;
      return timeSpan;
    }

    private void TimeSpanPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
      if (sender is TimeSpanEdit { SelectionStart: var selected, Value: { } t, DataContext: PropertyItem item })
      {
        if (item.PropertyGrid.SelectedPropertyItem is { Editor: DurationEditor editor } && editor == this)
        {
          var inc = e.Delta > 0 ? 1 : -1;
          if (selected >= 10)
          {
            _theTimeSpan.Value = new TimeSpan(t.Hours, t.Minutes, t.Seconds + inc);
          }
          else if (selected >= 5)
          {
            _theTimeSpan.Value = new TimeSpan(t.Hours, t.Minutes + inc, t.Seconds);
          }
          else if (selected >= 0)
          {
            _theTimeSpan.Value = new TimeSpan(t.Hours + inc, t.Minutes, t.Seconds);
          }
          e.Handled = true;
        }
      }
    }

    private void TimeSpanLostFocus(object sender, RoutedEventArgs e)
    {
      if (sender is TimeSpanEdit edit)
      {
        edit.IncrementOnScrolling = false;
      }
    }

    private void TimeSpanGotFocus(object sender, RoutedEventArgs e)
    {
      if (sender is TimeSpanEdit edit)
      {
        edit.IncrementOnScrolling = true;
      }
    }

    public override bool ShouldPropertyGridTryToHandleKeyDown(Key key)
    {
      if (key == Key.Tab)
      {
        if (_theTimeSpan.SelectionStart is >= 0 and <= 2)
        {
          _theTimeSpan.SelectionStart = 3;
        }
      }
      return false;
    }

    public override void Detach(PropertyViewItem property)
    {
      if (_theTimeSpan != null)
      {
        _theTimeSpan.GotFocus -= TimeSpanGotFocus;
        _theTimeSpan.LostFocus -= TimeSpanLostFocus;
        _theTimeSpan.PreviewMouseWheel -= TimeSpanPreviewMouseWheel;
        BindingOperations.ClearAllBindings(_theTimeSpan);
        _theTimeSpan?.Dispose();
        _theTimeSpan = null;
      }
    }
  }
}
