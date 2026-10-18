using EQLogParser;

#nullable enable annotations

namespace EQLogParserTest
{
  [TestClass]
  public class ExceptionUtilTest
  {
    [TestMethod]
    [DataRow(typeof(IOException), true)]
    [DataRow(typeof(UnauthorizedAccessException), true)]
    [DataRow(typeof(ArgumentException), false)]
    [DataRow(typeof(NullReferenceException), false)]
    public void IoActionLogsOnlyItsOwnExceptionsAndRethrowsTheOriginalWhenAsked(Type type, bool io)
    {
      foreach (var rethrow in new[] { false, true })
      {
        var error = Error(type);
        Exception? logged = null;
        var observed = Capture(() => ExceptionUtil.CatchIoExceptions(() => throw error,
          ex => logged = ex, rethrow));
        Assert.AreSame(io ? error : null, logged);
        Assert.AreSame(rethrow || !io ? error : null, observed);
      }
    }

    [TestMethod]
    [DataRow(typeof(IOException), true)]
    [DataRow(typeof(UnauthorizedAccessException), true)]
    [DataRow(typeof(NullReferenceException), false)]
    public void IoFunctionReturnsTheFallbackOrRethrowsWithoutReplacingTheException(Type type, bool io)
    {
      foreach (var rethrow in new[] { false, true })
      {
        var error = Error(type);
        Exception? logged = null;
        var value = 0;
        var observed = Capture(() => value = ExceptionUtil.CatchIoExceptions<int>(() => throw error, -99,
          ex => logged = ex, rethrow));
        Assert.AreSame(io ? error : null, logged);
        Assert.AreSame(rethrow || !io ? error : null, observed);
        if (io && !rethrow) Assert.AreEqual(-99, value);
      }
    }

    [TestMethod]
    [DataRow(typeof(IOException))]
    [DataRow(typeof(UnauthorizedAccessException))]
    public void SecurityWrappersLogTheOriginalAndFunctionsReturnTheirFallback(Type type)
    {
      var error = Error(type);
      Exception? logged = null;
      ExceptionUtil.CatchSecurityExceptions(() => throw error, ex => logged = ex);
      Assert.AreSame(error, logged);
      logged = null;
      Assert.AreEqual("fallback", ExceptionUtil.CatchSecurityExceptions<string>(
        () => throw error, "fallback", ex => logged = ex));
      Assert.AreSame(error, logged);
    }

    [TestMethod]
    [DataRow(typeof(DivideByZeroException))]
    [DataRow(typeof(InvalidOperationException))]
    [DataRow(typeof(StackOverflowException))]
    public void CatchAllWrappersAcceptNonIoExceptions(Type type)
    {
      var error = Error(type);
      Exception? logged = null;
      ExceptionUtil.CatchAllExceptions(() => throw error, ex => logged = ex);
      Assert.AreSame(error, logged);
      logged = null;
      Assert.AreEqual("safe", ExceptionUtil.CatchAllExceptions<string>(() => throw error, "safe", ex => logged = ex));
      Assert.AreSame(error, logged);
    }

    [TestMethod]
    public void ActionWrappersRunSuccessfulActionsAndAllowNoLogger()
    {
      var count = 0;
      ExceptionUtil.CatchIoExceptions(() => { count++; });
      ExceptionUtil.CatchSecurityExceptions(() => { count++; });
      ExceptionUtil.CatchAllExceptions(() => { count++; });
      Assert.AreEqual(3, count);
      ExceptionUtil.CatchIoExceptions(() => throw new IOException(), logError: null);
      ExceptionUtil.CatchAllExceptions(() => throw new InvalidOperationException(), logError: null);
    }

    [TestMethod]
    public void FunctionWrappersKeepSuccessfulValues()
    {
      Assert.AreEqual(42, ExceptionUtil.CatchIoExceptions(() => 42, -1));
      Assert.AreEqual("hello", ExceptionUtil.CatchSecurityExceptions(() => "hello", "fallback"));
      Assert.AreEqual(123, ExceptionUtil.CatchAllExceptions(() => 123, 0));
    }

    [TestMethod]
    [DataRow(typeof(IOException), true)]
    [DataRow(typeof(UnauthorizedAccessException), true)]
    [DataRow(typeof(DirectoryNotFoundException), true)]
    [DataRow(typeof(ArgumentException), false)]
    [DataRow(null, false)]
    public void IoClassificationIncludesDerivedExceptionsButNotNull(Type? type, bool expected)
    {
      Assert.AreEqual(expected, ExceptionUtil.IsIoException(type is null ? null : Error(type)));
    }

    private static Exception Error(Type type) => Activator.CreateInstance(type, "test") is Exception error
      ? error : throw new AssertFailedException($"Cannot construct {type.Name}.");

    private static Exception? Capture(Action action)
    {
      try
      {
        action();
        return null;
      }
      catch (Exception error)
      {
        return error;
      }
    }
  }
}
