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

    /// <summary>
    /// Adjusts the maximized window size to fit within the monitor work area (excludes taskbar).
    /// Call this in your Window's HwndSource hook to handle WM_GETMINMAXINFO.
    /// </summary>
    internal static IntPtr MaximizeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
      const int WM_GETMINMAXINFO = 0x0024;
      if (msg == WM_GETMINMAXINFO)
      {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam)!;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
          var mi = new MONITORINFO();
          mi.cbSize = Marshal.SizeOf(mi);
          if (GetMonitorInfo(monitor, ref mi))
          {
            var wa = mi.rcWork;
            var ma = mi.rcMonitor;
            mmi.ptMaxPosition.x = wa.Left - ma.Left;
            mmi.ptMaxPosition.y = wa.Top - ma.Top;
            mmi.ptMaxSize.x = wa.Right - wa.Left;
            mmi.ptMaxSize.y = wa.Bottom - wa.Top;
          }
        }
        Marshal.StructureToPtr(mmi, lParam, true);
        handled = true;
      }
      return IntPtr.Zero;
    }

    #endregion

    #region Extended Window Styles and Methods

    [Flags]
    internal enum ExtendedWindowStyles
    {
      WsExToolwindow = 0x00000080,
      WsExTransparent = 0x00000020,
      WsExTopmost = 0x00000008,
      WsExLayered = 0x00080000,
      WsExNoActive = 0x08000000
    }

    internal enum GetWindowLongFields
    {
      GwlExstyle = -20
    }

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

    public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
    {
      return IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLongPtr32(hWnd, nIndex);
    }

    internal static IntPtr SetWindowLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
      SetLastError(0);
      IntPtr result;
      int error;

      if (IntPtr.Size == 4)
      {
        var temp = IntSetWindowLong(hWnd, nIndex, IntPtrToInt32(dwNewLong));
        error = Marshal.GetLastWin32Error();
        result = new IntPtr(temp);
      }
      else
      {
        result = IntSetWindowLongPtr(hWnd, nIndex, dwNewLong);
        error = Marshal.GetLastWin32Error();
      }

      if (result == IntPtr.Zero && error != 0)
        throw new Win32Exception(error);

      return result;
    }

    #endregion

    #region Layered Window Attributes

    [Flags]
    internal enum LayeredWindowAttributesFlags
    {
      LWA_COLORKEY = 0x00000001,
      LWA_ALPHA = 0x00000002
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetLayeredWindowAttributes(
      IntPtr hwnd,
      uint crKey,
      byte bAlpha,
      uint dwFlags);

    #endregion

    #region File and Folder Utilities

    internal static string GetDownloadsFolderPath()
    {
      var pathPtr = IntPtr.Zero;
      var folderId = new Guid("374DE290-123F-4565-9164-39C4925E467B");
      try
      {
        var hr = SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out pathPtr);
        if (hr != 0)
          throw Marshal.GetExceptionForHR(hr)!;
        return Marshal.PtrToStringUni(pathPtr)!;
      }
      finally
      {
        if (pathPtr != IntPtr.Zero)
          Marshal.FreeCoTaskMem(pathPtr);
      }
    }

    #endregion

    #region Monitor & Maximize Definitions

    private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
      public POINT ptReserved;
      public POINT ptMaxSize;
      public POINT ptMaxPosition;
      public POINT ptMinTrackSize;
      public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
      public int cbSize;
      public RECT rcMonitor;
      public RECT rcWork;
      public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
      public int Left, Top, Right, Bottom;
    }

    #endregion

    #region Native Methods

    // Ensure you can change the DLL search path at runtime
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool SetDllDirectory(string lpPathName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")] private static extern IntPtr GetWindowLongPtr32(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")] private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)] private static extern int IntSetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)] private static extern IntPtr IntSetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern void SetLastError(int dwErrorCode);
    [DllImport("kernel32.dll")] private static extern int IntPtrToInt32(IntPtr ptr);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    #endregion
  }
}