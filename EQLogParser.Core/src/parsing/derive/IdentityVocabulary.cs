namespace EQLogParser;

/*
 * The words this application says out loud about identity, kept apart from the codes that produce them.
 *
 * Internally an identity is an `IdentityKind` plus a source string ("R15-healed", "Prior:R6-npcdb"). Those strings are
 * right where they belong — in a log line, in the ledger, in a diff of someone's overrides file — and wrong everywhere
 * a person reads them. The Names window spent years showing "R10-manual" in a column and needed a source lookup to know
 * what its own list meant, which is how a vocabulary test earns its keep: this table is closed, and its size is asserted
 * (`EveryRuleWordHasAWordWorthPrinting`).
 *
 * Two rules about the mapping itself:
 *   - AN UNKNOWN CODE ECHOES ITSELF. A rule added without a word here shows up as its own name — legible, and obvious
 *     enough to get a word written. Guessing ("R21-whatever" → "Evidence") would file a new kind of proof under an old
 *     meaning, and a wrong provenance is worse than jargon: the operator acts on it.
 *   - THE EARLIER-LOG MARK SURVIVES, because "this capture proved it" versus "an older log on this server said so" are
 *     different actions (the second may need re-checking, the first does not). The ledger stores the rule its verdict
 *     came from, so even a borrowed answer can say what kind of proof it was.
 */
internal static class IdentityVocabulary
{
  /// <summary>One entry in the Type dropdown: the word an operator reads and the verdict it writes.</summary>
  internal sealed record TypeOption(string Word, IdentityKind Kind);

  /*
   * Everything a Type cell offers, in one list — the whole identity vocabulary at a glance, which is what the old
   * right-click menu could not do (five items had to be remembered, and "take my claim back" was not even among them:
   * that verb existed only in the fight grids' menu).
   *
   * "Clear claim" carries IdentityKind.Unknown because that is exactly what ClassificationCommands.ClearVerdict means —
   * remove the operator's row and let this capture's own rules show through again. Each word appears EXACTLY ONCE: the
   * retired menu listed NPC twice ("Select an enemy to Set as NPC." was one of two handlers doing the same thing), and a
   * dropdown with two entries for one answer is a bug a person notices only after clicking.
   */
  internal static readonly TypeOption[] TypeOptions =
  [
    new("Player", IdentityKind.Player),
    new("Pet", IdentityKind.Pet),
    new("Mercenary", IdentityKind.Merc),
    new("NPC", IdentityKind.Npc),
    new("Clear claim", IdentityKind.Unknown),
  ];

  /// <summary>The TYPE cell: the header says Type, so a value has to read like a type and never like an enum identifier.</summary>
  internal static string TypeWord(IdentityKind kind) => kind switch
  {
    IdentityKind.Player => "Player",
    IdentityKind.Pet => "Pet",
    IdentityKind.Merc => "Merc",
    IdentityKind.Npc => "NPC",
    _ => "Unknown",
  };

  /*
   * The WHY cell: two words naming the KIND of evidence. "R15-healed" and "R3-presence" made somebody open the source to
   * read their own list; "Healed" and "Raid" do not. The cell also stops costing more room than it says: 256 fixed pixels
   * for `R15-healed`, where two words and a theme-scaled width need about half (docs/DesignNotes.md).
   */
  internal static string WhyWord(string? source)
  {
    if (string.IsNullOrEmpty(source)) return string.Empty;

    var earlier = false;
    if (source.StartsWith(PriorPrefix, StringComparison.Ordinal))
    {
      earlier = true;
      source = source[PriorPrefix.Length..];
    }

    // R5's owner suffix ("R5-owner:Sancus") comes off: the column describes the evidence, and the owner lives in the
    // Pet Owners window. Any other colon-split (none today) reads the same way rather than printing a path.
    var colon = source.IndexOf(':');
    if (colon > 0) source = source[..colon];

    var word = WhyWords.TryGetValue(source, out var known) ? known : source;
    return earlier ? $"{word} (earlier)" : word;
  }

  internal const string PriorPrefix = "Prior:";

  /*
   * Every source string ClassificationRules (and the override store) can put on a row. The list is asserted against the
   * rules by IdentityVocabularyTest, which is where a new rule learns it also needs a word here.
   */
  internal static readonly Dictionary<string, string> WhyWords = new(StringComparer.Ordinal)
  {
    ["R0-local"] = "You",
    ["R1-target"] = "Target",
    ["R1-conflict"] = "Conflict",
    ["R2-who"] = "Who",
    ["R3-chat"] = "Chat",
    ["R3-presence"] = "Raid",
    ["R3-merc"] = "Raid",
    ["R4-spell"] = "Spell",
    ["R5-called"] = "Called",
    ["R5-owner"] = "Owner",
    ["R6-npcdb"] = "NPC list",
    ["R7-graph"] = "Our side",
    ["R7-side"] = "Their side",
    ["R9-charm"] = "Charm",
    ["R10-manual"] = "Chosen",
    ["R13-merc"] = "Target",
    ["R14-article"] = "Name shape",
    ["R14-shape"] = "Name shape",
    ["R15-healed"] = "Healed",
    ["R16-comma"] = "Name title",
    ["R17-selffeed"] = "Drink",
    ["R18-healedpet"] = "Healed pet",
    ["R19-eyeowner"] = "Eye",
    ["R20-petspell"] = "Pet spell",

    // Two spellings the files themselves carry, kept mapped because they are already on disk:
    //   "Override" — what IdentityOverrideStore.LoadAll hands back as a row's source.
    //   "Manual"   — AddRow's word for a verdict read out of the overrides file when no timeline exists yet (this
    //                window opened before the first derive pass). Same fact as R10-manual, one spelling short of it.
    ["Override"] = "Chosen",
    ["Manual"] = "Chosen",
  };
}
