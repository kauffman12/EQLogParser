using EQLogParser;
using System;
using System.IO;

namespace EQLogParserTest
{
  [TestClass]
  public class ExceptionUtilTest
  {
    [TestMethod]
    public void CatchIoExceptions_Action_NoException_PassesThrough()
    {
      var executed = false;
      ExceptionUtil.CatchIoExceptions(() => executed = true);
      Assert.IsTrue(executed);
    }

    [TestMethod]
    public void CatchIoExceptions_Action_IOException_CatchesAndLogs()
    {
      Exception caught = null;
      ExceptionUtil.CatchIoExceptions(
        () => throw new IOException("test"),
        ex => caught = ex);
      Assert.IsInstanceOfType(caught, typeof(IOException));
    }

    [TestMethod]
    public void CatchIoExceptions_Action_UnauthorizedAccessException_CatchesAndLogs()
    {
      Exception caught = null;
      ExceptionUtil.CatchIoExceptions(
        () => throw new UnauthorizedAccessException("test"),
        ex => caught = ex);
      Assert.IsInstanceOfType(caught, typeof(UnauthorizedAccessException));
    }

    [TestMethod]
    public void CatchIoExceptions_Action_NullLogger_SwallowsException()
    {
      // Should not throw
      ExceptionUtil.CatchIoExceptions(
        () => throw new IOException("test"),
        logError: null);
    }

    [TestMethod]
    public void CatchIoExceptions_Action_Rethrow_RethrowsIOException()
    {
      bool threw = false;
      try
      {
        ExceptionUtil.CatchIoExceptions(
          () => throw new IOException("test"),
          logError: null,
          rethrow: true);
      }
      catch (IOException)
      {
        threw = true;
      }
      Assert.IsTrue(threw);
    }

    [TestMethod]
    public void CatchIoExceptions_Action_Rethrow_RethrowsUnauthorizedAccessException()
    {
      bool threw = false;
      try
      {
        ExceptionUtil.CatchIoExceptions(
          () => throw new UnauthorizedAccessException("test"),
          logError: null,
          rethrow: true);
      }
      catch (UnauthorizedAccessException)
      {
        threw = true;
      }
      Assert.IsTrue(threw);
    }

    [TestMethod]
    public void CatchIoExceptions_Action_NonIoException_PassesThrough()
    {
      bool threw = false;
      try
      {
        ExceptionUtil.CatchIoExceptions(
          () => throw new ArgumentException("not io"),
          logError: null);
      }
      catch (ArgumentException)
      {
        threw = true;
      }
      Assert.IsTrue(threw);
    }

    [TestMethod]
    public void CatchIoExceptions_Func_NoException_ReturnsValue()
    {
      var result = ExceptionUtil.CatchIoExceptions(() => 42, -1);
      Assert.AreEqual(42, result);
    }

    [TestMethod]
    public void CatchIoExceptions_Func_IOException_ReturnsDefault()
    {
      var result = ExceptionUtil.CatchIoExceptions(
        () => throw new IOException("test"),
        -1);
      Assert.AreEqual(-1, result);
    }

    [TestMethod]
    public void CatchIoExceptions_Func_UnauthorizedAccessException_ReturnsDefault()
    {
      var result = ExceptionUtil.CatchIoExceptions(
        () => throw new UnauthorizedAccessException("test"),
        -99);
      Assert.AreEqual(-99, result);
    }

    [TestMethod]
    public void CatchIoExceptions_Func_Rethrow_RethrowsIOException()
    {
      bool threw = false;
      try
      {
        ExceptionUtil.CatchIoExceptions(
          () => throw new IOException("test"),
          -1,
          rethrow: true);
      }
      catch (IOException)
      {
        threw = true;
      }
      Assert.IsTrue(threw);
    }

    [TestMethod]
    public void CatchIoExceptions_Func_NonIoException_PassesThrough()
    {
      bool threw = false;
      try
      {
        ExceptionUtil.CatchIoExceptions(
          () => throw new NullReferenceException("not io"),
          -1);
      }
      catch (NullReferenceException)
      {
        threw = true;
      }
      Assert.IsTrue(threw);
    }

    [TestMethod]
    public void CatchSecurityExceptions_Action_NoException_PassesThrough()
    {
      var executed = false;
      ExceptionUtil.CatchSecurityExceptions(() => executed = true);
      Assert.IsTrue(executed);
    }

    [TestMethod]
    public void CatchSecurityExceptions_Action_IOException_CatchesAndLogs()
    {
      Exception caught = null;
      ExceptionUtil.CatchSecurityExceptions(
        () => throw new IOException("test"),
        ex => caught = ex);
      Assert.IsInstanceOfType(caught, typeof(IOException));
    }

    [TestMethod]
    public void CatchSecurityExceptions_Action_UnauthorizedAccessException_CatchesAndLogs()
    {
      Exception caught = null;
      ExceptionUtil.CatchSecurityExceptions(
        () => throw new UnauthorizedAccessException("test"),
        ex => caught = ex);
      Assert.IsInstanceOfType(caught, typeof(UnauthorizedAccessException));
    }

    [TestMethod]
    public void CatchSecurityExceptions_Func_NoException_ReturnsValue()
    {
      var result = ExceptionUtil.CatchSecurityExceptions(() => "hello", "default");
      Assert.AreEqual("hello", result);
    }

    [TestMethod]
    public void CatchSecurityExceptions_Func_IOException_ReturnsDefault()
    {
      var result = ExceptionUtil.CatchSecurityExceptions(
        () => throw new IOException("test"),
        "fallback");
      Assert.AreEqual("fallback", result);
    }

    [TestMethod]
    public void CatchAllExceptions_Action_CatchesAllTypes()
    {
      Exception caught = null;
      ExceptionUtil.CatchAllExceptions(
        () => throw new DivideByZeroException("math"),
        ex => caught = ex);
      Assert.IsInstanceOfType(caught, typeof(DivideByZeroException));
    }

    [TestMethod]
    public void CatchAllExceptions_Action_NullLogger_SwallowsException()
    {
      // Should not throw
      ExceptionUtil.CatchAllExceptions(
        () => throw new InvalidOperationException("test"),
        logError: null);
    }

    [TestMethod]
    public void CatchAllExceptions_Func_CatchesAllTypes()
    {
      var result = ExceptionUtil.CatchAllExceptions(
        () => throw new StackOverflowException("oops"),
        "safe");
      Assert.AreEqual("safe", result);
    }

    [TestMethod]
    public void CatchAllExceptions_Func_NoException_ReturnsValue()
    {
      var result = ExceptionUtil.CatchAllExceptions(() => 123, 0);
      Assert.AreEqual(123, result);
    }

    [TestMethod]
    public void IsIoException_IOException_ReturnsTrue()
    {
      Assert.IsTrue(ExceptionUtil.IsIoException(new IOException()));
    }

    [TestMethod]
    public void IsIoException_UnauthorizedAccessException_ReturnsTrue()
    {
      Assert.IsTrue(ExceptionUtil.IsIoException(new UnauthorizedAccessException()));
    }

    [TestMethod]
    public void IsIoException_ArgumentException_ReturnsFalse()
    {
      Assert.IsFalse(ExceptionUtil.IsIoException(new ArgumentException()));
    }

    [TestMethod]
    public void IsIoException_Null_ReturnsFalse()
    {
      Assert.IsFalse(ExceptionUtil.IsIoException(null));
    }

    [TestMethod]
    public void IsIoException_DirectoryNotFoundException_ReturnsTrue()
    {
      // DirectoryNotFoundException derives from IOException
      Assert.IsTrue(ExceptionUtil.IsIoException(new DirectoryNotFoundException()));
    }

    [TestMethod]
    public void CatchIoExceptions_Action_LogErrorCalledBeforeRethrow()
    {
      var logCalled = false;
      try
      {
        ExceptionUtil.CatchIoExceptions(
          () => throw new IOException("test"),
          ex => logCalled = true,
          rethrow: true);
      }
      catch (IOException)
      {
        // expected
      }
      Assert.IsTrue(logCalled);
    }
  }
}
