using System.Diagnostics;

#nullable enable annotations

namespace EQLogParser;

/*
 * A timer callback, a work item and an async void continuation have no unhandled-exception net: the runtime ends the process. ThreadBoundary
 * is where those bodies hand the fault over instead, so what these tests pin is the two halves of that deal — the fault never comes back out,
 * and it costs one log line rather than one per tick (RepeatCap is the counter, since a timer that fails once fails every interval).
 */
[TestClass]
public sealed class ThreadBoundaryTest
{
  [TestInitialize]
  public void Reset() => RepeatCap.Reset();

  [TestMethod]
  public void GuardHoldsTheFaultAndLetsTheCallerCarryOn()
  {
    var after = false;

    ThreadBoundary.Guard("test site", () => throw new InvalidOperationException("save failed"));
    after = true;

    Assert.IsTrue(after, "a held fault must not stop the work that follows it");
  }

  [TestMethod]
  public void AFaultlessTickCostsNothingToSay()
  {
    var ran = false;
    ThreadBoundary.Guard("test site", () => ran = true);

    Assert.IsTrue(ran);
    Assert.AreEqual(0, RepeatCap.Silenced, "a tick that did not fault may not raise a count");
  }

  [TestMethod]
  public void ARepeatingFaultSpeaksOnceAndIsCountedAfterThat()
  {
    // Five failures of one kind on one site: the first is the diagnosis, the other four are the same sentence four times.
    for (var i = 0; i < 5; i++)
      ThreadBoundary.Guard("test site", () => throw new IOException("file locked"));

    Assert.AreEqual(4, RepeatCap.Silenced);
  }

  [TestMethod]
  public void ADifferentKindOfFaultStillGetsItsOwnLine()
  {
    ThreadBoundary.Guard("test site", () => throw new IOException("file locked"));
    ThreadBoundary.Guard("test site", () => throw new InvalidOperationException("something else entirely"));

    // Two distinct defects are two diagnoses; the cap keys on the failure, not on the occurrence.
    Assert.AreEqual(0, RepeatCap.Silenced);

    ThreadBoundary.Guard("test site", () => throw new IOException("file locked again"));
    Assert.AreEqual(1, RepeatCap.Silenced, "and the second one is behind its own first line");
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void ReportingFailuresCannotEscapeEvenWhenTheTraceFallbackAlsoFails(bool failTrace)
  {
    using var listener = new ReportingTraceListener(failTrace);
    Trace.Listeners.Add(listener);
    try
    {
      // Formatting the primary report fails before the logger is called; no logger replacement is needed.
      ThreadBoundary.Guard("test reporting", () => throw new ThrowingMessageException());
      ThreadBoundary.Note("test direct reporting", new ThrowingMessageException());
      ThreadBoundary.Note("test direct reporting", new ThrowingMessageException());
    }
    finally
    {
      Trace.Listeners.Remove(listener);
    }

    Assert.IsGreaterThanOrEqualTo(2, listener.Attempts, "both fault boundaries must try the fallback");
    Assert.AreEqual(1, RepeatCap.Silenced, "a reporting failure must not reopen the fault's voice cap");
    if (!failTrace)
    {
      StringAssert.Contains(listener.Output, "test reporting");
      StringAssert.Contains(listener.Output, nameof(ThrowingMessageException));
      StringAssert.Contains(listener.Output, "reporting failed");
    }
  }

  [TestMethod]
  [DataRow(false, false)]
  [DataRow(true, false)]
  [DataRow(false, true)]
  [DataRow(true, true)]
  public void ApplicationFallbackHoldsBothFormattingAndListenerFailures(bool failTrace, bool failFormatting)
  {
    using var listener = new ReportingTraceListener(failTrace);
    Trace.Listeners.Add(listener);
    try
    {
      Exception failure = failFormatting ? new ThrowingMessageException() : new IOException("disk unavailable");
      ThreadBoundary.TraceFailure("application exception report", failure);
    }
    finally
    {
      Trace.Listeners.Remove(listener);
    }

    if (failFormatting)
    {
      Assert.AreEqual(0, listener.Attempts, "formatting must be inside the non-throwing boundary too");
    }
    else
    {
      Assert.IsGreaterThanOrEqualTo(1, listener.Attempts);
      if (!failTrace)
      {
        StringAssert.Contains(listener.Output, "application exception report");
        StringAssert.Contains(listener.Output, "disk unavailable");
      }
    }
  }

  [TestMethod]
  public void NoteCapsAnExistingCatchBlockTheSameWay()
  {
    // The form used by lanes that already own a catch (chat archive, follow poll, trigger lanes): same voice, same cap.
    for (var i = 0; i < 3; i++)
    {
      try
      {
        throw new UnauthorizedAccessException("denied");
      }
      catch (Exception ex)
      {
        ThreadBoundary.Note("test lane", ex);
      }
    }

    Assert.AreEqual(2, RepeatCap.Silenced);
  }

  private sealed class ThrowingMessageException : Exception
  {
    public override string Message => throw new IOException("reporting failed");
  }

  private sealed class ReportingTraceListener(bool fail) : TraceListener
  {
    public override void Write(string? message)
    {
      Attempts++;
      if (fail) throw new IOException("trace failed");
      Output += message;
    }

    public override void WriteLine(string? message) => Write(message);

    internal int Attempts { get; private set; }
    internal string Output { get; private set; } = string.Empty;
  }
}
