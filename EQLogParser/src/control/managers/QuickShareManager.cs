using System.Collections.ObjectModel;
using System.Windows.Data;

namespace EQLogParser
{
  /* UI adapter over the cross-platform QuickShareState: keeps the collection the window binds to
   * on the UI thread and defers the dedup/ownership rules to the shared state. */
  internal class QuickShareManager
  {
    internal static QuickShareManager Instance { get; set; } = new();

    internal ObservableCollection<QuickShareRecord> Records { get; } = [];
    private readonly object _lock = new();

    internal QuickShareManager()
    {
      BindingOperations.EnableCollectionSynchronization(Records, _lock);
    }

    internal async void Add(QuickShareRecord record)
    {
      if (QuickShareState.Instance.Add(record))
      {
        // Marshal the view update to the UI thread; the dedup decision already happened in the shared state.
        await UiUtil.InvokeAsync(() => Records.Insert(0, record));
      }
    }

    internal bool IsMine(string key) => QuickShareState.Instance.IsMine(key);
  }
}
