using EQLogParser;

namespace EQLogParser.Test.src.store;

/*
 * "This spell name stood for more than one row" is a MAYBE, and an unambiguous buff line must not pay for it.
 *
 * ReceivedSpell used to initialise its candidate list inline, so every received-spell line allocated a List<SpellData>
 * whether or not the name was ambiguous. One capture's heap snapshot counted 656,686 ReceivedSpell objects against
 * 762,549 List<SpellData> — some 60 MB of list shells and backing arrays for a field that only means something when
 * it is non-empty (docs/DesignNotes.md → "Where a large capture's bytes actually are"). The list now arrives through
 * AddAmbiguity, which the parser calls only when a name resolved to several rows.
 *
 * Three things are pinned here because each is a different way this could go wrong: the read path never sees null (the
 * alternative was three call sites each remembering to check), the shared empty answer is ONE object rather than a
 * fresh list per spell (that is the actual saving, and `AreSame` is what sees it regress to `?? []`), and an empty
 * candidate handover allocates nothing — a wear-off line has no candidates by definition.
 */
[TestClass]
public class ReceivedSpellAmbiguityTest
{
  [TestMethod]
  public void ANameThatMatchedOneSpellCarriesNoCandidateList()
  {
    var one = new ReceivedSpell { Receiver = "Bithika", SpellData = new SpellData { Name = "Minor Healing" } };

    Assert.IsFalse(one.HasAmbiguity, "one row is not a maybe");
    Assert.AreEqual(0, one.AmbiguityCount);
    Assert.IsNotNull(one.Ambiguity, "readers never null-check; the unambiguous answer is an empty list, not a missing one");
    Assert.AreEqual(0, one.Ambiguity.Count);
  }

  [TestMethod]
  public void TheEmptyAnswerIsOneSharedObjectRatherThanAListPerSpell()
  {
    var first = new ReceivedSpell { Receiver = "Bithika", SpellData = new SpellData { Name = "Minor Healing" } };
    var second = new ReceivedSpell { Receiver = "Xathrad", SpellData = new SpellData { Name = "Greater Healing" } };

    // The saving is the sharing: `?? []` would read identically and put a list back on every buff line.
    Assert.AreSame(first.Ambiguity, second.Ambiguity);
  }

  [TestMethod]
  public void AnAmbiguousNameCarriesEveryRowItStoodFor()
  {
    var ambiguous = new ReceivedSpell { Receiver = "Bithika" };
    ambiguous.AddAmbiguity([new SpellData { Name = "Chill Skin I" }, new SpellData { Name = "Chill Skin II" }]);

    Assert.IsTrue(ambiguous.HasAmbiguity);
    Assert.AreEqual(2, ambiguous.AmbiguityCount);
    Assert.AreEqual("Chill Skin I", ambiguous.Ambiguity[0].Name);
    Assert.AreNotSame(ambiguous.Ambiguity, new ReceivedSpell().Ambiguity, "a name with candidates owns its own list");
  }

  [TestMethod]
  public void AnEmptyHandoverAllocatesNothing()
  {
    var wearOff = new ReceivedSpell { Receiver = "Bithika", IsWearOff = true };
    wearOff.AddAmbiguity([]);
    wearOff.AddAmbiguity(null);

    Assert.IsFalse(wearOff.HasAmbiguity, "a wear-off names nothing ambiguous and must not look like it did");
    Assert.AreEqual(0, wearOff.AmbiguityCount);
  }

  /*
   * The copy half of the same law. Buff lines whose NAME stands for several ranks arrive dozens of times a minute in a raid burn, and each
   * one used to keep its own List<SpellData> holding the same handful of database rows — the largest object-count growth a live-play heap
   * snapshot showed. Sharing is keyed by the identity of the rows, not by the spell name, because the candidate set legitimately varies for
   * one name with the tree path that produced it; getting that backwards would resolve a rank from a row this line was never offered.
   */
  [TestMethod]
  public void TwoSightsOfTheSameCandidateSetShareOneList()
  {
    var rank = new SpellData { Name = "Cessna Rk. IV", Id = "1" };
    var other = new SpellData { Name = "Cessna Rk. VII", Id = "2" };

    var first = new ReceivedSpell { Receiver = "Bithika" };
    first.AddAmbiguity([rank, other]);
    var second = new ReceivedSpell { Receiver = "Reisil" };
    second.AddAmbiguity([rank, other]);

    Assert.IsTrue(first.HasAmbiguity && second.HasAmbiguity);
    Assert.AreSame(first.Ambiguity, second.Ambiguity,
      "same rows in the same order is one set; the per-line copy is what this removes");

    // A different set for the same name must NOT borrow it — that would answer an 8-second resolution with rows it never saw.
    var third = new ReceivedSpell { Receiver = "Bithika" };
    third.AddAmbiguity([rank]);
    Assert.AreNotSame(first.Ambiguity, third.Ambiguity, "one candidate is a different set");
    CollectionAssert.AreEqual(new[] { rank }, third.Ambiguity);

    // The caller's list belongs to the tree and may be reused; what we kept is our own frozen copy.
    var transient = new List<SpellData> { rank, other };
    var fourth = new ReceivedSpell { Receiver = "Coas" };
    fourth.AddAmbiguity(transient);
    transient.Clear();
    Assert.AreEqual(2, fourth.Ambiguity.Count, "clearing the caller's list may not empty the buff's candidates");
  }
}
