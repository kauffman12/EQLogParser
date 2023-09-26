using Syncfusion.Windows.PropertyGrid;
using Syncfusion.Windows.Shared;
using System;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Data;
using System.Windows.Input;

namespace EQLogParser
{
  internal class DurationEditor : BaseTypeEditor
  {
    private TimeSpanEdit TheTimeSpan;
    private int Min;

    public DurationEditor()
    {
      Min = 0;
    }

    public DurationEditor(int min)
    {
      Min = min;
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

      BindingOperations.SetBinding(TheTimeSpan, TimeSpanEdit.ValueProperty, binding);
    }

    public override object Create(PropertyInfo propertyInfo) => Create();
    public override object Create(PropertyDescriptor descriotor) => Create();

    private object Create()
    {
      var timeSpan = new TimeSpanEdit
      {
        IncrementOnScrolling = false,
        MinValue = new System.TimeSpan(0, 0, Min),
        MaxValue = new System.TimeSpan(23, 59, 59),
        Format = "hh : mm : ss",
        Margin = new System.Windows.Thickness(0, 2, 0, 2)
      };

      TheTimeSpan = timeSpan;
      timeSpan.GotFocus += TimeSpanGotFocus;
      timeSpan.LostFocus += TimeSpanLostFocus;
      timeSpan.PreviewMouseWheel += TimeSpanPreviewMouseWheel;
      return timeSpan;
    }

    private void TimeSpanPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
      if (TheTimeSpan?.SelectionStart is int selected && TheTimeSpan.Value is TimeSpan t)
      {
        var inc = e.Delta > 0 ? 1 : -1;
        if (selected >= 10)
        {
          TheTimeSpan.Value = new TimeSpan(t.Hours, t.Minutes, t.Seconds + inc);
        }
        else if (selected >= 5)
        {
          TheTimeSpan.Value = new TimeSpan(t.Hours, t.Minutes + inc, t.Seconds);
        }
        else if (selected >= 0)
        {
          TheTimeSpan.Value = new TimeSpan(t.Hours + inc, t.Minutes, t.Seconds);
        }
        e.Handled = true;
      }
    }

    private void TimeSpanLostFocus(object sender, System.Windows.RoutedEventArgs e)
    {
      if (sender is TimeSpanEdit edit)
      {
        edit.IncrementOnScrolling = false;
      }
    }

    private void TimeSpanGotFocus(object sender, System.Windows.RoutedEventArgs e)
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
        if (TheTimeSpan.SelectionStart >= 0 && TheTimeSpan.SelectionStart <= 2)
        {
          TheTimeSpan.SelectionStart = 3;
        }
      }
      return false;
    }

    public override void Detach(PropertyViewItem property)
    {
      if (TheTimeSpan != null)
      {
        TheTimeSpan.GotFocus -= TimeSpanGotFocus;
        TheTimeSpan.LostFocus -= TimeSpanLostFocus;
        TheTimeSpan.PreviewMouseWheel -= TimeSpanPreviewMouseWheel;
        BindingOperations.ClearAllBindings(TheTimeSpan);
        TheTimeSpan?.Dispose();
        TheTimeSpan = null;
      }
    }
  }
}
