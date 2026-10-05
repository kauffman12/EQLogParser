/*
 * Annotations only, no null-flow analysis: the project builds with Nullable=disable, and this API speaks in optional
 * strings/kinds because a code legitimately has no cell word, no proof and no detail beside it. Stating that is not the
 * same as switching on warnings across code written before nullable existed.
 */
#nullable enable annotations

namespace EQLogParser;

/*
 * The words this application says out loud about identity, kept apart from the codes that produce them.
 *
 * Internally an identity is an `IdentityKind` plus a source string ("R15-healed", "R4-spell:Curse XVII", "Prior:R2-who").
 * Those strings are right where they belong — in a log line, in the ledger, in a diff of someone's overrides file — and
 * wrong everywhere a person reads them. Two tables live here:
 *
 *   WHY  (the cell)  — one to three words naming the KIND of proof: "Who", "Chat", "Cast", "Owner in Name", "Joined Raid".
 *   PROOF(the tooltip) — the same idea with its one detail: "From /who", "Cast Spire of Arcanum", "Healed by 20 raiders".
 *
 * Both are as short as they are because this is a 4,000-row list: a sentence per row made the pane read like a report and
 * the WHY column, at 256 fixed pixels, was the widest thing on screen (docs/DesignNotes.md).
 *
 * Four rules about the mapping itself:
 *
 *   - AN UNKNOWN CODE ECHOES ITSELF. A rule added without a word here shows up as its own name — legible, and obvious
 *     enough to get a word written. Guessing ("R24-whatever" → "Evidence") would file a new kind of proof under an old
 *     meaning, and a wrong provenance is worse than jargon: the operator acts on it. `IdentityVocabularyTest` checks the
 *     list in both directions and re-runs the rules fixture so no verdict reaches the screen wearing its code.
 *
 *   - A PRIOR IS NOT A DIFFERENT WORD, IT IS THE SAME PROOF ON OLDER EVIDENCE. The column used to print "Healed (earlier)",
 *     which cost width to say what the tooltip already said, and "Our side (earlier)" was two mysteries stacked. So the
 *     cell stays bare and the tooltip ends in "in previous log" — "From Chat in previous log", "Cast Curse XVII in
 *     previous log". The ledger stores the rule (and its detail: the cast, the owner) with the verdict, which is what
 *     lets a borrowed answer name the proof it borrowed.
 *
 *   - THE TOOLTIP IS NEVER BLANK. A name nothing placed gets "Not placed" / "Nothing identified it"; anything unmapped
 *     repeats its own cell word rather than showing an empty hover (docs: an empty tooltip reads as a broken pane).
 *
 *   - SOME ROWS CANNOT BE OVERRULED, AND SAY SO BY HAVING NO EDIT ICON. `CanOverrule` is where that is decided — a name
 *     whose own spelling proves it ("Tuona`s ward") or that is not a fighter at all (a spell effect) has exactly one
 *     right answer, and a pencil offering four wrong ones is worse than no pencil.
 */
internal static class IdentityVocabulary
{
  /// <summary>One entry in the Type dropdown: the word an operator reads and the verdict it writes.</summary>
  internal sealed record TypeOption(string Word, IdentityKind Kind);

  /*
   * Everything a Type cell offers, in one list — the whole identity vocabulary at a glance, which is what the old
   * right-click menu could not do (five items had to be remembered, and "take my claim back" was not even among them).
   *
   * "Clear claim" carries IdentityKind.Unknown because that is exactly what ClassificationCommands.ClearVerdict means —
   * remove the operator's row (and this server's memory of the name) and let this capture's own rules show through. Each
   * word appears EXACTLY ONCE: the retired menu listed NPC twice, and a dropdown with two entries for one answer is a bug
   * a person notices only after clicking.
   */
  internal static readonly TypeOption[] TypeOptions =
  [
    new("Player", IdentityKind.Player),
    new("Pet", IdentityKind.Pet),
    new("Mercenary", IdentityKind.Merc),
    new("NPC", IdentityKind.Npc),
    new("Clear claim", IdentityKind.Unknown),
  ];

  /*
   * What THIS row's Type dropdown offers: the part of the vocabulary a name can actually be.
   *
   * Two trims, each one something the capture's own grammar decides and an opinion cannot:
   *
   *   - **Mercenary is not a verdict you can put on a name.** A mercenary is what /target reported (R3-merc/R13-merc);
   *     typing "Mercenary" onto a raider moves her number off the player column and onto a column nothing else fills,
   *     and no file backs the claim afterwards. A row that already reads Mercenary keeps the entry — the popup opens on
   *     the current answer — and taking that verdict back is "Clear claim", which is where it always lived.
   *   - **An eye is not a fighter of any kind.** `Eye of Zamul` never acts (ClassificationRules.EyeSummonOwnerInName;
   *     docs/combat-mirror-design.md → "An eye is not a combatant"), so it cannot be a person, a mercenary, or somebody's
   *     summon — and minting a Pet row for it would sit beside that player's real pets and split one person's output.
   *     NPC is the only kind it can be.
   *
   * The current answer is always in the list: the popup preselects it, and a value missing from its own dropdown reads as
   * a blank cell. An operator's OWN claim keeps every entry — a wrong click has to stay correctable by another click.
   * Where the name itself settles the answer (`Sancus`s pet`, a spell) CanOverrule already hides the pencil; the list is
   * trimmed by that same call rather than by a second opinion, because two rules deciding one menu is how a pane ends up
   * offering what its own write path then refuses.
   */
  internal static IReadOnlyList<TypeOption> TypeOptionsFor(string? name, IdentityKind kind, string? source)
  {
    var code = CodeOf(source);
    if (code is "R10-manual" or "Manual" or "Override") return TypeOptions;

    var eye = name is { Length: > 0 } eyeName && ClassificationRules.EyeSummonOwnerInName(eyeName) is not null;
    var decided = !CanOverrule(name, source);

    var list = new List<TypeOption>(TypeOptions.Length);
    foreach (var option in TypeOptions)
    {
      if (option.Kind == kind || option.Kind == IdentityKind.Unknown) { list.Add(option); continue; }
      if (eye && option.Kind != IdentityKind.Npc) continue;   // an eye has exactly one kind it can be: NPC
      if (decided || option.Kind == IdentityKind.Merc) continue;
      list.Add(option);
    }
    return list;
  }

  /// <summary>The TYPE cell: the header says Type, so a value has to read like a type and never like an enum identifier.</summary>
  internal static string TypeWord(IdentityKind kind) => kind switch
  {
    IdentityKind.Player => "Player",
    IdentityKind.Pet => "Pet",
    IdentityKind.Merc => "Merc",
    IdentityKind.Npc => "NPC",
    _ => "Unknown",
  };

  /// <summary>The word a row with no verdict at all prints, so the column is never empty and the hover has something to repeat.</summary>
  internal const string NotPlaced = "Not placed";

  /// <summary>Stripped from a source that arrived as "Prior:&lt;rule&gt;" — an older log on this server said it.</summary>
  internal const string PriorPrefix = "Prior:";

  /// <summary>The reason tail: "R4-spell:Curse XVII" → "Curse XVII", "R5-owner:Sancus" → "Sancus", else null.</summary>
  internal static string? DetailOf(string? source)
  {
    if (string.IsNullOrEmpty(source)) return null;
    var s = source.StartsWith(PriorPrefix, StringComparison.Ordinal) ? source[PriorPrefix.Length..] : source;
    var colon = s.IndexOf(':');
    return colon > 0 && colon + 1 < s.Length ? s[(colon + 1)..] : null;
  }

  /// <summary>The code with its prefix and tail removed, and any prior marker taken off ("Prior:R4-spell:X" → "R4-spell").</summary>
  internal static string CodeOf(string? source)
  {
    if (string.IsNullOrEmpty(source)) return string.Empty;
    var s = source.StartsWith(PriorPrefix, StringComparison.Ordinal) ? source[PriorPrefix.Length..] : source;
    var colon = s.IndexOf(':');
    return colon > 0 ? s[..colon] : s;
  }

  /// <summary>
  /// Whether a source is one of R21's three spell verdicts (R21-spellshape / R21-spellcast / R21-spelleffect). One
  /// recognizer for the whole family, shared by CanOverrule and the report: three codes mean one answer to the operator,
  /// and a fourth spell code must not quietly grow a pencil because somebody compared against one spelling.
  /// </summary>
  internal static bool IsSpellEffect(string? source) => CodeOf(source).StartsWith("R21-", StringComparison.Ordinal);

  /// <summary>True when this verdict came from an earlier log on this server rather than from the open capture.</summary>
  internal static bool IsPrior(string? source) =>
    !string.IsNullOrEmpty(source) && source.StartsWith(PriorPrefix, StringComparison.Ordinal);

  /*
   * The WHY cell: a few words naming the KIND of proof. Never "(earlier)" — that half of the message belongs to the
   * tooltip, and paying column width for it made every row wider to say something only a hover needs (asked for directly:
   * "i dont want to see (earlier) in the why column").
   */
  internal static string WhyWord(string? source, IdentityKind kind = IdentityKind.Unknown)
  {
    var code = CodeOf(source);
    if (code.Length == 0) return kind == IdentityKind.Unknown ? NotPlaced : TypeWord(kind);
    return WhyWords.TryGetValue(code, out var known) ? known : code;
  }

  /*
   * The WHY tooltip: the proof in ONE line, with the detail that makes it checkable — the cast, the crowd, whose name.
   * " in previous log" is appended when the answer was borrowed from an older capture, which is the one thing a person
   * needs to weigh it, and the phrase is spelled out instead of abbreviated because the hover has room and the reader
   * should not have to learn a notation.
   *
   * `healedByCasters` comes from Row (it is a count the census had to walk the heal stream for); the tail comes from the
   * source itself, which is why R4 keeps the cast in the code it asserts and the ledger persists that string verbatim.
   */
  internal static string ProofText(string? source, IdentityKind kind, int healedByCasters = 0)
  {
    var code = CodeOf(source);
    var detail = DetailOf(source);
    var text = code switch
    {
      "R0-local" => "Your own name",
      "R1-target" => "From /target",
      "R1-conflict" => "Targeted as both NPC and player",
      "R2-who" => "From /who",
      "R3-chat" => "From Chat",
      "R3-joinraid" => "Joined Raid",
      "R3-leaveraid" => "Left Raid",
      "R3-joingroup" => "Joined Group",
      "R3-leftgroup" => "Left Group",
      "R3-leader" => "Led the Raid",
      "R3-merc" => "From /target as Mercenary",
      "R4-spell" => detail is null ? "Cast a Class Spell" : $"Cast {detail}",
      // The companion line names the summoner (MiscLineParser's census), so the proof says what they did.
      "R5-companion" => "Summoned a Companion",
      "R5-owner" => "Owner in Name",
      "R6-npcdb" => "On the NPC List",
      "R7-graph" => "It Fights NPCs",
      "R7-side" => "It Attacks Raid",
      "R9-charm" => "Charm Window",
      "R10-manual" or "Manual" or "Override" => $"You chose {TypeWord(kind)}",
      "R13-merc" => "From /target as Mercenary",
      "R14-shape" => "NPC Name Shape",
      "R15-healed" => healedByCasters > 0 ? $"Healed by {healedByCasters:N0} raiders" : "Healed by the Raid",
      "R16-comma" => "Titled Name",
      "R17-selffeed" => "Drank or Ate",
      "R18-healedpet" => "Healed by our Pets' Owner",
      "R19-eyeowner" => "Hit the Eye named after them",
      "R20-petspell" => detail is null ? "Pet Spell" : $"Cast {detail} (pet)",
      "R21-spellshape" => "No Caster in Line",
      "R21-spellcast" => "Casting Message",
      "R21-spelleffect" => "The Name of a Spell",

      /*
       * The two codes no rule writes; both mean "this application remembered it". Neither names a file, and the phrases
       * are honest about scope: players.txt and petmapping.txt are the ONLY two stores this app persists, so "the roster
       * this app saved" covers saved names and "the Pet Map" covers a mapped summon (whose person arrives in the detail).
       * Verified pets and mercenaries are learned while a log is open and never reach disk at all.
       */
      "RegistrySeed" => detail is null ? "On the roster this app saved" : $"In the Pet Map as {detail}'s",
      "You" => "Your own character",
      _ => code.Length > 0 ? code : kind == IdentityKind.Unknown ? "Nothing identified it" : TypeWord(kind),
    };

    return IsPrior(source) ? $"{text} in previous log" : text;
  }

  /*
   * Whether a Type pencil belongs on this row. Two kinds of name have exactly one right answer, and offering the other
   * three invites an operator to break something they cannot undo by clicking:
   *
   *   - A POSSESSIVE SUMMON (`Tuona`s ward`). The ownership word IS the evidence — R5 reads it off the name and asserts
   *     Pet at Certain — so the dropdown's Pet/NPC/Player choices are all wrong and "Clear claim" only puts the row back
   *     to Unknown for one derive.
   *   - A SPELL EFFECT (a name that is a damaging spell, or that only ever appears as a spell attacker). It is not a
   *     creature; typing it as a Player would put it on the roster.
   *
   * An operator verdict always counts as overrulable — taking a claim back is the whole point of the row's Clear entry.
   */
  internal static bool CanOverrule(string? name, string? source)
  {
    var code = CodeOf(source);

    // An operator's own claim always keeps its pencil: taking it back has to remain possible even on a name whose
    // spelling says otherwise, or a wrong click would be permanent.
    if (code is "R10-manual" or "Manual" or "Override") return true;
    if (IsSpellEffect(source)) return false;
    if (string.IsNullOrEmpty(name)) return true;

    // One recognizer for "is this a spell", shared with R21 (ClassificationRules.SpellNamed): a rule and the words shown
    // for its verdict must never disagree about what counts as a spell, or the pane hides a pencil on rows the rules are
    // still willing to call people.
    return ClassificationRules.OwnerInName(name) is null && !ClassificationRules.SpellNamed(name);
  }

  /*
   * Every source string ClassificationRules (and the override store) can put on a row. IdentityVocabularyTest asserts
   * this list in both directions against the rules fixture, which is where a new rule learns it needs a word here.
   */
  internal static readonly Dictionary<string, string> WhyWords = new(StringComparer.Ordinal)
  {
    ["R0-local"] = "You",
    ["R1-target"] = "Target",
    ["R1-conflict"] = "Conflict",
    ["R2-who"] = "Who",
    ["R3-chat"] = "Chat",

    /*
     * R3's presence evidence, split by WHICH line said it. It used to be one code ("R3-presence") and one word ("Raid"),
     * which the operator read as "Raid — what does that mean?": a join, a leave, a group join and a raid-leader flag are
     * four different sightings, and only one of them means "this person is in my raid right now" (asked for directly:
     * "if it's from join or leave messages say Joined Raid. Left Raid").
     */
    ["R3-joinraid"] = "Joined Raid",
    ["R3-leaveraid"] = "Left Raid",
    ["R3-joingroup"] = "Joined Group",
    ["R3-leftgroup"] = "Left Group",
    ["R3-leader"] = "Raid Leader",
    ["R3-merc"] = "Mercenary",

    // The tail of R4-spell is the cast that earned it, so the tooltip can name it ("Cast Curse XVII") — including from
    // the ledger, which stores this string verbatim and therefore keeps the detail across logs.
    ["R4-spell"] = "Spell",
    // The summon arrived at THEM: the line names the summoner, so the word says whose companion it was.
    ["R5-companion"] = "Companion",

    // The proof is the name's own spelling: `Tuona`s ward` carries its owner in it. "Owner" alone asked a question
    // ("what do you mean owner?") that this answer settles without a second column.
    ["R5-owner"] = "Owner in Name",
    ["R6-npcdb"] = "NPC List",

    // The attack graph says which side a name fights on, so the words say exactly that rather than "Our side", which
    // could be read as an assertion about loyalty instead of a count of who got hit.
    ["R7-graph"] = "Fights NPCs",
    ["R7-side"] = "Attacks Raid",
    ["R9-charm"] = "Charmed",
    ["R10-manual"] = "Chosen",
    ["R13-merc"] = "Mercenary",
    ["R14-shape"] = "NPC Name",
    ["R15-healed"] = "Healed",
    ["R16-comma"] = "Titled Name",
    ["R17-selffeed"] = "Drinking",
    ["R18-healedpet"] = "Our Pet",
    ["R19-eyeowner"] = "Own Eye",
    ["R20-petspell"] = "Pet Spell",

    /*
     * The three ways R21 learns a name is a spell — the damage line that named no caster, a casting message, and the spell
     * list (see ClassificationRules.ApplySpellEffects). ONE cell word for all three because the operator's question is
     * "what is this thing?", not "which of my data said so?": a spell is not a creature and must never read as a person.
     */
    ["R21-spellshape"] = "A Spell",
    ["R21-spellcast"] = "A Spell",
    ["R21-spelleffect"] = "A Spell",

    /*
     * Not a rule: this application's own memory, written before the capture said anything. A cold registry cannot
     * produce it in a test, which is exactly how this code reached the screen as its own name — so it is listed here and
     * in the test's word list next to the rules. "Legacy" is the operator's word for it; what sits underneath are the
     * roster this app saves and the pet map, and the tooltip says which one (a name that only LOOKS like one of ours
     * because an old session said so is the case where the difference matters).
     */
    ["RegistrySeed"] = "Legacy",

    // Two spellings the files themselves carry, kept mapped because they are already on disk:
    //   "Override" — what IdentityOverrideStore.LoadAll hands back as a row's source.
    //   "Manual"   — AddRow's word for a verdict read out of the overrides file when no timeline exists yet.
    ["Override"] = "Chosen",
    ["Manual"] = "Chosen",
  };
}
