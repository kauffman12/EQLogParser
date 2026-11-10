namespace EQLogParser;

// Scheduling only: the caller starts a returned work item and completes it after its prompt/import or final failure.
// docs/DesignNotes.md → "Quick Share imports have one worker per transport".
internal sealed class QuickShareImportQueue
{
  internal const int MaxAutomaticPending = 6;

  private readonly object _gate = new();
  private readonly Dictionary<string, QuickShareImport> _pending = new(StringComparer.Ordinal);
  private readonly Queue<QuickShareImport> _waiting = new();
  private QuickShareImport _active;

  internal QuickShareImport Enqueue(string key, string sender, bool autoMerge = false, bool isTrigger = false,
    string characterId = null, bool manual = false)
  {
    lock (_gate)
    {
      if (_pending.TryGetValue(key, out var existing))
      {
        existing.AddCharacter(characterId);
        return null;
      }

      if (!manual && _pending.Count >= MaxAutomaticPending)
      {
        return null;
      }

      var work = new QuickShareImport(key, sender, autoMerge, isTrigger);
      work.AddCharacter(characterId);
      _pending.Add(key, work);
      if (_active is null)
      {
        _active = work;
        return work;
      }

      _waiting.Enqueue(work);
      return null;
    }
  }

  internal QuickShareImport Complete(QuickShareImport work)
  {
    lock (_gate)
    {
      // A repeated or stale completion cannot retire the next import, even when its key has been reused.
      if (_active is null || !ReferenceEquals(_active, work))
      {
        return null;
      }

      _pending.Remove(work.Key);
      _active = _waiting.TryDequeue(out var next) ? next : null;
      return _active;
    }
  }
}
