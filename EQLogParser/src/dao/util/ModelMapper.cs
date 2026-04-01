using Riok.Mapperly.Abstractions;

namespace EQLogParser;

[Mapper(UseDeepCloning = true)]
internal static partial class ModelMapper
{
  public static partial Trigger Clone(this Trigger source);
  public static partial Overlay Clone(this Overlay source);
  public static partial TriggerNode Clone(this TriggerNode source);
  public static partial TriggerNode ToTriggerNode(this ExportTriggerNode source);
  public static partial Overlay ToOverlay(this LegacyOverlay source);
  public static partial LootRecord Clone(this LootRecord source);
}