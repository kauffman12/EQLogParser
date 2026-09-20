namespace EQLogParser;

// One log line flowing from a reader to a processor. Line is the full raw line including the
// "[Ddd Mmm dd HH:mm:ss yyyy] " header that processors slice off (Action = Line[27..]), Ts is
// the dotnet-epoch second for that line (readers cache it per distinct second), and IsMonitor
// flags lines appended while live-monitoring so replay-sensitive consumers (FCT) can drop them.
internal readonly record struct LogReaderItem(string Line, double Ts, bool IsMonitor);
