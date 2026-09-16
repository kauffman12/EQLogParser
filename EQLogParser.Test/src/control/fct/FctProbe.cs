namespace EQLogParser
{
  /*
   * Measurement helpers shared by the geometry tests, computed through the same FctMotion/FctLayout maths the renderer
   * uses — what a test asserts about is what gets drawn. Like FctAmbient, this lives in the test assembly, so sharing
   * costs production code nothing.
   */
  internal static class FctProbe
  {
    /* Where the drawn block actually is at an age: the same maths the canvases draw with, centre-anchored like the text. */
    internal static (double Left, double Top, double Right, double Bottom) BlockOf(FctHitState hit, double ageMs)
    {
      var t = FctMotion.Progress(hit, ageMs);
      var scale = FctMotion.ScaleOf(hit, ageMs);
      var half = (hit.ValueWidth * scale) / 2.0;
      var x = FctMotion.ArcedX(hit, t);

      return (x - half, FctMotion.RaisedY(hit, t), x + half, FctMotion.RaisedY(hit, t) + (FctLayout.TextHeight(hit) * scale));
    }
  }
}
