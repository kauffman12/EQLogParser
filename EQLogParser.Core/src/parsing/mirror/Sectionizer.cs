namespace EQLogParser.Mirror
{
  // One "Fight N" section: a run of consecutive fights whose starts stay within GroupTimeout of
  // the previous fight's end, plus the inactivity gaps that opened each following section.
  // Pure and deterministic — the display rows (WPF phase) are a projection of this.
  internal sealed class MirrorSection
  {
    public int Index;                    // 1-based "Fight N"
    public List<DerivedFight> Fights { get; } = [];
    public List<(double From, double To)> InactiveGaps { get; } = [];   // dividers BEFORE this section's fights
  }

  // The sectioning UX as a pure function (D5): divider rows come from real gaps between derived
  // fights, not fabricated Fight objects. Mirrors FightTable's grouping algorithm:
  //   tanking list     — new section when fight.BeginTime - max(LastTime since last divider) >= timeout
  //   non-tanking list — same, gated on DamageHits > 0, tracking LastDamageTime
  internal static class Sectionizer
  {
    public const int DefaultGroupTimeout = FightTableGroupTimeout;
    private const int FightTableGroupTimeout = 120;   // mirrors FightTable.GroupTimeout

    public static List<MirrorSection> Sectionize(IReadOnlyList<DerivedFight> fights, int groupTimeout = DefaultGroupTimeout, bool nonTanking = false)
    {
      var sections = new List<MirrorSection>();
      if (fights.Count == 0) return sections;

      var section = NewSection(1, sections);
      var lastTime = double.NaN;
      var group = 1;

      foreach (var fight in fights)
      {
        var reference = nonTanking ? fight.LastDamageTime : fight.LastTime;
        var begin = fight.BeginTime;

        if (!double.IsNaN(lastTime) && (nonTanking ? fight.DamageHits > 0 : true) && begin - lastTime >= groupTimeout)
        {
          // the divider opens the NEXT section — it renders before that section's first fight
          // row, so it is recorded on the new section
          group++;
          section = NewSection(group, sections);
          section.InactiveGaps.Add((lastTime, begin));
        }

        section.Fights.Add(fight);
        lastTime = double.IsNaN(lastTime) ? reference : Math.Max(lastTime, reference);
      }

      return sections;
    }

    private static MirrorSection NewSection(int index, List<MirrorSection> sections)
    {
      var section = new MirrorSection { Index = index };
      sections.Add(section);
      return section;
    }

    // Display rows for the WPF phase (and the report today): fights interleaved with their
    // preceding divider gaps, in list order.
    public static List<(bool IsDivider, double GapFrom, double GapTo, DerivedFight Fight)> ToDisplayRows(IReadOnlyList<DerivedFight> fights, int groupTimeout = DefaultGroupTimeout)
    {
      var rows = new List<(bool, double, double, DerivedFight)>();
      var lastTime = double.NaN;

      foreach (var fight in fights)
      {
        if (!double.IsNaN(lastTime) && fight.BeginTime - lastTime >= groupTimeout)
        {
          rows.Add((true, lastTime, fight.BeginTime, null));
        }

        rows.Add((false, 0, 0, fight));
        lastTime = double.IsNaN(lastTime) ? fight.LastTime : Math.Max(lastTime, fight.LastTime);
      }

      return rows;
    }
  }
}
