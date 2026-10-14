namespace EQLogParser
{
  internal class LineData
  {
    private string _action;
    private LineWords _words;
    private string[] _split;

    /* Assigning the text invalidates both cached views: a door must not answer from a copy of the line before the glue cut. */
    public string Action
    {
      get => _action;
      set
      {
        _action = value;
        _words = null;
        _split = null;
      }
    }

    public double BeginTime { get; set; }
    public long LineNumber { get; set; }

    /// <summary>The word view parsers read — built from Action on first touch, zero strings per word (see LineWords).</summary>
    public LineWords Words => _words ??= LineWords.Build(Action);

    /// <summary>
    /// The legacy door: a real string[] with empty entries, materialized only when a consumer asks. Assigned arrays are returned
    /// verbatim and rebuild Words from Action, so an assigned array is a display contract, not a second source of text.
    /// </summary>
    public string[] Split
    {
      get => _split ??= Action.Split(' ');
      set
      {
        _split = value;
        _words = null;
      }
    }

    /// <summary>True when the line came from LogReader's live tail loop instead of the initial replay load.</summary>
    public bool IsMonitor { get; set; }
  }
}
