using Microsoft.VisualStudio.TestTools.UnitTesting;

/* Same reason as EQLogParser.Test/src/control/fct/AssemblySettings.cs: the floating-text engine keeps its dials in process-wide statics
   (FctScale.Text/Crit/Time, FctLayout.LabelSide), and the canvas owns one of them: FctSkiaCanvas writes FctLayout.LabelSide when its
   setting changes, exactly as it does in the running overlay. A test that builds a canvas can therefore move geometry another class is
   mid-way through measuring, which makes results depend on core count rather than on the code under test. */
[assembly: DoNotParallelize]
