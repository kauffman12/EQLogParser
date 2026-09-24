using System;
using System.Windows;

namespace EQLogParser
{
  /*
   * The gesture rule behind the sound-path cell in the trigger property grid (TextSoundEditor): one mouse button
   * has to both open the file chooser and select the path for copying, so what separates them is how far the
   * pointer travelled during the press. Pure geometry by design — no control, no dispatcher, no STA thread — so every
   * case below is a pair of points and a click count. It lives in this assembly because `ClickNotDrag` does and needs
   * `System.Windows.Point`, which makes these tests need Windows like every other class here.
   *
   * The cases are numbered after the mistakes they hold shut:
   *   1. a click in place opens the chooser, which is the whole reason the cell is clickable;
   *   2. a drag across the path selects it and does not open the chooser;
   *   3. an excursion latches, so coming back over the press point does not buy the click back;
   *   4. the tail of a double-click (the word-select gesture) never arms a second chooser;
   *   5. a release nobody pressed is not a click — focus arriving in the cell cannot summon a dialog;
   *   6. jitter inside the tolerance stays a click, movement outside it in either axis does not;
   *   7. a junk tolerance falls back instead of turning every tremor into a selection.
   */
  [TestClass]
  public sealed class ClickNotDragTest
  {
    private const double Tol = ClickNotDrag.DefaultTolerance;

    // 1
    [TestMethod]
    public void AClickInPlaceIsAChoice()
    {
      var gesture = new ClickNotDrag();

      gesture.OnMouseDown(new Point(40, 8), 1);

      Assert.IsTrue(gesture.OnMouseUp(new Point(40, 8)), "a click on the path has to bring up the chooser");
    }

    // 2: the other half of the gesture — dragging across a long path must select it, not open a dialog
    [TestMethod]
    public void ASelectionDragIsNotAChoice()
    {
      var gesture = new ClickNotDrag();

      gesture.OnMouseDown(new Point(10, 8), 1);
      gesture.OnMouseMove(new Point(60, 9));

      Assert.IsFalse(gesture.OnMouseUp(new Point(120, 9)), "selecting a path is not a request to change it");
    }

    // 3: comparing press against release alone would call this a click; the excursion has to be remembered
    [TestMethod]
    public void ADragThatComesBackIsStillADrag()
    {
      var gesture = new ClickNotDrag();

      gesture.OnMouseDown(new Point(10, 8), 1);
      gesture.OnMouseMove(new Point(140, 8));
      gesture.OnMouseMove(new Point(11, 8));

      Assert.IsFalse(gesture.OnMouseUp(new Point(10, 8)),
        "a drag that happens to end where it started was still a drag, and must not open the chooser");

      // ...and the latch belongs to that press only: a cell that remembered it forever stops answering clicks after
      // the first selection drag, which is the same bug the report started with.
      gesture.OnMouseDown(new Point(10, 8), 1);

      Assert.IsTrue(gesture.OnMouseUp(new Point(10, 8)), "the latch has to clear on the next press");
    }

    // 4
    [TestMethod]
    public void TheSecondClickOfADoubleClickDoesNotArm()
    {
      var gesture = new ClickNotDrag();

      gesture.OnMouseDown(new Point(40, 8), 2);

      Assert.IsFalse(gesture.OnMouseUp(new Point(40, 8)), "the word the double-click selected is not worth a second chooser");
    }

    // 5
    [TestMethod]
    public void AReleaseWithoutAPressIsNotAChoice()
    {
      var gesture = new ClickNotDrag();

      Assert.IsFalse(gesture.OnMouseUp(new Point(40, 8)));

      // ...and one that was already answered does not fire twice for a second release over the same cell
      gesture.OnMouseDown(new Point(40, 8), 1);
      Assert.IsTrue(gesture.OnMouseUp(new Point(40, 8)));
      Assert.IsFalse(gesture.OnMouseUp(new Point(40, 8)), "one press buys one chooser");
    }

    // 6: the tolerance is a square, and both axes count — a wrapped path is selected by dragging down as much as across
    [TestMethod]
    public void JitterStaysAClickAndMovementInEitherAxisDoesNot()
    {
      var jitter = new ClickNotDrag();
      jitter.OnMouseDown(new Point(40, 8), 1);
      Assert.IsTrue(jitter.OnMouseUp(new Point(40 + Tol / 2.0, 8)), "a pointer that shakes a little is still a click");

      var sideways = new ClickNotDrag();
      sideways.OnMouseDown(new Point(40, 8), 1);
      Assert.IsFalse(sideways.OnMouseUp(new Point(40 + Tol * 3.0, 8)));

      var downwards = new ClickNotDrag();
      downwards.OnMouseDown(new Point(40, 8), 1);
      Assert.IsFalse(downwards.OnMouseUp(new Point(40, 8 + Tol * 3.0)));
    }

    // A move past the tolerance latches even when the release lands back inside it (the case the mouse handlers
    // deliver as one long drag with no intermediate reading).
    [TestMethod]
    public void OnlyAMovePastTheToleranceLatches()
    {
      var gesture = new ClickNotDrag();

      gesture.OnMouseDown(new Point(40, 8), 1);
      gesture.OnMouseMove(new Point(40 + Tol / 2.0, 8));

      Assert.IsTrue(gesture.OnMouseUp(new Point(40, 8)), "wandering inside the tolerance is still the same click");
    }

    // 7: a tolerance that leaked through as zero (or NaN) would call a shaking pointer a selection, so it has to
    // fall back — measured here by the jitter case passing under junk exactly as it does under the default.
    [TestMethod]
    public void AJunkToleranceFallsBackToTheDefault()
    {
      foreach (var junk in new[] { 0.0, -1.0, double.MinValue, double.NaN })
      {
        var jitter = new ClickNotDrag(junk);
        jitter.OnMouseDown(new Point(40, 8), 1);
        Assert.IsTrue(jitter.OnMouseUp(new Point(40 + Tol / 2.0, 8)), $"a tolerance of {junk} was not replaced by the default");

        var dragged = new ClickNotDrag(junk);
        dragged.OnMouseDown(new Point(40, 8), 1);
        Assert.IsFalse(dragged.OnMouseUp(new Point(400, 8)), $"a tolerance of {junk} must not swallow a drag either");
      }
    }
  }
}
