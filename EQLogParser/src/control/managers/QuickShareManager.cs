using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Data;

namespace EQLogParser
{
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
      // Marshal to UI thread to avoid thread-safety issues with ObservableCollection
      await UiUtil.InvokeAsync(() => AddInternal(record));
    }

    private void AddInternal(QuickShareRecord record)
    {
      // This method should only be called from UI thread
      if (Records.Count == 0 || Records[0].Key != record.Key ||
        Records[0].BeginTime != record.BeginTime)
      {
        Records.Insert(0, record);
      }
    }

    internal bool IsMine(string key)
    {
      // Read operations can be from any thread since binding synchronization handles it
      lock (_lock)
      {
        return Records.FirstOrDefault(r => r.IsMine && r.Key == key) != null;
      }
    }
  }

  public class QuickShareRecord
  {
    public double BeginTime { get; set; }
    public string Type { get; set; }
    public string To { get; set; }
    public string From { get; set; }
    public string Key { get; set; }
    public bool IsMine { get; set; }
  }
}
