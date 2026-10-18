using EQLogParser;

namespace EQLogParserTest
{
  [TestClass]
  public class ConditionParserStructureTest
  {
    [TestMethod]
    public void Parse_SimpleEquality_ParsesCorrectly()
    {
      var node = Parse("{s} = hello");
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
      var bin = (ConditionBinaryNode)Parse("{hp} > 50");
      Assert.AreEqual(ConditionTokenType.Greater, bin.Operator);
      Assert.AreEqual("hp", ((ConditionVariableNode)bin.Left).Name);
      Assert.AreEqual(50.0, ((ConditionLiteralNode)bin.Right).NumberValue);
    }

    [TestMethod]
    public void Parse_Contains_ParsesCorrectly()
    {
      Assert.AreEqual(ConditionTokenType.Contains, ((ConditionBinaryNode)Parse("{name} contains dragon")).Operator);
    }

    [TestMethod]
    public void Parse_AndExpression_ParsesCorrectly()
    {
      var node = Parse("{hp} > 50 and {mana} > 10");
      var top = (ConditionBinaryNode)node;
      Assert.AreEqual(ConditionTokenType.And, top.Operator);
      var left = (ConditionBinaryNode)top.Left;
      Assert.AreEqual("hp", ((ConditionVariableNode)left.Left).Name);
      Assert.AreEqual(ConditionTokenType.Greater, left.Operator);
      var right = (ConditionBinaryNode)top.Right;
      Assert.AreEqual("mana", ((ConditionVariableNode)right.Left).Name);
      Assert.AreEqual(ConditionTokenType.Greater, right.Operator);
    }

    [TestMethod]
    public void Parse_OrExpression_ParsesCorrectly()
    {
      Assert.AreEqual(ConditionTokenType.Or, ((ConditionBinaryNode)Parse("{a} = 1 or {b} = 2")).Operator);
    }

    [TestMethod]
    public void Parse_ParenthesizedExpression_ParsesCorrectly()
    {
      var top = (ConditionBinaryNode)Parse("({hp} > 50 and {mana} > 10) or {godmode} = true");
      Assert.AreEqual(ConditionTokenType.Or, top.Operator);
      Assert.AreEqual(ConditionTokenType.And, ((ConditionBinaryNode)top.Left).Operator);
    }

    [TestMethod]
    public void Parse_NotExpression_ParsesCorrectly()
    {
      var node = Parse("not {enabled}");
      Assert.AreEqual(ConditionNodeType.Unary, node.NodeType);
      var unary = (ConditionUnaryNode)node;
      Assert.AreEqual(ConditionTokenType.Not, unary.Operator);
      Assert.AreEqual("enabled", ((ConditionVariableNode)unary.Operand).Name);
    }

    [TestMethod]
    public void Parse_NullComparison_ParsesCorrectly()
    {
      var bin = (ConditionBinaryNode)Parse("{s} != null");
      Assert.AreEqual(ConditionTokenType.NotEquals, bin.Operator);
      Assert.IsTrue(((ConditionLiteralNode)bin.Right).IsNull);
    }

    [TestMethod]
    public void Parse_QuotedString_ParsesCorrectly()
    {
      var bin = (ConditionBinaryNode)Parse("{s} == \"hello world\"");
      Assert.AreEqual("hello world", ((ConditionLiteralNode)bin.Right).StringValue);
    }

    [TestMethod]
    public void Parse_SingleQuotedString_ParsesCorrectly()
    {
      var bin = (ConditionBinaryNode)Parse("{s} eq 'test'");
      Assert.AreEqual("test", ((ConditionLiteralNode)bin.Right).StringValue);
    }

    [TestMethod]
    public void Parse_BooleanLiteral_ParsesCorrectly()
    {
      var bin = (ConditionBinaryNode)Parse("{flag} = true");
      var lit = (ConditionLiteralNode)bin.Right;
      Assert.AreEqual(ConditionTokenType.Boolean, lit.Type);
      Assert.IsTrue(lit.BooleanValue);
    }

    [TestMethod]
    public void Parse_VariableToVariableComparison_ParsesCorrectly()
    {
      var bin = (ConditionBinaryNode)Parse("{a} = {b}");
      Assert.AreEqual(ConditionNodeType.Variable, bin.Left.NodeType);
      Assert.AreEqual(ConditionNodeType.Variable, bin.Right.NodeType);
      Assert.AreEqual("a", ((ConditionVariableNode)bin.Left).Name);
      Assert.AreEqual("b", ((ConditionVariableNode)bin.Right).Name);
    }

    [TestMethod]
    public void Parse_DollarPrefixedVariable_ParsesCorrectly()
    {
      var bin = (ConditionBinaryNode)Parse("${hello} == 'test'");
      Assert.AreEqual(ConditionNodeType.Variable, bin.Left.NodeType);
      Assert.AreEqual("hello", ((ConditionVariableNode)bin.Left).Name);
    }

    [TestMethod]
    public void Parse_MixedDollarAndBraceOnly_ParsesCorrectly()
    {
      var top = (ConditionBinaryNode)Parse("{test} > 3 && ${hello} == 'test'");
      Assert.AreEqual(ConditionTokenType.And, top.Operator);
      Assert.AreEqual("test", ((ConditionVariableNode)((ConditionBinaryNode)top.Left).Left).Name);
      Assert.AreEqual("hello", ((ConditionVariableNode)((ConditionBinaryNode)top.Right).Left).Name);
    }

    [TestMethod]
    public void Parse_ChainedAnds_ParsesCorrectly()
    {
      var top = (ConditionBinaryNode)Parse("{a} > 1 and {b} > 2 and {c} > 3");
      Assert.AreEqual(ConditionTokenType.And, top.Operator);
      Assert.AreEqual(ConditionTokenType.And, ((ConditionBinaryNode)top.Left).Operator);
    }

    [TestMethod]
    public void Parse_ChainedOrs_ParsesCorrectly()
    {
      Assert.AreEqual(ConditionTokenType.Or, ((ConditionBinaryNode)Parse("{a} = 1 or {b} = 2 or {c} = 3")).Operator);
    }

    [TestMethod]
    public void Parse_AndPrecedenceOverOr_ParsesCorrectly()
    {
      var top = (ConditionBinaryNode)Parse("{a} = 1 and {b} = 2 or {c} = 3");
      Assert.AreEqual(ConditionTokenType.Or, top.Operator);
      Assert.AreEqual(ConditionTokenType.And, ((ConditionBinaryNode)top.Left).Operator);
    }

    [TestMethod]
    public void Parse_NestedParentheses_ParsesCorrectly()
    {
      var bin = (ConditionBinaryNode)Parse("((({a} > 1)))");
      Assert.AreEqual("a", ((ConditionVariableNode)bin.Left).Name);
      Assert.AreEqual(ConditionTokenType.Greater, bin.Operator);
    }

    [TestMethod]
    public void Parse_DoubleNot_ParsesCorrectly()
    {
      var outer = (ConditionUnaryNode)Parse("not not {a}");
      Assert.AreEqual(ConditionTokenType.Not, outer.Operator);
      Assert.AreEqual(ConditionTokenType.Not, ((ConditionUnaryNode)outer.Operand).Operator);
    }

    [TestMethod]
    public void Parse_NotWithParentheses_ParsesCorrectly()
    {
      var unary = (ConditionUnaryNode)Parse("not ({a} and {b})");
      Assert.AreEqual(ConditionTokenType.Not, unary.Operator);
      Assert.AreEqual(ConditionNodeType.Binary, unary.Operand.NodeType);
    }

    [TestMethod]
    public void Parse_StandaloneNumber_ParsesCorrectly()
    {
      var node = Parse("42");
      Assert.AreEqual(ConditionNodeType.Literal, node.NodeType);
      var lit = (ConditionLiteralNode)node;
      Assert.AreEqual(ConditionTokenType.Number, lit.Type);
      Assert.AreEqual(42.0, lit.NumberValue);
    }

    [TestMethod]
    public void Parse_StandaloneBareword_ParsesCorrectly()
    {
      var node = Parse("hello");
      Assert.AreEqual(ConditionNodeType.Literal, node.NodeType);
      var lit = (ConditionLiteralNode)node;
      Assert.AreEqual(ConditionTokenType.String, lit.Type);
      Assert.AreEqual("hello", lit.StringValue);
    }

    [TestMethod]
    public void Parse_EmptyBraces_ParsesAsEmptyVariable()
    {
      var node = Parse("{}");
      Assert.AreEqual(ConditionNodeType.Variable, node.NodeType);
      Assert.AreEqual("", ((ConditionVariableNode)node).Name);
    }

    [TestMethod]
    public void Parse_NegativeNumber_ParsesCorrectly()
    {
      Assert.AreEqual(-42.0, ((ConditionLiteralNode)((ConditionBinaryNode)Parse("{temp} < -42")).Right).NumberValue);
    }

    [TestMethod]
    public void Parse_DecimalNumber_ParsesCorrectly()
    {
      Assert.AreEqual(12.5, ((ConditionLiteralNode)((ConditionBinaryNode)Parse("{value} >= 12.5")).Right).NumberValue);
    }

    [TestMethod]
    public void Parse_EmptyStringLiteral_ParsesCorrectly()
    {
      Assert.AreEqual("", ((ConditionLiteralNode)((ConditionBinaryNode)Parse("{s} = \"\"")).Right).StringValue);
    }

    [TestMethod]
    public void Parse_StandaloneVariable_ParsesCorrectly()
    {
      var node = Parse("{enabled}");
      Assert.AreEqual(ConditionNodeType.Variable, node.NodeType);
      Assert.AreEqual("enabled", ((ConditionVariableNode)node).Name);
    }

    [TestMethod]
    public void Parse_NestingDepth_AtLimit_ParsesCorrectly()
    {
      Assert.AreEqual(ConditionNodeType.Binary, Parse(new string('(', 10) + "{a} > 1" + new string(')', 10)).NodeType);
    }

    [TestMethod]
    [DataRow(11)]
    [DataRow(20)]
    public void ParenthesisDepthPastTheLimitIsRefused(int depth)
    {
      Assert.IsNull(ConditionParser.Parse(new string('(', depth) + "{a} > 1" + new string(')', depth)));
    }

    [TestMethod]
    public void Parse_NestingDepth_MixedParensAndNot_AtLimit_ParsesCorrectly()
    {
      _ = Parse("not not not not not not not not (({a}))");
    }

    [TestMethod]
    public void Parse_NestingDepth_MixedParensAndNot_ExceedsLimit_ReturnsNull()
    {
      Assert.IsNull(ConditionParser.Parse("not not not ((((((((({a}))))))))))"));
    }

    [TestMethod]
    public void Parse_NestingDepth_ChainedComparisons_DoesNotCountAsNesting()
    {
      _ = Parse("{a} > 1 and {b} > 2 and {c} > 3 and {d} > 4 and {e} > 5 and {f} > 6 and "
        + "{g} > 7 and {h} > 8 and {i} > 9 and {j} > 10 and {k} > 11");
    }

    [TestMethod]
    public void Parse_NestingDepth_ComplexExpression_AtLimit_ParsesCorrectly()
    {
      var bin = (ConditionBinaryNode)Parse(new string('(', 10) + "{a} > 1 and {b} < 2" + new string(')', 10));
      Assert.AreEqual(ConditionTokenType.And, bin.Operator);
    }

    [TestMethod]
    public void Parse_NestingDepth_OnlyNotOperators_ExceedsLimit_ReturnsNull()
    {
      Assert.IsNull(ConditionParser.Parse("not not not not not not not not not not not {a}"));
    }

    [TestMethod]
    public void Parse_NestingDepth_OnlyNotOperators_AtLimit_ParsesCorrectly()
    {
      _ = Parse("not not not not not not not not not not {a}");
    }

    [TestMethod]
    public void Parse_NumberWithLeadingZeros_ParsesCorrectly()
    {
      Assert.AreEqual(7.0, ((ConditionLiteralNode)((ConditionBinaryNode)Parse("{a} > 007")).Right).NumberValue);
    }

    [TestMethod]
    public void Parse_LargeNumber_ParsesCorrectly()
    {
      Assert.AreEqual(999999999999.0,
        ((ConditionLiteralNode)((ConditionBinaryNode)Parse("{a} > 999999999999")).Right).NumberValue);
    }

    [TestMethod]
    public void Parse_QuotedStringWithSpaces_PreservesSpaces()
    {
      Assert.AreEqual("hello   world",
        ((ConditionLiteralNode)((ConditionBinaryNode)Parse("{a} = \"hello   world\"")).Right).StringValue);
    }

    private static ConditionNode Parse(string expression)
    {
      var node = ConditionParser.Parse(expression);
      Assert.IsNotNull(node);
      return node;
    }
  }
}
