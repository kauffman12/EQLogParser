using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

namespace EQLogParser
{
  /*
   * Production FCT host: a topmost, non-activating overlay showing the live record feed from
   * FctManager (monitor lines only - historical replay never reaches this window). Toggled from
   * the Tools menu for testing; Esc closes it. Records arrive on the log reader thread and are
   * marshaled onto the dispatcher per the FctManager contract, one AddHit per record for now;
   * aggregation/grouping is a later step.
   */
  public partial class FctOverlayWindow : Window
  {
    private readonly FctSkiaCanvas _canvas;
    private double _lastStatsMs = -1000;

    // lets the Tools menu item uncheck itself when the window is closed with Esc
    public event Action EventsClosed;

    public FctOverlayWindow()
    {
      InitializeComponent();
      _canvas = fctCanvas;
      _canvas.EventsFrame += OnCanvasFrame;
      FctManager.Instance.EventsHitsProcessed += OnHitsProcessed;
      Loaded += (_, _) => _canvas.Start();
      Closed += OnClosed;
    }

    private void OnClosed(object sender, EventArgs e)
    {
      _canvas.Stop();
      FctManager.Instance.EventsHitsProcessed -= OnHitsProcessed;
      EventsClosed?.Invoke();
    }

    private void OnHitsProcessed(IReadOnlyList<FctHitCommand> batch)
    {
      // reader thread -> UI thread (see the FctManager contract)
      Application.Current?.Dispatcher.BeginInvoke(() =>
      {
        foreach (var cmd in batch)
        {
          _canvas.AddHit(Map(cmd.Lane), cmd.Value, cmd.Source, crit: cmd.Crit, minor: false);
        }
      });
    }

    /* Core's FctLane mirrors the UI's FctSimLane; an explicit map so a divergence is a compile
     * error here rather than a silent cast. */
    private static FctSimLane Map(FctLane lane) => lane switch
    {
      FctLane.DamageDealt => FctSimLane.DamageDealt,
      FctLane.DamageTaken => FctSimLane.DamageTaken,
      FctLane.HealingDealt => FctSimLane.HealingDealt,
      FctLane.HealingReceived => FctSimLane.HealingReceived,
      _ => FctSimLane.Crit,
    };

    /* 2 Hz header stats, the same observability used while tuning the simulation. */
    private void OnCanvasFrame(double now)
    {
      if (now - _lastStatsMs < 500)
      {
        return;
      }

      _lastStatsMs = now;
      statsText.Text = $"{_canvas.Fps:0} fps · {_canvas.ActiveCount} active";
    }

    /* Toggles between the hold style (rise and stay) and the fountain style (rise, fall, shrink). */
    private void FountainChanged(object sender, RoutedEventArgs e) => _canvas.FountainMotion = fountainCheck.IsChecked == true;

    private void HeaderDrag(object sender, MouseButtonEventArgs e) => DragMove();

    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
      if (e.Key == Key.Escape)
      {
        Close();
      }
    }
  }
}
