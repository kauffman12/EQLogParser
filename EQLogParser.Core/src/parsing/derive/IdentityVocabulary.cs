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
 *   WHY  (the cell)  — one to three words naming the KIND of proof: "Who", "Chat", "Class Spell", "Owner in Pet Name", "Joined Raid".
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
 *   - THE TOOLTIP IS NEVER BLANK. A name nothing placed gets "Not Placed" / "Nothing Identified It"; anything unmapped
 *     repeats its own cell word rather than showing an empty hover (docs: an empty tooltip reads as a broken pane).
 *
 *   - SOME ROWS CANNOT BE OVERRULED, AND SAY SO BY HAVING NO EDIT ICON. `CanOverrule` is where that is decided — a name
 *     whose own spelling proves it ("Tuona`s ward") or whose VERDICT says it is not a fighter at all (an R21 spell shape) has
 *     exactly one right answer, and a pencil offering four wrong ones is worse than no pencil. The test is the evidence, not
 *     the string: a raid member named after a spell keeps their pencil (Strangle, Rune — docs/DesignNotes.md).
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
  /*
   * One entry per word, named, and `TypeOptions` composes them - so a pane that wants a subset (the "Set … as" cascade takes the four
   * kinds and leaves "Spell"/"Clear claim" to the row-aware dropdown) reads the SAME record rather than retyping a string. The law that
   * each word appears exactly once now has arithmetic behind it: there is one object per word in the process.
   */
  internal static readonly TypeOption PlayerOption = new("Player", IdentityKind.Player);
  internal static readonly TypeOption PetOption = new("Pet", IdentityKind.Pet);
  internal static readonly TypeOption MercOption = new("Mercenary", IdentityKind.Merc);
  internal static readonly TypeOption NpcOption = new("NPC", IdentityKind.Npc);
  // Spell is the answer the rules could reach and an operator could not write: a caster-less spell name sitting in a
  // fighter's slot (docs/DesignNotes.md → "A name that equals a spell is not a spell row"). It claims no side, so choosing it
  // neither credits nor vetoes anybody - which is why it is safe to offer on every overrulable row.
  internal static readonly TypeOption SpellOption = new("Spell", IdentityKind.Spell);
  internal static readonly TypeOption ClearClaimOption = new("Clear claim", IdentityKind.Unknown);

  internal static readonly TypeOption[] TypeOptions =
  [
    PlayerOption, PetOption, MercOption, NpcOption, SpellOption, ClearClaimOption,
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
   *     docs/DesignNotes.md → "Breadth of evidence, measured"), so it cannot be a person, a mercenary, or somebody's
   *     summon — and minting a Pet row for it would sit beside that player's real pets and split one person's output.
   *     NPC is the only kind it can be.
   *
   * The current answer is always in the list: the popup preselects it, and a value missing from its own dropdown reads as
   * a blank cell. An operator's OWN claim keeps every entry — a wrong click has to stay correctable by another click.
   * Where the name itself settles the answer (`Sancus`s pet`) or the verdict says it is no fighter at all (an R21 spell
   * shape), CanOverrule already hides the pencil; the list is
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

  /*
   * A Spell row's cell carries DIRECTION, because that is the one question the four kinds could not answer about it: the name is
   * not a fighter, but the capture watched its facts go SOMEWHERE. Two words, closed, and only for Spell — named by WHOSE SIDE
   * CAST it, which is what a reader is actually asking: **NPC Spell** when everything it hit was one of ours (a mob's doT, an
   * environmental effect), **Player Spell** when everything it hit was an NPC. "Our Spell"/"Enemy Spell" were the first attempt
   * and both were wrong in different directions: "our" reads as *mine*, which no evidence supports — the capture shows a spell
   * landing on NPCs, not who cast it — and "enemy" names a relation to the reader instead of the thing on screen.
   *
   * Mixed or unreadable targets answer plain "Spell" rather than guessing, and the tooltip says which half it saw
   * (DirectionPhrase), so declining to name a caster stays visible rather than silent.
   */
  internal const string NpcSpellWord = "NPC Spell";
  internal const string PlayerSpellWord = "Player Spell";

  /// <summary>The TYPE cell for a row: the kind's word, with the caster side when the kind is Spell.</summary>
  internal static string TypeWordFor(IdentityKind kind, int hitsOnRaid, int hitsOnNpcs)
    => kind is not IdentityKind.Spell ? TypeWord(kind)
       : hitsOnRaid > 0 && hitsOnNpcs == 0 ? NpcSpellWord
       : hitsOnNpcs > 0 && hitsOnRaid == 0 ? PlayerSpellWord
       : TypeWord(kind);

  /*
   * What the capture watched a name DO, in the fewest honest words. A closed list of three, decided by WHO the defender's own
   * verdict was at the end of the pass — so the sentence and the identity column cannot contradict each other. A name that hit
   * raid AND NPCs says exactly that rather than being forced onto one side.
   *
   * The word is **NPC**, because that is what every Type cell, dropdown and header in this application says; "monster" and
   * "mob" appear nowhere else on screen and made a hover read like another program's vocabulary (asked directly: "we dont
   * really use 'monsters' anywhere in the app"). Title case throughout: first word always capital, short words like
   * in/and/the/by lower inside a phrase — which is what "In the NPC DB" and "Owner in Pet Name" already looked like.
   */
  /*
   * VICTIM GROUPS, closed at eight sentences. Pets are their OWN group because "players" is not the word for them: Ashenback
   * spends 798 facts on pets, and a raid full of pet lines read as people being beaten. Self is its own too, and it is what
   * makes the set honest - Bjpotratz's entire "raid-side" tally was 61 copies of her own refracted beam landing on her, which
   * this phrase used to render as "Damaged Players". A name whose only victim is itself now says so; a name with real targets
   * reports those and stays quiet about its self-hits (the row's count still holds them).
   *
   * Written as eight literals rather than assembled: the census builds this per name over the whole pool, and a closed list is
   * also what the test can check word for word. Longest is 30 characters - "Damaged Players, Pets and NPCs" - which is the cap.
   */
  internal const string DamagedPlayersPhrase = "Damaged Players";
  internal const string DamagedNpcsPhrase = "Damaged NPCs";
  internal const string DamagedPetsPhrase = "Damaged Pets";
  internal const string DamagedSelfPhrase = "Damaged Itself";
  internal const string DamagedPlayersAndNpcsPhrase = "Damaged Players and NPCs";
  internal const string DamagedPlayersAndPetsPhrase = "Damaged Players and Pets";
  internal const string DamagedNpcsAndPetsPhrase = "Damaged NPCs and Pets";
  internal const string DamagedEveryonePhrase = "Damaged Players, Pets and NPCs";

  /// <summary>The longest sentence this table can hand out; the hover-length law is asserted against it.</summary>
  internal const int MaxClauseLength = 30;

  /*
   * Whether the spell database this application ships knows a SPELL row's name, as its own hover line. Asked directly:
   * "the ones listed as spell maybe also check if they're in the spell database? that seems useful to know" — because the
   * three R21 proofs answer HOW the name was learned (an empty caster slot, a casting message, the list itself) and only
   * the third of them says anything about the data. So a curse whose rank postdates this build's spells.txt now reads
   * `No Caster in Spell Damage` / `Not in the Spell DB` instead of leaving the reader to guess whether the app has ever
   * heard of it, and an operator's own Spell verdict says the same thing about the name they typed.
   *
   * "Spell DB" is the sibling of the existing "In the NPC DB", and no filename appears (the operator cannot open what a
   * hover points at). Only a row that READS as a Spell asks the question — asking it of 4,000 people would add a line to
   * every tooltip to say something nobody asks about them.
   */
  internal const string InSpellDbPhrase = "In the Spell DB";
  internal const string NotInSpellDbPhrase = "Not in the Spell DB";

  /// <summary>Where the fact clause sorts among the claims: above inference and databases, below what was heard or seen
  /// directly — the capture watched it happen, which outranks a guess but not a target frame or an operator's word.</summary>
  internal const int FactClauseRank = 75;

  /*
   * The fact clause for a row's hover, or null when its facts point nowhere decidable. One bit per victim group, so the whole
   * table is one switch: no allocation in a path that runs once per name over a pool of thousands.
   */
  internal static string? DirectionPhrase(int hitsOnPlayers, int hitsOnNpcs, int hitsOnPets = 0, int selfHits = 0)
  {
    var victims = (hitsOnPlayers > 0 ? 1 : 0) | (hitsOnNpcs > 0 ? 2 : 0) | (hitsOnPets > 0 ? 4 : 0);
    return victims switch
    {
      0 => selfHits > 0 ? DamagedSelfPhrase : null,
      1 => DamagedPlayersPhrase,
      2 => DamagedNpcsPhrase,
      4 => DamagedPetsPhrase,
      3 => DamagedPlayersAndNpcsPhrase,
      5 => DamagedPlayersAndPetsPhrase,
      6 => DamagedNpcsAndPetsPhrase,
      _ => DamagedEveryonePhrase
    };
  }

  /*
   * How important a CLAIM is when a hover can only show a few of them (IdentityPriorStore's own lanes and the rule book both
   * appear here). Ordered by how directly the app KNOWS: an operator's word beats a target frame, which beats a voice in chat,
   * which beats what the name did, which beats inference, which beats a database or a guess about grammar. Unknown codes get
   * the middle rank and sort by their own spelling, so an unranked new rule still appears in a stable position rather than
   * jumping between passes.
   */
  /*
   * THE TABLE, NOT A SWITCH — because "every rule says how important it is" is assertable only if the list can be read. The
   * identity-coverage test checks these keys against WhyWord's keys in both directions (a rank without a word, a word without
   * a rank), so a new rule cannot ship that reaches a hover and sorts by accident. The two memory lanes ("Imported" from
   * identity-priors.txt's roster row, "PetMap" from its pet-map heir) sit at the bottom: they phrase through ProofText like any
   * other source, but memory is the weakest thing the app can say about a name.
   */
  internal static readonly IReadOnlyDictionary<string, int> ClaimRanks = new Dictionary<string, int>(StringComparer.Ordinal)
  {
    ["R10-manual"] = 100, ["Manual"] = 100, ["Override"] = 100,
    ["R1-target"] = 90, ["R3-merc"] = 90, ["R13-merc"] = 90, ["R2-who"] = 90, ["R0-local"] = 90, ["R1-conflict"] = 90,
    ["R3-chat"] = 80, ["R3-joinraid"] = 80, ["R3-leader"] = 80, ["R22-guildmate"] = 80,
    ["R3-joingroup"] = 80, ["R3-leaveraid"] = 80, ["R3-leftgroup"] = 80,
    ["R9-charm"] = 70, ["R5-owner"] = 70, ["R5-companion"] = 70, ["R19-eyeowner"] = 70, ["R17-selffeed"] = 70,
    ["R20-petspell"] = 70, ["R4-spell"] = 70, ["R15-healed"] = 70, ["R18-healedpet"] = 70, ["R24-petslot"] = 70,
    ["R7-graph"] = 60, ["R7-side"] = 60,
    ["R6-npcdb"] = 50, ["R14-shape"] = 50, ["R16-comma"] = 50, ["R23-loot"] = 50,
    ["R21-spellshape"] = 50, ["R21-spellcast"] = 50, ["R21-spelleffect"] = 50,
    ["RegistrySeed"] = 20, [IdentityPriorStore.RosterReason] = 20, [IdentityPriorStore.OwnerReason] = 20,
  };

  /// <summary>The neutral rank for a code the table above does not list — see ClaimRanks for why that list is asserted.</summary>
  internal const int UnrankedClaimRank = 45;

  internal static int ClaimRank(string? source)
    => !string.IsNullOrEmpty(source) && ClaimRanks.TryGetValue(CodeOf(source), out var rank) ? rank : UnrankedClaimRank;

  /// <summary>The TYPE cell: the header says Type, so a value has to read like a type and never like an enum identifier.</summary>
  internal static string TypeWord(IdentityKind kind) => kind switch
  {
    IdentityKind.Player => "Player",
    IdentityKind.Pet => "Pet",
    // "Mercenary", not "Merc": the Type dropdown offers that word, and a cell reading "Merc" beside a
    // popup preselected on "Mercenary" looks like two different answers to the same question.
    IdentityKind.Merc => "Mercenary",
    IdentityKind.Npc => "NPC",
    IdentityKind.Spell => "Spell",
    _ => "Unknown",
  };

  /// <summary>The word a row with no verdict at all prints, so the column is never empty and the hover has something to repeat.</summary>
  internal const string NotPlaced = "Not Placed";

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

  /// <summary>Whether this source is the one R21 proof that IS a spell-database lookup (R21-spelleffect). Its hover clause
  /// already states membership, so the report does not print a second line saying the same thing.</summary>
  internal static bool IsSpellListClaim(string? source) => CodeOf(source) == "R21-spelleffect";

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

    /*
     * The roster lane of identity-priors.txt, answered before the switch because it is the one proof whose own words
     * already say "carried over": appending " in previous log" to it would print the same sentence twice. No filename
     * (the operator cannot open what this window points at) and no rule code, because no rule wrote it.
     */
    if (code == IdentityPriorStore.RosterReason)
      return "On the Saved Player Roster";

    // The ledger's ownership lane (petmapping.txt's heir): same shape as the roster row above — memory rather than a rule,
    // and the sentence has to say WHICH of the two files' worth of memory this is, because a mapping can outlive both the
    // pet and the claim that it was ever a pet.
    if (code == IdentityPriorStore.OwnerReason)
      return "On the Saved Pet Map";

    var text = code switch
    {
      "R0-local" => "Your Own Name",
      "R1-target" => "From /target",
      "R1-conflict" => "Targeted as NPC and Player",
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
      // The name itself is the evidence, and the phrase now says WHICH name: the pet's (asked directly: "instead of
      // Owner in Name can the tooltip say Owner in Pet Name to be more clear?").
      "R5-owner" => "Owner in Pet's Name",
      // npcs.txt is the game's creature database this app ships, and "NPC DB" is what an operator calls it.
      "R6-npcdb" => "In the NPC DB",
      // No "It " prefix (every other clause starts with what the name did) and no "mobs": the app's word is NPC.
      // R7-graph counts edges in both directions and lands on whichever side dominates; R7-side is the narrower
      // sighting that it swings at raid members at all.
      "R7-graph" => "Attacks NPCs",
      "R7-side" => "Attacks Players",
      // The capture's own combat line named it: `X has been charmed.` "Charm Window" was the mechanism's name — jargon
      // a reader has to be taught before the hover means anything.
      "R9-charm" => "Reported Charmed",
      "R10-manual" or "Manual" or "Override" => $"You Chose {TypeWord(kind)}",
      "R13-merc" => "From /target as Mercenary",
      // Which shape? The article is the game's own "this is a thing" marker, so the hover says where it looked
      // instead of gesturing at "shape" (same reason R21 stopped saying "No Caster in Line").
      "R14-shape" => "Name Begins With an Article",
      "R15-healed" => healedByCasters > 0 ? $"Healed by {healedByCasters:N0} Raiders" : "Healed by Players",
      // `Ghulel, Tier Doomservant` — a comma with a title behind it, which is how the game writes a named mob.
      "R16-comma" => "Name Carries a Title",
      // The client's own guild list, not a guess from behaviour: see PreLineParser's census.
      "R23-loot" => "Took Loot From a Corpse",
      /*
       * The line is an ACHIEVEMENT announcement; the guild part is only WHY it can be trusted (the client writes it from its
       * own guild list), so neither the cell nor the hover mentions it. Asked directly twice, the second time about this very
       * clause: "i didnt want guildmate mentioned. thats not important. just call it Achievement Message." Nothing about the
       * rule counts, matches or remembers guilds beyond the one line it reads, and a word implying a relationship nobody
       * measured invites the question ("how many guildmates?") this pane cannot answer. The CODE stays `R22-guildmate` — it is
       * a machine word in the ledger and the log, where renaming would orphan rows already written.
       */
      "R22-guildmate" => "Achievement Message",
      "R17-selffeed" => "Ate or Drank",
      // Not "our": a capture says who healed whom, not whose side the reader is on.
      "R18-healedpet" => "Healed by a Player Pet",
      "R19-eyeowner" => "Hit Their Own Eye",
      // No spell name: the detail was the longest string on the pane for information nobody checks ("which pet spell?"),
      // and the cell word already says what it is. Asked directly: "Pet Cast Hobble of Spirits can just say Cast Pet Spell".
      "R20-petspell" => "Cast Pet Spell",
      // The SPELL is what the line proves, so the clause says who it can hit rather than naming the spell (spell names
      // are the longest strings this pane has ever carried, and nobody verifies them).
      "R24-petslot" => "Hit by a Pet-Only Spell",

      /*
       * R21's three proofs, phrased so the CELL can carry the plain kind word ("Spell") and the hover carry the
       * difference. "No Caster in Line" was true of every damage line ever parsed — the informative half is WHICH slot
       * was empty, so it says where it looked (asked directly: "instead of 'No Caster in Line' be more specific").
       */
      "R21-spellshape" => "No Caster in Spell Damage",
      "R21-spellcast" => "Seen Being Cast",
      "R21-spelleffect" => "Name of a Known Spell",

      /*
       * The two codes no rule writes; both mean "this application remembered it", and the hover is where that gets
       * specific. Neither names a file (the operator cannot open what a hover points at), and the phrases are honest about
       * scope: players.txt and petmapping.txt are the ONLY two stores this app persists, so the verified-player branch
       * says the list it came from — "From old Verified List", the words an operator uses for players.txt — while a mapped
       * summon says it is the pet map and names the person in the detail. Verified pets and mercenaries are learned while a
       * log is open and never reach disk at all, so they are never described as saved state.
       */
      "RegistrySeed" => detail is null ? "From the Old Verified List" : $"In the Pet Map as {detail}'s",
      "You" => "Your Own Character",
      _ => code.Length > 0 ? code : kind == IdentityKind.Unknown ? "Nothing Identified It" : TypeWord(kind),
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

    /*
     * What settles a Type is the EVIDENCE that produced the verdict, not whether the name matches a spells.txt row. R21 is
     * refused above; a name carrying an owner word inside it ("Tuona`s ward") is refused here, because that spelling IS the
     * evidence and the dropdown could only be wrong.
     *
     * A name that merely equals a spell keeps its pencil. Two raid members in eqlog_Kizant_xegony-2.txt are exactly that -
     * "Strangle" (9,346 attack facts) and "Rune" (19,339), both verdicted Player through R4-spell, both names the shipped
     * spells.txt answers for (level 128 / mask 8192 and level 126 / mask 8192 - player-castable rows) - and the shape test
     * this replaced took their pencil away: a mis-claim on a real person could not be corrected from this window at all, the
     * one failure mode every other rule here is written to avoid. A behaviour verdict (graph, heals, /who, chat, a cast) is
     * somebody ACTING, so it stays correctable even when the string is also a spell.
     */
    return ClassificationRules.OwnerInName(name) is null;
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
    /*
     * "Class Spell", not "Spell": R21's verdict IS a spell — it is one — and two rows reading the same Why word for
     * opposite reasons ("this cast a bard rank, so it is a person" vs "its name is a spell, so it is not") is the
     * ambiguity worth avoiding. The tooltip names the actual cast either way.
     */
    ["R4-spell"] = "Class Spell",
    // The summon arrived at THEM: the line names the summoner, so the word says whose companion it was.
    ["R5-companion"] = "Companion",

    // The proof is the name's own spelling: `Tuona`s ward` carries its owner in it. "Owner" alone asked a question
    // ("what do you mean owner?") that this answer settles without a second column.
    ["R5-owner"] = "Owner in Pet Name",
    ["R6-npcdb"] = "NPC DB",

    // The attack graph says which side a name fights on, so the words count who got hit rather than asserting loyalty —
    // and the noun is NPC, the word this application uses everywhere else. "Mobs" lived only in these two cells.
    ["R7-graph"] = "Attacks NPC",
    ["R7-side"] = "Attacks Player",
    ["R9-charm"] = "Charmed",
    ["R10-manual"] = "Chosen",
    ["R13-merc"] = "Mercenary",
    ["R14-shape"] = "NPC Name",
    ["R15-healed"] = "Healed",
    ["R16-comma"] = "Titled Name",
    ["R23-loot"] = "Looted",
    // **Achievement**, not "Guildmate": what happened is that an achievement was announced. Nothing about the rule counts,
    // matches or remembers guilds beyond the one line it reads, and a cell word implying a relationship nobody measured
    // invites exactly the question ("how many guildmates?") this pane cannot answer.
    ["R22-guildmate"] = "Achievement",
    ["R17-selffeed"] = "Ate or Drank",
    ["R18-healedpet"] = "Pet Healed It",
    ["R19-eyeowner"] = "Hit Own Eye",
    ["R20-petspell"] = "Pet Spell",
    ["R24-petslot"] = "Pet Only Spell",

    /*
     * The three ways R21 learns a name is a spell — the damage line that named no caster, a casting message, and the spell
     * list (see ClassificationRules.ApplySpellEffects). ONE cell word for all three because the operator's question is
     * "what is this thing?", not "which of my data said so?": a spell is not a creature and must never read as a person.
     */
    /*
     * ONE cell word for all three, and it is the kind word the Type column already uses. "A Spell" read as a sentence
     * fragment beside rows that said plain "Spell" (asked directly: "it's weird that sometimes there's A Spell and Spell").
     */
    ["R21-spellshape"] = "Spell",
    ["R21-spellcast"] = "Spell",
    ["R21-spelleffect"] = "Spell",

    /*
     * Not a rule: this application's own memory, written before the capture said anything. A cold registry cannot
     * produce it in a test, which is exactly how this code reached the screen as its own name — so it is listed here and
     * in the test's word list next to the rules. The CELL stays short ("Legacy": the column is 4,000 rows deep and one
     * wide word makes the whole list ragged); where the name was remembered FROM is the tooltip's job — the old Verified
     * Players list, which is what players.txt was called on screen, or the pet map for a summon. A name that only LOOKS
     * like one of ours because an old session said so is exactly the case where that difference matters, and it is a
     * hover-length question, not a column-width one.
     */
    ["RegistrySeed"] = "Legacy",

    /*
     * Also not a rule, and also unreachable from a cold test (nothing in a fixture writes the roster lane), which is why
     * it is listed here and in the test's word list beside the rules: an unmapped code echoes itself on screen, and
     * "Imported" echoing as "Imported" would read as a bug report rather than as where the name came from.
     */
    [IdentityPriorStore.RosterReason] = "Saved Roster",

    /*
     * The ledger's ownership lane, and the same argument as the row above: no fixture writes it, so the corpus run beside
     * these tests cannot reach it, and an unmapped code echoes itself on screen. "Pet Map" is what the operator called the
     * file; the filename itself never appears on screen (docs/DesignNotes.md -> "What this application remembers").
     */
    [IdentityPriorStore.OwnerReason] = "Pet Map",

    // Two spellings the files themselves carry, kept mapped because they are already on disk:
    //   "Override" — what IdentityOverrideStore.LoadAll hands back as a row's source.
    //   "Manual"   — AddRow's word for a verdict read out of the overrides file when no timeline exists yet.
    ["Override"] = "Chosen",
    ["Manual"] = "Chosen",
  };
}
