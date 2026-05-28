using System;
using System.Collections.Generic;

namespace EQLogParser
{
  /// <summary>
  /// Utility methods for collection operations.
  /// </summary>
  internal static class CollectionUtil
  {
    /// <summary>
    /// Performs a binary search on a sorted list using a custom comparer function.
    /// Returns the index of the element if found, or the bitwise complement of the insertion point if not found.
    /// </summary>
    /// <typeparam name="T">The type of elements in the list.</typeparam>
    /// <param name="list">The sorted list to search.</param>
    /// <param name="comparer">A function that returns the comparison result for an element (negative if less than target, zero if equal, positive if greater).</param>
    /// <returns>The index of the found element, or ~insertionPoint if not found.</returns>
    internal static int BinarySearch<T>(IReadOnlyList<T> list, Func<T, int> comparer)
    {
      int low = 0, high = list.Count - 1;

      while (low <= high)
      {
        var mid = low + ((high - low) / 2);
        var comparison = comparer(list[mid]);

        switch (comparison)
        {
          case 0:
            return mid;
          case < 0:
            low = mid + 1;
            break;
          default:
            high = mid - 1;
            break;
        }
      }

      return ~low;
    }
  }
}
