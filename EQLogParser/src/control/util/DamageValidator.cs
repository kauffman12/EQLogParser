namespace EQLogParser
{
  internal class DamageValidator
  {
    private readonly bool _assassinateEnabled = AppSettings.IsAssassinateDamageEnabled;
    private readonly bool _baneEnabled = AppSettings.IsBaneDamageEnabled;
    private readonly bool _dsEnabled = AppSettings.IsDamageShieldDamageEnabled;
    private readonly bool _finishingBlowEnabled = AppSettings.IsFinishingBlowDamageEnabled;
    private readonly bool _headshotEnabled = AppSettings.IsHeadshotDamageEnabled;
    private readonly bool _slayUndeadEnabled = AppSettings.IsSlayUndeadDamageEnabled;

    // save this up front. we work with a constant state for their values

    /// <summary>
    /// Validates if the damage record should be processed based on current settings.
    /// </summary>
    public bool IsValid(DamageRecord record)
    {
      if (LineModifiersParser.IsAssassinate(record.ModifiersMask) && !_assassinateEnabled)
      {
        return false;
      }

      if (record.Type == Labels.Bane && !_baneEnabled)
      {
        return false;
      }

      if (record.Type == Labels.Ds && !_dsEnabled)
      {
        return false;
      }

      if (LineModifiersParser.IsFinishingBlow(record.ModifiersMask) && !_finishingBlowEnabled)
      {
        return false;
      }

      if (LineModifiersParser.IsHeadshot(record.ModifiersMask) && !_headshotEnabled)
      {
        return false;
      }

      if (LineModifiersParser.IsSlayUndead(record.ModifiersMask) && !_slayUndeadEnabled)
      {
        return false;
      }

      return true;
    }

    /// <summary>
    /// Static version that takes settings as parameters for inlining at call sites.
    /// Avoids allocating a DamageValidator instance per call.
    /// </summary>
    internal static bool IsDamageValid(short modifiersMask, string type,
      bool isAssEnabled, bool isBaneEnabled, bool isDsEnabled,
      bool isFinishingEnabled, bool headshotEnabled, bool isSlayUndeadEnabled)
    {
      if (LineModifiersParser.IsAssassinate(modifiersMask) && !isAssEnabled) return false;
      if (type == Labels.Bane && !isBaneEnabled) return false;
      if (type == Labels.Ds && !isDsEnabled) return false;
      if (LineModifiersParser.IsFinishingBlow(modifiersMask) && !isFinishingEnabled) return false;
      if (LineModifiersParser.IsHeadshot(modifiersMask) && !headshotEnabled) return false;
      if (LineModifiersParser.IsSlayUndead(modifiersMask) && !isSlayUndeadEnabled) return false;
      return true;
    }

    /// <summary>
    /// Checks if any damage types are currently filtered out.
    /// </summary>
    public bool IsDamageLimited()
    {
      return !_dsEnabled || !_assassinateEnabled || !_baneEnabled || !_finishingBlowEnabled || !_headshotEnabled || !_slayUndeadEnabled;
    }
  }
}
