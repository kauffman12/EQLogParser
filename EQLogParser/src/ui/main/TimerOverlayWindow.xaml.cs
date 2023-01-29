using Syncfusion.Data.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
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
    private static SolidColorBrush BarelyVisible = new SolidColorBrush { Color = (Color) ColorConverter.ConvertFromString("#01000000") };
    private static SolidColorBrush BorderColor = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#AA000000") };
    private Dictionary<string, TimerBar> TimerBarCache = new Dictionary<string, TimerBar>();
    private List<TimerBar> TimerBarCreateOrder = new List<TimerBar>();
    private Overlay Overlay;
    private bool Preview = false;
    private double SavedHeight;
    private double SavedWidth;
    private double SavedTop = double.NaN;
    private double SavedLeft = double.NaN;
    private int CurrentOrder;
    private bool CurrentUseStandardTime;

    public TimerOverlayWindow(string overlayId, bool preview = false)
    {
      InitializeComponent();
      Preview = preview;
      this.border.SetResourceReference(Border.BackgroundProperty, "OverlayBrushColor-" + overlayId);
      title.SetResourceReference(TextBlock.TextProperty, "OverlayText-" + overlayId);
      Overlay = TriggerOverlayManager.Instance.GetTimerOverlayById(overlayId, out _);
      CurrentOrder = Overlay.SortBy;
      CurrentUseStandardTime = Overlay.UseStandardTime;

      this.Height = Overlay.Height;
      this.Width = Overlay.Width;
      this.Top = Overlay.Top;
      this.Left = Overlay.Left;

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

    internal void CreateTimer(string name, double endTime, bool preview = false)
    {
      name = string.IsNullOrEmpty(name) ? "Unknown Timer" : name;
      var timerBar = new TimerBar();
      timerBar.Init(Overlay.Id, name, endTime, preview);
      TimerBarCache[name] = timerBar;
      TimerBarCreateOrder.Add(timerBar);

      if (CurrentUseStandardTime)
      {
        double currentTime = DateUtil.ToDouble(DateTime.Now);
        double max = TimerBarCreateOrder.Select(timerBar => timerBar.GetRemainingTime(currentTime)).Max();
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

    internal void ResetTimer(string name, double endTime)
    {
      name = string.IsNullOrEmpty(name) ? "Unknown Timer" : name;
      if (TimerBarCache.TryGetValue(name, out TimerBar timerBar))
      {
        timerBar.Update(endTime);
        content.Children.Remove(timerBar);

        if (CurrentUseStandardTime)
        {
          double currentTime = DateUtil.ToDouble(DateTime.Now);
          double max = TimerBarCreateOrder.Select(timerBar => timerBar.GetRemainingTime(currentTime)).Max();
          TimerBarCreateOrder.ForEach(timerBar => timerBar.SetStandardTime(max));
        }

        InsertTimerBar(timerBar);
      }
      else
      {
        CreateTimer(name, endTime);
      }
    }

    internal bool Tick()
    {
      if (CurrentOrder != Overlay.SortBy)
      {
        CurrentOrder = Overlay.SortBy;
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

      if (CurrentUseStandardTime != Overlay.UseStandardTime)
      {
        CurrentUseStandardTime = Overlay.UseStandardTime;
        if (CurrentUseStandardTime)
        {
          double currentTime = DateUtil.ToDouble(DateTime.Now);
          double max = TimerBarCreateOrder.Select(timerBar => timerBar.GetRemainingTime(currentTime)).Max();
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
        TimerBarCreateOrder.Remove(timerBar);
      });

      return !remaining;
    }

    private void CloseClick(object sender, RoutedEventArgs e) => TriggerOverlayManager.Instance.ClosePreviewTimerOverlay(Overlay.Id);

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
      Overlay.Height = SavedHeight = this.Height;
      Overlay.Width = SavedWidth = this.Width;
      Overlay.Top = SavedTop = this.Top;
      Overlay.Left = SavedLeft = this.Left;
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
