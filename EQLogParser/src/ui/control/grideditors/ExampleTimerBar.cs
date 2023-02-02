using Syncfusion.Windows.PropertyGrid;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;

namespace EQLogParser
{
  internal class ExampleTimerBar : ITypeEditor
  {
    private List<TimerBar> TheTimerBars = new List<TimerBar>();

    public void Attach(PropertyViewItem property, PropertyItem info)
    {
      TheTimerBars.Last().Init(info.Value as string, "Trigger Name #1", DateUtil.ToDouble(DateTime.Now) + 80, new Trigger(), true);
    }

    public object Create(PropertyInfo propertyInfo) => Create();
    public object Create(PropertyDescriptor descriotor) => Create();

    private object Create()
    {
      var timerBar = new TimerBar { Margin = new System.Windows.Thickness(5) };
      TheTimerBars.Add(timerBar);
      return timerBar;
    }

    public void Detach(PropertyViewItem property)
    {
      TheTimerBars.Clear();
    }
  }
}
