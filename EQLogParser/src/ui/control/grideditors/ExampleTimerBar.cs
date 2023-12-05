using Syncfusion.Windows.PropertyGrid;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Input;

namespace EQLogParser
{
  internal class ExampleTimerBar : ITypeEditor
  {
    private TimerBar _theTimerBar;

    public void Attach(PropertyViewItem property, PropertyItem info)
    {
      var overlayId = info.Value as string;
      _theTimerBar.Init(overlayId);
      _theTimerBar.Update("Example Timer Bar #1", "00:30", 60.0, new TimerData());
    }

    public object Create(PropertyInfo propertyInfo) => Create();
    public object Create(PropertyDescriptor descriotor) => Create();

    private object Create()
    {
      var timerBar = new TimerBar { Margin = new Thickness(5) };
      _theTimerBar = timerBar;
      return timerBar;
    }

    public void Detach(PropertyViewItem property)
    {
      _theTimerBar = null;
    }

    public bool ShouldPropertyGridTryToHandleKeyDown(Key key)
    {
      return false;
    }
  }
}
