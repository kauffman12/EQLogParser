using System;

namespace EQLogParser
{
  /*
   * Contract for FCT render backends. Implemented by the WPF vector renderer (FctSimCanvas) and
   * the SkiaSharp renderer (FctSkiaCanvas); production code (the future FctManager) targets this
   * interface so the rendering substrate stays swappable.
   */
  internal interface IFctSimCanvas
  {
    /* Canvas clock in ms since Start(); fired once per rendered frame before new hits are fed. */
    event Action<double> EventsFrame;

    int ActiveCount { get; }
    double Fps { get; }
    double AvgFrameMs { get; }
    double LastFrameMs { get; }
    double DrawsPerSec { get; }

    void Start();
    void Stop();

    void AddHit(FctSimLane lane, double value, string action, bool crit, bool minor = false);
    bool TryAccumulate(FctSimLane lane, double amount);
  }
}
