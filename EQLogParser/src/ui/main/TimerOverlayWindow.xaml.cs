using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for TimerOverlayWindow.xaml
  /// </summary>
  public partial class TimerOverlayWindow : Window
  {
    private Dictionary<string, TimerBar> TimerBarCache = new Dictionary<string, TimerBar>();

    private string OverlayId;

    public TimerOverlayWindow(string overlayId)
    {
      InitializeComponent();
      OverlayId = overlayId;
    }

    internal void CreateTimer(string name, double endTime)
    {
      var timerBar = new TimerBar();
      timerBar.InitOverlay(OverlayId, name, endTime);
      TimerBarCache[name] = timerBar;
      content.Children.Add(timerBar);
    }

    internal void ResetTimer(string name, double endTime)
    {
      if (TimerBarCache.TryGetValue(name, out TimerBar timerBar))
      {
        timerBar.Update(endTime);
      }
      else
      {
        CreateTimer(name, endTime);
      }
    }

    internal bool Tick()
    {
      bool remaining = false;
      var removeList = new List<TimerBar>();

      foreach (var child in content.Children)
      {
        if (child is TimerBar bar)
        {
          if (bar.Tick())
          {
            removeList.Add(bar);
          }
          else
          {
            remaining = true;
          }
        }
      }

      removeList.ForEach(timerBar =>
      {
        content.Children.Remove(timerBar);
        TimerBarCache.Remove(timerBar.GetBarName());
      });

      return !remaining;
    }

    private void OverlayMouseLeftDown(object sender, MouseButtonEventArgs e) => this.DragMove();

    protected override void OnSourceInitialized(EventArgs e)
    {
      base.OnSourceInitialized(e);
      var source = (HwndSource)PresentationSource.FromVisual(this);
      // set to layered and topmost by xaml
      int exStyle = (int)NativeMethods.GetWindowLongPtr(source.Handle, (int)NativeMethods.GetWindowLongFields.GWL_EXSTYLE);
      exStyle |= (int)NativeMethods.ExtendedWindowStyles.WS_EX_TOOLWINDOW;
      NativeMethods.SetWindowLong(source.Handle, (int)NativeMethods.GetWindowLongFields.GWL_EXSTYLE, (IntPtr)exStyle);
    }
  }
}
