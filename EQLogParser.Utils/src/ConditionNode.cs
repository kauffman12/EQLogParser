namespace EQLogParser
{
  /// <summary>Base class for all condition expression AST nodes.</summary>
  public abstract class ConditionNode
  {
    public abstract ConditionNodeType NodeType { get; }
  }

  public enum ConditionNodeType
  {
    Binary,
    Unary,
    Variable,
    Literal,
  }

  /// <summary>A binary expression: left op right (e.g. {hp} > 50)</summary>
  public class ConditionBinaryNode : ConditionNode
  {
    public override ConditionNodeType NodeType => ConditionNodeType.Binary;
    public ConditionNode Left { get; set; } = null!;
    public ConditionTokenType Operator { get; set; }
    public ConditionNode Right { get; set; } = null!;
  }

  /// <summary>A unary expression: not operand (e.g. not {enabled})</summary>
  public class ConditionUnaryNode : ConditionNode
  {
    public override ConditionNodeType NodeType => ConditionNodeType.Unary;
    public ConditionTokenType Operator { get; set; }  // Not
    public ConditionNode Operand { get; set; } = null!;
  }

  /// <summary>A variable reference: {name}</summary>
  public class ConditionVariableNode : ConditionNode
  {
    public override ConditionNodeType NodeType => ConditionNodeType.Variable;
    public string Name { get; set; } = "";
  }

  /// <summary>A literal value: string, number, boolean, or null.</summary>
  public class ConditionLiteralNode : ConditionNode
  {
    public override ConditionNodeType NodeType => ConditionNodeType.Literal;
    public ConditionTokenType Type { get; set; }  // String, Number, Boolean, Null
    public string StringValue { get; set; }
    public double NumberValue { get; set; }
    public bool BooleanValue { get; set; }

    /// <summary>True if this literal represents a null value.</summary>
    public bool IsNull => Type == ConditionTokenType.Null;
  }
}
