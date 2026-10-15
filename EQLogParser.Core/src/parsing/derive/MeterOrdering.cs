using System;
using System.Collections.Generic;

namespace EQLogParser;

// The boards sort timestamp blocks before first creating rows. Retain block clocks and first appearances only,
// so equal-total ordering and the shared expanded/top-level ranks do not require retaining or recounting records.
internal sealed class MeterOrdering
{
  private readonly List<(MeterDirectionSlice Slice, int Version)> _layout = [];
  private readonly Dictionary<(string Name, string Owner, bool Valid), long> _first = [];

  internal void Assign(CombinedStats combined, IReadOnlyList<MeterDirectionSlice> slices,
    Dictionary<string, HashSet<string>> groups,
    Dictionary<(string Name, string Owner), MeterAmount> amounts, bool tanking,
    Func<string, string, string> resolve)
  {
    if (!Matches(slices)) Rebuild(slices);
    var creation = new Dictionary<string, long>(StringComparer.Ordinal);
    var top = new Dictionary<string, long>(StringComparer.Ordinal);
    var childOrder = new Dictionary<(string Group, string Child), long>();
    foreach (var (key, position) in _first)
    {
      Min(creation, key.Name, position * 2);
      if (!key.Valid) continue;
      var name = resolve(key.Name, key.Owner);
      Min(top, name, position);
      if (name != key.Name) Min(creation, name, position * 2 + 1);
      var child = (name, key.Name);
      if (!childOrder.TryGetValue(child, out var old) || position < old) childOrder[child] = position;
    }

    var ordered = new List<PlayerStats>(combined.StatsList);
    ordered.Sort((a, b) => creation[a.Name].CompareTo(creation[b.Name]));
    combined.StatsList.Sort((a, b) => top[a.Name].CompareTo(top[b.Name]));
    combined.StatsList.Sort(static (a, b) => b.Total.CompareTo(a.Total));
    if (tanking)
    {
      for (var i = 0; i < combined.StatsList.Count; i++) combined.StatsList[i].Rank = checked((ushort)(i + 1));
      return;
    }

    var children = new Dictionary<string, PlayerStats>(StringComparer.Ordinal);
    foreach (var (key, amount) in amounts)
    {
      if (!children.TryGetValue(key.Name, out var child))
        children[key.Name] = child = new PlayerStats { Name = key.Name };
      child.Total += amount.Total;
    }
    var expanded = new List<PlayerStats>();
    foreach (var row in ordered)
    {
      if (row.Name == row.OrigName)
      {
        expanded.Add(row);
        children[row.Name] = row;
      }
      else
      {
        var names = new List<string>(groups[row.Name]);
        names.Sort((a, b) => childOrder[(row.Name, a)].CompareTo(childOrder[(row.Name, b)]));
        foreach (var name in names) expanded.Add(children[name]);
      }
    }
    expanded.Sort(static (a, b) => b.Total.CompareTo(a.Total));
    for (var i = 0; i < expanded.Count; i++)
    {
      expanded[i].Rank = checked((ushort)(i + 1));
      if (i < combined.StatsList.Count) combined.StatsList[i].Rank = checked((ushort)(i + 1));
    }
  }

  private bool Matches(IReadOnlyList<MeterDirectionSlice> slices)
  {
    if (slices.Count != _layout.Count) return false;
    for (var i = 0; i < slices.Count; i++)
      if (!ReferenceEquals(slices[i], _layout[i].Slice) || slices[i].OrderVersion != _layout[i].Version) return false;
    return true;
  }

  private void Rebuild(IReadOnlyList<MeterDirectionSlice> slices)
  {
    _layout.Clear();
    _first.Clear();
    var blocks = new List<(double Time, int Slice, int Block)>();
    for (var i = 0; i < slices.Count; i++)
    {
      var slice = slices[i];
      _layout.Add((slice, slice.OrderVersion));
      for (var b = 0; b < slice.Blocks.Count; b++) blocks.Add((slice.Blocks[b], i, b));
    }
    blocks.Sort(static (a, b) => a.Time.CompareTo(b.Time));
    var positions = new int[slices.Count][];
    for (var i = 0; i < slices.Count; i++) positions[i] = new int[slices[i].Blocks.Count];
    for (var i = 0; i < blocks.Count; i++) positions[blocks[i].Slice][blocks[i].Block] = i;
    for (var i = 0; i < slices.Count; i++)
      foreach (var candidates in slices[i].First.Values)
        foreach (var item in candidates)
        {
          var position = ((long)positions[i][item.Block] << 32) + (uint)item.Position;
          var key = (item.Name, item.Owner, item.Valid);
          if (!_first.TryGetValue(key, out var old) || position < old) _first[key] = position;
        }
  }

  private static void Min(Dictionary<string, long> values, string name, long position)
  {
    if (!values.TryGetValue(name, out var old) || position < old) values[name] = position;
  }
}
