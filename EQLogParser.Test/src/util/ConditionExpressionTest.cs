using EQLogParser;
using System.Collections.Generic;

namespace EQLogParserTest
{
  [TestClass]
  public class ConditionExpressionTest
  {
    // ---- Parser Tests ----

    [TestMethod]
    public void Parse_Null_ReturnsNull()
    {
      Assert.IsNull(ConditionParser.Parse(null));
    }

    [TestMethod]
    public void Parse_Empty_ReturnsNull()
    {
      Assert.IsNull(ConditionParser.Parse(""));
    }

    [TestMethod]
    public void Parse_WhitespaceOnly_ReturnsNull()
    {
      Assert.IsNull(ConditionParser.Parse("   "));
    }

    [TestMethod]
    public void Parse_SimpleEquality_ParsesCorrectly()
    {
      var node = ConditionParser.Parse("{s} = hello");
      Assert.IsNotNull(node);
      Assert.AreEqual(ConditionNodeType.Binary, node.NodeType);
      var bin = (ConditionBinaryNode)node;
      Assert.AreEqual(ConditionTokenType.Equals, bin.Operator);
      Assert.AreEqual(ConditionNodeType.Variable, bin.Left.NodeType);
      Assert.AreEqual("s", ((ConditionVariableNode)bin.Left).Name);
      Assert.AreEqual(ConditionNodeType.Literal, bin.Right.NodeType);
      Assert.AreEqual("hello", ((ConditionLiteralNode)bin.Right).StringValue);
    }

    [TestMethod]
    public void Parse_GreaterThan_ParsesCorrectly()
    {
      var node = ConditionParser.Parse("{hp} > 50");
      Assert.IsNotNull(node);
      var bin = (ConditionBinaryNode)node;
      Assert.AreEqual(ConditionTokenType.Greater, bin.Operator);
      Assert.AreEqual("hp", ((ConditionVariableNode)bin.Left).Name);
      Assert.AreEqual(50.0, ((ConditionLiteralNode)bin.Right).NumberValue);
    }

    [TestMethod]
    public void Parse_Contains_ParsesCorrectly()
    {
      var node = ConditionParser.Parse("{name} contains dragon");
      Assert.IsNotNull(node);
      var bin = (ConditionBinaryNode)node;
      Assert.AreEqual(ConditionTokenType.Contains, bin.Operator);
    }

    [TestMethod]
    public void Parse_AndExpression_ParsesCorrectly()
    {
      var node = ConditionParser.Parse("{hp} > 50 and {mana} > 10");
      Assert.IsNotNull(node);
      var top = (ConditionBinaryNode)node;
      Assert.AreEqual(ConditionTokenType.And, top.Operator);
      // Left side: hp > 50
      var left = (ConditionBinaryNode)top.Left;
      Assert.AreEqual("hp", ((ConditionVariableNode)left.Left).Name);
      Assert.AreEqual(ConditionTokenType.Greater, left.Operator);
      // Right side: mana > 10
      var right = (ConditionBinaryNode)top.Right;
      Assert.AreEqual("mana", ((ConditionVariableNode)right.Left).Name);
      Assert.AreEqual(ConditionTokenType.Greater, right.Operator);
    }

    [TestMethod]
    public void Parse_OrExpression_ParsesCorrectly()
    {
      var node = ConditionParser.Parse("{a} = 1 or {b} = 2");
      Assert.IsNotNull(node);
      var top = (ConditionBinaryNode)node;
      Assert.AreEqual(ConditionTokenType.Or, top.Operator);
    }

    [TestMethod]
    public void Parse_ParenthesizedExpression_ParsesCorrectly()
    {
      var node = ConditionParser.Parse("({hp} > 50 and {mana} > 10) or {godmode} = true");
      Assert.IsNotNull(node);
      var top = (ConditionBinaryNode)node;
      Assert.AreEqual(ConditionTokenType.Or, top.Operator);
      // Left side is parenthesized: (hp > 50 and mana > 10)
      var left = (ConditionBinaryNode)top.Left;
      Assert.AreEqual(ConditionTokenType.And, left.Operator);
    }

    [TestMethod]
    public void Parse_NotExpression_ParsesCorrectly()
    {
      var node = ConditionParser.Parse("not {enabled}");
      Assert.IsNotNull(node);
      Assert.AreEqual(ConditionNodeType.Unary, node.NodeType);
      var unary = (ConditionUnaryNode)node;
      Assert.AreEqual(ConditionTokenType.Not, unary.Operator);
      Assert.AreEqual("enabled", ((ConditionVariableNode)unary.Operand).Name);
    }

    [TestMethod]
    public void Parse_NullComparison_ParsesCorrectly()
    {
      var node = ConditionParser.Parse("{s} != null");
      Assert.IsNotNull(node);
      var bin = (ConditionBinaryNode)node;
      Assert.AreEqual(ConditionTokenType.NotEquals, bin.Operator);
      var lit = (ConditionLiteralNode)bin.Right;
      Assert.IsTrue(lit.IsNull);
    }

    [TestMethod]
    public void Parse_QuotedString_ParsesCorrectly()
    {
      var node = ConditionParser.Parse("{s} == \"hello world\"");
      Assert.IsNotNull(node);
      var bin = (ConditionBinaryNode)node;
      Assert.AreEqual("hello world", ((ConditionLiteralNode)bin.Right).StringValue);
    }

    [TestMethod]
    public void Parse_SingleQuotedString_ParsesCorrectly()
    {
      var node = ConditionParser.Parse("{s} eq 'test'");
      Assert.IsNotNull(node);
      var bin = (ConditionBinaryNode)node;
      Assert.AreEqual("test", ((ConditionLiteralNode)bin.Right).StringValue);
    }

    [TestMethod]
    public void Parse_BooleanLiteral_ParsesCorrectly()
    {
      var node = ConditionParser.Parse("{flag} = true");
      Assert.IsNotNull(node);
      var bin = (ConditionBinaryNode)node;
      var lit = (ConditionLiteralNode)bin.Right;
      Assert.AreEqual(ConditionTokenType.Boolean, lit.Type);
      Assert.IsTrue(lit.BooleanValue);
    }

    [TestMethod]
    public void Parse_OperatorAliases_ParsesCorrectly()
    {
      // Test various operator aliases
      Assert.IsNotNull(ConditionParser.Parse("{a} eq {b}"));
      Assert.IsNotNull(ConditionParser.Parse("{a} neq {b}"));
      Assert.IsNotNull(ConditionParser.Parse("{a} <> {b}"));
      Assert.IsNotNull(ConditionParser.Parse("{a} gt 5"));
      Assert.IsNotNull(ConditionParser.Parse("{a} gte 5"));
      Assert.IsNotNull(ConditionParser.Parse("{a} lt 5"));
      Assert.IsNotNull(ConditionParser.Parse("{a} lte 5"));
      Assert.IsNotNull(ConditionParser.Parse("{a} && {b}"));
      Assert.IsNotNull(ConditionParser.Parse("{a} || {b}"));
      Assert.IsNotNull(ConditionParser.Parse("!{a}"));
    }

    [TestMethod]
    public void Parse_CaseInsensitiveKeywords_ParsesCorrectly()
    {
      Assert.IsNotNull(ConditionParser.Parse("{a} = TRUE"));
      Assert.IsNotNull(ConditionParser.Parse("{a} = FALSE"));
      Assert.IsNotNull(ConditionParser.Parse("{a} = NULL"));
      Assert.IsNotNull(ConditionParser.Parse("{a} AND {b}"));
      Assert.IsNotNull(ConditionParser.Parse("{a} OR {b}"));
      Assert.IsNotNull(ConditionParser.Parse("NOT {a}"));
      Assert.IsNotNull(ConditionParser.Parse("{a} CONTAINS test"));
    }

    [TestMethod]
    public void Parse_UnclosedBrace_ReturnsNull()
    {
      Assert.IsNull(ConditionParser.Parse("{hp > 50"));
    }

    [TestMethod]
    public void Parse_UnclosedQuote_ReturnsNull()
    {
      Assert.IsNull(ConditionParser.Parse("{s} = \"hello"));
    }

    [TestMethod]
    public void Parse_MissingClosingParen_ReturnsNull()
    {
      Assert.IsNull(ConditionParser.Parse("({hp} > 50 and {mana} > 10"));
    }

    [TestMethod]
    public void Parse_NegativeNumber_ParsesCorrectly()
    {
      var node = ConditionParser.Parse("{temp} < -42");
      Assert.IsNotNull(node);
      var bin = (ConditionBinaryNode)node;
      Assert.AreEqual(-42.0, ((ConditionLiteralNode)bin.Right).NumberValue);
    }

    [TestMethod]
    public void Parse_DecimalNumber_ParsesCorrectly()
    {
      var node = ConditionParser.Parse("{value} >= 12.5");
      Assert.IsNotNull(node);
      var bin = (ConditionBinaryNode)node;
      Assert.AreEqual(12.5, ((ConditionLiteralNode)bin.Right).NumberValue);
    }

    [TestMethod]
    public void Parse_EmptyStringLiteral_ParsesCorrectly()
    {
      var node = ConditionParser.Parse("{s} = \"\"");
      Assert.IsNotNull(node);
      var bin = (ConditionBinaryNode)node;
      Assert.AreEqual("", ((ConditionLiteralNode)bin.Right).StringValue);
    }

    [TestMethod]
    public void Parse_StandaloneVariable_ParsesCorrectly()
    {
      var node = ConditionParser.Parse("{enabled}");
      Assert.IsNotNull(node);
      Assert.AreEqual(ConditionNodeType.Variable, node.NodeType);
      Assert.AreEqual("enabled", ((ConditionVariableNode)node).Name);
    }

    // ---- Evaluator Tests ----

    private static string? Resolve(Dictionary<string, string> vars, string name)
    {
      vars.TryGetValue(name, out var val);
      return val;
    }

    [TestMethod]
    public void Evaluate_NullNode_ReturnsTrue()
    {
      Assert.IsTrue(ConditionEvaluator.Evaluate(null!, name => "test"));
    }

    [TestMethod]
    public void Evaluate_Equality_True()
    {
      var node = ConditionParser.Parse("{s} = hello");
      Assert.IsTrue(ConditionEvaluator.Evaluate(node!, name => name == "s" ? "hello" : null));
    }

    [TestMethod]
    public void Evaluate_Equality_False()
    {
      var node = ConditionParser.Parse("{s} = hello");
      Assert.IsFalse(ConditionEvaluator.Evaluate(node!, name => name == "s" ? "world" : null));
    }

    [TestMethod]
    public void Evaluate_Equality_CaseInsensitive()
    {
      var node = ConditionParser.Parse("{s} = Hello");
      Assert.IsTrue(ConditionEvaluator.Evaluate(node!, name => name == "s" ? "HELLO" : null));
    }

    [TestMethod]
    public void Evaluate_GreaterThan_True()
    {
      var node = ConditionParser.Parse("{hp} > 50");
      Assert.IsTrue(ConditionEvaluator.Evaluate(node!, name => name == "hp" ? "75" : null));
    }

    [TestMethod]
    public void Evaluate_GreaterThan_False()
    {
      var node = ConditionParser.Parse("{hp} > 50");
      Assert.IsFalse(ConditionEvaluator.Evaluate(node!, name => name == "hp" ? "25" : null));
    }

    [TestMethod]
    public void Evaluate_LessThan_True()
    {
      var node = ConditionParser.Parse("{count} < 10");
      Assert.IsTrue(ConditionEvaluator.Evaluate(node!, name => name == "count" ? "3" : null));
    }

    [TestMethod]
    public void Evaluate_Contains_True()
    {
      var node = ConditionParser.Parse("{name} contains dragon");
      Assert.IsTrue(ConditionEvaluator.Evaluate(node!, name => name == "name" ? "Red Dragon" : null));
    }

    [TestMethod]
    public void Evaluate_Contains_CaseInsensitive()
    {
      var node = ConditionParser.Parse("{name} contains dragon");
      Assert.IsTrue(ConditionEvaluator.Evaluate(node!, name => name == "name" ? "RED DRAGON" : null));
    }

    [TestMethod]
    public void Evaluate_Contains_False()
    {
      var node = ConditionParser.Parse("{name} contains dragon");
      Assert.IsFalse(ConditionEvaluator.Evaluate(node!, name => name == "name" ? "wolf" : null));
    }

    [TestMethod]
    public void Evaluate_And_BothTrue()
    {
      var node = ConditionParser.Parse("{hp} > 50 and {mana} > 10");
      var vars = new Dictionary<string, string> { ["hp"] = "75", ["mana"] = "20" };
      Assert.IsTrue(ConditionEvaluator.Evaluate(node!, n => Resolve(vars, n)));
    }

    [TestMethod]
    public void Evaluate_And_LeftFalse()
    {
      var node = ConditionParser.Parse("{hp} > 50 and {mana} > 10");
      var vars = new Dictionary<string, string> { ["hp"] = "25", ["mana"] = "20" };
      Assert.IsFalse(ConditionEvaluator.Evaluate(node!, n => Resolve(vars, n)));
    }

    [TestMethod]
    public void Evaluate_And_RightFalse()
    {
      var node = ConditionParser.Parse("{hp} > 50 and {mana} > 10");
      var vars = new Dictionary<string, string> { ["hp"] = "75", ["mana"] = "5" };
      Assert.IsFalse(ConditionEvaluator.Evaluate(node!, n => Resolve(vars, n)));
    }

    [TestMethod]
    public void Evaluate_Or_LeftTrue()
    {
      var node = ConditionParser.Parse("{a} = 1 or {b} = 2");
      var vars = new Dictionary<string, string> { ["a"] = "1", ["b"] = "99" };
      Assert.IsTrue(ConditionEvaluator.Evaluate(node!, n => Resolve(vars, n)));
    }

    [TestMethod]
    public void Evaluate_Or_RightTrue()
    {
      var node = ConditionParser.Parse("{a} = 1 or {b} = 2");
      var vars = new Dictionary<string, string> { ["a"] = "99", ["b"] = "2" };
      Assert.IsTrue(ConditionEvaluator.Evaluate(node!, n => Resolve(vars, n)));
    }

    [TestMethod]
    public void Evaluate_Or_BothFalse()
    {
      var node = ConditionParser.Parse("{a} = 1 or {b} = 2");
      var vars = new Dictionary<string, string> { ["a"] = "99", ["b"] = "99" };
      Assert.IsFalse(ConditionEvaluator.Evaluate(node!, n => Resolve(vars, n)));
    }

    [TestMethod]
    public void Evaluate_Not_True()
    {
      var node = ConditionParser.Parse("not {disabled}");
      // disabled is unset (null) -> variable is falsy -> not falsy = true
      Assert.IsTrue(ConditionEvaluator.Evaluate(node!, name => null));
    }

    [TestMethod]
    public void Evaluate_Not_False()
    {
      var node = ConditionParser.Parse("not {disabled}");
      // disabled is set -> variable is truthy -> not truthy = false
      Assert.IsFalse(ConditionEvaluator.Evaluate(node!, name => name == "disabled" ? "yes" : null));
    }

    [TestMethod]
    public void Evaluate_NullEquality_SetVariable()
    {
      var node = ConditionParser.Parse("{s} = null");
      Assert.IsTrue(ConditionEvaluator.Evaluate(node!, name => null));
    }

    [TestMethod]
    public void Evaluate_NullEquality_UnsetVariable()
    {
      var node = ConditionParser.Parse("{s} != null");
      Assert.IsTrue(ConditionEvaluator.Evaluate(node!, name => name == "s" ? "hello" : null));
    }

    [TestMethod]
    public void Evaluate_NullEquality_SetVariable_False()
    {
      var node = ConditionParser.Parse("{s} != null");
      Assert.IsFalse(ConditionEvaluator.Evaluate(node!, name => null));
    }

    [TestMethod]
    public void Evaluate_EmptyStringComparison()
    {
      var node = ConditionParser.Parse("{s} = \"\"");
      Assert.IsTrue(ConditionEvaluator.Evaluate(node!, name => name == "s" ? "" : null));
    }

    [TestMethod]
    public void Evaluate_BooleanLiteral_True()
    {
      var node = ConditionParser.Parse("{flag} = true");
      Assert.IsTrue(ConditionEvaluator.Evaluate(node!, name => name == "flag" ? "true" : null));
    }

    [TestMethod]
    public void Evaluate_ParenthesizedExpression()
    {
      var node = ConditionParser.Parse("({hp} > 50 and {mana} > 10) or {godmode} = true");
      var vars = new Dictionary<string, string> { ["hp"] = "25", ["mana"] = "5", ["godmode"] = "true" };
      Assert.IsTrue(ConditionEvaluator.Evaluate(node!, n => Resolve(vars, n)));
    }

    [TestMethod]
    public void Evaluate_ComplexExpression_AllTrue()
    {
      var node = ConditionParser.Parse("({hp} > 50 and {mana} > 10) or {godmode} = true");
      var vars = new Dictionary<string, string> { ["hp"] = "75", ["mana"] = "20", ["godmode"] = "false" };
      Assert.IsTrue(ConditionEvaluator.Evaluate(node!, n => Resolve(vars, n)));
    }

    [TestMethod]
    public void Evaluate_ComplexExpression_AllFalse()
    {
      var node = ConditionParser.Parse("({hp} > 50 and {mana} > 10) or {godmode} = true");
      var vars = new Dictionary<string, string> { ["hp"] = "25", ["mana"] = "5", ["godmode"] = "false" };
      Assert.IsFalse(ConditionEvaluator.Evaluate(node!, n => Resolve(vars, n)));
    }

    [TestMethod]
    public void Evaluate_NumericComparison_FailsOnNonNumeric()
    {
      var node = ConditionParser.Parse("{value} > 10");
      // Non-numeric value should cause comparison to return false
      Assert.IsFalse(ConditionEvaluator.Evaluate(node!, name => name == "value" ? "abc" : null));
    }

    [TestMethod]
    public void Evaluate_GreaterEqual_Boundary()
    {
      var node = ConditionParser.Parse("{count} >= 5");
      Assert.IsTrue(ConditionEvaluator.Evaluate(node!, name => name == "count" ? "5" : null));
      Assert.IsTrue(ConditionEvaluator.Evaluate(node!, name => name == "count" ? "10" : null));
      Assert.IsFalse(ConditionEvaluator.Evaluate(node!, name => name == "count" ? "4" : null));
    }

    [TestMethod]
    public void Evaluate_LessEqual_Boundary()
    {
      var node = ConditionParser.Parse("{count} <= 5");
      Assert.IsTrue(ConditionEvaluator.Evaluate(node!, name => name == "count" ? "5" : null));
      Assert.IsTrue(ConditionEvaluator.Evaluate(node!, name => name == "count" ? "2" : null));
      Assert.IsFalse(ConditionEvaluator.Evaluate(node!, name => name == "count" ? "6" : null));
    }

    [TestMethod]
    public void Evaluate_NegativeNumbers()
    {
      var node = ConditionParser.Parse("{temp} < -10");
      Assert.IsTrue(ConditionEvaluator.Evaluate(node!, name => name == "temp" ? "-20" : null));
      Assert.IsFalse(ConditionEvaluator.Evaluate(node!, name => name == "temp" ? "-5" : null));
    }

    [TestMethod]
    public void Evaluate_DecimalNumbers()
    {
      var node = ConditionParser.Parse("{value} > 12.5");
      Assert.IsTrue(ConditionEvaluator.Evaluate(node!, name => name == "value" ? "13.0" : null));
      Assert.IsFalse(ConditionEvaluator.Evaluate(node!, name => name == "value" ? "12.0" : null));
    }

    [TestMethod]
    public void Evaluate_StandaloneVariable_Truthy()
    {
      var node = ConditionParser.Parse("{enabled}");
      Assert.IsTrue(ConditionEvaluator.Evaluate(node!, name => name == "enabled" ? "yes" : null));
    }

    [TestMethod]
    public void Evaluate_StandaloneVariable_Falsy()
    {
      var node = ConditionParser.Parse("{enabled}");
      Assert.IsFalse(ConditionEvaluator.Evaluate(node!, name => null));
    }

    [TestMethod]
    public void Evaluate_StandaloneVariable_EmptyString()
    {
      var node = ConditionParser.Parse("{enabled}");
      Assert.IsFalse(ConditionEvaluator.Evaluate(node!, name => ""));
    }

    // ---- Integration: Parse + Evaluate round-trip ----

    [TestMethod]
    public void RoundTrip_SimpleCondition()
    {
      var node = ConditionParser.Parse("{s} == testing");
      Assert.IsNotNull(node);
      Assert.IsTrue(ConditionEvaluator.Evaluate(node!, name => name == "s" ? "testing" : null));
      Assert.IsFalse(ConditionEvaluator.Evaluate(node!, name => name == "s" ? "other" : null));
    }
  }
}
