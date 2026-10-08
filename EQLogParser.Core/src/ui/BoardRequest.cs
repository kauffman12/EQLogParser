#nullable enable annotations

namespace EQLogParser;

/*
 * WHY the summary boards were asked for. A board can be asked for by seven different events and they cost seconds apiece on a
 * large capture, so "the damage grid filled three times" is only answerable if every ask carries its own name (see StatsBuildTrace,
 * which prints this on the build's log line). The words are also a closed list on purpose: an announcement that cannot say which of
 * these it is has no business being anonymous in the log — that is how a duplicated door survives a review.
 */
internal enum BoardReason
{
  /// <summary>"Select All" / "Unselect All" / "Select group" was clicked.</summary>
  SelectCommand,

  /// <summary>A selection change parked behind the open context menu, released as it closed (SelectionSettle.CloseMenu).</summary>
  MenuClose,

  /// <summary>The settle timer after a click or a drag across rows.</summary>
  SettleTick,

  /// <summary>The pane replaced every row from a snapshot and restored the selection onto the new row objects.</summary>
  SnapshotSwap,

  /// <summary>A derive pass edited or removed a row the operator has selected, so its numbers are gone.</summary>
  RowEdited,

  /// <summary>Facts or identity verdicts moved since this selection was last built — the same rows answer differently now.</summary>
  ContentMoved,

  /// <summary>The Refresh button: rebuild whatever is on screen, no questions asked.</summary>
  Manual,
}

/// <summary>One ask: these fights, produced by this capture state, for this reason.</summary>
internal sealed record BoardRequest(IReadOnlyList<DerivedFight> Fights, long ContentStamp, BoardReason Reason, string? Detail = null);
