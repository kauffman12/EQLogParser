using System;

namespace EQLogParser
{
  /* Evaluates a parsed condition expression AST against a variable resolver.
   * The resolver is a function that maps variable names to their string values.
   * Returns null for unset variables. */
  public static class ConditionEvaluator
  {
    /* Delegate for resolving variable names to values. Return null for unset variables. */
    public delegate string VariableResolver(string name);

    /* Evaluate a condition AST node against the given variable resolver.
     * A null node returns true (no condition = always passes). */
    public static bool Evaluate(ConditionNode node, VariableResolver resolve)
    {
      if (node is null)
        return true; // No condition = always passes

      return EvaluateNode(node, resolve);
    }

    private static bool EvaluateNode(ConditionNode node, VariableResolver resolve)
    {
      return node.NodeType switch
      {
        ConditionNodeType.Binary => EvaluateBinary((ConditionBinaryNode)node, resolve),
        ConditionNodeType.Unary => EvaluateUnary((ConditionUnaryNode)node, resolve),
        ConditionNodeType.Variable => EvaluateVariable((ConditionVariableNode)node, resolve),
        ConditionNodeType.Literal => EvaluateLiteral((ConditionLiteralNode)node),
        _ => false,
      };
    }

    private static bool EvaluateBinary(ConditionBinaryNode node, VariableResolver resolve)
    {
      // Short-circuit evaluation for boolean operators
      if (node.Operator == ConditionTokenType.Or)
      {
        if (EvaluateNode(node.Left, resolve)) return true;
        return EvaluateNode(node.Right, resolve);
      }
      if (node.Operator == ConditionTokenType.And)
      {
        if (!EvaluateNode(node.Left, resolve)) return false;
        return EvaluateNode(node.Right, resolve);
      }

      // Comparison operators
      var left = ResolveValue(node.Left, resolve);
      var right = ResolveValue(node.Right, resolve);
      return Compare(left, node.Left, node.Operator, right, node.Right);
    }

    private static bool EvaluateUnary(ConditionUnaryNode node, VariableResolver resolve)
    {
      if (node.Operator == ConditionTokenType.Not)
        return !EvaluateNode(node.Operand, resolve);
      return false;
    }

    private static bool EvaluateVariable(ConditionVariableNode node, VariableResolver resolve)
    {
      var value = resolve(node.Name);
      // A variable is "truthy" if it's set and non-empty
      return !string.IsNullOrEmpty(value);
    }

    private static bool EvaluateLiteral(ConditionLiteralNode node)
    {
      return node.Type switch
      {
        ConditionTokenType.Boolean => node.BooleanValue,
        ConditionTokenType.Null => false, // null literal is falsy in standalone context
        _ => true, // strings and numbers are truthy
      };
    }

    /* Resolve a node to its raw string value for comparisons. */
    private static string ResolveValue(ConditionNode node, VariableResolver resolve)
    {
      return node.NodeType switch
      {
        ConditionNodeType.Variable => resolve(((ConditionVariableNode)node).Name),
        ConditionNodeType.Literal => ResolveLiteralValue((ConditionLiteralNode)node),
        _ => null, // Sub-expressions shouldn't appear as comparison operands in valid ASTs
      };
    }

    private static string ResolveLiteralValue(ConditionLiteralNode node)
    {
      return node.Type switch
      {
        ConditionTokenType.String => node.StringValue,
        ConditionTokenType.Number => node.NumberValue.ToString(),
        ConditionTokenType.Boolean => node.BooleanValue ? "true" : "false",
        ConditionTokenType.Null => null,
        _ => null,
      };
    }

    private static bool Compare(string left, ConditionNode leftNode, ConditionTokenType op, string right, ConditionNode rightNode)
    {
      // Null equality checks — distinguish "two unset vars" from "explicit null literal"
      if (op == ConditionTokenType.Equals)
        return Equals(left, leftNode, right, rightNode);
      if (op == ConditionTokenType.NotEquals)
        return !Equals(left, leftNode, right, rightNode);

      // Contains (case-insensitive) — null right-hand side is falsy
      if (op == ConditionTokenType.Contains)
        return right is not null && (left?.Contains(right, StringComparison.OrdinalIgnoreCase) ?? false);

      // Numeric comparisons: try to parse both sides as doubles.
      // Unset (null) variables are treated as 0 so that conditions like
      // {hp} < 50 evaluate intuitively even before hp has been set.
      // Non-numeric strings still cause the comparison to fail (return false).
      var lVal = left is null ? 0 : TextUtils.ParseDouble(left.AsSpan());
      var rVal = right is null ? 0 : TextUtils.ParseDouble(right.AsSpan());

      if (double.IsNaN(lVal) || double.IsNaN(rVal))
        return false; // Non-numeric string value can't be compared numerically

      return op switch
      {
        ConditionTokenType.Greater => lVal > rVal,
        ConditionTokenType.GreaterEqual => lVal >= rVal,
        ConditionTokenType.Less => lVal < rVal,
        ConditionTokenType.LessEqual => lVal <= rVal,
        _ => false,
      };
    }

    private static bool Equals(string left, ConditionNode leftNode, string right, ConditionNode rightNode)
    {
      // Handle null cases:
      // - {var} = null literal → true when var is unset (explicit null check)
      // - {var1} = {var2} → false when both are unset (two unset vars are not equal)
      if (left is null || right is null)
      {
        var leftIsNullLiteral = leftNode is ConditionLiteralNode ln && ln.Type == ConditionTokenType.Null;
        var rightIsNullLiteral = rightNode is ConditionLiteralNode rn && rn.Type == ConditionTokenType.Null;

        // If at least one side is an explicit null literal, treat as a null check
        if (leftIsNullLiteral || rightIsNullLiteral)
          return left is null && right is null; // both must be null for equality

        // Both are unset variables — not equal
        return false;
      }

      return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
  }
}
