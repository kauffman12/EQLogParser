using EQLogParser;

namespace EQLogParser;

/*
 * The vocabulary a person reads, as opposed to the codes the rules write. The Names window used to print "R10-manual" and
 * "Prior:R7-graph" in a column called Why; that column is now two or three words ("Chosen", "Owner in Name", "Joined Raid")
 * and its tooltip is the proof in one line, so the mapping has to be bounded as carefully as the rules themselves — which
 * means asserting what it covers, like every other closed vocabulary here. A new rule arrives with a word AND with an entry
 * in RuleWords below; either half missing fails here.
 *
 * Three contracts beyond coverage, all from the operator's complaints (docs/DesignNotes.md → "The identity pane holds still"):
 *
 *   - The cell never carries "(earlier)". A borrowed verdict prints the same word as a local one and the tooltip says
 *     "in previous log" — the column is for identifying the proof, the hover is for weighing it.
 *   - Nothing renders blank. A name no rule placed says "Not placed" and hovers as "Nothing identified it".
 *   - The tooltip names EVIDENCE, not sentences: a cast, a count, a source line — which is why R4 keeps the rank inside its
 *     source string and ProofText reads it back out.
 */
[TestClass]
public class IdentityVocabularyTest
{
  [TestInitialize]
  public void Setup() => PlayerRegistry.Instance.Clear();

  [TestCleanup]
  public void Cleanup() => PlayerRegistry.Instance.Clear();

  /*
   * Every source string ClassificationRules and the override store can put on a row, asserted in both directions against
   * IdentityVocabulary.WhyWords: a rule that forgets the table prints a raw code, and an entry no rule produces any more is
   * dead weight that hides the fact that a word went unused.
   */
  private static readonly string[] RuleWords =
  [
    "R0-local", "R1-target", "R1-conflict", "R2-who", "R3-chat",

    /*
     * R3's five presence sightings, split in this round precisely so the column can say WHICH one it read — "Joined Raid"
     * and "Left Raid" are different facts about a name, and one word covering both is what an operator asked about.
     */
    "R3-joinraid", "R3-leaveraid", "R3-joingroup", "R3-leftgroup", "R3-leader", "R3-merc",

    "R4-spell", "R5-companion", "R5-owner", "R6-npcdb", "R7-graph", "R7-side", "R9-charm", "R10-manual",
    "R13-merc", "R14-shape", "R15-healed", "R16-comma", "R17-selffeed", "R18-healedpet", "R19-eyeowner",
    "R20-petspell",
    "R21-spellshape", "R21-spellcast", "R21-spelleffect",

    // "Your guildmate X has completed …" - the client's own guild list speaking (R22). The census that made it a rule:
    // docs/DesignNotes.md -> "What the cold misses actually are".
    "R22-guildmate",

    // A loot line names the taker (R23), replacing an AddVerifiedPlayer call that wrote a store these rules never read.
    "R23-loot",

    /*
     * Not a rule, and it cannot come out of a fixture: PipelineHarness clears PlayerRegistry, so nothing in any test
     * seeds from it. That blind spot is how "RegistrySeed" was printable as a verdict word — the corpus guard below walks
     * real rows from a COLD registry and never sees it. It belongs in this list so the vocabulary tests hold it: a row
     * that came from this app's own saved memory reads "From old Verified List".
     */
    "RegistrySeed",

    /*
     * Also not a rule, and for the same testable reason: no fixture writes the ledger's roster lane, so the corpus run
     * below cannot produce it. It is membership imported from players.txt (IdentityPriorStore.RosterReason), and its one
     * word is "Imported" - never a filename, never a rule code.
     */
    IdentityPriorStore.RosterReason,

    /*
     * And the same again for the ledger's OWNERSHIP lane (IdentityPriorStore.OwnerReason): only a petmapping.txt import
     * writes it, so no cold fixture can produce a row wearing it. Its word is "Pet Map" — the operator's name for the file
     * that held this data before the ledger did — and unlike the roster word it is NOT self-spelled, because "PetMap" on a
     * screen reads like a bug report rather than a provenance.
     */
    IdentityPriorStore.OwnerReason,

    // The two spellings the FILES carry, so they must map even though no rule writes them: what
    // IdentityOverrideStore.LoadAll reports as a row's source ("Override"), and AddRow's word for an override read out of
    // the file before any timeline existed — this window opened before the first derive pass ("Manual").
    "Override", "Manual",
  ];

  /*
   * The one vocabulary entry allowed to display itself verbatim. It is not a rule code — no R-number, no dash, nothing
   * LooksLikeACode accepts — it is the English word identity-priors.txt writes in its Reason field for a name that is on
   * this application's roster, and the cell says the same word the operator would read if they opened the file. Every
   * OTHER key must be translated, which is what the count below holds.
   */
  private static readonly string[] SelfSpelled = [IdentityPriorStore.RosterReason];

  /*
   * The one vocabulary entry allowed to be wider than the cell was sized for, on the operator's own request: "Legacy" sat
   * in that column telling a reader nothing they could act on, so the row now says WHERE the name was remembered from.
   * A rare row buying a longer phrase is worth it; a second entry would mean the column is simply too narrow, and that is
   * a width conversation, not a word list. Anything else that does not fit has to be shortened.
   */
  private static readonly string[] WideWords = ["RegistrySeed"];

  [TestMethod]
  public void EveryRuleWordHasAWordWorthPrinting()
  {
    foreach (var code in RuleWords)
    {
      var word = IdentityVocabulary.WhyWord(code);
      Assert.AreNotEqual(string.Empty, word, $"{code} prints nothing");

      /*
       * The guard is "no RULE CODE reaches the screen", and LooksLikeACode below is what enforces that. Differing from
       * the key is only a proxy for it, and one entry legitimately spells itself: SelfSpelled below, counted against the
       * table at the end so this exemption cannot quietly grow to cover a code somebody forgot words for.
       */
      if (Array.IndexOf(SelfSpelled, code) < 0)
        Assert.AreNotEqual(code, word, $"{code} has no word of its own and reached the screen as its own code");
      else Assert.AreEqual(code, word, "a word allowed to spell itself stopped spelling itself");
      Assert.IsFalse(LooksLikeACode(word), $"{code} mapped to something that is still a rule code: {word}");
      if (Array.IndexOf(WideWords, code) < 0)
        Assert.IsTrue(word.Length <= 14, $"{code} maps to a phrase too wide for the cell: {word}");
    }

    // The other direction: nothing in the table that no rule can produce.
    foreach (var key in IdentityVocabulary.WhyWords.Keys)
      CollectionAssert.Contains(RuleWords, key, $"{key} is mapped but no rule writes it");

    Assert.AreEqual(RuleWords.Length, IdentityVocabulary.WhyWords.Count,
        "the vocabulary and the rules disagree about how many verdict words exist");

    // The self-spelling exemption, counted: one word in the whole vocabulary may equal its own key, and it is this one.
    CollectionAssert.AreEquivalent(WideWords,
        IdentityVocabulary.WhyWords.Where(kv => kv.Value.Length > 14).Select(kv => kv.Key).ToArray(),
        "a word outgrew the cell; the wide-word exemption is one entry and a width conversation, not a list");
    CollectionAssert.AreEquivalent(SelfSpelled,
                                   IdentityVocabulary.WhyWords.Where(e => e.Value == e.Key).Select(e => e.Key).ToList(),
                                   "a provenance reached the screen as its own file token (or a listed word stopped spelling itself)");

    /*
     * Two codes may share a word, but only where they are genuinely the SAME answer written down twice: "Chosen" is one
     * operator verdict that three files spell differently, and "Mercenary" is the same /target finding that two rules can
     * reach. A third such group means one cell now stands for two kinds of proof — which is the failure this guards.
     */
    /*
     * The R21 trio shares one word on purpose: three ways of learning a name is a spell (the damage line named no caster, a
     * casting message, the spell list) and the column answers "what is this thing?", not "which of my data said so" — the
     * distinction rides in the tooltip. Override/Manual/R10-manual are one operator verdict spelled three ways by three files,
     * and R3-merc/R13-merc are the same /target finding two rules reach.
     */
    string[] shared = ["Override", "Manual", "R10-manual", "R3-merc", "R13-merc",
                       "R21-spellshape", "R21-spellcast", "R21-spelleffect"];
    foreach (var group in IdentityVocabulary.WhyWords.GroupBy(kvp => kvp.Value).Where(g => g.Count() > 1))
      Assert.IsTrue(group.All(kvp => shared.Contains(kvp.Key)),
                    $"the word \"{group.Key}\" stands for more than one rule: {string.Join(", ", group.Select(kvp => kvp.Key))}");

    // No value carries a prior marker any more: "(earlier)" belongs to the tooltip.
    CollectionAssert.AllItemsAreNotNull(IdentityVocabulary.WhyWords.Values.ToArray());
    Assert.IsFalse(IdentityVocabulary.WhyWords.Values.Any(v => v.Contains("earlier", StringComparison.OrdinalIgnoreCase)),
                   "a WHY cell that says \"earlier\" pays column width for a fact only a hover needs");
  }

  /*
   * The fixture walks the REAL rules, so this is the assertion that a new rule cannot ship without a word: every verdict
   * the pipeline can put on a row must come out as vocabulary, never as its own code.
   */
  [TestMethod]
  public void NoRuleCodeReachesTheScreenOnTheFixture()
  {
    var path = Path.Combine(AppContext.BaseDirectory, "mini-data", "derive", "rules-fixture.txt");
    Assert.IsTrue(File.Exists(path), $"missing fixture: {path}");

    var run = PipelineHarness.RunFileDerived(path);
    var timeline = new EntityTimeline();
    ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);
    var report = ClassificationReport.Build(timeline, run.Facts, run.HealFacts,
                                            IdentityOverrideStore.Instance, PlayerRegistry.Instance);

    Assert.IsTrue(report.TotalNames > 10, "the fixture produced too few rows to prove anything");

    var wearingItsCode = new List<string>();
    foreach (var row in report.Rows)
    {
      if (string.IsNullOrEmpty(row.Reason)) continue;
      var word = IdentityVocabulary.WhyWord(row.Reason, row.Kind);
      if (word == IdentityVocabulary.CodeOf(row.Reason) || LooksLikeACode(word))
        wearingItsCode.Add($"{row.Name}: {row.Reason} -> {word}");
    }

    Assert.AreEqual(0, wearingItsCode.Count,
        "verdicts reached the screen as their own code:\n" + string.Join("\n", wearingItsCode));

    // The tooltip has to be a sentence fragment about evidence, and never empty — the operator asked for one short line.
    foreach (var row in report.Rows)
    {
      var proof = IdentityVocabulary.ProofText(row.Reason, row.Kind, row.HealedByCasters);
      Assert.IsFalse(string.IsNullOrWhiteSpace(proof), $"{row.Name} would hover as nothing");
      Assert.IsFalse(proof.Contains("\n"), $"{row.Name}'s proof is more than one line: {proof}");
      Assert.IsFalse(LooksLikeACode(proof), $"{row.Name}'s proof still names a rule code: {proof}");
    }

    // And the presence split reached the fixture: this capture's join line reads as a join, not as the old blanket word.
    var raidos = report.Find("Raidos");
    Assert.IsNotNull(raidos, "the fixture's raid-join name is missing");
    Assert.AreEqual("Joined Raid", IdentityVocabulary.WhyWord(raidos!.Reason));
  }

  /*
   * The Type dropdown per row: the window shows one list for the whole vocabulary, and an operator read that as "this pane
   * decides whether my raider is a mercenary". Two kinds are not opinions — Mercenary is what /target reported (typing it
   * onto a raider moves her number to a column nothing else fills), and an eye is not a fighter of any kind — and where the
   * name itself settles the answer, CanOverrule hides the pencil and this list keeps only the way back out. One recognizer
   * for both, so the menu can never offer what the write path refuses.
   */
  [TestMethod]
  public void TheTypeListOffersOnlyWhatANameCanBe()
  {
    static string[] Words(System.Collections.Generic.IReadOnlyList<IdentityVocabulary.TypeOption> options) =>
      [.. options.Select(o => o.Word)];

    var raider = IdentityVocabulary.TypeOptionsFor("Berta", IdentityKind.Player, "R3-joinraid");
    CollectionAssert.Contains(Words(raider), "Player", "the row's own answer must be listed — the popup preselects it");
    CollectionAssert.Contains(Words(raider), "Pet", "an operator may disagree about a summon");
    CollectionAssert.Contains(Words(raider), "Clear claim", "taking a claim back is always reachable");
    CollectionAssert.DoesNotContain(Words(raider), "Mercenary",
                                    "a mercenary is what /target said; typing it onto a raider relocates her damage");

    var merc = IdentityVocabulary.TypeOptionsFor("Stormpaw", IdentityKind.Merc, "R13-merc");
    CollectionAssert.Contains(Words(merc), "Mercenary", "the answer the row already gives is always in its list");
    Assert.IsFalse(merc.Count(o => o.Kind == IdentityKind.Merc) > 1, "one entry per answer, never two");

    // An eye: never a person, never somebody's pet row (that would split its owner's output), NPC or nothing.
    foreach (var kind in new[] { IdentityKind.Npc, IdentityKind.Unknown })
    {
      var eye = IdentityVocabulary.TypeOptionsFor("Eye of Zamul", kind, "R6-npcdb");
      CollectionAssert.Contains(Words(eye), "NPC");
      CollectionAssert.Contains(Words(eye), "Clear claim");
      foreach (var no in new[] { IdentityKind.Player, IdentityKind.Pet, IdentityKind.Merc })
        Assert.IsFalse(eye.Any(o => o.Kind == no), $"an eye may not be set to {no}");
    }

    // A summon whose spelling names its master: decided by the name, so nothing to choose but the way out.
    var pet = IdentityVocabulary.TypeOptionsFor("Sancus`s pet", IdentityKind.Pet, "R5-owner:Sancus");
    CollectionAssert.AreEquivalent(new[] { "Pet", "Clear claim" }, Words(pet));

    // An operator's own claim keeps the whole vocabulary: a wrong click has to stay correctable by another click.
    Assert.AreEqual(IdentityVocabulary.TypeOptions.Length,
                    IdentityVocabulary.TypeOptionsFor("Eye of Zamul", IdentityKind.Player, "R10-manual").Count);
  }

  /*
   * What this application remembers about a name, as opposed to what the capture showed: the two codes no rule writes,
   * which is also why the corpus guard cannot see them (tests run with a cleared registry). The column says "From old Verified List";
   * the tooltip says WHICH store, in words — never a filename and never the internal name of the seed.
   */
  [TestMethod]
  public void WhatTheAppRemembersReadsLegacyAndNamesItsStore()
  {
    Assert.AreEqual("From old Verified List", IdentityVocabulary.WhyWord("RegistrySeed"));
    Assert.AreEqual("On the roster this app saved", IdentityVocabulary.ProofText("RegistrySeed", IdentityKind.Player));

    // A mapped summon names its person — that pair is the whole reason the row exists.
    Assert.AreEqual("In the Pet Map as Sancus's", IdentityVocabulary.ProofText("RegistrySeed:Sancus", IdentityKind.Pet));

    // The roster lane of the ledger is memory too, and it says so in one clause that already contains "carried over" -
    // appending the usual " in previous log" to it would print the same sentence twice.
    Assert.AreEqual("Imported", IdentityVocabulary.WhyWord($"{IdentityVocabulary.PriorPrefix}{IdentityPriorStore.RosterReason}"));
    Assert.AreEqual("Carried over from the roster this app saved",
                    IdentityVocabulary.ProofText($"{IdentityVocabulary.PriorPrefix}{IdentityPriorStore.RosterReason}", IdentityKind.Unknown));

    // Your own character reads alike whichever of the two codes carried it, and hovers as a sentence.
    Assert.AreEqual(IdentityVocabulary.WhyWord("R0-local"), IdentityVocabulary.WhyWord("You"));
    Assert.AreEqual("Your own character", IdentityVocabulary.ProofText("You", IdentityKind.Player));
  }

  [TestMethod]
  public void AnUnknownCodeEchoesItselfRatherThanBeingGuessedAt()
  {
    Assert.AreEqual("R24-somethingelse", IdentityVocabulary.WhyWord("R24-somethingelse"));

    // Ordinal, so near-misses are not folded in: "R15 healed" is not R15-healed and must not read as "Healed".
    Assert.AreEqual("R15 healed", IdentityVocabulary.WhyWord("R15 healed"));
    Assert.AreEqual("r15-healed", IdentityVocabulary.WhyWord("r15-healed"));

    // Same for the tooltip: a code with no words says the code, because inventing a proof would be worse.
    Assert.AreEqual("R24-somethingelse", IdentityVocabulary.ProofText("R24-somethingelse", IdentityKind.Player));
  }

  /*
   * Nothing renders blank. The column used to print an empty cell for an unplaced name and the hover showed nothing at all,
   * which reads as a rendering bug; the request was explicit — never an empty tooltip, "at least repeat the value from the
   * column". A name no rule placed now answers in both places.
   */
  [TestMethod]
  public void NoEvidenceAtAllStillSaysSomething()
  {
    Assert.AreEqual(IdentityVocabulary.NotPlaced, IdentityVocabulary.WhyWord(null, IdentityKind.Unknown));
    Assert.AreEqual(IdentityVocabulary.NotPlaced, IdentityVocabulary.WhyWord(string.Empty, IdentityKind.Unknown));
    Assert.AreEqual("Nothing identified it", IdentityVocabulary.ProofText(null, IdentityKind.Unknown));

    // A kind with no reason at all (a hand-written row before any timeline existed) repeats its type rather than printing
    // the word for an unplaced name.
    Assert.AreEqual("Pet", IdentityVocabulary.WhyWord(null, IdentityKind.Pet));
  }

  /*
   * A borrowed verdict keeps both facts, in the two places that can carry them: the cell prints the SAME word as a local
   * verdict (so the column stays narrow and means one thing), and the tooltip adds "in previous log" (so the reader knows
   * this capture never proved it — the difference between leaving a row alone and re-checking it).
   */
  [TestMethod]
  public void AnEarlierVerdictSaysSoInTheTooltipAndNotInTheCell()
  {
    Assert.AreEqual("Fights Mobs", IdentityVocabulary.WhyWord("Prior:R7-graph"));
    Assert.AreEqual("NPC DB", IdentityVocabulary.WhyWord("Prior:R6-npcdb"));
    Assert.AreEqual("Chosen", IdentityVocabulary.WhyWord("Prior:Override"));

    Assert.AreEqual("It Fights Mobs in previous log", IdentityVocabulary.ProofText("Prior:R7-graph", IdentityKind.Player));
    // No hover names a FILE (the operator cannot open one from here): npcs.txt and spells.txt read as the lists they are.
    Assert.AreEqual("In the NPC DB in previous log", IdentityVocabulary.ProofText("Prior:R6-npcdb", IdentityKind.Npc));

    // The tail survives the prefix, which is what lets an older log's spell verdict still name its cast.
    Assert.AreEqual("R4-spell", IdentityVocabulary.CodeOf("Prior:R4-spell:Boastful Bellow XLVII"));
    Assert.AreEqual("Boastful Bellow XLVII", IdentityVocabulary.DetailOf("Prior:R4-spell:Boastful Bellow XLVII"));
    Assert.AreEqual("Cast Boastful Bellow XLVII in previous log",
                    IdentityVocabulary.ProofText("Prior:R4-spell:Boastful Bellow XLVII", IdentityKind.Player));
  }

  /*
   * R5's owner evidence names the KIND of proof, not the owner: "Owner in Name" says the word `Tuona`s ward` carries its
   * own answer. WHO owns it belongs to the Pet Owners window, and printing it here was one of the reasons the old column
   * needed a second dock's width.
   */
  [TestMethod]
  public void TheOwnerSuffixComesOffTheCell()
  {
    Assert.AreEqual("Owner in Name", IdentityVocabulary.WhyWord("R5-owner:Sancus"));
    Assert.AreEqual("Owner in Name in previous log", IdentityVocabulary.ProofText("Prior:R5-owner:Sancus", IdentityKind.Pet));

    // A called pet names who called it, because that line IS the sighting.
    // The line names the summoner, so the words say what THEY did — "Called by X" belonged to the backwards reading,
    // in which the name was a pet and X was whoever it arrived at.
    Assert.AreEqual("Companion", IdentityVocabulary.WhyWord("R5-companion"));
    Assert.AreEqual("Summoned a Companion", IdentityVocabulary.ProofText("R5-companion", IdentityKind.Player));
  }

  /*
   * The proof line, one case per shape the census can build. These are the strings the operator reads on hover, so they are
   * pinned word for word — including the crowd count (which is what actually answered "why is that mob one of ours?") and
   * the cast rank (which is what answers "why is THIS name a player?").
   */
  [TestMethod]
  public void TheProofLineNamesTheEvidence()
  {
    Assert.AreEqual("From /who", IdentityVocabulary.ProofText("R2-who", IdentityKind.Player));
    Assert.AreEqual("From Chat", IdentityVocabulary.ProofText("R3-chat", IdentityKind.Player));
    Assert.AreEqual("Joined Raid", IdentityVocabulary.ProofText("R3-joinraid", IdentityKind.Player));
    Assert.AreEqual("Left Raid", IdentityVocabulary.ProofText("R3-leaveraid", IdentityKind.Player));
    Assert.AreEqual("Led the Raid", IdentityVocabulary.ProofText("R3-leader", IdentityKind.Player));
    Assert.AreEqual("Cast Spire of Arcanum", IdentityVocabulary.ProofText("R4-spell:Spire of Arcanum", IdentityKind.Player));
    Assert.AreEqual("Cast Hobble of Spirits Snare VI (pet)",
                    IdentityVocabulary.ProofText("R20-petspell:Hobble of Spirits Snare VI", IdentityKind.Pet));
    Assert.AreEqual("Healed by 20 raiders", IdentityVocabulary.ProofText("R15-healed", IdentityKind.Player, 20));
    Assert.AreEqual("Healed by the Raid", IdentityVocabulary.ProofText("R15-healed", IdentityKind.Player),
                    "a heal-based verdict with no walk over the heal stream still has to say what kind of proof it was");
    Assert.AreEqual("You chose NPC", IdentityVocabulary.ProofText("R10-manual", IdentityKind.Npc));
    Assert.AreEqual("The Name of a Spell", IdentityVocabulary.ProofText("R21-spelleffect", IdentityKind.Npc));
    Assert.AreEqual("No Caster in Line", IdentityVocabulary.ProofText("R21-spellshape", IdentityKind.Npc));
    Assert.AreEqual("Casting Message", IdentityVocabulary.ProofText("R21-spellcast", IdentityKind.Npc));
  }

  /*
   * Two kinds of name have exactly one right Type, and a pencil next to them offers three wrong answers. Everything else —
   * including a verdict the operator wrote themselves — keeps it.
   */
  [TestMethod]
  public void ANameThatDecidesItsOwnTypeGetsNoPencil()
  {
    Assert.IsFalse(IdentityVocabulary.CanOverrule("Tuona`s ward", "R5-owner:Tuona"),
                   "the ownership word in the name IS the evidence; the dropdown could only be wrong");
    Assert.IsFalse(IdentityVocabulary.CanOverrule("Sancus`s pet", null),
                   "no verdict yet is not a reason to offer one that contradicts the name");
    Assert.IsFalse(IdentityVocabulary.CanOverrule("Sonic Bang", "Prior:R21-spellcast"),
               "a spell verdict remembered from an older log keeps the pencil off too — CodeOf strips the marker, so IsSpellEffect sees R21 either way");
    Assert.IsTrue(IdentityVocabulary.CanOverrule("Useless", "R7-graph"),
               "a name the graph merely placed keeps its pencil — that verdict is a guess to correct");
    Assert.IsFalse(IdentityVocabulary.CanOverrule("Sonic Bang", "R7-graph"),
               "a name spells.txt answers for gets no pencil even when only the graph placed it: it cannot be a fighter");
    Assert.IsFalse(IdentityVocabulary.CanOverrule("Sonic Bang", "R21-spelleffect"),
                   "a spell effect typed as a Player would put it on the roster");

    Assert.IsTrue(IdentityVocabulary.CanOverrule("Vendorsmith", "R7-graph"));
    Assert.IsTrue(IdentityVocabulary.CanOverrule("Tuona`s ward", "R10-manual"),
                  "an operator's own claim must always be takeable back, whatever the name says");
    Assert.IsTrue(IdentityVocabulary.CanOverrule("Some Hunter", "R15-healed"));
  }

  /*
   * A spell name is a spell, rank and formulation included — because spells.txt carries every one of them as its OWN row
   * ("Curse XVII" at 72133, "Curse XVII Rk. II" at 72134), so the name a post-RoF log prints answers on its own and there is
   * no bare-name fallback to test. A mob and a player must both come back false: this predicate decides who gets a pencil in
   * the Names window, and a false positive there silences a real raid member.
   */
  [TestMethod]
  public void SpellNamesAnswerOnTheNameTheLogPrints()
  {
    Assert.IsTrue(ClassificationRules.SpellNamed("Curse XVII"));
    Assert.IsTrue(ClassificationRules.SpellNamed("Curse XVII Rk. II"), "formulations are their own rows in the data");
    Assert.IsTrue(ClassificationRules.SpellNamed("Fire Trap"));

    Assert.IsFalse(ClassificationRules.SpellNamed("Tuona"));
    Assert.IsFalse(ClassificationRules.SpellNamed("A bixie commander"));
    Assert.IsFalse(ClassificationRules.SpellNamed(null));
    Assert.IsFalse(ClassificationRules.SpellNamed(string.Empty));
  }

  // The TYPE cell reads like a type, never like an enum identifier; "Unknown" covers "no rule placed this name".
  [TestMethod]
  public void TheTypeColumnPrintsWordsNotEnumIdentifiers()
  {
    Assert.AreEqual("Player", IdentityVocabulary.TypeWord(IdentityKind.Player));
    Assert.AreEqual("Pet", IdentityVocabulary.TypeWord(IdentityKind.Pet));
    Assert.AreEqual("Merc", IdentityVocabulary.TypeWord(IdentityKind.Merc));
    Assert.AreEqual("NPC", IdentityVocabulary.TypeWord(IdentityKind.Npc), "the enum's word is \"Npc\"");
    Assert.AreEqual("Unknown", IdentityVocabulary.TypeWord(IdentityKind.Unknown),
                    "a blank reads like a rendering failure; this name simply has no verdict");
  }

  /*
   * The Type dropdown, as data: the whole identity vocabulary in one list — which is exactly what the retired right-click
   * menu was not, since its items had to be remembered and one of them ("clear my claim") almost nobody knew existed.
   */
  [TestMethod]
  public void TheTypeDropdownOffersEachAnswerExactlyOnce()
  {
    var options = IdentityVocabulary.TypeOptions;

    CollectionAssert.AreEqual(
        new[] { "Player", "Pet", "Mercenary", "NPC", "Clear claim" },
        options.Select(o => o.Word).ToArray());

    Assert.AreEqual(options.Length, options.Select(o => o.Kind).Distinct().Count(),
        "two entries writing the same verdict is a dropdown bug a person finds only after clicking");
  }

  /*
   * "Clear claim" carries IdentityKind.Unknown on purpose: ClassificationCommands.ClearVerdict removes the row and lets the
   * capture's own rules speak, which is what "no verdict" means. It is NOT a sixth type — the cell for an unplaced name says
   * "Unknown" and stays grey — and Unknown appears in the list exactly once, as that action.
   */
  [TestMethod]
  public void ClearingAClaimIsTheUnknownKindRatherThanASixthType()
  {
    var clear = IdentityVocabulary.TypeOptions.Single(o => o.Word == "Clear claim");
    Assert.AreEqual(IdentityKind.Unknown, clear.Kind);

    // ...and Unknown is offered once only — as the way to stop having a verdict, never as a verdict to set.
    Assert.AreEqual(1, IdentityVocabulary.TypeOptions.Count(o => o.Kind == IdentityKind.Unknown));
  }

  private static bool LooksLikeACode(string word)
    => word.Length > 2 && word[0] == 'R' && char.IsDigit(word[1]) && word.Contains('-');
}
