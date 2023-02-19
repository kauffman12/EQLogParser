using Syncfusion.Compression.Zip;
using Syncfusion.Data.Extensions;
using Syncfusion.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for TimerOverlayWindow.xaml
  /// </summary>
  public partial class TimerOverlayWindow : Window
  {
    private Overlay TheOverlay;
    private bool Preview = false;
    private long SavedHeight;
    private long SavedWidth;
    private long SavedTop = long.MaxValue;
    private long SavedLeft = long.MaxValue;
    private Dictionary<string, TimerData> CooldownTimerData = new Dictionary<string, TimerData>();

    internal TimerOverlayWindow(Overlay overlay, bool preview = false)
    {
      InitializeComponent();
      Preview = preview;
      TheOverlay = overlay;
      title.SetResourceReference(TextBlock.TextProperty, "OverlayText-" + TheOverlay.Id);

      Height = TheOverlay.Height;
      Width = TheOverlay.Width;
      Top = TheOverlay.Top;
      Left = TheOverlay.Left;

      if (preview)
      {
        MainActions.SetTheme(this, MainWindow.CurrentTheme);
        ResizeMode = ResizeMode.CanResizeWithGrip;
        SetResourceReference(BorderBrushProperty, "PreviewBackgroundBrush");
        SetResourceReference(BackgroundProperty, "OverlayBrushColor-" + TheOverlay.Id);
        title.Visibility = Visibility.Visible;
        buttonsPanel.Visibility = Visibility.Visible;
      }
      else
      {
        border.SetResourceReference(Border.BackgroundProperty, "OverlayBrushColor-" + TheOverlay.Id);
      }
    }

    private void CloseClick(object sender, RoutedEventArgs e) => TriggerOverlayManager.Instance.ClosePreviewTimerOverlay(TheOverlay.Id);

    internal void CreatePreviewTimer(string displayName, string timeText, double progress)
    {
      var timerBar = new TimerBar();
      timerBar.Init(TheOverlay.Id);
      timerBar.Update(displayName, timeText, progress);
      content.Children.Add(timerBar);
    }

    internal bool Tick(List<TimerData> timerList)
    {
      var currentTicks = DateTime.Now.Ticks;
      double maxDurationTicks = double.NaN;
      IEnumerable<TimerData> orderedList = null;

      if (timerList.Count > 0)
      {
        if (TheOverlay.SortBy == 0)
        {
          // create order
          orderedList = timerList.Where(timerData => timerData.SelectedOverlays.Contains(TheOverlay.Id));
        }
        else
        {
          // remaining order
          orderedList = timerList.Where(timerData => timerData.SelectedOverlays.Contains(TheOverlay.Id))
            .OrderBy(timerData => (timerData.EndTicks - currentTicks));
        }

        if (TheOverlay.UseStandardTime)
        {
          if (orderedList.Any())
          {
            maxDurationTicks = orderedList.Select(timerData => timerData.DurationTicks).Max();
          }
        }
      }

      var count = 0;
      var childCount = content.Children.Count;
      var handledKeys = new Dictionary<string, bool>();

      if (orderedList != null)
      {
        foreach (var timerData in orderedList)
        {
          var remainingTicks = timerData.EndTicks - currentTicks;
          remainingTicks = Math.Max(remainingTicks, 0);

          if (TheOverlay.TimerMode == 1 && timerData.TriggerAgainOption == 1 && timerData.ResetTicks > 0)
          {
            handledKeys[timerData.Key] = true;
            CooldownTimerData[timerData.Key] = timerData;
          }

          TimerBar timerBar;
          if (count < childCount)
          {
            timerBar = content.Children[count] as TimerBar;
            if (timerBar.Visibility != Visibility.Visible)
            {
              timerBar.Visibility = Visibility;
            }
          }
          else
          {
            timerBar = new TimerBar();
            timerBar.Init(TheOverlay.Id);
            content.Children.Add(timerBar);
            childCount++;
          }

          UpdateTimerBar(remainingTicks, timerBar, timerData, maxDurationTicks);
          count++;
        }
      }

      if (TheOverlay.TimerMode == 1)
      {
        var idleList = new List<dynamic>();
        var resetList = new List<dynamic>();

        foreach (var timerData in CooldownTimerData.Values)
        {
          if (!handledKeys.ContainsKey(timerData.Key))
          {
            var remainingTicks = timerData.ResetTicks - currentTicks;
            var data = new { RemainingTicks = remainingTicks, TimerData = timerData };
            if (remainingTicks > 0)
            {
              resetList.Add(data);
            }
            else
            {
              idleList.Add(data);
            }
          }
        }

        foreach (var data in idleList.OrderBy(data => data.TimerData.DurationTicks))
        {
          TimerBar timerBar;
          if (count >= childCount)
          {
            timerBar = new TimerBar();
            timerBar.Init(TheOverlay.Id);
            content.Children.Add(timerBar);
            childCount++;
          }
          else
          {
            timerBar = content.Children[count] as TimerBar;
          }

          UpdateCooldownTimerBar(data.RemainingTicks, timerBar, data.TimerData);
          count++;
        }

        foreach (var data in resetList.OrderBy(data => data.RemainingTicks))
        {
          TimerBar timerBar;
          if (count >= childCount)
          {
            timerBar = new TimerBar();
            timerBar.Init(TheOverlay.Id);
            content.Children.Add(timerBar);
            childCount++;
          }
          else
          {
            timerBar = content.Children[count] as TimerBar;
          }

          UpdateCooldownTimerBar(data.RemainingTicks, timerBar, data.TimerData);
          count++;
        }
      }

      var complete = (TheOverlay.TimerMode == 0) ? (count == 0) : false;
      while (count < childCount)
      {
        if (content.Children[count].Visibility == Visibility.Collapsed)
        {
          break;
        }

        content.Children[count].Visibility = Visibility.Collapsed;
        count++;
      }

      return complete;
    }

    private void UpdateTimerBar(double remainingTicks, TimerBar timerBar, TimerData timerData, double maxDurationTicks)
    {
      var endTicks = double.IsNaN(maxDurationTicks) ? timerData.DurationTicks : maxDurationTicks;
      var progress = remainingTicks / endTicks * 100.0;
      var timeText = DateUtil.FormatSimpleMS(remainingTicks / TimeSpan.TicksPerSecond);
      timerBar.SetActive();
      timerBar.Update(timerData.DisplayName, timeText, progress);
    }

    private void UpdateCooldownTimerBar(double remainingTicks, TimerBar timerBar, TimerData timerData)
    {
      if (remainingTicks > 0)
      {
        var progress = 100.0 - (remainingTicks / timerData.ResetDurationTicks * 100.0);
        var timeText = DateUtil.FormatSimpleMS(remainingTicks / TimeSpan.TicksPerSecond);
        timerBar.SetReset();
        timerBar.Update(timerData.DisplayName, timeText, progress);
      }
      else
      {
        var timeText = DateUtil.FormatSimpleMS(timerData.DurationTicks / TimeSpan.TicksPerSecond);
        timerBar.SetIdle();
        timerBar.Update(timerData.DisplayName, timeText, 100.0);
      }
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
      TheOverlay.Height = SavedHeight = (long)Height;
      TheOverlay.Width = SavedWidth = (long)Width;
      TheOverlay.Top = SavedTop = (long)Top;
      TheOverlay.Left = SavedLeft = (long)Left;
      saveButton.IsEnabled = false;
      cancelButton.IsEnabled = false;
      closeButton.IsEnabled = true;
      TriggerOverlayManager.Instance.UpdateOverlays();
      TriggerOverlayManager.Instance.Select(TheOverlay);
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
      Height = SavedHeight;
      Width = SavedWidth;
      Top = SavedTop;
      Left = SavedLeft;
      saveButton.IsEnabled = false;
      cancelButton.IsEnabled = false;
      closeButton.IsEnabled = true;
    }

    private void OverlayMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
      DragMove();

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
      SavedHeight = (long)Height;
      SavedWidth = (long)Width;
      SavedTop = (long)Top;
      SavedLeft = (long)Left;
    }

    private void WindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
      if (SavedTop != long.MaxValue)
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

    private void WindowClosing(object sender, System.ComponentModel.CancelEventArgs e) => content.Children.Clear();

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
