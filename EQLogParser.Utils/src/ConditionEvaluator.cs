using System;

namespace EQLogParser
{
    /// <summary>
    /// Evaluates a parsed condition expression AST against a variable resolver.
    /// The resolver is a function that maps variable names to their string values.
    /// Returns null for unset variables.
    /// </summary>
    public static class ConditionEvaluator
    {
        /// <summary>Delegate for resolving variable names to values. Return null for unset variables.</summary>
        public delegate string VariableResolver(string name);

        /// <summary>Evaluate a condition AST node against the given variable resolver.</summary>
        /// <param name="node">The parsed AST node (null means always true / no condition).</param>
        /// <param name="resolve">Function to resolve variable names to values.</param>
        /// <returns>true if the condition evaluates to true, false otherwise. Null node returns true.</returns>
        public static bool Evaluate(ConditionNode node, VariableResolver resolve)
        {
            if (node == null)
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
            return Compare(left, node.Operator, right);
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

        /// <summary>Resolve a node to its raw string value for comparisons.</summary>
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

        private static bool Compare(string left, ConditionTokenType op, string right)
        {
            // Null equality checks
            if (op == ConditionTokenType.Equals)
                return Equals(left, right);
            if (op == ConditionTokenType.NotEquals)
                return !Equals(left, right);

            // Contains (case-insensitive) — null right-hand side is falsy
            if (op == ConditionTokenType.Contains)
                return right != null && (left?.Contains(right, StringComparison.OrdinalIgnoreCase) ?? false);

            // Numeric comparisons: try to parse both sides as doubles
            if (!double.TryParse(left, out var lVal))
                return false;
            if (!double.TryParse(right, out var rVal))
                return false;

            return op switch
            {
                ConditionTokenType.Greater => lVal > rVal,
                ConditionTokenType.GreaterEqual => lVal >= rVal,
                ConditionTokenType.Less => lVal < rVal,
                ConditionTokenType.LessEqual => lVal <= rVal,
                _ => false,
            };
        }

        private static bool Equals(string left, string right)
        {
            if (left == null && right == null) return true;
            if (left == null || right == null) return false;
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
