using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace EQLogParser
{
  internal class BulkObservableCollection<T> : ObservableCollection<T>
  {
    private readonly int _maxSize;
    private bool _suppressNotifications;

    public BulkObservableCollection(int maxSize = int.MaxValue)
    {
      _maxSize = maxSize;
    }

    public void AddRange(IEnumerable<T> items)
    {
      _suppressNotifications = true;

      foreach (var item in items)
      {
        Items.Insert(0, item);
      }

      EnforceMaxSize();

      _suppressNotifications = false;

      OnCollectionChanged(new NotifyCollectionChangedEventArgs(
          NotifyCollectionChangedAction.Reset));
    }

    protected override void InsertItem(int index, T item)
    {
      base.InsertItem(index, item);
      EnforceMaxSize();
    }

    private void EnforceMaxSize()
    {
      if (_maxSize <= 0)
        return;

      while (Items.Count > _maxSize)
      {
        Items.RemoveAt(0);
      }
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
      if (!_suppressNotifications)
        base.OnCollectionChanged(e);
    }
  }
}
