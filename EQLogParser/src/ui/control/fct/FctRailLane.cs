using System;

/*
 * FctRailLane - the four columns of split mode.
 *
 * What people see on a split screen are COLUMNS, and what they ask for is "incoming goes in that column, heals in
 * that one". The first model offered only halves, and placement then did what placement does: two categories sharing
 * a half got woven into neighbouring sub-columns by the burst scorer (measured: x = 200, 271, 343 in one half), so
 * the machine had columns while the settings had sides — and nobody could say which column a value would use.
 * Four named lanes make the visible thing the configurable thing: each is a quarter of the width, owns its stream
 * territory outright, and shares nothing with any other category that did not choose it.
 *
 * Numbering runs left to right across the screen, as the names read: left1 is the far-left column, right2 the far
 * right. The old side spellings ("left", "right") parse to the OUTER lane of that side, which is where a side's
 * numbers were centred (a quarter's centre equals a half's centre only at the outer lanes).
 */
namespace EQLogParser
{
  internal enum FctRailLane { Left1, Left2, Right1, Right2 }

  internal static class FctRailLanes
  {
    // Lane number across the screen: 0 = left1 ... 3 = right2.
    public static int Index(FctRailLane lane) => lane switch
    {
      FctRailLane.Left1 => 0,
      FctRailLane.Left2 => 1,
      FctRailLane.Right1 => 2,
      _ => 3,
    };

    // Which half a lane sits in - split's per-side rules (opposite-of-heal defaults) still speak in sides.
    public static FctRegionSide SideOf(FctRailLane lane) => Index(lane) < 2 ? FctRegionSide.Left : FctRegionSide.Right;

    // Settings spelling for a lane.
    public static string Token(FctRailLane lane) => lane switch
    {
      FctRailLane.Left1 => "left1",
      FctRailLane.Left2 => "left2",
      FctRailLane.Right1 => "right1",
      _ => "right2",
    };

    /* Reads a stored lane. Accepts the old side spellings so an older config lands in the matching outer lane, and
     * anything else falls back rather than dropping the choice. */
    public static FctRailLane Parse(string raw, FctRailLane fallback)
    {
      switch ((raw ?? string.Empty).Trim().ToLowerInvariant())
      {
        case "left1": return FctRailLane.Left1;
        case "left2": return FctRailLane.Left2;
        case "right1": return FctRailLane.Right1;
        case "right2": return FctRailLane.Right2;
        case "left": return FctRailLane.Left1;
        case "right": return FctRailLane.Right2;
        default: return fallback;
      }
    }

    /* The lane a side-based setting means: the outer column, centred where the half used to be centred. */
    public static FctRailLane OfSide(FctRegionSide side) => side == FctRegionSide.Left ? FctRailLane.Left1 : FctRailLane.Right2;
  }

}