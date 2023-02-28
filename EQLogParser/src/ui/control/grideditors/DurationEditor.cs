using Syncfusion.Windows.PropertyGrid;
using Syncfusion.Windows.Shared;
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

    public DurationEditor(int min = 0)
    {
      Min = min;
    }

    public override void Attach(PropertyViewItem property, PropertyItem info)
    {
      Binding binding = new Binding("Value")
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
        MaxValue = new System.TimeSpan(0, 59, 59),
        Format = "mm:ss"
      };

      TheTimeSpan = timeSpan;
      timeSpan.GotFocus += TimeSpanGotFocus;
      timeSpan.LostFocus += TimeSpanLostFocus;
      return timeSpan;
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
        BindingOperations.ClearAllBindings(TheTimeSpan);
        TheTimeSpan?.Dispose();
        TheTimeSpan = null;
      }
    }
  }
}
