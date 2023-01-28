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
      TheTimerBar.Init(info.Value as string, "Trigger Name #1", DateUtil.ToDouble(DateTime.Now) + 80, true);
    }

    public object Create(PropertyInfo propertyInfo)
    {
      TheTimerBar = new TimerBar { Margin = new System.Windows.Thickness(5) };
      return TheTimerBar;
    }

    public object Create(PropertyDescriptor descriotor)
    {
      TheTimerBar = new TimerBar { Margin = new System.Windows.Thickness(5) };
      return TheTimerBar;
    }

    public void Detach(PropertyViewItem property)
    {
      TheTimerBar = null;
    }
  }
}
