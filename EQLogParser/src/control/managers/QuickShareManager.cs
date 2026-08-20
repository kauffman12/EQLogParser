using System.Collections.ObjectModel;
using System.Windows.Data;

namespace EQLogParser
{
  /* UI adapter over the cross-platform QuickShareState: keeps the collection the window binds to
   * on the UI thread and defers the dedup/ownership rules to the shared state. Every accepted
   * record — from any producer (GINA or legacy shares) — is mirrored in through the Accepted
   * event, so the bound collection can never fall out of sync with the state. */
  internal class QuickShareManager
  {
    internal static QuickShareManager Instance { get; set; } = new();

    internal ObservableCollection<QuickShareRecord> Records { get; } = [];
    private readonly object _lock = new();

    internal QuickShareManager()
    {
      BindingOperations.EnableCollectionSynchronization(Records, _lock);
      // Mirror each accepted record into the bound collection on the UI thread.
      QuickShareState.Instance.Accepted += record => _ = UiUtil.InvokeAsync(() => Records.Insert(0, record));
    }

    /* Legacy share call path; GINA adds directly through QuickShareState (Core can't see this class). */
    internal void Add(QuickShareRecord record) => QuickShareState.Instance.Add(record);

    internal bool IsMine(string key) => QuickShareState.Instance.IsMine(key);
  }
}
