using System;
using System.Runtime.InteropServices;

namespace EQLogParser
{
  internal class NativeMethods
  {
    #region Window styles

    [Flags]
    internal enum ExtendedWindowStyles
    {
      // ...
      WS_EX_TOOLWINDOW = 0x00000080,
      WS_EX_TRANSPARENT = 0x00000020
      // ...
    }

    internal enum GetWindowLongFields
    {
      // ...
      GWL_EXSTYLE = (-20),
      // ...
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Portability", "CA1901:PInvokeDeclarationsShouldBePortable", MessageId = "return")]
    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern IntPtr GetWindowLongPtr32(IntPtr hWnd, int nIndex);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Interoperability", "CA1400:PInvokeEntryPointsShouldExist")]
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
      else
      {
        return GetWindowLongPtr32(hWnd, nIndex);
      }
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
        Int32 tempResult = IntSetWindowLong(hWnd, nIndex, IntPtrToInt32(dwNewLong));
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
        throw new System.ComponentModel.Win32Exception(error);
      }

      return result;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Interoperability", "CA1400:PInvokeEntryPointsShouldExist")]
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr IntSetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern Int32 IntSetWindowLong(IntPtr hWnd, int nIndex, Int32 dwNewLong);

    private static int IntPtrToInt32(IntPtr intPtr)
    {
      return unchecked((int)intPtr.ToInt64());
    }

    [DllImport("kernel32.dll", EntryPoint = "SetLastError")]
    internal static extern void SetLastError(int dwErrorCode);
    #endregion
  }
}
