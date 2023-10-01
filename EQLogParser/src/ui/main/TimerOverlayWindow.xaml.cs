using DotLiquid.Util;
using Syncfusion.Data.Extensions;
using System;
using System.Collections.Generic;
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
    private TriggerNode Node;
    private bool Preview = false;
    private long SavedHeight;
    private long SavedWidth;
    private long SavedTop = long.MaxValue;
    private long SavedLeft = long.MaxValue;
    private Dictionary<string, TimerData> CooldownTimerData = new Dictionary<string, TimerData>();
    private Dictionary<string, ShortDurationData> ShortDurationBars = new Dictionary<string, ShortDurationData>();
    private Dictionary<string, Window> PreviewWindows = null;

    internal TimerOverlayWindow(TriggerNode node, Dictionary<string, Window> previews = null)
    {
      InitializeComponent();
      Node = node;
      Preview = previews != null;
      PreviewWindows = previews;
      title.SetResourceReference(TextBlock.TextProperty, "OverlayText-" + Node.Id);

      Height = Node.OverlayData.Height;
      Width = Node.OverlayData.Width;
      Top = Node.OverlayData.Top;
      Left = Node.OverlayData.Left;

      if (Preview)
      {
        MainActions.SetTheme(this, MainWindow.CurrentTheme);
        ResizeMode = ResizeMode.CanResizeWithGrip;
        SetResourceReference(BorderBrushProperty, "PreviewBackgroundBrush");
        SetResourceReference(BackgroundProperty, "OverlayBrushColor-" + Node.Id);
        title.Visibility = Visibility.Visible;
        buttonsPanel.Visibility = Visibility.Visible;
        CreatePreviewTimer("Example Trigger Name", "03:00", 90.0);
        CreatePreviewTimer("Example Trigger Name #2", "02:00", 60.0);
        CreatePreviewTimer("Example Trigger Name #3", "01:00", 30.0);
        TriggerStateManager.Instance.TriggerUpdateEvent += TriggerUpdateEvent;
      }
      else
      {
        border.SetResourceReference(Border.BackgroundProperty, "OverlayBrushColor-" + Node.Id);
      }
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    internal void CreatePreviewTimer(string displayName, string timeText, double progress)
    {
      var timerBar = new TimerBar();
      timerBar.Init(Node.Id);
      timerBar.Update(displayName, timeText, progress, new TimerData());
      content.Children.Add(timerBar);
    }

    internal void ShortTick(List<TimerData> timerList)
    {
      if (timerList.Count > 0)
      {
        var currentTicks = DateTime.Now.Ticks;
        foreach (var timerData in timerList.Where(timerData => timerData.TimerType == 2 && ShouldProcess(timerData)))
        {
          if (ShortDurationBars.TryGetValue(timerData.Key, out var value))
          {
            var remainingTicks = timerData.EndTicks - currentTicks;
            UpdateTimerBar(remainingTicks, value.TheTimerBar, timerData, value.MaxDuration, true);
          }
          else
          {
            Tick(timerList);
            return;
          }
        }
      }
    }

    internal bool Tick(List<TimerData> timerList)
    {
      var currentTicks = DateTime.Now.Ticks;
      var maxDurationTicks = double.NaN;
      IEnumerable<TimerData> orderedList = null;

      if (Node.OverlayData.Width != Width)
      {
        Width = Node.OverlayData.Width;
      }
      else if (Node.OverlayData.Height != Height)
      {
        Height = Node.OverlayData.Height;
      }
      else if (Node.OverlayData.Top != Top)
      {
        Top = Node.OverlayData.Top;
      }
      else if (Node.OverlayData.Left != Left)
      {
        Left = Node.OverlayData.Left;
      }

      if (timerList.Count > 0)
      {
        if (Node.OverlayData.SortBy == 0)
        {
          // create order
          orderedList = timerList.Where(timerData => ShouldProcess(timerData));
        }
        else
        {
          // remaining order
          orderedList = timerList.Where(timerData => ShouldProcess(timerData))
            .OrderBy(timerData => timerData.EndTicks - currentTicks);
        }

        if (Node.OverlayData.UseStandardTime)
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

          if (Node.OverlayData.TimerMode == 1 && timerData.ResetTicks > 0)
          {
            handledKeys[timerData.Key] = true;
            CooldownTimerData[timerData.Key] = timerData;
          }

          TimerBar timerBar;
          if (count < childCount)
          {
            timerBar = content.Children[count] as TimerBar;
          }
          else
          {
            timerBar = new TimerBar();
            timerBar.Init(Node.Id);
            content.Children.Add(timerBar);
            childCount++;
          }

          UpdateTimerBar(remainingTicks, timerBar, timerData, maxDurationTicks);
          count++;
        }
      }

      var oldestIdleTicks = double.NaN;
      var idleList = new List<dynamic>();
      var resetList = new List<dynamic>();
      if (Node.OverlayData.TimerMode == 1)
      {
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
              if (double.IsNaN(oldestIdleTicks))
              {
                oldestIdleTicks = Math.Abs(data.RemainingTicks);
              }
              else
              {
                oldestIdleTicks = Math.Min(oldestIdleTicks, Math.Abs(data.RemainingTicks));
              }

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
            timerBar.Init(Node.Id);
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
            timerBar.Init(Node.Id);
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

      var complete = false;
      if (Node.OverlayData.TimerMode == 0)
      {
        complete = count == 0;
      }
      else if (Node.OverlayData.IdleTimeoutSeconds > 0)
      {
        complete = (count == idleList.Count) && (oldestIdleTicks > (Node.OverlayData.IdleTimeoutSeconds * TimeSpan.TicksPerSecond));
      }

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

    private void UpdateTimerBar(double remainingTicks, TimerBar timerBar, TimerData timerData, double maxDurationTicks, bool shortTick = false)
    {
      var endTicks = double.IsNaN(maxDurationTicks) ? timerData.DurationTicks : maxDurationTicks;
      var progress = remainingTicks / endTicks * 100.0;
      var timeText = timerData.TimerType == 2 ? DateUtil.FormatSimpleMillis((long)remainingTicks) : DateUtil.FormatSimpleMS((long)remainingTicks);
      timerBar.SetActive();
      timerBar.Update(GetDisplayName(timerData), timeText, progress, timerData);

      if (!shortTick)
      {
        if (timerData.TimerType == 2)
        {
          if (!ShortDurationBars.TryGetValue(timerData.Key, out var value))
          {
            value = new ShortDurationData();
            ShortDurationBars[timerData.Key] = value;
          }

          value.TheTimerBar = timerBar;
          value.MaxDuration = maxDurationTicks;
        }
      }

      if (remainingTicks < (TimeSpan.TicksPerMillisecond * 60))
      {
        timerBar.Visibility = Visibility.Collapsed;

        if (timerData.TimerType == 2)
        {
          ShortDurationBars.Remove(timerData.Key);
        }
      }
      else if (timerBar.Visibility != Visibility.Visible)
      {
        timerBar.Visibility = Visibility;
      }
    }

    private void UpdateCooldownTimerBar(double remainingTicks, TimerBar timerBar, TimerData timerData)
    {
      if (remainingTicks > 0)
      {
        var progress = 100.0 - (remainingTicks / timerData.ResetDurationTicks * 100.0);
        var timeText = DateUtil.FormatSimpleMS((long)remainingTicks);
        timerBar.SetReset();
        timerBar.Update(GetDisplayName(timerData), timeText, progress, timerData);
      }
      else
      {
        var timeText = DateUtil.FormatSimpleMS(timerData.DurationTicks);
        timerBar.SetIdle();
        timerBar.Update(GetDisplayName(timerData), timeText, 100.0, timerData);
      }

      if (timerBar.Visibility != Visibility.Visible)
      {
        timerBar.Visibility = Visibility;
      }
    }

    private string GetDisplayName(TimerData timerData)
    {
      if (timerData.Repeated > -1)
      {
        return timerData.DisplayName.Replace("{repeated}", timerData.Repeated.ToString(), StringComparison.OrdinalIgnoreCase);
      }
      else
      {
        return timerData.DisplayName;
      }
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
      Node.OverlayData.Height = SavedHeight = (long)Height;
      Node.OverlayData.Width = SavedWidth = (long)Width;
      Node.OverlayData.Top = SavedTop = (long)Top;
      Node.OverlayData.Left = SavedLeft = (long)Left;
      saveButton.IsEnabled = false;
      cancelButton.IsEnabled = false;
      closeButton.IsEnabled = true;
      TriggerStateManager.Instance.Update(Node);
      TriggerManager.Instance.CloseOverlay(Node.Id);
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

    private void TriggerUpdateEvent(TriggerNode node)
    {
      if (Node.Id == node.Id)
      {
        if (Node != node)
        {
          Node = node;
        }

        Height = Node.OverlayData.Height;
        Width = Node.OverlayData.Width;
        Top = Node.OverlayData.Top;
        Left = Node.OverlayData.Left;
        saveButton.IsEnabled = false;
        cancelButton.IsEnabled = false;
        closeButton.IsEnabled = true;
      }
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

    private bool ShouldProcess(TimerData timerData)
    {
      if (timerData.SelectedOverlays.Count == 0 && Node?.OverlayData.IsDefault == true)
      {
        return true;
      }

      return timerData.SelectedOverlays.Contains(Node.Id);
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

    private void WindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
      TriggerStateManager.Instance.TriggerUpdateEvent -= TriggerUpdateEvent;
      content.Children.Clear();
      CooldownTimerData.Clear();
      ShortDurationBars.Clear();
      PreviewWindows?.Remove(Node.Id);
      PreviewWindows = null;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
      base.OnSourceInitialized(e);

      if (!Preview)
      {
        var source = (HwndSource)PresentationSource.FromVisual(this);
        // set to layered and topmost by xaml
        var exStyle = (int)NativeMethods.GetWindowLongPtr(source.Handle, (int)NativeMethods.GetWindowLongFields.GWL_EXSTYLE);
        exStyle |= (int)NativeMethods.ExtendedWindowStyles.WS_EX_TOOLWINDOW | (int)NativeMethods.ExtendedWindowStyles.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLong(source.Handle, (int)NativeMethods.GetWindowLongFields.GWL_EXSTYLE, (IntPtr)exStyle);
      }
    }

    private class ShortDurationData
    {
      public TimerBar TheTimerBar { get; set; }
      public double MaxDuration { get; set; }
    }
  }
}
