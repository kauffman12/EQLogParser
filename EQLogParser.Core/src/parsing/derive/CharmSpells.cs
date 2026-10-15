namespace EQLogParser
{
  // SPA 22 supplies the spell/message cache. A matching non-charm cast or no matching cast proves no charm.
  // docs/DesignNotes.md → "Charm ownership follows the spell's lands-on-other message".
  internal static class CharmSpells
  {
    internal static bool IsCharmSpellName(string spell)
      => !string.IsNullOrWhiteSpace(spell) && EQDataStore.Instance.IsCharmSpell(spell.Trim());

    internal static bool TryProcessLanding(string action, double time)
    {
      if (!EQDataStore.Instance.TryGetCharmLanding(action, out var target, out var message)) return false;
      var lane = ParseLane.Current;
      var owner = lane.CharmCasts.OwnerFor(message, time);
      if (string.IsNullOrEmpty(owner)) return false;
      lane.Charm?.Invoke(target, time, true, owner);
      return true;
    }

    internal static bool IsLocalOwnerWord(string ownerWord)
      => !string.IsNullOrEmpty(ownerWord) && ownerWord.Equals("your", StringComparison.OrdinalIgnoreCase);
  }
}
