namespace EQLogParser;

// Meter RDPS and rolling charts count the same labelled log seconds, including the current second.
internal static class RollingWindow
{
  internal const int Seconds = 6;
}
