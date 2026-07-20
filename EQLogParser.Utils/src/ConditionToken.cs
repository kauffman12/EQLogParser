using System;

namespace EQLogParser
{
    /// <summary>Token types for the variable condition expression language.</summary>
    public enum ConditionTokenType
    {
        End,
        Variable,       // {name}
        String,         // "hello" or 'hello' or bareword
        Number,         // 123, -42, 12.5
        Boolean,        // true / false
        Null,           // null

        Equals,         // = == eq
        NotEquals,      // != <> neq
        Greater,        // > gt
        GreaterEqual,   // >= ge gte
        Less,           // < lt
        LessEqual,      // <= le lte
        Contains,       // contains

        And,            // and &&
        Or,             // or ||
        Not,            // not !

        LeftParen,      // (
        RightParen,     // )
    }

    /// <summary>A single token produced by the condition expression tokenizer.</summary>
    public readonly struct ConditionToken
    {
        public ConditionTokenType Type { get; }
        public string RawText { get; }
        public string VariableName { get; }  // Set when Type == Variable
        public double NumberValue { get; }     // Set when Type == Number
        public bool BooleanValue { get; }      // Set when Type == Boolean

        public ConditionToken(ConditionTokenType type, string rawText)
        {
            Type = type;
            RawText = rawText;
            VariableName = null!;
            NumberValue = 0;
            BooleanValue = false;
        }

        public ConditionToken(ConditionTokenType type, string rawText, string variableName)
        {
            Type = type;
            RawText = rawText;
            VariableName = variableName;
            NumberValue = 0;
            BooleanValue = false;
        }

        public ConditionToken(ConditionTokenType type, string rawText, double numberValue)
        {
            Type = type;
            RawText = rawText;
            VariableName = null!;
            NumberValue = numberValue;
            BooleanValue = false;
        }

        public ConditionToken(ConditionTokenType type, string rawText, bool booleanValue)
        {
            Type = type;
            RawText = rawText;
            VariableName = null!;
            NumberValue = 0;
            BooleanValue = booleanValue;
        }
    }
}
