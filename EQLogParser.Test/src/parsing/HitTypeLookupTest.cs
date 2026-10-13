namespace EQLogParser;

[TestClass]
public sealed class HitTypeLookupTest
{
  [TestMethod]
  public void SpanAndStringDoorsAgreeOnEveryVerbAndPlural()
  {
    (string Verb, string Plural)[] words =
    [
      ("bash", "bashes"), ("backstab", "backstabs"), ("bite", "bites"), ("claw", "claws"),
      ("cleave", "cleaves"), ("crush", "crushes"), ("frenzy", "frenzies"), ("gore", "gores"),
      ("hit", "hits"), ("kick", "kicks"), ("learn", "learns"), ("maul", "mauls"),
      ("punch", "punches"), ("pierce", "pierces"), ("reave", "reaves"), ("rend", "rends"),
      ("shoot", "shoots"), ("slash", "slashes"), ("slam", "slams"), ("slice", "slices"),
      ("smash", "smashes"), ("smite", "smites"), ("stab", "stabs"), ("sting", "stings"),
      ("strike", "strikes"), ("sweep", "sweeps")
    ];
    foreach (var (verb, plural) in words)
    {
      foreach (var text in new[] { verb, plural })
      {
        // A substring view, not a stand-alone string: compare only the token's own span.
        var token = LineWords.Build($"before {text} after")[1];
        Assert.AreEqual(plural, ParserUtil.GetHitType(text));
        Assert.AreEqual(plural, ParserUtil.GetHitType(token));
        Assert.IsNull(ParserUtil.GetHitType(LineWords.Build(text.ToUpperInvariant())[0]));
      }
    }
  }

  [TestMethod]
  public void EmptyAndUnknownTokensReturnNoHitType()
  {
    foreach (var text in new[] { "", "hit.", "hít", "hitsx", "h", "not-a-verb" })
    {
      Assert.IsNull(ParserUtil.GetHitType(text));
      Assert.IsNull(ParserUtil.GetHitType(LineWords.Build(text)[0]));
    }
    string? missing = null;
    Assert.IsNull(ParserUtil.GetHitType(missing));
  }
}
