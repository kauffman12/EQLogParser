using System;

namespace EQLogParser
{
  /// <summary>
  /// Host-provided lookups for data owned by WPF-side stores (EQDataStore's
  /// spell/class knowledge) and localization labels. Null/empty defaults let
  /// Core code degrade gracefully when the host has not wired the hooks (e.g.
  /// in tests). Wired by the WPF host in App.xaml.cs.
  /// </summary>
  internal static class CombatRecordLookup
  {
    /// <summary>Looks up a healing spell by name (host: EQDataStore). Null when unknown.</summary>
    public static Func<string, SpellData> HealingSpellByName { get; set; } = _ => null;

    /// <summary>Whether the given name is a player spell (host: EQDataStore).</summary>
    public static Func<string, bool> IsPlayerSpell { get; set; } = _ => false;

    /// <summary>Whether the given string is a known player class name (host: EQDataStore).</summary>
    public static Func<string, bool> IsValidClassName { get; set; } = _ => false;

    /// <summary>Maps a player class name to its SpellClass bitmask (host: EQDataStore). Null when unknown.</summary>
    public static Func<string, SpellClass?> ClassEnumByName { get; set; } = _ => null;

    /// <summary>Class name labels used when verifying player abilities (host: resx labels).</summary>
    public static string RogueClass { get; set; }
    public static string RangerClass { get; set; }
    public static string PaladinClass { get; set; }
  }
}
