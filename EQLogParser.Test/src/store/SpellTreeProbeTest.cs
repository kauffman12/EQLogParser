using Microsoft.VisualStudio.TestTools.UnitTesting;

#nullable enable annotations

namespace EQLogParser;

/*
 * The spell-word trees (LandsOnOther / LandsOnYou / WearOff) after they became frozen read-forms.
 *
 * Why this is worth its own file: the tree walk runs on every buff and wear-off line — **5,305,309 probes over one real capture** — and it
 * used to materialise a string per level just to key a Dictionary lookup (26.9 MB of characters a parse, all of it garbage), plus one result
 * object per search (2,336,056 of them). Both halves are now allocation-free, which changes the SHAPE of the code (`AlternateLookup` over a
 * span, a value-type answer), so the behaviour that has to stay identical is asserted here:
 *
 *   1. MATCHING IS STILL ORDINAL. The trees key on the words as spells.txt spelled them, and subtype/label vocabulary is ordinal by project
 *      law. "Renewal" must not find "renewal" — a case-insensitive freeze would quietly re-point buff lines at another spell row.
 *   2. THE WHOLE PATH STILL RESOLVES, from the tail word back to the head, and a miss at any level falls back to the node it stopped on.
 *   3. FREEZING IS ONCE, AND A LATE WRITE IS A BUG — a tree written after Freeze would silently never be read.
 */
[TestClass]
public class SpellTreeProbeTest
{
  private static SpellData Spell(string name) => new SpellData { Name = name, NameAbbrv = name };

  /*
   * root → "Wood" → "the" → "of" → "Spirit", which is how `Spirit of the Wood III` sits in a LandsOnYou message read from its tail.
   * The SpellData lives at the head word, because that is where the walk ends when it consumes everything.
   */
  private static SpellTreeNode BuildChain(out List<SpellData> atHead, out List<SpellData> atWood)
  {
    var root = new SpellTreeNode();
    var wood = new SpellTreeNode();
    var the = new SpellTreeNode();
    var of = new SpellTreeNode();
    var spirit = new SpellTreeNode();

    atWood = [Spell("wood-landing")];
    atHead = [Spell("spirit-of-the-wood")];

    root.AddWord("Wood", wood);
    wood.AddWord("the", the);
    wood.SpellData.AddRange(atWood);
    the.AddWord("of", of);
    of.AddWord("Spirit", spirit);
    spirit.SpellData.AddRange(atHead);

    root.Freeze();
    return root;
  }

  [TestMethod]
  public void AWordIsFoundBySpanAndStillMatchedOrdinally()
  {
    var root = BuildChain(out var atHead, out _);

    Assert.IsTrue(root.TryWord("Wood".AsSpan(), out var child), "a slice of somebody else's string is a perfectly good key");
    Assert.IsFalse(root.TryWord("wood".AsSpan(), out _), "ordinal: the tree keys on the word as spells.txt spelled it");
    Assert.IsFalse(root.TryWord("Wooden".AsSpan(), out _), "a prefix is not a match");

    var line = "xx and Wood then".AsSpan(7, 4); // the same four letters, sitting inside somebody else's string
    Assert.IsTrue(root.TryWord(line, out var bySlice), "the probe reads the word where it already lies");
    Assert.AreSame(child.SpellData, bySlice.SpellData);

    // Walking to the head still lands on the head's rows.
    Assert.IsTrue(child.TryWord("the".AsSpan(), out var level2));
    Assert.IsTrue(level2.TryWord("of".AsSpan(), out var level3));
    Assert.IsTrue(level3.TryWord("Spirit".AsSpan(), out var head));
    CollectionAssert.AreEquivalent(atHead, head.SpellData);
  }

  [TestMethod]
  public void ThePathSearchResolvesFromTailToHeadWithoutMaterialisingWords()
  {
    var root = BuildChain(out var atHead, out var atWood);

    var whole = LineWords.Build("Spirit of the Wood");
    var found = EQDataStore.SearchSpellPath(root, whole, whole.Length - 1);
    Assert.AreEqual(0, found.DataIndex, "the walk consumed every word");
    CollectionAssert.AreEquivalent(atHead, found.SpellData);

    // A miss at level one — the common case — answers with this node's own rows and the index it stopped at.
    var miss = LineWords.Build("Spirit of the Wol");
    var missed = EQDataStore.SearchSpellPath(root, miss, miss.Length - 1);
    Assert.AreEqual(miss.Length - 1, missed.DataIndex);
    Assert.AreEqual(0, missed.SpellData.Count, "an unrooted tree holds nothing");

    // A partial walk stops on the node it reached: "Wood" is a level of its own here.
    var partial = LineWords.Build("something Wood");
    var partResult = EQDataStore.SearchSpellPath(root, partial, partial.Length - 1);
    Assert.AreEqual(0, partResult.DataIndex, "the walk stops at the last word it consumed");
    CollectionAssert.AreEquivalent(atWood, partResult.SpellData);
  }

  [TestMethod]
  public void TheAnswerIsAValueNotAnObject()
  {
    // One result object per search was 2.3 M allocations a capture; every consumer copies the two fields it wants.
    Assert.IsTrue(typeof(SpellTreeResult).IsValueType, "SpellTreeResult must stay a struct");
    Assert.IsNull(typeof(SpellTreeNode).GetProperty("Words"),
      "the tree exposes no live Dictionary to probe — reads go through the span door only");
  }

  [TestMethod]
  public void WritingATreeAfterItIsFrozenRefuses()
  {
    var root = new SpellTreeNode();
    root.AddWord("Wood", new SpellTreeNode());
    root.Freeze();

    // A late insert would be invisible forever (the frozen form is what every probe reads), so it says so instead.
    Assert.ThrowsExactly<InvalidOperationException>(() => root.AddWord("Spirit", new SpellTreeNode()));

    root.Freeze(); // idempotent: a second freeze is a no-op, not a rebuild of an already-dropped builder
    Assert.IsTrue(root.TryWord("Wood".AsSpan(), out _));
  }

  [TestMethod]
  public void AnEmptyTreeProbesQuietly()
  {
    var root = new SpellTreeNode();
    root.Freeze();

    Assert.IsFalse(root.TryWord("anything".AsSpan(), out _));
    var split = LineWords.Build("nothing here");
    var found = EQDataStore.SearchSpellPath(root, split, split.Length - 1);
    Assert.AreEqual(split.Length - 1, found.DataIndex);
    Assert.AreEqual(0, found.SpellData.Count, "an empty tree answers with its own empty list");
  }
}
