namespace EQLogParser
{
  // Dispatcher-owned answer state: a completed read stays owed until a current, covering answer is published.
  internal sealed class NpcStatsRefresh
  {
    private long _minimumFacts;
    private long? _wantedKey;
    private long? _publishedKey;

    public bool Pending { get; private set; }
    public long Generation { get; private set; }

    public void WaitFor(long minimumFacts)
    {
      if (!Pending || minimumFacts > _minimumFacts) _minimumFacts = minimumFacts;
      Pending = true;
    }

    public bool Covers(long projectedFacts) => !Pending || projectedFacts >= _minimumFacts;

    public bool Ask(long key, long projectedFacts)
    {
      if (!Covers(projectedFacts)) return false;
      _wantedKey = key;
      if (_publishedKey != key) return true;

      // The same answer can cover a read-complete event that arrived just after publication.
      Complete(key, projectedFacts);
      return false;
    }

    public bool CanPublish(long key, long projectedFacts) => _wantedKey == key && Covers(projectedFacts);

    public bool Begin(long key, long projectedFacts)
    {
      if (!CanPublish(key, projectedFacts)) return false;
      // A replacement may fail partway through publication; the previous grid is then no longer reusable.
      _publishedKey = null;
      return true;
    }

    public bool Complete(long key, long projectedFacts)
    {
      if (!CanPublish(key, projectedFacts)) return false;
      _publishedKey = key;
      Pending = false;
      _minimumFacts = 0;
      return true;
    }

    public void Invalidate()
    {
      Generation++;
      _wantedKey = null;
      _publishedKey = null;
    }

    public void Reset()
    {
      Invalidate();
      Pending = false;
      _minimumFacts = 0;
    }
  }
}
