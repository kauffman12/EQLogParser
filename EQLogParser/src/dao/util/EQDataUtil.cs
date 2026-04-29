using System;
using System.Collections.Generic;

namespace EQLogParser
{
  internal class SpellAbbrvComparer : IEqualityComparer<SpellData>
  {
    public bool Equals(SpellData x, SpellData y) => x?.NameAbbrv == y?.NameAbbrv;
    public int GetHashCode(SpellData obj) => obj.NameAbbrv.GetHashCode();
  }

  internal class EQDataUtil
  {
    internal static int SpellDurationCompare(SpellData a, SpellData b)
    {
      if (ReferenceEquals(a, b))
      {
        return 0;
      }

      if (a is null)
      {
        return 1;
      }

      if (b is null)
      {
        return -1;
      }

      var result = b.Duration.CompareTo(a.Duration);

      if (result == 0)
      {
        var aHasId = int.TryParse(a.Id, out var aInt);
        var bHasId = int.TryParse(b.Id, out var bInt);

        if (aHasId && bHasId)
        {
          result = bInt.CompareTo(aInt);
        }
        else if (aHasId && !bHasId)
        {
          result = -1;
        }
        else if (!aHasId && bHasId)
        {
          result = 1;
        }
        else
        {
          result = string.Compare(a.Id, b.Id, StringComparison.Ordinal);
        }
      }

      return result;
    }
  }
}
