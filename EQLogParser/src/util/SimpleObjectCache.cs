using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace EQLogParser
{
  public class SimpleObjectCache<T>
  {
    private readonly Dictionary<int, object> _cache = [];

    public T Add(T obj)
    {
      var hashCode = obj.GetHashCode();

      if (!_cache.TryGetValue(hashCode, out var value))
      {
        _cache[hashCode] = obj;  // Store the object directly if it's the first one.
        return obj;
      }

      if (value is T existingObj)
      {
        // There's already one object with this hash code.
        if (existingObj.Equals(obj))
        {
          return existingObj;  // Return the existing object if it's equal.
        }

        // Two objects with the same hash code: upgrade to a list.
        var newList = new List<T> { existingObj, obj };
        _cache[hashCode] = newList;
        return obj;
      }

      // There's already a list of objects with this hash code.
      var list = (List<T>)value;
      foreach (var item in CollectionsMarshal.AsSpan(list))
      {
        if (item.Equals(obj))
        {
          return item;  // Return the existing object if it's equal.
        }
      }

      list.Add(obj);  // Add the new object to the list.
      return obj;
    }

    internal void Clear() => _cache.Clear();
  }
}
