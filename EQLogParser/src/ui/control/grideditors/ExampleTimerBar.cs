using Syncfusion.Windows.PropertyGrid;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Input;

namespace EQLogParser
{
  internal class ExampleTimerBar : ITypeEditor
  {
    private TimerBar TheTimerBar;

    public void Attach(PropertyViewItem property, PropertyItem info)
    {
      var overlayId = info.Value as string;
      TheTimerBar.Init(overlayId);
      TheTimerBar.Update("Example Timer Bar #1", "00:30", 60.0);
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

    public bool ShouldPropertyGridTryToHandleKeyDown(Key key)
    {
      return false;
    }
  }
}
