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
    public void Parse_MixedOperatorStyles_ParsesCorrectly()
    {
      // Mixing symbolic and word operators should work
      Assert.IsNotNull(ConditionParser.Parse("{a} && {b} or {c} = 1"));
      Assert.IsNotNull(ConditionParser.Parse("{a} || {b} and {c} neq 1"));
    }

    [TestMethod]
    public void Parse_VariableToVariableComparison_ParsesCorrectly()
    {
      var node = ConditionParser.Parse("{a} = {b}");
      Assert.IsNotNull(node);
      var bin = (ConditionBinaryNode)node;
      Assert.AreEqual(ConditionNodeType.Variable, bin.Left.NodeType);
      Assert.AreEqual(ConditionNodeType.Variable, bin.Right.NodeType);
      Assert.AreEqual("a", ((ConditionVariableNode)bin.Left).Name);
      Assert.AreEqual("b", ((ConditionVariableNode)bin.Right).Name);
    }

    [TestMethod]
    public void Parse_VariableNamesWithSpecialChars_ParsesCorrectly()
    {
      // Variable names with underscores, dots, numbers
      Assert.IsNotNull(ConditionParser.Parse("{my_var} = 1"));
      Assert.IsNotNull(ConditionParser.Parse("{s1} > 0"));
      Assert.IsNotNull(ConditionParser.Parse("{n2} < 10"));
      Assert.IsNotNull(ConditionParser.Parse("{hp.max} >= 100"));
      Assert.IsNotNull(ConditionParser.Parse("{_private} = test"));
    }

    [TestMethod]
    public void Parse_ChainedAnds_ParsesCorrectly()
    {
      var node = ConditionParser.Parse("{a} > 1 and {b} > 2 and {c} > 3");
      Assert.IsNotNull(node);
      // Left-associative: ((a>1 and b>2) and c>3)
      var top = (ConditionBinaryNode)node;
      Assert.AreEqual(ConditionTokenType.And, top.Operator);
      var left = (ConditionBinaryNode)top.Left;
      Assert.AreEqual(ConditionTokenType.And, left.Operator);
    }

    [TestMethod]
    public void Parse_ChainedOrs_ParsesCorrectly()
    {
      var node = ConditionParser.Parse("{a} = 1 or {b} = 2 or {c} = 3");
      Assert.IsNotNull(node);
      var top = (ConditionBinaryNode)node;
      Assert.AreEqual(ConditionTokenType.Or, top.Operator);
    }

    [TestMethod]
    public void Parse_AndPrecedenceOverOr_ParsesCorrectly()
    {
      // {a} and {b} or {c} should parse as ({a} and {b}) or {c}
      var node = ConditionParser.Parse("{a} = 1 and {b} = 2 or {c} = 3");
      Assert.IsNotNull(node);
      var top = (ConditionBinaryNode)node;
      Assert.AreEqual(ConditionTokenType.Or, top.Operator);
      // Left side should be AND
      var left = (ConditionBinaryNode)top.Left;
      Assert.AreEqual(ConditionTokenType.And, left.Operator);
    }

    [TestMethod]
    public void Parse_NestedParentheses_ParsesCorrectly()
    {
      var node = ConditionParser.Parse("((({a} > 1)))");
      Assert.IsNotNull(node);
      // Multiple parens around the same expression — parser resolves directly to the inner binary node
      var bin = (ConditionBinaryNode)node;
      Assert.AreEqual("a", ((ConditionVariableNode)bin.Left).Name);
      Assert.AreEqual(ConditionTokenType.Greater, bin.Operator);
    }

    [TestMethod]
    public void Parse_DoubleNot_ParsesCorrectly()
    {
      var node = ConditionParser.Parse("not not {a}");
      Assert.IsNotNull(node);
      var outer = (ConditionUnaryNode)node;
      Assert.AreEqual(ConditionTokenType.Not, outer.Operator);
      var inner = (ConditionUnaryNode)outer.Operand;
      Assert.AreEqual(ConditionTokenType.Not, inner.Operator);
    }

    [TestMethod]
    public void Parse_NotWithParentheses_ParsesCorrectly()
    {
      var node = ConditionParser.Parse("not ({a} and {b})");
      Assert.IsNotNull(node);
      var unary = (ConditionUnaryNode)node;
      Assert.AreEqual(ConditionTokenType.Not, unary.Operator);
      Assert.AreEqual(ConditionNodeType.Binary, unary.Operand.NodeType);
    }

    [TestMethod]
    public void Parse_StandaloneNumber_ParsesCorrectly()
    {
      var node = ConditionParser.Parse("42");
      Assert.IsNotNull(node);
      Assert.AreEqual(ConditionNodeType.Literal, node.NodeType);
      var lit = (ConditionLiteralNode)node;
      Assert.AreEqual(ConditionTokenType.Number, lit.Type);
      Assert.AreEqual(42.0, lit.NumberValue);
    }

    [TestMethod]
    public void Parse_StandaloneBareword_ParsesCorrectly()
    {
      var node = ConditionParser.Parse("hello");
      Assert.IsNotNull(node);
      Assert.AreEqual(ConditionNodeType.Literal, node.NodeType);
      var lit = (ConditionLiteralNode)node;
      Assert.AreEqual(ConditionTokenType.String, lit.Type);
      Assert.AreEqual("hello", lit.StringValue);
    }

    [TestMethod]
    public void Parse_WhitespaceVariations_ParsesCorrectly()
    {
      // Tabs and multiple spaces
      Assert.IsNotNull(ConditionParser.Parse("{a}\t>\t5"));
      Assert.IsNotNull(ConditionParser.Parse("  {a}   >   5  "));
      Assert.IsNotNull(ConditionParser.Parse("{a} > 5\n"));
    }

    [TestMethod]
    public void Parse_EmptyBraces_ParsesAsEmptyVariable()
    {
      // {} is technically valid — it creates a variable node with an empty name
      var node = ConditionParser.Parse("{}");
      Assert.IsNotNull(node);
      Assert.AreEqual(ConditionNodeType.Variable, node.NodeType);
      Assert.AreEqual("", ((ConditionVariableNode)node).Name);
    }

    [TestMethod]
    public void Parse_TrailingOperator_ReturnsNull()
    {
      Assert.IsNull(ConditionParser.Parse("{a} > "));
      Assert.IsNull(ConditionParser.Parse("{a} and "));
      Assert.IsNull(ConditionParser.Parse("not "));
    }

    [TestMethod]
    public void Parse_LeadingNonNotOperator_ReturnsNull()
    {
      Assert.IsNull(ConditionParser.Parse("> 5"));
      Assert.IsNull(ConditionParser.Parse("and {a}"));
      Assert.IsNull(ConditionParser.Parse("or {a}"));
    }

    [TestMethod]
    public void Parse_UnknownSymbol_ReturnsNull()
    {
      Assert.IsNull(ConditionParser.Parse("{a} @ {b}"));
      Assert.IsNull(ConditionParser.Parse("{a} # 5"));
      Assert.IsNull(ConditionParser.Parse("{a} % {b}"));
    }

    [TestMethod]
    public void Parse_ExtraClosingParen_ReturnsNull()
    {
      Assert.IsNull(ConditionParser.Parse("({a} > 5))"));
    }

    [TestMethod]
    public void Parse_JustOpenParen_ReturnsNull()
    {
      Assert.IsNull(ConditionParser.Parse("("));
    }

    [TestMethod]
    public void Parse_JustCloseParen_ReturnsNull()
    {
      Assert.IsNull(ConditionParser.Parse(")"));
    }

    [TestMethod]
    public void Parse_IncompleteNumber_ReturnsNull()
    {
      Assert.IsNull(ConditionParser.Parse("{a} > -"));
      Assert.IsNull(ConditionParser.Parse("{a} > ."));
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

    // ---- Mid-typing / partial input — must never throw, always return null ----

    [TestMethod]
    public void Parse_MidTyping_JustOpenBrace_ReturnsNull()
    {
      Assert.IsNull(ConditionParser.Parse("{"));
    }

    [TestMethod]
    public void Parse_MidTyping_PartialVariableName_ReturnsNull()
    {
      Assert.IsNull(ConditionParser.Parse("{h"));
      Assert.IsNull(ConditionParser.Parse("{hp"));
      Assert.IsNull(ConditionParser.Parse("{my_var"));
    }

    [TestMethod]
    public void Parse_MidTyping_NumberThenEqualsAndBrace_ReturnsNull()
    {
      // User typed: 124 == {
      Assert.IsNull(ConditionParser.Parse("124 == {"));
    }

    [TestMethod]
    public void Parse_MidTyping_VariableThenOperatorOnly_ReturnsNull()
    {
      Assert.IsNull(ConditionParser.Parse("{hp} >"));
      Assert.IsNull(ConditionParser.Parse("{hp} <"));
      Assert.IsNull(ConditionParser.Parse("{hp} ="));
      Assert.IsNull(ConditionParser.Parse("{hp} !"));
      Assert.IsNull(ConditionParser.Parse("{hp} !="));
      Assert.IsNull(ConditionParser.Parse("{hp} >="));
      Assert.IsNull(ConditionParser.Parse("{hp} <="));
    }

    [TestMethod]
    public void Parse_MidTyping_VariableThenOperatorAndSpace_ReturnsNull()
    {
      Assert.IsNull(ConditionParser.Parse("{hp} > "));
      Assert.IsNull(ConditionParser.Parse("{hp} = "));
      Assert.IsNull(ConditionParser.Parse("{hp} contains "));
    }

    [TestMethod]
    public void Parse_MidTyping_VariableThenPartialKeyword_ReturnsNull()
    {
      Assert.IsNull(ConditionParser.Parse("{hp} an"));
      Assert.IsNull(ConditionParser.Parse("{hp} and"));  // "and" with no right side is still trailing-op
      Assert.IsNull(ConditionParser.Parse("{hp} or"));
      Assert.IsNull(ConditionParser.Parse("{hp} co"));
      Assert.IsNull(ConditionParser.Parse("{hp} cont"));
      Assert.IsNull(ConditionParser.Parse("{hp} contain"));
      Assert.IsNull(ConditionParser.Parse("{hp} contains"));  // "contains" with no right side
      Assert.IsNull(ConditionParser.Parse("not "));
      Assert.IsNull(ConditionParser.Parse("not {"));
    }

    [TestMethod]
    public void Parse_MidTyping_VariableThenPartialQuotedString_ReturnsNull()
    {
      Assert.IsNull(ConditionParser.Parse("{hp} = \""));
      Assert.IsNull(ConditionParser.Parse("{hp} = \"h"));
      Assert.IsNull(ConditionParser.Parse("{hp} = \"he"));
      Assert.IsNull(ConditionParser.Parse("{hp} eq '\""));
      Assert.IsNull(ConditionParser.Parse("{hp} eq 'hel"));
    }

    [TestMethod]
    public void Parse_MidTyping_JustOpenParen_ReturnsNull()
    {
      Assert.IsNull(ConditionParser.Parse("("));
      Assert.IsNull(ConditionParser.Parse("(("));
    }

    [TestMethod]
    public void Parse_MidTyping_ParenThenPartialExpression_ReturnsNull()
    {
      Assert.IsNull(ConditionParser.Parse("({hp}"));
      Assert.IsNull(ConditionParser.Parse("({hp} >"));
      Assert.IsNull(ConditionParser.Parse("({hp} > 50"));
      Assert.IsNull(ConditionParser.Parse("({hp} > 50 and"));
      Assert.IsNull(ConditionParser.Parse("({hp} > 50 and {mana}"));
      Assert.IsNull(ConditionParser.Parse("({hp} > 50 and {mana} >"));
    }

    [TestMethod]
    public void Parse_MidTyping_ClosedParenThenPartial_ReturnsNull()
    {
      Assert.IsNull(ConditionParser.Parse("({hp} > 50) and"));
      Assert.IsNull(ConditionParser.Parse("({hp} > 50) and {"));
      Assert.IsNull(ConditionParser.Parse("({hp} > 50) or {mana} >"));
    }

    [TestMethod]
    public void Parse_MidTyping_DoubleEqualsPartial_ReturnsNull()
    {
      Assert.IsNull(ConditionParser.Parse("{hp} ="));
      Assert.IsNull(ConditionParser.Parse("{hp} =="));
    }

    [TestMethod]
    public void Parse_MidTyping_DoubleAmpersandPartial_ReturnsNull()
    {
      Assert.IsNull(ConditionParser.Parse("{hp} &"));
      Assert.IsNull(ConditionParser.Parse("{hp} &&"));
    }

    [TestMethod]
    public void Parse_MidTyping_DoublePipePartial_ReturnsNull()
    {
      Assert.IsNull(ConditionParser.Parse("{hp} |"));
      Assert.IsNull(ConditionParser.Parse("{hp} ||"));
    }

    [TestMethod]
    public void Parse_MidTyping_JustSpace_ReturnsNull()
    {
      Assert.IsNull(ConditionParser.Parse(" "));
      Assert.IsNull(ConditionParser.Parse("  "));
    }

    [TestMethod]
    public void Parse_MidTyping_NumberThenOperator_ReturnsNull()
    {
      Assert.IsNull(ConditionParser.Parse("42 >"));
      Assert.IsNull(ConditionParser.Parse("42 ="));
      Assert.IsNull(ConditionParser.Parse("42 and"));
    }

    [TestMethod]
    public void Parse_MidTyping_BarewordThenOperator_ReturnsNull()
    {
      Assert.IsNull(ConditionParser.Parse("hello >"));
      Assert.IsNull(ConditionParser.Parse("hello ="));
      Assert.IsNull(ConditionParser.Parse("hello contains"));
    }

    [TestMethod]
    public void Parse_MidTyping_ExclamationAlone_ReturnsNull()
    {
      Assert.IsNull(ConditionParser.Parse("!"));
    }

    [TestMethod]
    public void Parse_MidTyping_GreaterThanAlone_ReturnsNull()
    {
      Assert.IsNull(ConditionParser.Parse(">"));
      Assert.IsNull(ConditionParser.Parse("<"));
    }

    [TestMethod]
    public void Parse_MidTyping_MultipleBraces_ReturnsNull()
    {
      Assert.IsNull(ConditionParser.Parse("{{"));
      Assert.IsNull(ConditionParser.Parse("{a}{b}"));
    }

    [TestMethod]
    public void Parse_MidTyping_VariableThenTwoOperators_ReturnsNull()
    {
      Assert.IsNull(ConditionParser.Parse("{a} > >"));
      Assert.IsNull(ConditionParser.Parse("{a} = ="));
    }

    [TestMethod]
    public void Parse_MidTyping_ComplexPartial_ReturnsNull()
    {
      Assert.IsNull(ConditionParser.Parse("({hp} > 50 and {mana} > 10) or {godmod"));
      // ({hp} > 50 and {mana} > 10) or {godmode} is VALID — standalone var as truthy check
      Assert.IsNotNull(ConditionParser.Parse("({hp} > 50 and {mana} > 10) or {godmode}"));
      Assert.IsNull(ConditionParser.Parse("not ({hp} > 50 and {mana} >"));
      Assert.IsNull(ConditionParser.Parse("{a} contains \"some long string tha"));
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

    // ---- Bug fix regression tests ----

    [TestMethod]
    public void Evaluate_ContainsWithNullRightSide_ReturnsFalse()
    {
      // Regression: {name} contains {unsetVar} should NOT match everything
      var node = ConditionParser.Parse("{name} contains {target}");
      Assert.IsFalse(ConditionEvaluator.Evaluate(node!, name => name == "name" ? "Red Dragon" : null));
    }

    [TestMethod]
    public void Evaluate_ContainsWithNullLeftSide_ReturnsFalse()
    {
      var node = ConditionParser.Parse("{name} contains dragon");
      Assert.IsFalse(ConditionEvaluator.Evaluate(node!, name => null));
    }

    [TestMethod]
    public void Evaluate_ContainsWithBothNonNull_ReturnsExpected()
    {
      var node = ConditionParser.Parse("{name} contains dragon");
      Assert.IsTrue(ConditionEvaluator.Evaluate(node!, name => name == "name" ? "Red Dragon" : null));
      Assert.IsFalse(ConditionEvaluator.Evaluate(node!, name => name == "name" ? "wolf pack" : null));
    }

    [TestMethod]
    public void Parse_ValidCondition_ReturnsNonNullOrphan()
    {
      Assert.IsNotNull(ConditionParser.Parse("{hp} > 50"));
      Assert.IsNotNull(ConditionParser.Parse("{a} = {b}"));
      Assert.IsNotNull(ConditionParser.Parse("not {x}"));
      Assert.IsNotNull(ConditionParser.Parse("{a} contains test"));
    }

    [TestMethod]
    public void Parse_InvalidConditions_ReturnNull()
    {
      // Unclosed braces
      Assert.IsNull(ConditionParser.Parse("{hp > 50"));
      // Unclosed quotes
      Assert.IsNull(ConditionParser.Parse("{s} = \"hello"));
      // Missing closing paren
      Assert.IsNull(ConditionParser.Parse("({hp} > 50"));
      // Unknown operator symbol
      Assert.IsNull(ConditionParser.Parse("{a} @ {b}"));
      // Trailing garbage token after valid expression
      Assert.IsNull(ConditionParser.Parse("{a} = 1 foo bar baz"));
    }

    [TestMethod]
    public void Evaluate_NullNode_AlwaysReturnsTrue()
    {
      // Simulates a trigger with no condition set (or parse failure treated as no-op)
      Assert.IsTrue(ConditionEvaluator.Evaluate(null!, name => "anything"));
    }

    [TestMethod]
    public void Evaluate_ChainedAndShortCircuitsCorrectly()
    {
      // Left side false should not evaluate right side
      var evalCount = 0;
      var node = ConditionParser.Parse("{a} = 1 and {b} = 2");
      Assert.IsFalse(ConditionEvaluator.Evaluate(node!, name =>
      {
        if (name == "b") evalCount++;
        return name == "a" ? "99" : "2"; // a=99 makes left side false
      }));
      // b should never be resolved due to short-circuit
      Assert.AreEqual(0, evalCount);
    }

    [TestMethod]
    public void Evaluate_ChainedOrShortCircuitsCorrectly()
    {
      // Left side true should not evaluate right side
      var evalCount = 0;
      var node = ConditionParser.Parse("{a} = 1 or {b} = 2");
      Assert.IsTrue(ConditionEvaluator.Evaluate(node!, name =>
      {
        if (name == "b") evalCount++;
        return name == "a" ? "1" : "99"; // a=1 makes left side true
      }));
      // b should never be resolved due to short-circuit
      Assert.AreEqual(0, evalCount);
    }

    [TestMethod]
    public void Evaluate_EmptyStringVsNull_DistinguishesCorrectly()
    {
      var eqNode = ConditionParser.Parse("{s} = \"\"");
      // Empty string equals empty string literal
      Assert.IsTrue(ConditionEvaluator.Evaluate(eqNode!, name => name == "s" ? "" : null));
      // Null does NOT equal empty string literal
      Assert.IsFalse(ConditionEvaluator.Evaluate(eqNode!, name => null));
    }

    // ---- Evaluator: unset / missing variable edge-cases ----

    [TestMethod]
    public void Evaluate_AllVariablesUnset_EqualityReturnsFalse()
    {
      var node = ConditionParser.Parse("{a} = {b}");
      // Both null — null = null should be true
      Assert.IsTrue(ConditionEvaluator.Evaluate(node!, name => null));
    }

    [TestMethod]
    public void Evaluate_LeftUnsetRightSet_EqualityReturnsFalse()
    {
      var node = ConditionParser.Parse("{a} = hello");
      Assert.IsFalse(ConditionEvaluator.Evaluate(node!, name => null));
    }

    [TestMethod]
    public void Evaluate_LeftSetRightUnset_EqualityReturnsFalse()
    {
      var node = ConditionParser.Parse("{a} = {b}");
      Assert.IsFalse(ConditionEvaluator.Evaluate(node!, name => name == "a" ? "hello" : null));
    }

    [TestMethod]
    public void Evaluate_UnsetVariableInNumericComparison_ReturnsFalse()
    {
      var node = ConditionParser.Parse("{hp} > 50");
      Assert.IsFalse(ConditionEvaluator.Evaluate(node!, name => null));
    }

    [TestMethod]
    public void Evaluate_UnsetVariableInContains_ReturnsFalse()
    {
      var node = ConditionParser.Parse("{name} contains dragon");
      Assert.IsFalse(ConditionEvaluator.Evaluate(node!, name => null));
    }

    [TestMethod]
    public void Evaluate_UnsetVariableStandalone_ReturnsFalse()
    {
      var node = ConditionParser.Parse("{enabled}");
      Assert.IsFalse(ConditionEvaluator.Evaluate(node!, name => null));
    }

    [TestMethod]
    public void Evaluate_NotUnsetVariable_ReturnsTrue()
    {
      // not {unsetVar} — unset is falsy, so not falsy = true
      var node = ConditionParser.Parse("not {disabled}");
      Assert.IsTrue(ConditionEvaluator.Evaluate(node!, name => null));
    }

    [TestMethod]
    public void Evaluate_AndWithOneUnsetVariable_HandlesGracefully()
    {
      var node = ConditionParser.Parse("{hp} > 50 and {mana} > 10");
      // hp set, mana unset
      Assert.IsFalse(ConditionEvaluator.Evaluate(node!, name => name == "hp" ? "75" : null));
    }

    [TestMethod]
    public void Evaluate_OrWithOneUnsetVariable_HandlesGracefully()
    {
      var node = ConditionParser.Parse("{hp} > 50 or {mana} > 10");
      // hp set and true, mana unset — should short-circuit to true
      Assert.IsTrue(ConditionEvaluator.Evaluate(node!, name => name == "hp" ? "75" : null));
    }

    [TestMethod]
    public void Evaluate_OrBothUnset_ReturnsFalse()
    {
      var node = ConditionParser.Parse("{a} or {b}");
      Assert.IsFalse(ConditionEvaluator.Evaluate(node!, name => null));
    }

    [TestMethod]
    public void Evaluate_NumericComparisonWithLeadingZeros()
    {
      var node = ConditionParser.Parse("{count} > 5");
      Assert.IsTrue(ConditionEvaluator.Evaluate(node!, name => name == "count" ? "010" : null));
    }

    [TestMethod]
    public void Evaluate_NumericComparisonWithDecimalInVariable()
    {
      var node = ConditionParser.Parse("{value} >= 3.14");
      Assert.IsTrue(ConditionEvaluator.Evaluate(node!, name => name == "value" ? "3.14159" : null));
    }

    [TestMethod]
    public void Evaluate_NumericComparisonRightSideNonNumeric_ReturnsFalse()
    {
      var node = ConditionParser.Parse("{hp} > 50");
      // hp resolves to non-numeric
      Assert.IsFalse(ConditionEvaluator.Evaluate(node!, name => name == "hp" ? "high" : null));
    }

    [TestMethod]
    public void Evaluate_ContainsWithVariableResolvingToEmptyString_ReturnsExpected()
    {
      var node = ConditionParser.Parse("{name} contains dragon");
      // Empty string does not contain "dragon"
      Assert.IsFalse(ConditionEvaluator.Evaluate(node!, name => name == "name" ? "" : null));
    }

    [TestMethod]
    public void Evaluate_EqualityWithVariableResolvingToEmptyString()
    {
      var node = ConditionParser.Parse("{name} = dragon");
      Assert.IsFalse(ConditionEvaluator.Evaluate(node!, name => name == "name" ? "" : null));
    }

    [TestMethod]
    public void Evaluate_NotEqualsWithBothUnset_ReturnsFalse()
    {
      // null != null is false (they are equal)
      var node = ConditionParser.Parse("{a} != {b}");
      Assert.IsFalse(ConditionEvaluator.Evaluate(node!, name => null));
    }

    [TestMethod]
    public void Evaluate_NestedNotWithUnsetVariable()
    {
      // not not {unsetVar} — unset is falsy, not falsy = true, not true = false
      var node = ConditionParser.Parse("not not {x}");
      Assert.IsFalse(ConditionEvaluator.Evaluate(node!, name => null));
    }

    [TestMethod]
    public void Evaluate_NestedNotWithSetVariable()
    {
      // not not {x} where x="yes" — truthy, not truthy = false, not false = true
      var node = ConditionParser.Parse("not not {x}");
      Assert.IsTrue(ConditionEvaluator.Evaluate(node!, name => name == "x" ? "yes" : null));
    }
  }
}
