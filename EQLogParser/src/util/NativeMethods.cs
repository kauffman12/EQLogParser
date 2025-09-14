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

    /// <summary>
    /// Tweaks the final position/size while truly maximized (WS_MAXIMIZE) so the window
    /// leaves a tiny slit on the taskbar edge of the current monitor. Do NOT set handled=true.
    /// </summary>
    internal static IntPtr WindowPosHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
      if (msg != WM_WINDOWPOSCHANGING)
        return IntPtr.Zero;

      // Only adjust when the style actually has WS_MAXIMIZE
      var stylePtr = GetWindowLongPtr(hwnd, GWL_STYLE);
      var style = unchecked((int)stylePtr.ToInt64());
      if ((style & WS_MAXIMIZE) == 0)
        return IntPtr.Zero;

      // Get monitor and its work/monitor rects
      var hMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
      if (hMon == IntPtr.Zero)
        return IntPtr.Zero;

      var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
      if (!GetMonitorInfo(hMon, ref mi))
        return IntPtr.Zero;

      // Locate taskbar on this monitor
      var hTb = FindTaskbarOnMonitor(mi.rcMonitor);
      if (hTb == IntPtr.Zero || !GetWindowRect(hTb, out var tbRect))
        return IntPtr.Zero;

      // Read/modify/write WINDOWPOS
      var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);

      // Leave a small slit on the taskbar edge (2px is enough; bump if needed)
      const int GAP = 1;
      switch (EdgeOfTaskbar(mi.rcMonitor, tbRect))
      {
        case 0: // Left
          wp.x += GAP;
          wp.cx = Math.Max(0, wp.cx - GAP);
          break;
        case 1: // Top
          wp.y += GAP;
          wp.cy = Math.Max(0, wp.cy - GAP);
          break;
        case 2: // Right
          wp.cx = Math.Max(0, wp.cx - GAP);
          break;
        case 3: // Bottom
          wp.cy = Math.Max(0, wp.cy - GAP);
          break;
      }

      Marshal.StructureToPtr(wp, lParam, fDeleteOld: false);
      // Important: don't mark handled; let Windows continue with our tweaked WINDOWPOS.
      return IntPtr.Zero;
    }


    #endregion

    #region Native Methods

    internal static void SetWindowTopMost(IntPtr hWnd)
    {
      var result = SetWindowPos(hWnd, new IntPtr(HWND_TOPMOST), 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
      if (!result)
      {
        var errorCode = Marshal.GetLastWin32Error();
        throw new InvalidOperationException($"SetWindowPos failed with error code {errorCode}");
      }
    }

    internal static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
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

    // Find the taskbar HWND that sits on the same monitor (primary or secondary).
    private static IntPtr FindTaskbarOnMonitor(RECT mon)
    {
      // Primary taskbar class
      var primary = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
      if (primary != IntPtr.Zero && GetWindowRect(primary, out var r1) && Intersects(mon, r1))
        return primary;

      // Secondary taskbars (multi-monitor)
      var h = IntPtr.Zero;
      while ((h = FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
      {
        if (GetWindowRect(h, out var r2) && Intersects(mon, r2))
          return h;
      }

      return IntPtr.Zero;
    }

    private static bool Intersects(RECT a, RECT b) =>
      !(b.Left >= a.Right || b.Right <= a.Left || b.Top >= a.Bottom || b.Bottom <= a.Top);

    // 0=Left, 1=Top, 2=Right, 3=Bottom
    private static int EdgeOfTaskbar(RECT mon, RECT tb)
    {
      if (tb.Left <= mon.Left && tb.Right <= mon.Left + 10) return 0; // left
      if (tb.Top <= mon.Top && tb.Bottom <= mon.Top + 10) return 1; // top
      if (tb.Right >= mon.Right && tb.Left >= mon.Right - 10) return 2; // right
      return 3; // default bottom
    }

    private const int MONITOR_DEFAULTTONEAREST = 0x00000002;
    private const int HWND_TOPMOST = -1;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const int GWL_STYLE = -16;
    private const int WS_MAXIMIZE = 0x01000000;
    private const int SM_CXSIZEFRAME = 32;
    private const int SM_CYSIZEFRAME = 33;
    private const int SM_CXPADDEDBORDER = 92;

    [Flags]
    private enum LayeredWindowAttributesFlags
    {
      LWA_COLORKEY = 0x00000001,
      LWA_ALPHA = 0x00000002
    }

    [Flags]
    internal enum ExtendedWindowStyles
    {
      WsExToolwindow = 0x00000080,
      WsExTransparent = 0x00000020,
      WsExTopmost = 0x00000008,
      WsExLayered = 0x00080000,
      WsExNoActive = 0x08000000
    }

    [Flags]
    internal enum GetWindowLongFields
    {
      GwlExstyle = -20
    }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
      public IntPtr hwnd, hwndInsertAfter;
      public int x, y, cx, cy;
      public uint flags;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] internal static extern bool SetDllDirectory(string lpPathName);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern void SetLastError(int dwErrorCode);
    [DllImport("kernel32.dll")] private static extern int IntPtrToInt32(IntPtr ptr);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string windowTitle);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    [DllImport("user32.dll", EntryPoint = "GetWindowLong")] private static extern IntPtr GetWindowLongPtr32(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")] private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)] private static extern int IntSetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)] private static extern IntPtr IntSetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    #endregion
  }
}