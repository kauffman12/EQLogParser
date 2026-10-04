using EQLogParser;

namespace EQLogParser;

/*
 * The vocabulary a person reads, as opposed to the codes the rules write. The Names window used to print "R10-manual"
 * and "Prior:R7-graph" in a column called Why; that column is now two words ("Chosen", "Our side (earlier)"), so the
 * mapping has to be bounded as carefully as the rules themselves — which means asserting what it covers, like every
 * other closed vocabulary here (a new rule arrives with a word, not with a code that reaches the screen).
 */
[TestClass]
public class IdentityVocabularyTest
{
  [TestInitialize]
  public void Setup() => PlayerRegistry.Instance.Clear();

  [TestCleanup]
  public void Cleanup() => PlayerRegistry.Instance.Clear();

  /*
   * Every source string ClassificationRules and the override store can put on a row. A new rule adds its word to
   * IdentityVocabulary AND to this list: forgetting the table leaves the column printing a raw code, and forgetting this
   * list leaves a stale entry nobody produces any more — both fail here, in both directions.
   */
  private static readonly string[] RuleWords =
  [
    "R0-local", "R1-target", "R1-conflict", "R2-who", "R3-chat", "R3-presence", "R3-merc", "R4-spell",
    "R5-called", "R5-owner", "R6-npcdb", "R7-graph", "R7-side", "R9-charm", "R10-manual",
    "R13-merc", "R14-article", "R14-shape", "R15-healed", "R16-comma", "R17-selffeed",
    "R18-healedpet", "R19-eyeowner", "R20-petspell",

    // The two spellings the FILES carry, so they must map even though no rule writes them: what
    // IdentityOverrideStore.LoadAll reports as a row's source ("Override"), and AddRow's word for an override read out of
    // the file before any timeline existed — this window opened before the first derive pass ("Manual").
    "Override", "Manual",
  ];

  [TestMethod]
  public void EveryRuleWordHasAWordWorthPrinting()
  {
    foreach (var code in RuleWords)
    {
      var word = IdentityVocabulary.WhyWord(code);
      Assert.AreNotEqual(string.Empty, word, $"{code} prints nothing");
      Assert.AreNotEqual(code, word, $"{code} has no word of its own and reached the screen as its own code");
      Assert.IsFalse(LooksLikeACode(word), $"{code} mapped to something that is still a rule code: {word}");
    }

    // The other direction: nothing in the table that no rule can produce.
    foreach (var key in IdentityVocabulary.WhyWords.Keys)
    {
      CollectionAssert.Contains(RuleWords, key,
          $"the WHY table holds \"{key}\", which no rule writes - a stale word hides that the code is gone");
    }
  }

  /*
   * THE REAL CORPUS CHECK. Over the rules fixture — which exercises target frames, join lines, chat, spell families,
   * ownership, name shapes and the NPC database in one file — no row may reach the screen still wearing its code. This
   * is the assertion that catches a rule nobody thought to write a word for, because it asks the rules what they
   * actually produce rather than asking a list what somebody remembered.
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

    foreach (var row in report.Rows)
    {
      if (string.IsNullOrEmpty(row.Reason)) continue;   // no evidence at all: no text is the honest answer

      var why = IdentityVocabulary.WhyWord(row.Reason);
      Assert.AreNotEqual(row.Reason, why, $"{row.Name}: \"{row.Reason}\" reached the WHY column untranslated");
      Assert.IsFalse(LooksLikeACode(why), $"{row.Name}: WHY reads \"{why}\", which is still a rule code");
    }
  }

  /*
   * A code nobody wrote a word for echoes ITSELF. The alternative — a fallback like "Evidence" — would file a new kind of
   * proof under an old meaning, and provenance is the one thing this window exists to be honest about.
   */
  [TestMethod]
  public void AnUnknownCodeEchoesItselfRatherThanBeingGuessedAt()
  {
    Assert.AreEqual("R21-somethingelse", IdentityVocabulary.WhyWord("R21-somethingelse"));

    // Ordinal, so near-misses are not folded in: "R15 healed" is not R15-healed and must not read as "Healed".
    Assert.AreEqual("R15 healed", IdentityVocabulary.WhyWord("R15 healed"));
    Assert.AreEqual("r15-healed", IdentityVocabulary.WhyWord("r15-healed"));
  }

  [TestMethod]
  public void NoEvidenceAtAllPrintsNoText()
  {
    Assert.AreEqual(string.Empty, IdentityVocabulary.WhyWord(null));
    Assert.AreEqual(string.Empty, IdentityVocabulary.WhyWord(string.Empty));
  }

  /*
   * A borrowed verdict keeps BOTH facts: what kind of proof the older log had, and that it was an older log. The ledger
   * stores the rule its answer came from, so "Our side (earlier)" is not invented — and "(earlier)" is the half that
   * tells the operator this capture never proved it, which is the difference between leaving a row alone and re-checking.
   */
  [TestMethod]
  public void AnEarlierVerdictSaysWhatItWasAndThatItWasEarlier()
  {
    Assert.AreEqual("Our side (earlier)", IdentityVocabulary.WhyWord("Prior:R7-graph"));
    Assert.AreEqual("NPC list (earlier)", IdentityVocabulary.WhyWord("Prior:R6-npcdb"));
    Assert.AreEqual("Chosen (earlier)", IdentityVocabulary.WhyWord("Prior:Override"));
  }

  /*
   * R5's owner suffix comes off in the cell. "Owner" is the evidence; WHO owns it lives in the Pet Owners window, and the
   * whole reason this column shrank is that it printed names nobody came here to read (the pane used to need a second
   * dock's width for four columns).
   */
  [TestMethod]
  public void TheOwnerSuffixComesOffTheCell()
  {
    Assert.AreEqual("Owner", IdentityVocabulary.WhyWord("R5-owner:Sancus"));
    Assert.AreEqual("Owner (earlier)", IdentityVocabulary.WhyWord("Prior:R5-owner:Sancus"));
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
   * The Type dropdown, as data: the whole identity vocabulary in one list (which is what a menu of items you had to
   * remember could not be), each answer ONCE — the retired right-click menu listed NPC twice, two handlers doing the same
   * thing behind two menu lines.
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
   * "Clear claim" is not a type — it is the absence of the operator's claim, and IdentityKind.Unknown is exactly what
   * ClassificationCommands.ClearVerdict writes (remove the row, let this capture's own rules show through again). Keeping
   * it in the same enum-typed list is what stops the pane from needing a second verb for one file.
   *
   * The one deliberate mismatch: the dropdown says "Mercenary", the cell says "Merc". A pick-list has room for the word a
   * player uses; a four-column grid does not.
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
