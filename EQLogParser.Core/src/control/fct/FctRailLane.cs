using System;

/*
 * FctRailLane - the four columns of split mode.
 *
 * What people see on a split screen are COLUMNS, and what they ask for is "incoming goes in that column, heals in
 * that one". The first model offered only halves, and placement then did what placement does: two categories sharing
 * a half got woven into neighbouring sub-columns by the burst scorer (measured: x = 200, 271, 343 in one half), so
 * the machine had columns while the settings had sides — and nobody could say which column a value would use.
 * Four named lanes make the visible thing the configurable thing: the lanes a category books own their territory
 * outright and share nothing with any other category - two claimants to a half of the width take it in quarters, one
 * claimant takes the whole half, because a lane nobody booked is air the number may use rather than a wall (FctStage
 *
 * Numbering runs left to right across the screen, as the names read: left1 is the far-left column, right2 the far
 * right. The old side spellings ("left", "right") parse to the OUTER lane of that side. A lane that owns its half alone
 * keeps it as REGION - labels, sway and clamps measure the whole half - but its numbers stay parked at the centre of the
 * slot they would hold in a full house (FctStage.SpineFor): freeing a neighbour buys space, never a moved column.
 */
namespace EQLogParser
{
  /*
   * The four columns, plus `None`: "this category gets no column", which is how split mode says "do not show me that at
   * all" now that the show list stopped carrying damage-in / damage-out switches (it lists nine rows instead, one per kind
   * of number, and a player hiding their outgoing damage hides it by giving it nowhere to go). Nothing is placed on None —
   * the gate in FctIngest stops those numbers before they reach geometry — and None's only geometric meaning is the
   * absence itself: an empty lane frees its share of the half to its neighbours (FctStage tiles the lanes that book).
   *
   * Fountain (bands) ignores lane settings entirely, which means it ignores None too: incoming and outgoing both draw there,
   * because that scheme's whole arrangement is the two of them facing each other across a clear middle.
   */
  internal enum FctRailLane { None, Left1, Left2, Right1, Right2 }

  internal static class FctRailLanes
  {
    /* The four columns a category can stream down, in the order a search should try them (FctConfigState puts a category that
       shares a column head-on into the first of these it can have). "none" is a decision about drawing, not a place to put a
       number, so it is deliberately not one of the columns. */
    public static readonly FctRailLane[] Columns = [FctRailLane.Left1, FctRailLane.Left2, FctRailLane.Right1, FctRailLane.Right2];

    // Lane number across the screen: 0 = left1 ... 3 = right2. None has no column, and says so rather than borrowing one.
    public static int Index(FctRailLane lane) => lane switch
    {
      FctRailLane.Left1 => 0,
      FctRailLane.Left2 => 1,
      FctRailLane.Right1 => 2,
      FctRailLane.Right2 => 3,
      _ => -1,
    };

    /* Which half a lane sits in - split's per-side rules (opposite-of-heal defaults) still speak in sides. None has none, so
     * it answers with the left like any unknown value would: nothing is placed there, and an answer is cheaper than a throw
     * over a setting a hand-editor can write. */
    public static FctRegionSide SideOf(FctRailLane lane) => Index(lane) < 2 ? FctRegionSide.Left : FctRegionSide.Right;

    // Settings spelling for a lane.
    public static string Token(FctRailLane lane) => lane switch
    {
      FctRailLane.None => "none",
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
        case "none": return FctRailLane.None;
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