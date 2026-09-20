using System.Collections.Concurrent;

namespace EQLogParser;

// Consumes a stream of log lines handed over by a reader. Implementations: LogProcessor (the
// parsing pipeline — used by the app and headless tests) and TriggerProcessor (WPF trigger
// tester). LinkTo starts consumption; Dispose must wait for it to drain.
internal interface ILogProcessor : IDisposable
{
  void LinkTo(BlockingCollection<LogReaderItem> collection);
}
