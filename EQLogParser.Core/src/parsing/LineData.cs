namespace EQLogParser
{
  internal class LineData
  {
    public string Action { get; set; }
    public double BeginTime { get; set; }
    public long LineNumber { get; set; }
    public string[] Split { get; set; }

    /* true when the line came from LogReader's live tail loop instead of the initial replay load */
    public bool IsMonitor { get; set; }
  }
}
