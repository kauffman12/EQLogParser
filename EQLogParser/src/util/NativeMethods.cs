using System;
using System.Collections.Generic;
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

    [SuppressMessage("Microsoft.Interoperability", "CA1400:PInvokeEntryPointsShouldExist")]
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr IntSetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int IntSetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

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

  [StructLayout(LayoutKind.Sequential)]
  internal struct DoubleKeyValuePair
  {
    public IntPtr Key;
    public double Value;
  }

  internal static class CachingLib
  {
    [DllImport("EQLogParserCache.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void CreateMap(string id);

    [DllImport("EQLogParserCache.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void CreateSet(string id);

    [DllImport("EQLogParserCache.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern bool TryAddDoubleToMap(string id, string key, double value);

    [DllImport("EQLogParserCache.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern bool TryAddStringToMap(string id, string key, string value);

    [DllImport("EQLogParserCache.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern bool TryAddStringToSet(string id, string key);

    [DllImport("EQLogParserCache.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern bool TryRemoveFromMap(string id, string key);

    [DllImport("EQLogParserCache.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern bool TryRemoveFromSet(string id, string key);

    [DllImport("EQLogParserCache.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern bool IsInMap(string id, string key);

    [DllImport("EQLogParserCache.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern bool IsInSet(string id, string key);

    [DllImport("EQLogParserCache.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern long GetMapSize(string id);

    [DllImport("EQLogParserCache.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern long GetSetSize(string id);

    [DllImport("EQLogParserCache.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr GetStringMapValue(string id, string key);

    [DllImport("EQLogParserCache.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern double GetDoubleMapValue(string id, string key);

    [DllImport("EQLogParserCache.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr GetDoubleMapEntries(string id, out int size);

    public static IEnumerable<(string, double)> GetAllMapEntries(string id)
    {
      var ptr = GetDoubleMapEntries(id, out var size);

      if (ptr == IntPtr.Zero)
      {
        yield break;
      }

      for (var i = 0; i < size; i++)
      {
        var entryPtr = new IntPtr(ptr.ToInt64() + (i * Marshal.SizeOf(typeof(DoubleKeyValuePair))));
        var entry = Marshal.PtrToStructure<DoubleKeyValuePair>(entryPtr);
        var key = Marshal.PtrToStringAnsi(entry.Key);
        yield return (key, entry.Value);
      }

      // Free the allocated memory
      Marshal.FreeCoTaskMem(ptr);
    }

    public static bool TryGetMapValue(string id, string key, out string result)
    {
      result = null;
      var ptr = GetStringMapValue(id, key);
      if (ptr != IntPtr.Zero)
      {
        result = Marshal.PtrToStringAnsi(ptr);
        return true;
      }

      return false;
    }

    public static bool TryGetMapValue(string id, string key, out double result)
    {
      result = 0;
      var value = GetDoubleMapValue(id, key);
      if (value > double.MinValue)
      {
        result = value;
        return true;
      }

      return false;
    }
  }
}
