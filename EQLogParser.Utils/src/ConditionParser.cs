using System;
using System.Collections.Generic;

namespace EQLogParser
{
  /// <summary>
  /// Parses variable condition expressions into an AST.
  /// Grammar: expression -> and (OR and)* ; and -> unary (AND unary)* ; unary -> NOT unary | comparison
  /// comparison -> primary (OP primary)? ; primary -> variable | literal | '(' expression ')'
  /// </summary>
  public static class ConditionParser
  {
    /// <summary>Maximum allowed nesting depth for parentheses and unary operators to prevent stack overflow.</summary>
    private const int MaxNestingDepth = 10;

    // Operator keywords mapped to token types (case-insensitive)
    private static readonly Dictionary<string, ConditionTokenType> Operators = new(StringComparer.OrdinalIgnoreCase)
    {
      ["="] = ConditionTokenType.Equals,
      ["=="] = ConditionTokenType.Equals,
      ["eq"] = ConditionTokenType.Equals,
      ["!="] = ConditionTokenType.NotEquals,
      ["<>"] = ConditionTokenType.NotEquals,
      ["neq"] = ConditionTokenType.NotEquals,
      [">"] = ConditionTokenType.Greater,
      ["gt"] = ConditionTokenType.Greater,
      [">="] = ConditionTokenType.GreaterEqual,
      ["ge"] = ConditionTokenType.GreaterEqual,
      ["gte"] = ConditionTokenType.GreaterEqual,
      ["<"] = ConditionTokenType.Less,
      ["lt"] = ConditionTokenType.Less,
      ["<="] = ConditionTokenType.LessEqual,
      ["le"] = ConditionTokenType.LessEqual,
      ["lte"] = ConditionTokenType.LessEqual,
      ["contains"] = ConditionTokenType.Contains,
      ["and"] = ConditionTokenType.And,
      ["&&"] = ConditionTokenType.And,
      ["or"] = ConditionTokenType.Or,
      ["||"] = ConditionTokenType.Or,
      ["not"] = ConditionTokenType.Not,
      ["!"] = ConditionTokenType.Not,
    };

    /// <summary>Parse a condition expression string into an AST node. Returns null on parse error.</summary>
    public static ConditionNode Parse(string expression)
    {
      if (string.IsNullOrWhiteSpace(expression))
        return null;

      var tokens = Tokenize(expression);
      if (tokens is null)
        return null; // e.g. unclosed brace while user is still typing

      var parser = new ParserContext(tokens);

      try
      {
        var node = ParseOr(parser);
        if (parser.Current.Type != ConditionTokenType.End)
          return null; // Unexpected trailing tokens
        return node;
      }
      catch
      {
        return null;
      }
    }

    private static List<ConditionToken> Tokenize(string input)
    {
      var tokens = new List<ConditionToken>();
      int i = 0;
      int len = input.Length;

      while (i < len)
      {
        // Skip whitespace
        if (char.IsWhiteSpace(input[i]))
        {
          i++;
          continue;
        }

        // Variable: {name} or ${name}
        if (input[i] == '{' || (input[i] == '$' && i + 1 < len && input[i + 1] == '{'))
        {
          int braceStart = input[i] == '$' ? i + 1 : i;
          int close = input.IndexOf('}', braceStart + 1);
          if (close < 0) return null; // Unclosed brace
          string rawText = input.Substring(i, close - i + 1);
          string name = input.Substring(braceStart + 1, close - braceStart - 1);
          tokens.Add(new ConditionToken(ConditionTokenType.Variable, rawText, name));
          i = close + 1;
          continue;
        }

        // Quoted string: "..." or '...'
        if (input[i] == '"' || input[i] == '\'')
        {
          char quote = input[i];
          int start = i;
          i++;
          while (i < len && input[i] != quote)
            i++;
          if (i >= len) return null; // Unclosed quote
          string value = input.Substring(start + 1, i - start - 1);
          tokens.Add(new ConditionToken(ConditionTokenType.String, input.Substring(start, i - start + 1), value));
          i++;
          continue;
        }

        // Parentheses
        if (input[i] == '(')
        {
          tokens.Add(new ConditionToken(ConditionTokenType.LeftParen, "("));
          i++;
          continue;
        }
        if (input[i] == ')')
        {
          tokens.Add(new ConditionToken(ConditionTokenType.RightParen, ")"));
          i++;
          continue;
        }

        // Number: optional minus, digits, optional decimal
        if (char.IsDigit(input[i]) || (input[i] == '-' && i + 1 < len && char.IsDigit(input[i + 1])))
        {
          int start = i;
          if (input[i] == '-') i++;
          while (i < len && char.IsDigit(input[i])) i++;
          if (i < len && input[i] == '.')
          {
            i++;
            while (i < len && char.IsDigit(input[i])) i++;
          }
          string numStr = input.Substring(start, i - start);
          if (double.TryParse(numStr, out double numVal))
          {
            tokens.Add(new ConditionToken(ConditionTokenType.Number, numStr, numVal));
          }
          else
          {
            return null; // Invalid number
          }
          continue;
        }

        // Operator symbols: == != >= <= && || = > < ! & |
        if ("=!<>&|".IndexOf(input[i]) >= 0)
        {
          // Try two-character operators first
          if (i + 1 < len)
          {
            string two = input.Substring(i, 2);
            if (Operators.TryGetValue(two, out var opTwo))
            {
              tokens.Add(new ConditionToken(opTwo, two));
              i += 2;
              continue;
            }
          }
          // Single character
          string one = input.Substring(i, 1);
          if (Operators.TryGetValue(one, out var opOne))
          {
            tokens.Add(new ConditionToken(opOne, one));
            i++;
            continue;
          }
          return null; // Unknown operator symbol
        }

        // Word: operator keyword, boolean literal, null literal, or bareword string
        if (char.IsLetter(input[i]) || input[i] == '_')
        {
          int start = i;
          while (i < len && (char.IsLetterOrDigit(input[i]) || input[i] == '_' || input[i] == '.'))
            i++;
          string word = input.Substring(start, i - start);

          // Check if it's an operator keyword
          if (Operators.TryGetValue(word, out var opType))
          {
            tokens.Add(new ConditionToken(opType, word));
            continue;
          }

          // Boolean literals
          if (string.Equals(word, "true", StringComparison.OrdinalIgnoreCase))
          {
            tokens.Add(new ConditionToken(ConditionTokenType.Boolean, word, true));
            continue;
          }
          if (string.Equals(word, "false", StringComparison.OrdinalIgnoreCase))
          {
            tokens.Add(new ConditionToken(ConditionTokenType.Boolean, word, false));
            continue;
          }

          // Null literal
          if (string.Equals(word, "null", StringComparison.OrdinalIgnoreCase))
          {
            tokens.Add(new ConditionToken(ConditionTokenType.Null, word));
            continue;
          }

          // Bareword string (e.g. "hello")
          tokens.Add(new ConditionToken(ConditionTokenType.String, word, word));
          continue;
        }

        // Unknown character
        return null;
      }

      tokens.Add(new ConditionToken(ConditionTokenType.End, null));
      return tokens;
    }

    // ---- Recursive Descent Parser ----

    private static ConditionNode ParseOr(ParserContext p)
    {
      var node = ParseAnd(p);
      while (p.Current.Type == ConditionTokenType.Or)
      {
        p.Next();
        var right = ParseAnd(p);
        node = new ConditionBinaryNode { Left = node, Operator = ConditionTokenType.Or, Right = right };
      }
      return node;
    }

    private static ConditionNode ParseAnd(ParserContext p)
    {
      var node = ParseUnary(p);
      while (p.Current.Type == ConditionTokenType.And)
      {
        p.Next();
        var right = ParseUnary(p);
        node = new ConditionBinaryNode { Left = node, Operator = ConditionTokenType.And, Right = right };
      }
      return node;
    }

    private static ConditionNode ParseUnary(ParserContext p)
    {
      if (p.Current.Type == ConditionTokenType.Not)
      {
        p.EnterAndCheckDepth();
        p.Next();
        var operand = ParseUnary(p);
        p.ExitDepth();
        return new ConditionUnaryNode { Operator = ConditionTokenType.Not, Operand = operand };
      }
      return ParseComparison(p);
    }

    private static ConditionNode ParseComparison(ParserContext p)
    {
      var left = ParsePrimary(p);

      // Check for comparison operator
      if (IsComparisonOp(p.Current.Type))
      {
        var op = p.Current.Type;
        p.Next();
        var right = ParsePrimary(p);
        return new ConditionBinaryNode { Left = left, Operator = op, Right = right };
      }

      return left;
    }

    private static bool IsComparisonOp(ConditionTokenType type) => type switch
    {
      ConditionTokenType.Equals or ConditionTokenType.NotEquals or
      ConditionTokenType.Greater or ConditionTokenType.GreaterEqual or
      ConditionTokenType.Less or ConditionTokenType.LessEqual or
      ConditionTokenType.Contains => true,
      _ => false,
    };

    private static ConditionNode ParsePrimary(ParserContext p)
    {
      var token = p.Current;

      // Parenthesized expression
      if (token.Type == ConditionTokenType.LeftParen)
      {
        p.EnterAndCheckDepth();
        p.Next();
        var node = ParseOr(p);
        if (p.Current.Type != ConditionTokenType.RightParen)
          throw new InvalidOperationException("Missing closing parenthesis");
        p.Next();
        p.ExitDepth();
        return node;
      }

      // Variable
      if (token.Type == ConditionTokenType.Variable)
      {
        p.Next();
        return new ConditionVariableNode { Name = token.VariableName ?? "" };
      }

      // Literal
      if (token.Type is ConditionTokenType.String or ConditionTokenType.Number or
        ConditionTokenType.Boolean or ConditionTokenType.Null)
      {
        p.Next();
        return new ConditionLiteralNode
        {
          Type = token.Type,
          StringValue = token.Type == ConditionTokenType.String ? (token.VariableName ?? "") : (token.RawText ?? ""),
          NumberValue = token.NumberValue,
          BooleanValue = token.BooleanValue,
        };
      }

      throw new InvalidOperationException($"Unexpected token: {token.Type}");
    }

    // ---- Parser Context ----

    private class ParserContext
    {
      private readonly List<ConditionToken> _tokens;
      private int _index;
      private int _depth;

      public ConditionToken Current => _tokens is not null && _index < _tokens.Count
        ? _tokens[_index]
        : new ConditionToken(ConditionTokenType.End, null);

      public ParserContext(List<ConditionToken> tokens)
      {
        _tokens = tokens;
        _index = 0;
        _depth = 0;
      }

      public void Next()
      {
        if (_index < _tokens.Count - 1)
          _index++;
      }

      public void ExitDepth() => _depth--;

      /// <summary>Increments depth and throws if the maximum nesting depth is exceeded.</summary>
      public void EnterAndCheckDepth()
      {
        _depth++;
        if (_depth > MaxNestingDepth)
          throw new InvalidOperationException("Expression nesting depth exceeded");
      }
    }
  }
}
