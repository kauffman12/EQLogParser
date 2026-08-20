using Riok.Mapperly.Abstractions;

namespace EQLogParser
{
  /* Deep-cloning mappers for UI-only models. The trigger store model mappings live in the
   * cross-platform core (EQLogParser.Core ModelMapper). */
  [Mapper(UseDeepCloning = true)]
  internal static partial class ModelMapper
  {
    public static partial LootRecord Clone(this LootRecord source);
  }
}
