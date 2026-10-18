using System;
using System.Runtime.InteropServices;

namespace EQLogParser.Wpf.Test
{
  [TestClass]
  public class ContentSizedHeightHookTest
  {
    [TestMethod]
    [DataRow(0)]
    [DataRow(39)]
    [DataRow(52)]
    [DataRow(78)]
    public void OnlyTheNativeMinimumHeightIsRelaxedAndWpfStillProcessesTheMessage(int nativeMinimum)
    {
      // MINMAXINFO is five POINTs (ten 32-bit integers), including on a 64-bit process.
      int[] original = [1, 2, 3000, 2000, -10, -20, 120, nativeMinimum, 5000, 4000];
      var actual = RewriteMessage(0x0024, original, out var handled);
      int[] expected = [1, 2, 3000, 2000, -10, -20, 120, 0, 5000, 4000];

      CollectionAssert.AreEqual(expected, actual);
      Assert.IsFalse(handled, "Consuming the message would leave WPF's cached native minimum in place.");
    }

    [TestMethod]
    public void AnUnrelatedMessageDoesNotRewriteItsPayload()
    {
      int[] original = [1, 2, 3000, 2000, -10, -20, 120, 39, 5000, 4000];
      var actual = RewriteMessage(0x0046, original, out var handled);

      CollectionAssert.AreEqual(original, actual);
      Assert.IsFalse(handled);
    }

    [TestMethod]
    public void AMissingPayloadIsNotDereferenced()
    {
      var handled = false;
      var result = NativeMethods.ContentSizedHeightHook(IntPtr.Zero, 0x0024, IntPtr.Zero, IntPtr.Zero, ref handled);

      Assert.AreEqual(IntPtr.Zero, result);
      Assert.IsFalse(handled);
    }

    private static int[] RewriteMessage(int message, int[] original, out bool handled)
    {
      var buffer = Marshal.AllocHGlobal(original.Length * sizeof(int));
      try
      {
        Marshal.Copy(original, 0, buffer, original.Length);
        handled = false;
        NativeMethods.ContentSizedHeightHook(IntPtr.Zero, message, IntPtr.Zero, buffer, ref handled);
        var actual = new int[original.Length];
        Marshal.Copy(buffer, actual, 0, actual.Length);
        return actual;
      }
      finally
      {
        Marshal.FreeHGlobal(buffer);
      }
    }
  }
}
