using System.Collections.Concurrent;

namespace EQLogParser;

// Consumes a stream of log lines handed over by a reader. Implementations: LogProcessor (the
// parsing pipeline — used by the app and headless tests) and TriggerProcessor (WPF trigger
// tester). LinkTo starts consumption; Dispose must wait for it to drain.
//
// The unit of the stream is a BATCH (LogReaderItem[]), because the handoff across threads is what
// costs, not the parsing: one Add per line measured at roughly half the CPU of the whole ingest loop.
// A batch is a transport shape only — a consumer hands every line to the pipeline in arrival order on
// one thread, so nothing downstream can tell how the file was chopped up.
internal interface ILogProcessor : IDisposable
{
  void LinkTo(BlockingCollection<LogReaderItem[]> collection);
}
