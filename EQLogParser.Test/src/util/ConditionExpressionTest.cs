using EQLogParser;

#nullable enable annotations

namespace EQLogParserTest
{
  [TestClass]
  public class ConditionExpressionTest
  {
    [TestMethod]
    public void Evaluate_NullNode_ReturnsTrue()
    {
      Assert.IsTrue(ConditionEvaluator.Evaluate(null, _ => "test"));
    }

    [TestMethod]
    [DataRow("{hp} > 50 and {mana} > 10", "hp", "75", "mana", "20", true)]
    [DataRow("{hp} > 50 and {mana} > 10", "hp", "25", "mana", "20", false)]
    [DataRow("{hp} > 50 and {mana} > 10", "hp", "75", "mana", "5", false)]
    [DataRow("{a} = 1 or {b} = 2", "a", "1", "b", "99", true)]
    [DataRow("{a} = 1 or {b} = 2", "a", "99", "b", "2", true)]
    [DataRow("{a} = 1 or {b} = 2", "a", "99", "b", "99", false)]
    public void BooleanOperatorsCombineBothOperands(string expression, string first, string firstValue,
      string second, string secondValue, bool expected)
    {
      var values = new Dictionary<string, string> { [first] = firstValue, [second] = secondValue };
      Assert.AreEqual(expected, ConditionEvaluator.Evaluate(Parse(expression), name => Resolve(values, name)));
    }

    [TestMethod]
    [DataRow("25", "5", "true", true)]
    [DataRow("75", "20", "false", true)]
    [DataRow("25", "5", "false", false)]
    public void ParenthesizedAndRemainsOneOperandOfOr(string hp, string mana, string godmode, bool expected)
    {
      var values = new Dictionary<string, string> { ["hp"] = hp, ["mana"] = mana, ["godmode"] = godmode };
      var node = Parse("({hp} > 50 and {mana} > 10) or {godmode} = true");
      Assert.AreEqual(expected, ConditionEvaluator.Evaluate(node, name => Resolve(values, name)));
    }

    [TestMethod]
    [DataRow("{count} >= 5", "count", "5", true)]
    [DataRow("{count} >= 5", "count", "10", true)]
    [DataRow("{count} >= 5", "count", "4", false)]
    [DataRow("{count} <= 5", "count", "5", true)]
    [DataRow("{count} <= 5", "count", "2", true)]
    [DataRow("{count} <= 5", "count", "6", false)]
    [DataRow("{temp} < -10", "temp", "-20", true)]
    [DataRow("{temp} < -10", "temp", "-5", false)]
    [DataRow("{value} > 12.5", "value", "13.0", true)]
    [DataRow("{value} > 12.5", "value", "12.0", false)]
    [DataRow("{a} > 0", "a", "1", true)]
    [DataRow("{a} > 0", "a", "0", false)]
    [DataRow("{a} > 0", "a", "-1", false)]
    [DataRow("{a} <= 0", "a", "0", true)]
    [DataRow("{a} <= 0", "a", "-1", true)]
    [DataRow("{a} <= 0", "a", "1", false)]
    [DataRow("{s} == testing", "s", "testing", true)]
    [DataRow("{s} == testing", "s", "other", false)]
    [DataRow("{name} contains dragon", "name", "wolf pack", false)]
    [DataRow("{a} != hello", "a", "world", true)]
    [DataRow("{a} != hello", "a", "hello", false)]
    [DataRow("{a} != hello", "a", "HELLO", false)]
    public void ComparisonsHandleEqualityBoundariesSignsAndFractions(
      string expression, string variable, string value, bool expected)
    {
      Assert.AreEqual(expected,
        ConditionEvaluator.Evaluate(Parse(expression), name => name == variable ? value : null));
    }

    [TestMethod]
    public void Evaluate_ChainedAndShortCircuitsCorrectly()
    {
      var rightReads = 0;
      Assert.IsFalse(ConditionEvaluator.Evaluate(Parse("{a} = 1 and {b} = 2"), name =>
      {
        if (name == "b") rightReads++;
        return name == "a" ? "99" : "2";
      }));
      Assert.AreEqual(0, rightReads);
    }

    [TestMethod]
    public void Evaluate_ChainedOrShortCircuitsCorrectly()
    {
      var rightReads = 0;
      Assert.IsTrue(ConditionEvaluator.Evaluate(Parse("{a} = 1 or {b} = 2"), name =>
      {
        if (name == "b") rightReads++;
        return name == "a" ? "1" : "99";
      }));
      Assert.AreEqual(0, rightReads);
    }

    [TestMethod]
    public void Evaluate_EmptyStringVsNull_DistinguishesCorrectly()
    {
      var node = Parse("{s} = \"\"");
      Assert.IsTrue(ConditionEvaluator.Evaluate(node, name => name == "s" ? "" : null));
      Assert.IsFalse(ConditionEvaluator.Evaluate(node, _ => null));
    }

    [TestMethod]
    public void Evaluate_DollarPrefixedVariable_ResolvesCorrectly()
    {
      var node = Parse("${hello} == 'test'");
      Assert.IsTrue(ConditionEvaluator.Evaluate(node, name => name == "hello" ? "test" : null));
      Assert.IsFalse(ConditionEvaluator.Evaluate(node, name => name == "hello" ? "other" : null));
      Assert.IsFalse(ConditionEvaluator.Evaluate(node, name => name == "$hello" ? "test" : null));
    }

    [TestMethod]
    public void Evaluate_MixedDollarAndBraceOnly_ResolvesCorrectly()
    {
      var node = Parse("{test} > 3 && ${hello} == 'test'");
      var values = new Dictionary<string, string> { ["test"] = "10", ["hello"] = "test" };
      Assert.IsTrue(ConditionEvaluator.Evaluate(node, name => Resolve(values, name)));
    }

    [TestMethod]
    public void Evaluate_VariableToVariableNumericComparison()
    {
      var node = Parse("{a} > {b}");
      var values = new Dictionary<string, string> { ["a"] = "10", ["b"] = "5" };
      Assert.IsTrue(ConditionEvaluator.Evaluate(node, name => Resolve(values, name)));
      values["a"] = "3";
      Assert.IsFalse(ConditionEvaluator.Evaluate(node, name => Resolve(values, name)));
    }

    [TestMethod]
    public void Evaluate_VariableToVariableContains()
    {
      var node = Parse("{a} contains {b}");
      var values = new Dictionary<string, string> { ["a"] = "Red Dragon", ["b"] = "dragon" };
      Assert.IsTrue(ConditionEvaluator.Evaluate(node, name => Resolve(values, name)));
      values["b"] = "wolf";
      Assert.IsFalse(ConditionEvaluator.Evaluate(node, name => Resolve(values, name)));
    }

    [TestMethod]
    public void Evaluate_ChainedNotWithParens()
    {
      var node = Parse("not not ({a} > 5)");
      Assert.IsTrue(ConditionEvaluator.Evaluate(node, name => name == "a" ? "10" : null));
      Assert.IsFalse(ConditionEvaluator.Evaluate(node, name => name == "a" ? "3" : null));
    }

    [TestMethod]
    public void Evaluate_StringEqualityCaseInsensitive_BothVariables()
    {
      var values = new Dictionary<string, string> { ["a"] = "HELLO", ["b"] = "hello" };
      Assert.IsTrue(ConditionEvaluator.Evaluate(Parse("{a} = {b}"), name => Resolve(values, name)));
    }

    [TestMethod]
    public void Evaluate_NagIsNull_ShortHand()
    {
      var node = Parse("!{SpellBeingCast}");
      var values = new Dictionary<string, string>();
      Assert.IsTrue(ConditionEvaluator.Evaluate(node, name => Resolve(values, name)));
      values["SpellBeingCast"] = "Fireball";
      Assert.IsFalse(ConditionEvaluator.Evaluate(node, name => Resolve(values, name)));
    }

    [TestMethod]
    public void Evaluate_NagEqualityOr_CombinedWithAnd_StaysGrouped()
    {
      var node = Parse("{zone} contains \"Norg\" && ({spell} = \"Fireball\" || {spell} = \"Flame Strike\")");
      var values = new Dictionary<string, string> { ["zone"] = "NORG-TH", ["spell"] = "flame strike" };
      Assert.IsTrue(ConditionEvaluator.Evaluate(node, name => Resolve(values, name)));
      values["spell"] = "Arcane";
      Assert.IsFalse(ConditionEvaluator.Evaluate(node, name => Resolve(values, name)));
      values["spell"] = "Flame Strike";
      values["zone"] = "Kunark";
      Assert.IsFalse(ConditionEvaluator.Evaluate(node, name => Resolve(values, name)), "The OR must not escape its group.");
    }

    [TestMethod]
    public void Evaluate_NagNegatedEqualityOr()
    {
      var node = Parse("!({spell} = \"Fireball\" || {spell} = \"Flame Strike\")");
      var values = new Dictionary<string, string>();
      Assert.IsTrue(ConditionEvaluator.Evaluate(node, name => Resolve(values, name)));
      values["spell"] = "Arcane";
      Assert.IsTrue(ConditionEvaluator.Evaluate(node, name => Resolve(values, name)));
      values["spell"] = "FIREBALL";
      Assert.IsFalse(ConditionEvaluator.Evaluate(node, name => Resolve(values, name)));
    }

    [TestMethod]
    public void Evaluate_NagContains_CaseInsensitiveSubstring()
    {
      var values = new Dictionary<string, string> { ["zone"] = "norg-th, deeps of" };
      Assert.IsTrue(ConditionEvaluator.Evaluate(Parse("{zone} contains \"Norg\""), name => Resolve(values, name)));
    }

    private static ConditionNode Parse(string expression)
    {
      var node = ConditionParser.Parse(expression);
      Assert.IsNotNull(node, "A parser failure must not pass as the evaluator's null-AST default.");
      return node;
    }

    private static string? Resolve(Dictionary<string, string> values, string name) => values.GetValueOrDefault(name);
  }
}
