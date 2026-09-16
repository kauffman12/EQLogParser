using Microsoft.VisualStudio.TestTools.UnitTesting;

/* These tests exercise an engine whose dials are process-wide (FctScale, FctLayout.LabelSide: see FctAmbient). Interleaving classes
   would let one class's temporary dial be read as another's ambient geometry, which is order-dependent rather than wrong-on-Windows —
   the two runners disagree, and neither answer is worth having. Sequential, explicitly, on every machine. */
[assembly: DoNotParallelize]
