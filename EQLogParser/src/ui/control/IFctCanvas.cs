using System;

namespace EQLogParser
{
  /*
   * Contract for an FCT render backend: the host targets this so the drawing substrate stays swappable
   * (SkiaSharp today, WPF vector as the A/B reference). Feed and lifecycle only — the tuning counters live
   * in IFctDiagnostics so the production path does not carry a profiler interface.
   */
  internal interface IFctCanvas
  {
    /* Canvas clock in ms since Start(); fired every render tick before the paint decision, which is where
     * the host drains its feed. */
    event Action<double> EventsFrame;

    int ActiveCount { get; }

    /* How new hits move (hold / fountain / pulse / spray); see FctMotionStyle for what each one is for. Applies to
     * hits spawned from now on, which is what makes switching it during a fight a practical way to choose. */
    FctMotionStyle MotionStyle { get; set; }

    /* Region scheme for new hits: bands (my hits rise above the protected middle strip, hits on me sink below it)
     * or the original left/right halves. Hosts set it from settings.ini so it can be flipped mid-fight. */
    FctLayoutMode Layout { get; set; }

    void Start();
    void Stop();

    /* valueText is the literal main line for zero-damage labels ("Dodge"); null means show Value. */
    void AddHit(FctLane lane, double value, string source, bool crit, bool minor = false, bool periodic = false, string valueText = null);
  }

  /* Per-second counters for tuning and dogfooding. Overload has to be visible to be fixed. */
  internal interface IFctDiagnostics
  {
    double Fps { get; }
    double AvgFrameMs { get; }

    /* Worst frame in the current stats window. Smoothness lives in this number and not in the average: one 40 ms frame
     * inside a second of 6 ms frames is a visible hitch that no average will ever show. */
    double MaxFrameMs { get; }

    /* Measured display refresh, so painted fps can be read against it: 72 fps under a 144 Hz refresh is pacing, and a
     * 60 fps reading under a 60 Hz refresh with a high MaxFrameMs is overload. */
    double DisplayHz { get; }

    double LastFrameMs { get; }
    double DrawsPerSec { get; }

    /* Hits lost to the lane caps (the feed's own drops are counted on FctManager). */
    int DroppedCount { get; }
  }
}
