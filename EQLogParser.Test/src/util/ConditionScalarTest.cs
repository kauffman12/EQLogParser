using EQLogParser;
using System.Globalization;

#nullable enable annotations

namespace EQLogParserTest
{
  [TestClass]
  public class ConditionScalarTest
  {
    [TestMethod]
    [DataRow("en-US")]
    [DataRow("de-DE")]
    [DataRow("fr-FR")]
    public void DecimalLiteralsParseAndCompareIndependentlyOfCulture(string culture)
    {
      var original = CultureInfo.CurrentCulture;
      try
      {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        var node = ConditionParser.Parse("{value} > 12.5");
        Assert.IsInstanceOfType<ConditionBinaryNode>(node);
        var literal = ((ConditionBinaryNode)node).Right;
        Assert.IsInstanceOfType<ConditionLiteralNode>(literal);
        Assert.AreEqual(12.5, ((ConditionLiteralNode)literal).NumberValue);
        Assert.IsTrue(ConditionEvaluator.Evaluate(node, _ => "13.0"));
        Assert.IsFalse(ConditionEvaluator.Evaluate(node, _ => "12.5"));

        foreach (var token in new[] { "3.5", "0.3", "12.12" })
        {
          var equality = ConditionParser.Parse("{value} = " + token);
          Assert.IsNotNull(equality);
          Assert.IsTrue(ConditionEvaluator.Evaluate(equality, _ => token), token);
        }
      }
      finally
      {
        CultureInfo.CurrentCulture = original;
      }
    }

    [TestMethod]
    [DataRow(null, false)]
    [DataRow("", false)]
    [DataRow("   ", false)]
    [DataRow("{a} eq {b}", true)]
    [DataRow("{a} neq {b}", true)]
    [DataRow("{a} <> {b}", true)]
    [DataRow("{a} gt 5", true)]
    [DataRow("{a} gte 5", true)]
    [DataRow("{a} lt 5", true)]
    [DataRow("{a} lte 5", true)]
    [DataRow("{a} && {b}", true)]
    [DataRow("{a} || {b}", true)]
    [DataRow("!{a}", true)]
    [DataRow("{a} && {b} or {c} = 1", true)]
    [DataRow("{a} || {b} and {c} neq 1", true)]
    [DataRow("{my_var} = 1", true)]
    [DataRow("{s1} > 0", true)]
    [DataRow("{n2} < 10", true)]
    [DataRow("{hp.max} >= 100", true)]
    [DataRow("{_private} = test", true)]
    [DataRow("$", false)]
    [DataRow("${", false)]
    [DataRow("${h", false)]
    [DataRow("${hel", false)]
    [DataRow("${hello", false)]
    [DataRow("${hello} ==", false)]
    [DataRow("${hello} == '\"", false)]
    [DataRow("{a}\t>\t5", true)]
    [DataRow("  {a}   >   5  ", true)]
    [DataRow("{a} > 5\n", true)]
    [DataRow("{a} > ", false)]
    [DataRow("{a} and ", false)]
    [DataRow("not ", false)]
    [DataRow("> 5", false)]
    [DataRow("and {a}", false)]
    [DataRow("or {a}", false)]
    [DataRow("{a} @ {b}", false)]
    [DataRow("{a} # 5", false)]
    [DataRow("{a} % {b}", false)]
    [DataRow("({a} > 5))", false)]
    [DataRow("(", false)]
    [DataRow(")", false)]
    [DataRow("{a} > -", false)]
    [DataRow("{a} > .", false)]
    [DataRow("{a} = TRUE", true)]
    [DataRow("{a} = FALSE", true)]
    [DataRow("{a} = NULL", true)]
    [DataRow("{a} AND {b}", true)]
    [DataRow("{a} OR {b}", true)]
    [DataRow("NOT {a}", true)]
    [DataRow("{a} CONTAINS test", true)]
    [DataRow("{hp > 50", false)]
    [DataRow("{s} = \"hello", false)]
    [DataRow("({hp} > 50 and {mana} > 10", false)]
    [DataRow("{", false)]
    [DataRow("{h", false)]
    [DataRow("{hp", false)]
    [DataRow("{my_var", false)]
    [DataRow("124 == {", false)]
    [DataRow("{hp} >", false)]
    [DataRow("{hp} <", false)]
    [DataRow("{hp} =", false)]
    [DataRow("{hp} !", false)]
    [DataRow("{hp} !=", false)]
    [DataRow("{hp} >=", false)]
    [DataRow("{hp} <=", false)]
    [DataRow("{hp} > ", false)]
    [DataRow("{hp} = ", false)]
    [DataRow("{hp} contains ", false)]
    [DataRow("{hp} an", false)]
    [DataRow("{hp} and", false)]
    [DataRow("{hp} or", false)]
    [DataRow("{hp} co", false)]
    [DataRow("{hp} cont", false)]
    [DataRow("{hp} contain", false)]
    [DataRow("{hp} contains", false)]
    [DataRow("not {", false)]
    [DataRow("{hp} = \"", false)]
    [DataRow("{hp} = \"h", false)]
    [DataRow("{hp} = \"he", false)]
    [DataRow("{hp} eq '\"", false)]
    [DataRow("{hp} eq 'hel", false)]
    [DataRow("((", false)]
    [DataRow("({hp}", false)]
    [DataRow("({hp} >", false)]
    [DataRow("({hp} > 50", false)]
    [DataRow("({hp} > 50 and", false)]
    [DataRow("({hp} > 50 and {mana}", false)]
    [DataRow("({hp} > 50 and {mana} >", false)]
    [DataRow("({hp} > 50) and", false)]
    [DataRow("({hp} > 50) and {", false)]
    [DataRow("({hp} > 50) or {mana} >", false)]
    [DataRow("{hp} ==", false)]
    [DataRow("{hp} &", false)]
    [DataRow("{hp} &&", false)]
    [DataRow("{hp} |", false)]
    [DataRow("{hp} ||", false)]
    [DataRow(" ", false)]
    [DataRow("  ", false)]
    [DataRow("42 >", false)]
    [DataRow("42 =", false)]
    [DataRow("42 and", false)]
    [DataRow("hello >", false)]
    [DataRow("hello =", false)]
    [DataRow("hello contains", false)]
    [DataRow("!", false)]
    [DataRow(">", false)]
    [DataRow("<", false)]
    [DataRow("{{", false)]
    [DataRow("{a}{b}", false)]
    [DataRow("{a} > >", false)]
    [DataRow("{a} = =", false)]
    [DataRow("({hp} > 50 and {mana} > 10) or {godmod", false)]
    [DataRow("({hp} > 50 and {mana} > 10) or {godmode}", true)]
    [DataRow("not ({hp} > 50 and {mana} >", false)]
    [DataRow("{a} contains \"some long string tha", false)]
    [DataRow("{hp} > 50", true)]
    [DataRow("{a} = {b}", true)]
    [DataRow("not {x}", true)]
    [DataRow("{a} contains test", true)]
    [DataRow("{a} = 1 foo bar baz", false)]
    [DataRow("()", false)]
    [DataRow("(({a} > 5)", false)]
    public void ParserAcceptanceIncludesAliasesAndIncompleteEditorInput(string? expression, bool accepted)
    {
      Assert.AreEqual(accepted, ConditionParser.Parse(expression) is not null);
    }

    [TestMethod]
    [DataRow("{s} = hello", "s", "hello", true)]
    [DataRow("{s} = hello", "s", "world", false)]
    [DataRow("{s} = Hello", "s", "HELLO", true)]
    [DataRow("{hp} > 50", "hp", "75", true)]
    [DataRow("{hp} > 50", "hp", "25", false)]
    [DataRow("{count} < 10", "count", "3", true)]
    [DataRow("{name} contains dragon", "name", "Red Dragon", true)]
    [DataRow("{name} contains dragon", "name", "RED DRAGON", true)]
    [DataRow("{name} contains dragon", "name", "wolf", false)]
    [DataRow("not {disabled}", null, null, true)]
    [DataRow("not {disabled}", "disabled", "yes", false)]
    [DataRow("{s} = null", null, null, true)]
    [DataRow("{s} != null", "s", "hello", true)]
    [DataRow("{s} != null", null, null, false)]
    [DataRow("{s} = \"\"", "s", "", true)]
    [DataRow("{flag} = true", "flag", "true", true)]
    [DataRow("{value} > 10", "value", "abc", false)]
    [DataRow("{enabled}", "enabled", "yes", true)]
    [DataRow("{enabled}", null, null, false)]
    [DataRow("{enabled}", null, "", false)]
    [DataRow("{name} contains {target}", "name", "Red Dragon", false)]
    [DataRow("{name} contains dragon", null, null, false)]
    [DataRow("{a} = {b}", null, null, false)]
    [DataRow("{a} = hello", null, null, false)]
    [DataRow("{a} = {b}", "a", "hello", false)]
    [DataRow("{hp} > 50", null, null, false)]
    [DataRow("{hp} < 50", null, null, true)]
    [DataRow("{hp} <= 50", null, null, true)]
    [DataRow("{hp} > 50 and {mana} > 10", "hp", "75", false)]
    [DataRow("{hp} > 50 or {mana} > 10", "hp", "75", true)]
    [DataRow("{a} or {b}", null, null, false)]
    [DataRow("{count} > 5", "count", "010", true)]
    [DataRow("{value} >= 3.14", "value", "3.14159", true)]
    [DataRow("{hp} > 50", "hp", "high", false)]
    [DataRow("{name} contains dragon", "name", "", false)]
    [DataRow("{name} = dragon", "name", "", false)]
    [DataRow("{a} != {b}", null, null, true)]
    [DataRow("not not {x}", null, null, false)]
    [DataRow("not not {x}", "x", "yes", true)]
    public void ScalarEvaluationDistinguishesMissingEmptyNumericAndTextValues(
      string expression, string? variable, string? value, bool expected)
    {
      var node = ConditionParser.Parse(expression);
      Assert.IsNotNull(node, "A parser failure must not pass as the evaluator's intentional null-AST default.");
      Assert.AreEqual(expected,
        ConditionEvaluator.Evaluate(node, name => variable is null || name == variable ? value : null));
    }
  }
}
