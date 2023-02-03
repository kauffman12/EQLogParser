using Syncfusion.Data.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for TimerOverlayWindow.xaml
  /// </summary>
  public partial class TimerOverlayWindow : Window
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private static SolidColorBrush BarelyVisible = new SolidColorBrush { Color = (Color) ColorConverter.ConvertFromString("#01000000") };
    private static SolidColorBrush BorderColor = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#AA000000") };
    private Dictionary<string, List<TimerBar>> TimerBarCache = new Dictionary<string, List<TimerBar>>();
    private List<TimerBar> TimerBarCreateOrder = new List<TimerBar>();
    private Overlay TheOverlay;
    private bool Preview = false;
    private double SavedHeight;
    private double SavedWidth;
    private double SavedTop = double.NaN;
    private double SavedLeft = double.NaN;
    private int CurrentOrder;
    private bool CurrentUseStandardTime;

    internal TimerOverlayWindow(Overlay overlay, bool preview = false)
    {
      InitializeComponent();
      Preview = preview;
      TheOverlay = overlay;
      this.border.SetResourceReference(Border.BackgroundProperty, "OverlayBrushColor-" + TheOverlay.Id);
      title.SetResourceReference(TextBlock.TextProperty, "OverlayText-" + TheOverlay.Id);
      CurrentOrder = TheOverlay.SortBy;
      CurrentUseStandardTime = TheOverlay.UseStandardTime;

      this.Height = TheOverlay.Height;
      this.Width = TheOverlay.Width;
      this.Top = TheOverlay.Top;
      this.Left = TheOverlay.Left;

      if (preview)
      {
        MainActions.SetTheme(this, MainWindow.CurrentTheme);
        this.ResizeMode = ResizeMode.CanResizeWithGrip;
        this.Background = BarelyVisible;
        this.BorderBrush = BorderColor;
        title.Visibility = Visibility.Visible;
        buttonsPanel.Visibility = Visibility.Visible;
      }
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    internal void CreateTimer(string displayName, double endTime, Trigger trigger, bool preview = false)
    {
      if (!string.IsNullOrEmpty(trigger.Name))
      {
        var timerBar = new TimerBar();
        timerBar.Init(TheOverlay.Id, trigger.Name, displayName, endTime, trigger, preview);

        if (!TimerBarCache.TryGetValue(trigger.Name, out List<TimerBar> timerList))
        {
          timerList = new List<TimerBar>();
          TimerBarCache[trigger.Name] = timerList;
        }

        timerList.Add(timerBar);
        TimerBarCreateOrder.Add(timerBar);

        if (CurrentUseStandardTime)
        {
          var currentTime = DateUtil.ToDouble(DateTime.Now);
          var max = TimerBarCreateOrder.Select(timerBar => timerBar.GetRemainingTime(currentTime)).Max();
          TimerBarCreateOrder.ForEach(timerBar => timerBar.SetStandardTime(max));
        }

        if (CurrentOrder == 0)
        {
          content.Children.Add(timerBar);
        }
        else
        {
          InsertTimerBar(timerBar);
        }
      }
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    internal void ResetTimer(string displayName, double endTime, Trigger trigger)
    {
      if (TimerBarCache.TryGetValue(trigger.Name, out List<TimerBar> timerList))
      {
        timerList.ForEach(timerBar =>
        {
          timerBar.Update(endTime);
          content.Children.Remove(timerBar);

          if (CurrentUseStandardTime)
          {
            var currentTime = DateUtil.ToDouble(DateTime.Now);
            var max = TimerBarCreateOrder.Select(timerBar => timerBar.GetRemainingTime(currentTime)).Max();
            TimerBarCreateOrder.ForEach(timerBar => timerBar.SetStandardTime(max));
          }

          InsertTimerBar(timerBar);
        });
      }
      else
      {
        CreateTimer(displayName, endTime, trigger);
      }
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    internal void EndTimer(Trigger trigger)
    {
      if (TimerBarCache.TryGetValue(trigger.Name, out List<TimerBar> timerList))
      {
        if (timerList.Count > 0)
        {
          timerList[0].EndTimer();
        }
      }
   }

    [MethodImpl(MethodImplOptions.Synchronized)]
    internal bool Tick()
    {
      if (CurrentOrder != TheOverlay.SortBy)
      {
        CurrentOrder = TheOverlay.SortBy;
        content.Children.Clear();
        if (CurrentOrder == 0)
        {
          TimerBarCreateOrder.ForEach(timerBar => content.Children.Add(timerBar));
        }
        else
        {
          TimerBarCreateOrder.ForEach(timerBar => InsertTimerBar(timerBar));
        }
      }

      if (CurrentUseStandardTime != TheOverlay.UseStandardTime)
      {
        CurrentUseStandardTime = TheOverlay.UseStandardTime;
        if (CurrentUseStandardTime)
        {
          var currentTime = DateUtil.ToDouble(DateTime.Now);
          var max = TimerBarCreateOrder.Select(timerBar => timerBar.GetRemainingTime(currentTime)).Max();
          TimerBarCreateOrder.ForEach(timerBar => timerBar.SetStandardTime(max));
        }
        else
        {
          TimerBarCreateOrder.ForEach(timerBar => timerBar.SetStandardTime(double.NaN));
        }
      }

      bool remaining = false;
      var removeList = new List<TimerBar>();

      foreach (var child in content.Children)
      {
        if (child is TimerBar bar)
        {
          if (bar.Tick())
          {
            if (TheOverlay.TimerMode == 0 || !bar.CanCooldown())
            {
              removeList.Add(bar);
            }
            else
            {
              if (!bar.IsCooldown())
              {
                bar.SetCooldown(true);
              }

              remaining = true;
            }
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
        TimerBarCreateOrder.Remove(timerBar);
        if (TimerBarCache.TryGetValue(timerBar.GetBarKey(), out List<TimerBar> timerList))
        {
          timerList.Remove(timerBar);

          if (timerList.Count == 0)
          {
            TimerBarCache.Remove(timerBar.GetBarKey());
          }
        }
      });

      return !remaining;
    }

    private void CloseClick(object sender, RoutedEventArgs e) => TriggerOverlayManager.Instance.ClosePreviewTimerOverlay(TheOverlay.Id);

    private void OverlayMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
      this.DragMove();

      if (!saveButton.IsEnabled)
      {
        saveButton.IsEnabled = true;
        closeButton.IsEnabled = false;
      }

      if (!cancelButton.IsEnabled)
      {
        cancelButton.IsEnabled = true;
        closeButton.IsEnabled = false;
      }
    }

    private void WindowLoaded(object sender, RoutedEventArgs e)
    {
      SavedHeight = this.Height;
      SavedWidth = this.Width;
      SavedTop = this.Top;
      SavedLeft = this.Left;
    }

    private void InsertTimerBar(TimerBar timerBar)
    {
      int found = -1;
      double currentTime = DateUtil.ToDouble(DateTime.Now);
      for (int i = 0; i < content.Children.Count; i++)
      {
        if (content.Children[i] is TimerBar current)
        {
          if (timerBar.GetRemainingTime(currentTime) < current.GetRemainingTime(currentTime))
          {
            found = i;
            break;
          }
        }
      }

      if (found != -1)
      {
        content.Children.Insert(found, timerBar);
      }
      else
      {
        content.Children.Add(timerBar);
      }
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
      TheOverlay.Height = SavedHeight = this.Height;
      TheOverlay.Width = SavedWidth = this.Width;
      TheOverlay.Top = SavedTop = this.Top;
      TheOverlay.Left = SavedLeft = this.Left;
      saveButton.IsEnabled = false;
      cancelButton.IsEnabled = false;
      closeButton.IsEnabled = true;
      TriggerOverlayManager.Instance.UpdateOverlays();
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
      this.Height = SavedHeight;
      this.Width = SavedWidth;
      this.Top = SavedTop;
      this.Left = SavedLeft;
      saveButton.IsEnabled = false;
      cancelButton.IsEnabled = false;
      closeButton.IsEnabled = true;
    }

    private void WindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
      if (!double.IsNaN(SavedTop))
      {
        if (!saveButton.IsEnabled)
        {
          saveButton.IsEnabled = true;
          closeButton.IsEnabled = false;
        }

        if (!cancelButton.IsEnabled)
        {
          cancelButton.IsEnabled = true;
          closeButton.IsEnabled = false;
        }
      }
    }

    private void WindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
      TimerBarCache.Clear();
      TimerBarCreateOrder.Clear();
      content.Children.Clear();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
      base.OnSourceInitialized(e);

      if (!Preview)
      {
        var source = (HwndSource)PresentationSource.FromVisual(this);
        // set to layered and topmost by xaml
        int exStyle = (int)NativeMethods.GetWindowLongPtr(source.Handle, (int)NativeMethods.GetWindowLongFields.GWL_EXSTYLE);
        exStyle |= (int)NativeMethods.ExtendedWindowStyles.WS_EX_TOOLWINDOW | (int)NativeMethods.ExtendedWindowStyles.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLong(source.Handle, (int)NativeMethods.GetWindowLongFields.GWL_EXSTYLE, (IntPtr)exStyle);
      }
    }
  }
}
