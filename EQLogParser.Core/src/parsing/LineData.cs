namespace EQLogParser
{
  internal class LineData
  {
    private LineWords _words;
    private string[] _split;

    public string Action { get; set; }
    public double BeginTime { get; set; }
    public long LineNumber { get; set; }

    /*
     * The word view parsers should read: built from Action on first touch (so it always reflects the post-glue-line
     * text), zero per-word strings — see LineWords for why this exists.
     */
    public LineWords Words => _words ??= LineWords.Build(Action);

    /*
     * The legacy door: a real string[] with empty entries, materialized only when a consumer asks — and built
     * directly from Action, NOT through Words, so a Misc/Cast line pays exactly what it paid before (one Split,
     * no extra scan) while a damage-only line pays no array at all. Words stays the parser's own view; both doors
     * describe the same text because every producer sets Action (all of them do; tests set both).
     * A setter value invalidates the cached word view so the doors cannot disagree about an assigned array.
     */
    public string[] Split
    {
      get => _split ??= Action.Split(' ');
      set { _split = value; _words = null; }
    }

    /* true when the line came from LogReader's live tail loop instead of the initial replay load */
    public bool IsMonitor { get; set; }
  }
}
