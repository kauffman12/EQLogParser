using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Data;

namespace EQLogParser
{
  internal class QuickShareManager : ILifecycle
  {
    internal static QuickShareManager Instance { get; set; } = new();

    public ObservableCollection<QuickShareRecord> Records { get; } = [];
    private readonly object _lock = new();

    internal QuickShareManager()
    {
      BindingOperations.EnableCollectionSynchronization(Records, _lock);
      LifecycleManager.Register(this);
    }

    public void Clear(bool serverChanged) => Shutdown();

    internal void Add(QuickShareRecord record)
    {
      lock (_lock)
      {
        if (Records.Count == 0 || Records[0].Key != record.Key ||
          !Records[0].BeginTime.Equals(record.BeginTime))
        {
          Records.Insert(0, record);
        }
      }
    }

    internal bool IsMine(string key)
    {
      lock (_lock)
      {
        return Records.FirstOrDefault(r => r.IsMine && r.Key == key) != null;
      }
    }

    public void Shutdown()
    {
      lock (_lock)
      {
        Records.Clear();
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
