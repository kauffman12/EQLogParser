namespace EQLogParser;

// A preview save may refresh only its own editable overlay, without spending unrelated pending edits.
internal static class OverlayGeometrySync
{
  // Desktop positions can be negative; signed Win32 coordinate limits also leave room for wide desktops.
  internal const int MinCoordinate = int.MinValue;
  internal const int MaxCoordinate = int.MaxValue;

  internal static bool Apply(string selectedId, Overlay editable, TriggerNode updated)
  {
    if (string.IsNullOrEmpty(selectedId) || editable is null || updated?.OverlayData is not { } saved
      || !string.Equals(selectedId, updated.Id, System.StringComparison.Ordinal))
    {
      return false;
    }

    editable.Left = saved.Left;
    editable.Top = saved.Top;
    editable.Width = saved.Width;
    editable.Height = saved.Height;
    return true;
  }
}
