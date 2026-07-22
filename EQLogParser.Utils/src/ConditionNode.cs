namespace EQLogParser
{
  /* Base class for all condition expression AST nodes. */
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

  /* A binary expression: left op right (e.g. {hp} > 50) */
  public class ConditionBinaryNode : ConditionNode
  {
    public override ConditionNodeType NodeType => ConditionNodeType.Binary;
    public ConditionNode Left { get; set; } = null;
    public ConditionTokenType Operator { get; set; }
    public ConditionNode Right { get; set; } = null;
  }

  /* A unary expression: not operand (e.g. not {enabled}) */
  public class ConditionUnaryNode : ConditionNode
  {
    public override ConditionNodeType NodeType => ConditionNodeType.Unary;
    public ConditionTokenType Operator { get; set; }  // Not
    public ConditionNode Operand { get; set; } = null;
  }

  /* A variable reference: {name} */
  public class ConditionVariableNode : ConditionNode
  {
    public override ConditionNodeType NodeType => ConditionNodeType.Variable;
    public string Name { get; set; } = "";
  }

  /* A literal value: string, number, boolean, or null. */
  public class ConditionLiteralNode : ConditionNode
  {
    public override ConditionNodeType NodeType => ConditionNodeType.Literal;
    public ConditionTokenType Type { get; set; }  // String, Number, Boolean, Null
    public string StringValue { get; set; }
    public double NumberValue { get; set; }
    public bool BooleanValue { get; set; }

    /* True if this literal represents a null value. */
    public bool IsNull => Type == ConditionTokenType.Null;
  }
}
