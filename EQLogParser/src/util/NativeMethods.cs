using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace EQLogParser
{
  internal static class NativeMethods
  {
    #region Window styles

    internal static IntPtr ProblemHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
      if (msg == 0x000D) // WM_GETTEXT
      {
        Marshal.SetLastSystemError(122);
      }
      return IntPtr.Zero;
    }

    internal static IntPtr BandAidHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
      if (msg == 0x000D) // WM_GETTEXT
      {
        Marshal.SetLastSystemError(0);
      }
      return IntPtr.Zero;
    }

    [Flags]
    internal enum ExtendedWindowStyles
    {
      // ...
      WsExToolwindow = 0x00000080,
      WsExTransparent = 0x00000020,
      WsExTopmost = 0x00000008,
      // ...
    }

    internal enum GetWindowLongFields
    {
      // ...
      GwlExstyle = -20,
      // ...
    }

    const int SwpNosize = 0x0001;
    const int SwpNomove = 0x0002;
    const int SwpNoactivate = 0x0010;

    [SuppressMessage("Microsoft.Portability", "CA1901:PInvokeDeclarationsShouldBePortable", MessageId = "return")]
    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern IntPtr GetWindowLongPtr32(IntPtr hWnd, int nIndex);

    [SuppressMessage("Microsoft.Interoperability", "CA1400:PInvokeEntryPointsShouldExist")]
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    // This static method is required because Win32 does not support
    // GetWindowLongPtr directly
    public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
    {
      if (IntPtr.Size == 8)
      {
        return GetWindowLongPtr64(hWnd, nIndex);
      }

      return GetWindowLongPtr32(hWnd, nIndex);
    }

    internal static IntPtr SetWindowLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
      IntPtr result;
      // Win32 SetWindowLong doesn't clear error on success
      SetLastError(0);

      int error;
      if (IntPtr.Size == 4)
      {
        // use SetWindowLong
        var tempResult = IntSetWindowLong(hWnd, nIndex, IntPtrToInt32(dwNewLong));
        error = Marshal.GetLastWin32Error();
        result = new IntPtr(tempResult);
      }
      else
      {
        // use SetWindowLongPtr
        result = IntSetWindowLongPtr(hWnd, nIndex, dwNewLong);
        error = Marshal.GetLastWin32Error();
      }

      if ((result == IntPtr.Zero) && (error != 0))
      {
        throw new Win32Exception(error);
      }

      return result;
    }

    internal static void SetWindowTopMost(IntPtr hWnd)
    {
      SetWindowPos(hWnd, new IntPtr(-1), 0, 0, 0, 0, SwpNosize | SwpNomove | SwpNoactivate);
    }

    [SuppressMessage("Microsoft.Interoperability", "CA1400:PInvokeEntryPointsShouldExist")]
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr IntSetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern Int32 IntSetWindowLong(IntPtr hWnd, int nIndex, Int32 dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private static int IntPtrToInt32(IntPtr intPtr)
    {
      return unchecked((int)intPtr.ToInt64());
    }

    [DllImport("kernel32.dll", EntryPoint = "SetLastError")]
    internal static extern void SetLastError(int dwErrorCode);
    #endregion
  }
}
