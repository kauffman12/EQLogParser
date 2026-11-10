namespace EQLogParser;

internal sealed class QuickShareImport(string key, string sender, bool autoMerge, bool isTrigger)
{
  private readonly object _gate = new();
  private readonly HashSet<string> _characterIds = [];

  internal string Key { get; } = key;
  internal string Sender { get; } = sender;
  internal bool AutoMerge { get; } = autoMerge;
  internal bool IsTrigger { get; } = isTrigger;

  internal void AddCharacter(string characterId)
  {
    if (characterId is null) return;
    lock (_gate)
    {
      _characterIds.Add(characterId);
    }
  }

  // Readers can join while a prompt is open; the store must never enumerate their live HashSet.
  internal HashSet<string> GetCharacterIds()
  {
    lock (_gate)
    {
      return [.. _characterIds];
    }
  }
}
