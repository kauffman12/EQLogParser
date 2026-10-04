namespace EQLogParser
{
  /// <summary>
  /// Centralized application settings shared between UI and core layers.
  /// UI code writes values; core code (parsing, control, dao) reads values.
  /// All writes happen on the UI thread; reads are thread-safe via volatile.
  /// </summary>
  internal static class AppSettings
  {
    /// <summary>
    /// Path to the currently loaded log file.
    /// </summary>
    public static volatile string CurrentLogFile;

    /// <summary>
    /// Whether AoE healing spells should be included in healing summaries.
    /// </summary>
    public static volatile bool IsAoEHealingEnabled = true;

    /// <summary>
    /// Whether healing to swarm pets should be included in healing summaries.
    /// </summary>
    public static volatile bool IsHealingSwarmPetsEnabled = true;

    /// <summary>
    /// Whether assassinate damage should be included in damage summaries.
    /// </summary>
    public static volatile bool IsAssassinateDamageEnabled = true;

    /// <summary>
    /// Whether bane damage should be included in damage summaries.
    /// </summary>
    public static volatile bool IsBaneDamageEnabled = true;

    /// <summary>
    /// Whether damage shield damage should be included in damage summaries.
    /// </summary>
    public static volatile bool IsDamageShieldDamageEnabled = true;

    /// <summary>
    /// Whether finishing blow damage should be included in damage summaries.
    /// </summary>
    public static volatile bool IsFinishingBlowDamageEnabled = true;

    /// <summary>
    /// Whether headshot damage should be included in damage summaries.
    /// </summary>
    public static volatile bool IsHeadshotDamageEnabled = true;

    /// <summary>
    /// Whether slay undead damage should be included in damage summaries.
    /// </summary>
    public static volatile bool IsSlayUndeadDamageEnabled = true;

    /// <summary>
    /// Whether EMU (EqEMU) parsing mode is enabled.
    /// </summary>
    public static volatile bool IsEmuParsingEnabled;

    /// <summary>
    /// Whether the damage overlay window is currently open.
    /// </summary>
    public static volatile bool IsDamageOverlayOpen;

    /// <summary>
    /// Whether Ctrl+C in SendToEQ should map to the SendToEQ action instead of copy.
    /// </summary>
    public static volatile bool IsMapSendToEqEnabled;

    /// <summary>
    /// The string offset (exclusive) where the action begins in chat log lines.
    /// Corresponds to the fixed-length timestamp prefix in EQ chat archives.
    /// </summary>
    public const int ActionIndex = 27;
  }
}
