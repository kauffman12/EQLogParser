using System;
using System.IO;

namespace EQLogParser
{
  /// <summary>
  /// Common exception handling utilities for file I/O and other operations.
  /// </summary>
  public static class ExceptionUtil
  {
    /// <summary>
    /// Optional global error callback for code that cannot pass a per-call logger (e.g., deep utility code).
    /// The main project should register its log4net logger during startup.
    /// </summary>
    public static Action<Exception> GlobalLogError;

    /// <summary>
    /// Optional global debug callback for non-critical diagnostic messages.
    /// </summary>
    public static Action<string> GlobalLogDebug;

    /// <summary>
    /// Executes an action and catches IOException and UnauthorizedAccessException.
    /// </summary>
    /// <param name="action">The action to execute.</param>
    /// <param name="logError">Optional logger to call with caught exceptions. Pass null to silently ignore.</param>
    /// <param name="rethrow">If true, re-throws the exception after logging (caller can still catch it).</param>
    public static void CatchIoExceptions(Action action, Action<Exception> logError = null, bool rethrow = false)
    {
      try
      {
        action();
      }
      catch (IOException ex)
      {
        logError?.Invoke(ex);
        if (rethrow) throw;
      }
      catch (UnauthorizedAccessException ex)
      {
        logError?.Invoke(ex);
        if (rethrow) throw;
      }
    }

    /// <summary>
    /// Executes a function and catches IOException and UnauthorizedAccessException.
    /// </summary>
    /// <param name="func">The function to execute.</param>
    /// <param name="defaultValue">Value to return if an exception is caught.</param>
    /// <param name="logError">Optional logger to call with caught exceptions. Pass null to silently ignore.</param>
    /// <param name="rethrow">If true, re-throws the exception after logging (caller can still catch it). defaultValue is not used when rethrow is true.</param>
    /// <returns>The result of the function, or defaultValue if an exception is caught.</returns>
    public static T CatchIoExceptions<T>(Func<T> func, T defaultValue, Action<Exception> logError = null, bool rethrow = false)
    {
      try
      {
        return func();
      }
      catch (IOException ex)
      {
        logError?.Invoke(ex);
        if (rethrow) throw;
        return defaultValue;
      }
      catch (UnauthorizedAccessException ex)
      {
        logError?.Invoke(ex);
        if (rethrow) throw;
        return defaultValue;
      }
    }

    /// <summary>
    /// Executes an action and catches IOException, UnauthorizedAccessException, and SecurityException.
    /// </summary>
    /// <param name="action">The action to execute.</param>
    /// <param name="logError">Optional logger to call with caught exceptions. Pass null to silently ignore.</param>
    public static void CatchSecurityExceptions(Action action, Action<Exception> logError = null)
    {
      try
      {
        action();
      }
      catch (IOException ex)
      {
        logError?.Invoke(ex);
      }
      catch (UnauthorizedAccessException ex)
      {
        logError?.Invoke(ex);
      }
#if WINDOWS
      catch (SecurityException ex)
      {
        logError?.Invoke(ex);
      }
#endif
    }

    /// <summary>
    /// Executes a function and catches IOException, UnauthorizedAccessException, and SecurityException.
    /// </summary>
    /// <param name="func">The function to execute.</param>
    /// <param name="defaultValue">Value to return if an exception is caught.</param>
    /// <param name="logError">Optional logger to call with caught exceptions. Pass null to silently ignore.</param>
    /// <returns>The result of the function, or defaultValue if an exception is caught.</returns>
    public static T CatchSecurityExceptions<T>(Func<T> func, T defaultValue, Action<Exception> logError = null)
    {
      try
      {
        return func();
      }
      catch (IOException ex)
      {
        logError?.Invoke(ex);
        return defaultValue;
      }
      catch (UnauthorizedAccessException ex)
      {
        logError?.Invoke(ex);
        return defaultValue;
      }
#if WINDOWS
      catch (SecurityException ex)
      {
        logError?.Invoke(ex);
        return defaultValue;
      }
#endif
    }

    /// <summary>
    /// Executes an action and catches all exceptions.
    /// </summary>
    /// <param name="action">The action to execute.</param>
    /// <param name="logError">Optional logger to call with caught exceptions. Pass null to silently ignore.</param>
    public static void CatchAllExceptions(Action action, Action<Exception> logError = null)
    {
      try
      {
        action();
      }
      catch (Exception ex)
      {
        logError?.Invoke(ex);
      }
    }

    /// <summary>
    /// Executes a function and catches all exceptions.
    /// </summary>
    /// <param name="func">The function to execute.</param>
    /// <param name="defaultValue">Value to return if an exception is caught.</param>
    /// <param name="logError">Optional logger to call with caught exceptions. Pass null to silently ignore.</param>
    /// <returns>The result of the function, or defaultValue if an exception is caught.</returns>
    public static T CatchAllExceptions<T>(Func<T> func, T defaultValue, Action<Exception> logError = null)
    {
      try
      {
        return func();
      }
      catch (Exception ex)
      {
        logError?.Invoke(ex);
        return defaultValue;
      }
    }

    /// <summary>
    /// Checks if an exception is a file I/O related exception (IOException or UnauthorizedAccessException).
    /// </summary>
    public static bool IsIoException(Exception ex) => ex is IOException or UnauthorizedAccessException;
  }
}
