using Riok.Mapperly.Abstractions;

namespace EQLogParser
{
  /* Deep-cloning mappers for Core domain models (trigger store + combat log records). */
  [Mapper(UseDeepCloning = true)]
  internal static partial class ModelMapper
  {
    public static partial LootRecord Clone(this LootRecord source);
    public static partial Trigger Clone(this Trigger source);
    public static partial Overlay Clone(this Overlay source);
    public static partial TriggerNode Clone(this TriggerNode source);
    // OriginalId must survive the copy — Import() matches re-imported NAG nodes by it.
    [MapProperty(nameof(ExportTriggerNode.OriginalId), nameof(TriggerNode.OriginalId))]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Mapper", "RMG020:Source member is not mapped to any target member", Justification = "Local Member")]
    public static partial TriggerNode ToTriggerNode(this ExportTriggerNode source);
  }
}
