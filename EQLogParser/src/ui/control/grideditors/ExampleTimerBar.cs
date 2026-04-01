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
      _theTimerBar.Visibility = Visibility.Visible;
    }

    public object Create(PropertyInfo _) => Create();
    public object Create(PropertyDescriptor _) => Create();

    private object Create()
    {
      if (_theTimerBar != null)
        return _theTimerBar;

      _theTimerBar = new TimerBar { Margin = new Thickness(5) };
      return _theTimerBar;
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
