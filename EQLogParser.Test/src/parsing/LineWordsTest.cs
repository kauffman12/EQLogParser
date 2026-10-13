using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Text;

namespace EQLogParser.Parsing
{
  /*
   * LineWords exists to replace `string.Split(' ') exactly — same tokenization (empty entries included, leading and
   * trailing spaces included), same FindStop, same JoinWords, same ParseUInt — so the parsers' index arithmetic
   * survived migration untouched. These tests pin that equivalence against the ORIGINAL implementations and against
   * string.Split directly, including a seeded fuzz corpus, because a tokenizer that quietly disagrees on double
   * spaces shifts every downstream slice and corrupts names — the worst possible failure mode for this codebase.
   */
  [TestClass]
  public class LineWordsTest
  {
    // Corpus: the shapes the parsers actually slice, plus every degenerate space shape Split has an opinion about.
    private static readonly string[] Corpus =
    [
      "",
      " ",
      "  ",
      " a",
      "a ",
      "a b",
      "a  b",
      "a   b  c ",
      " Your SwornSlayer hits A for 3 damage.",
      "You hit A for 3 damage.",
      "Ghoul hits You for 3 damage. (Strikethrough)",
      "Your pet bites A for 50 damage. (DualWield)(Primary)",
      "A is slashed by Bob for 48 damage.",
      "Bob's pet bites A for 1 damage.",
      "You are petted by Bob's pet.",
      "Zorgzen's warder has captured a ghoul.",
      "Your wolf is slain by Zorgzen.",
      "A drains 30 points of blood from Bob.",
      "You stand up.",
      "Kizant dies.",
      "Bard's pet hits YOU, but YOU block! (Strikethrough)",
      "You deliver a critical blast! (15094)",
      "Zon binds the party. Blow!! (170)(Secondary)",
      "Bob lands a Crippling Blow!(1234)",
      "Someone casts a Ankylosis by Kizant.",
      "\tTabbed\tline\there.",
      "unicode: Kaýlos hits Þor for 9 damage.",
      "a  b   c    d",            // runs of spaces everywhere
      "(leading paren) hits you", // tag-shaped head
      "trailing tags (a) (b)",
      "x )(",
      "()()",
      "(",
      ")",
      "some ( unbalanced here",
      "word with  double  spaces  between",
    ];

    [TestMethod]
    public void TokenizationMatchesStringSplit()
    {
      foreach (var line in Corpus)
      {
        var words = LineWords.Build(line);
        var split = line.Split(' ');
        Assert.AreEqual(split.Length, words.Length, $"length for <{line}>");

        for (var i = 0; i < split.Length; i++)
        {
          Assert.AreEqual(split[i], words[i].ToString(), $"token {i} of <{line}>");
          Assert.AreEqual(split[i].Length, words[i].Length, $"token {i} length of <{line}>");
          Assert.AreEqual(split[i].Length == 0, words[i].IsEmpty, $"token {i} emptiness of <{line}>");
        }
      }
    }

    [TestMethod]
    public void TokenizationMatchesStringSplitUnderFuzz()
    {
      // Seeded, alphabet small enough to breed dense space runs and punctuation collisions.
      var rng = new Random(20260807);
      var alphabet = new[] { 'a', 'b', ' ', '(', ')', '\'', 's', '.', '!' };

      for (var iteration = 0; iteration < 50_000; iteration++)
      {
        var len = rng.Next(0, 40);
        var sb = new StringBuilder(len);
        for (var i = 0; i < len; i++)
        {
          sb.Append(alphabet[rng.Next(alphabet.Length)]);
        }

        var line = sb.ToString();
        var words = LineWords.Build(line);
        var split = line.Split(' ');
        if (split.Length != words.Length)
        {
          Assert.Fail($"fuzz length mismatch for <{line}>: {split.Length} vs {words.Length}");
        }

        for (var i = 0; i < split.Length; i++)
        {
          if (split[i] != words[i].ToString())
          {
            Assert.Fail($"fuzz token {i} mismatch for <{line}>: <{split[i]}> vs <{words[i]}>");
          }
        }
      }
    }

    [TestMethod]
    public void EmptyActionBuildsTheSplitShapedView()
    {
      // "".Split(' ') is ONE empty token — the view must agree or every index arithmetic downstream drifts.
      // Null has no Split behavior to mirror (the old door would have thrown); it reads as zero tokens.
      Assert.AreEqual(0, LineWords.Build(null).Length);
      Assert.AreEqual(1, LineWords.Build(string.Empty).Length);
      Assert.IsTrue(LineWords.Build(string.Empty)[0].IsEmpty);
      Assert.AreEqual(-1, LineWords.Build(null).FindStop()); // ParserUtil.FindStop(empty array) is -1 too
    }

    [TestMethod]
    public void FindStopMatchesParserUtil()
    {
      foreach (var line in Corpus)
      {
        var expected = ParserUtil.FindStop(line.Split(' '));
        var actual = LineWords.Build(line).FindStop();
        Assert.AreEqual(expected, actual, $"FindStop for <{line}>");
      }
    }

    [TestMethod]
    public void FindStopMatchesParserUtilUnderFuzz()
    {
      var rng = new Random(991);
      var alphabet = new[] { 'a', 'b', ' ', '(', ')', '\'', 's', '.', '!' };

      for (var iteration = 0; iteration < 20_000; iteration++)
      {
        var len = rng.Next(0, 30);
        var sb = new StringBuilder(len);
        for (var i = 0; i < len; i++)
        {
          sb.Append(alphabet[rng.Next(alphabet.Length)]);
        }

        var line = sb.ToString();
        var expected = ParserUtil.FindStop(line.Split(' '));
        var actual = LineWords.Build(line).FindStop();
        if (expected != actual)
        {
          Assert.Fail($"fuzz FindStop mismatch for <{line}>: {expected} vs {actual}");
        }
      }
    }

    [TestMethod]
    public void JoinWordsMatchesParserUtil()
    {
      foreach (var line in Corpus)
      {
        var split = line.Split(' ');
        var words = LineWords.Build(line);
        for (var start = 0; start < split.Length; start++)
        {
          for (var count = 0; start + count <= split.Length; count++)
          {
            Assert.AreEqual(ParserUtil.JoinWords(split, start, count), words.JoinWords(start, count),
              $"JoinWords({start},{count}) of <{line}>");
          }
        }
      }
    }

    [TestMethod]
    public void JoinWordsLongPathStillSkipsNothingTheOldOneKept()
    {
      // >256 chars takes the old implementation's fallback, which joined WITHOUT skipping empties. Pinning that
      // asymmetry on purpose: a "cleaner" rewrite here would change spell names on pathological lines.
      var longWord = new string('x', 200);
      var line = $"{longWord}  {new string('y', 200)}"; // double space = an empty entry between
      var split = line.Split(' ');
      var words = LineWords.Build(line);
      Assert.AreEqual(ParserUtil.JoinWords(split, 0, split.Length), words.JoinWords(0, split.Length));
    }

    [TestMethod]
    public void ParseUIntMatchesTheArrayPath()
    {
      var words = LineWords.Build("deal 12345 damage (99)");
      Assert.AreEqual(uint.MaxValue, words.ParseUInt(0));
      Assert.AreEqual(12345u, words.ParseUInt(1));
      Assert.AreEqual(uint.MaxValue, words.ParseUInt(2));
      Assert.AreEqual(uint.MaxValue, words.ParseUInt(3)); // "(99)" is not a number either way
    }

    [TestMethod]
    public void WordEqualityIsOrdinalLikeTheOldStringComparison()
    {
      var words = LineWords.Build("Hits hits HITS");
      Assert.IsTrue(words[0] == "Hits");
      Assert.IsFalse(words[0] == "hits");           // ordinal, not ignore-case
      Assert.IsTrue(words[1] == "hits");
      Assert.IsTrue(words[2] != "hits");
      Assert.IsTrue("Hits" == words[0]);              // either operand order
      Assert.IsFalse(words[0] == null);             // old: an array element == null was false
      Assert.IsTrue(LineWords.Build("x  y")[1].IsEmpty); // the empty middle token compares empty, not missing
    }

    [TestMethod]
    public void WordEndsWithAndStartsWithMatchStringSemantics()
    {
      var words = LineWords.Build("Blocker blocking Block!");
      Assert.IsTrue(words[0].StartsWith("Block", StringComparison.OrdinalIgnoreCase));
      Assert.IsFalse(words[0].StartsWith("block", StringComparison.Ordinal));
      Assert.IsTrue(words[2].EndsWith("!", StringComparison.OrdinalIgnoreCase));
      Assert.IsTrue(words[2].StartsWith('B'));
      Assert.IsFalse(LineWords.Build(" a b")[0].StartsWith('a')); // empty token starts with nothing
    }

    [TestMethod]
    public void IndexFromEndWorksLikeTheArray()
    {
      var words = LineWords.Build("Ghoul hits You for 3 damage. (Strikethrough)");
      Assert.AreEqual("(Strikethrough)", words[^1].ToString());
      Assert.AreEqual("damage.", words[^2].ToString());
    }

    [TestMethod]
    public void JoinRawKeepsEmptyTokensLikeStringJoin()
    {
      foreach (var line in Corpus)
      {
        AssertRawRanges(line);
      }

      AssertRawRanges($" {new string('x', 300)}  {new string('y', 300)} ");
    }

    [TestMethod]
    public void JoinRawMatchesStringJoinUnderFuzz()
    {
      var rng = new Random(4203);
      const string alphabet = " ab  .\tÞ";
      for (var iteration = 0; iteration < 2000; iteration++)
      {
        var chars = new char[rng.Next(0, 100)];
        for (var i = 0; i < chars.Length; i++)
        {
          chars[i] = alphabet[rng.Next(alphabet.Length)];
        }

        AssertRawRanges(new string(chars));
      }
    }

    [TestMethod]
    public void JoinRawRejectsInvalidPositiveRangesButKeepsEmptyRangeBehavior()
    {
      var words = LineWords.Build("a  b");
      Assert.ThrowsExactly<IndexOutOfRangeException>(() => words.JoinRaw(-1, 1));
      Assert.ThrowsExactly<IndexOutOfRangeException>(() => words.JoinRaw(words.Length, 1));
      Assert.ThrowsExactly<IndexOutOfRangeException>(() => words.JoinRaw(1, words.Length));
      Assert.ThrowsExactly<IndexOutOfRangeException>(() => words.JoinRaw(0, int.MaxValue));
      Assert.ThrowsExactly<IndexOutOfRangeException>(() => LineWords.Empty.JoinRaw(0, 1));
      Assert.AreEqual(string.Empty, words.JoinRaw(-1, 0));
      Assert.AreEqual(string.Empty, words.JoinRaw(int.MaxValue, -1));
      Assert.AreEqual(string.Empty, LineWords.Empty.JoinRaw(0, 0));
    }

    [TestMethod]
    public void LegacySplitDoorSeesTheSameArray()
    {
      var lineData = new LineData { Action = "You hit A for 3 damage." };
      CollectionAssert.AreEqual(lineData.Action.Split(' '), lineData.Split);

      // The setter contract: an assigned array is what the getter returns; Words rebuilds from Action.
      var assigned = "other text here".Split(' ');
      lineData.Split = assigned;
      Assert.AreSame(assigned, lineData.Split);
    }

    private static void AssertRawRanges(string line)
    {
      var split = line.Split(' ');
      var words = LineWords.Build(line);
      for (var start = 0; start <= split.Length; start++)
      {
        for (var count = 0; count <= split.Length - start; count++)
        {
          Assert.AreEqual(string.Join(" ", split, start, count), words.JoinRaw(start, count),
            $"JoinRaw({start},{count}) of <{line}>");
        }
      }
    }
  }
}
