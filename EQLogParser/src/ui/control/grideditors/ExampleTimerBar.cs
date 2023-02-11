using Syncfusion.Windows.PropertyGrid;
using System;
using System.ComponentModel;
using System.Reflection;

namespace EQLogParser
{
  internal class ExampleTimerBar : ITypeEditor
  {
    private TimerBar TheTimerBar;

    public void Attach(PropertyViewItem property, PropertyItem info)
    {
      TheTimerBar.Init(info.Value as string, "timerKey", "Trigger Name #1", DateUtil.ToDouble(DateTime.Now) + 80, new Trigger(), true);
    }

    public object Create(PropertyInfo propertyInfo) => Create();
    public object Create(PropertyDescriptor descriotor) => Create();

    private object Create()
    {
      var timerBar = new TimerBar { Margin = new System.Windows.Thickness(5) };
      TheTimerBar = timerBar;
      return timerBar;
    }

    public void Detach(PropertyViewItem property)
    {
      TheTimerBar = null;
    }
  }
}
