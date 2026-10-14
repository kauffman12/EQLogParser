using System.Collections.Concurrent;

namespace EQLogParser;

// Consumes a stream of log lines from a reader: LogProcessor (the parsing pipeline, app and headless tests) or TriggerProcessor (trigger
// tester). LinkTo starts consumption; Dispose waits for the drain.
//
// The unit is a BATCH, because the thread crossing costs and the parse does not — one Add per line measured about half the CPU of the whole
// ingest loop. A batch is transport only: consumers hand lines to the pipeline one at a time in arrival order on one thread, so nothing
// downstream can tell how the file was chopped up.
internal interface ILogProcessor : IDisposable
{
  void LinkTo(BlockingCollection<LogReaderItem[]> collection);
}
