using System.Collections.ObjectModel;
using System.Windows.Data;

namespace EQLogParser
{
  /* UI adapter over the cross-platform QuickShareState: keeps the collection the window binds to
   * on the UI thread and defers the dedup/ownership rules to the shared state. Every accepted
   * record — from any producer (GINA or legacy shares) — is mirrored in through the Accepted
   * event, plus the history replayed by Subscribe, so the bound collection can never fall out of
   * sync with the state no matter when the window is first opened. */
  internal class QuickShareManager
  {
    internal static QuickShareManager Instance { get; } = new();

    internal ObservableCollection<QuickShareRecord> Records { get; } = [];
    private readonly object _lock = new();

    // One instance only: each constructor subscribes to Accepted, so a second one would mirror every
    // accepted record into a bound collection twice.
    private QuickShareManager()
    {
      BindingOperations.EnableCollectionSynchronization(Records, _lock);
      // Subscribe through QuickShareState.Subscribe so the records accepted before this manager
      // exists are replayed instead of lost: nothing constructs it at startup, so a GINA share seen
      // while just parsing a log used to land in the state and never reach the window opened later.
      QuickShareState.Instance.Subscribe(MirrorRecord);
    }

    // Mirrors one accepted (or replayed) record into the bound collection on the UI thread. The
    // raiser is the chat-parsing thread, so this must stay fire-and-forget — InvokeAsyncLogged
    // because a plain discarded post leaves a failure (e.g. collection torn down during shutdown)
    // unobserved and silently stops the mirror. Both paths insert at index 0 and Subscribe replays
    // oldest-first, so the window stays newest-first either way.
    private void MirrorRecord(QuickShareRecord record) => UiUtil.InvokeAsyncLogged(
      () => Records.Insert(0, record), "QuickShareManager: mirroring accepted record into the bound collection");

    /* Legacy share call path; GINA adds directly through QuickShareState (Core can't see this class). */
    internal void Add(QuickShareRecord record) => QuickShareState.Instance.Add(record);

    internal bool IsMine(string key) => QuickShareState.Instance.IsMine(key);
  }
}
