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

    /* Fountain style (rise-fall-shrink) vs the default hold style; applies to new hits only. */
    bool FountainMotion { get; set; }

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
    double LastFrameMs { get; }
    double DrawsPerSec { get; }

    /* Hits lost to the lane caps (the feed's own drops are counted on FctManager). */
    int DroppedCount { get; }
  }
}
