using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace EQLogParser
{
  internal static class NativeMethods
  {
    #region Hooks

    /// <summary>
    /// Simulates an error for WM_GETTEXT to handle specific scenarios.
    /// </summary>
    internal static IntPtr ProblemHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
      if (msg == 0x000D) // WM_GETTEXT
      {
        Marshal.SetLastSystemError(122); // ERROR_INSUFFICIENT_BUFFER
      }
      return IntPtr.Zero;
    }

    /// <summary>
    /// Clears errors for WM_GETTEXT, ensuring no lingering issues occur.
    /// </summary>
    internal static IntPtr BandAidHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
      if (msg == 0x000D) // WM_GETTEXT
      {
        Marshal.SetLastSystemError(0); // Clear error
      }
      return IntPtr.Zero;
    }

    #endregion

    #region Extended Window Styles and Methods

    [Flags]
    internal enum ExtendedWindowStyles
    {
      WsExToolwindow = 0x00000080,   // Window does not appear in the taskbar or Alt+Tab.
      WsExTransparent = 0x00000020,  // Window is transparent to mouse events.
      WsExTopmost = 0x00000008,      // Window is always on top.
      WsExLayered = 0x00080000,      // Supports layered behavior (e.g., transparency).
      WsExNoActive = 0x08000000      // Prevent window from becoming active
    }

    internal enum GetWindowLongFields
    {
      GwlExstyle = -20 // Retrieves the extended window styles.
    }

    /// <summary>
    /// Sets a window as topmost without stealing focus or changing its size/position.
    /// </summary>
    internal static void SetWindowTopMost(IntPtr hWnd)
    {
      const int HWND_TOPMOST = -1;
      const uint SWP_NOSIZE = 0x0001;
      const uint SWP_NOMOVE = 0x0002;
      const uint SWP_NOACTIVATE = 0x0010;

      var result = SetWindowPos(hWnd, new IntPtr(HWND_TOPMOST), 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
      if (!result)
      {
        var errorCode = Marshal.GetLastWin32Error();
        throw new InvalidOperationException($"SetWindowPos failed with error code {errorCode}");
      }
    }

    /// <summary>
    /// Gets the extended window styles for the specified window handle.
    /// </summary>
    public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
    {
      return IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLongPtr32(hWnd, nIndex);
    }

    /// <summary>
    /// Sets the extended window styles for the specified window handle.
    /// </summary>
    internal static IntPtr SetWindowLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
      SetLastError(0); // Clear previous errors

      IntPtr result;
      int error;

      if (IntPtr.Size == 4)
      {
        var tempResult = IntSetWindowLong(hWnd, nIndex, IntPtrToInt32(dwNewLong));
        error = Marshal.GetLastWin32Error();
        result = new IntPtr(tempResult);
      }
      else
      {
        result = IntSetWindowLongPtr(hWnd, nIndex, dwNewLong);
        error = Marshal.GetLastWin32Error();
      }

      if (result == IntPtr.Zero && error != 0)
      {
        throw new Win32Exception(error);
      }

      return result;
    }

    #endregion

    #region Layered Window Attributes

    [Flags]
    internal enum LayeredWindowAttributesFlags
    {
      LWA_COLORKEY = 0x00000001, // Use a color key for transparency.
      LWA_ALPHA = 0x00000002     // Use an alpha value for transparency.
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetLayeredWindowAttributes(
        IntPtr hwnd,
        uint crKey,       // Transparency color key
        byte bAlpha,      // Transparency level (0 = transparent, 255 = opaque)
        uint dwFlags);    // Flags (LWA_COLORKEY or LWA_ALPHA)

    #endregion

    #region File and Folder Utilities

    /// <summary>
    /// Retrieves the path to the Downloads folder.
    /// </summary>
    internal static string GetDownloadsFolderPath()
    {
      var path = IntPtr.Zero;
      var folderId = new Guid("374DE290-123F-4565-9164-39C4925E467B"); // FOLDERID_Downloads

      try
      {
        var hr = SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out path);
        if (hr != 0)
        {
          throw Marshal.GetExceptionForHR(hr)!;
        }
        return Marshal.PtrToStringUni(path);
      }
      finally
      {
        if (path != IntPtr.Zero)
        {
          Marshal.FreeCoTaskMem(path);
        }
      }
    }

    #endregion

    #region Native Methods

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern IntPtr GetWindowLongPtr32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int IntSetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr IntSetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    private static int IntPtrToInt32(IntPtr intPtr) => unchecked((int)intPtr.ToInt64());

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool SetDllDirectory(string lpPathName);

    [DllImport("kernel32.dll", EntryPoint = "SetLastError")]
    internal static extern void SetLastError(int dwErrorCode);

    #endregion
  }
}