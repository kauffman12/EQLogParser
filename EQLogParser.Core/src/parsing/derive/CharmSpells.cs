namespace EQLogParser
{
  // Cast ownership and wear-off evidence share the spell-data gate, independent of spell names or rank conventions.
  // See docs/DesignNotes.md → "Charm ownership follows the spell's lands-on-other message".
  internal static class CharmSpells
  {
    internal static bool IsCharmSpellName(string spell)
      => !string.IsNullOrWhiteSpace(spell) && EQDataStore.Instance.IsCharmSpell(spell.Trim());

    internal static bool IsCharmMessage(string message)
    {
      var text = message.AsSpan().Trim();
      return text.Equals("has been charmed.", StringComparison.OrdinalIgnoreCase)
        || text.Equals("is charmed.", StringComparison.OrdinalIgnoreCase);
    }

    // "Your" in a wear-off names the log author, using the same local-player key as cast evidence.
    internal static bool IsLocalOwnerWord(string ownerWord)
      => !string.IsNullOrEmpty(ownerWord) && ownerWord.Equals("your", StringComparison.OrdinalIgnoreCase);
  }
}
