namespace EQLogParser.Wpf.Test
{
  /*
   * The dirty door of the "Sliding Reset" checkbox (TriggersView.OnEditorSiblingChanged → SlidingResetUnsaved).
   *
   * That checkbox writes a SIBLING property -- Trigger.RepeatedResetSlides beside the RepeatedResetTime row the grid tracks -- so the grid's own
   * ValueChanged never fires for it and an earlier version reported every checkbox event as unsaved work. The bug that shipped was subtler than
   * "Save never enables": attaching a row to a newly selected trigger pushes the STORED value into a box that had no value yet, which arrives as
   * a Checked event with no click behind it, so Save lit up for every trigger merely clicked. The pane's other doors already compare against
   * Node.TriggerData (the brush properties in ValueChanged); this is the same law, reached by the same route.
   *
   * Only the question is testable here -- constructing TriggersView means building the whole pane -- which is exactly why the comparison is a
   * static on the pane rather than an inline condition nobody can call.
   */
  [TestClass]
  public class SlidingResetDirtyStateTest
  {
    /** The pane holding a trigger whose stored copy says one thing and whose editable model says another (or the same). */
    private static TriggerPropertyModel PaneHolding(Trigger savedData, bool modelValue) => new()
    {
      RepeatedResetSlides = modelValue,
      Node = new TriggerNode { Id = "sliding-reset-test", TriggerData = savedData }
    };

    private static Trigger Saved(bool slidingReset) => new() { Pattern = "a pattern", RepeatedResetSlides = slidingReset };

    [TestMethod]
    public void AStoredValuePushedIntoTheBoxIsNotUnsavedWork()
    {
      // The shape of a click on a trigger: the model was filled from storage, so model and store agree, and the checkbox is now being told.
      var pane = PaneHolding(Saved(true), true);

      Assert.IsFalse(TriggersView.SlidingResetUnsaved(pane),
        "a control being handed its own stored value is not somebody editing it, and Save staying greyed is the whole point");
    }

    [TestMethod]
    public void AToggleAgainstTheSavedTriggerIsUnsavedWork()
    {
      // Both directions matter: a box ticked over an off trigger and one cleared off a trigger that had it.
      Assert.IsTrue(TriggersView.SlidingResetUnsaved(PaneHolding(Saved(false), true)),
        "the choice has to reach the file, so this is the one event that must wake Save up");
      Assert.IsTrue(TriggersView.SlidingResetUnsaved(PaneHolding(Saved(true), false)),
        "and clearing a saved choice is just as unsaved as setting one");
    }

    [TestMethod]
    public void ATriggerThatNeverAskedForSlidingResetSaysNothing()
    {
      // The ordinary case for most triggers in a real file: off in storage, off in the pane.
      Assert.IsFalse(TriggersView.SlidingResetUnsaved(PaneHolding(Saved(false), false)),
        "an untouched trigger is not work waiting to be saved");
    }
  }
}
