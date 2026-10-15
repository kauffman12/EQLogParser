namespace EQLogParser;

// Per-stream recent casts, including non-charms sharing a charm landing message. SpellCast retains interruption updates.
internal sealed class CharmCastHistory
{
  private readonly List<(double Time, SpellCast Cast)> _casts = [];

  internal void Add(SpellCast cast, double time)
  {
    if (!EQDataStore.Instance.HasCharmCandidateMessage(cast.Spell)) return;
    // Retain the same generous log-time horizon as the cast store; qualification still reads only six seconds.
    _casts.RemoveAll(entry => entry.Time < time - RecordsStore.CastHistoryHorizonS);
    _casts.Add((time, cast));
  }

  internal string OwnerFor(string message, double time)
  {
    var store = EQDataStore.Instance;
    var latest = double.NegativeInfinity;
    SpellCast found = null;
    var isCharm = false;
    // A backwards timestamp must not change which eligible cast is newest. Equal seconds prefer the later arrival.
    foreach (var entry in _casts)
    {
      if (entry.Time > time || entry.Time < time - CharmWindowPolicy.OwnerCastLookbackS || entry.Time < latest) continue;
      if (!store.TryMatchCharmCast(entry.Cast.Spell, message, out var charm)) continue;
      latest = entry.Time;
      found = entry.Cast;
      isCharm = charm;
    }
    return found is { Interrupted: false } && isCharm ? found.Caster : null;
  }

  internal void Clear() => _casts.Clear();
}
