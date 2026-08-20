namespace EQLogParser
{
  internal class DamageValidator
  {
    private readonly bool _assassinateEnabled;
    private readonly bool _baneEnabled;
    private readonly bool _dsEnabled;
    private readonly bool _finishingBlowEnabled;
    private readonly bool _headshotEnabled;
    private readonly bool _slayUndeadEnabled;

    public DamageValidator(bool assassinateEnabled, bool baneEnabled, bool dsEnabled, bool finishingBlowEnabled, bool headshotEnabled, bool slayUndeadEnabled)
    {
      _assassinateEnabled = assassinateEnabled;
      _baneEnabled = baneEnabled;
      _dsEnabled = dsEnabled;
      _finishingBlowEnabled = finishingBlowEnabled;
      _headshotEnabled = headshotEnabled;
      _slayUndeadEnabled = slayUndeadEnabled;
    }

    /// <summary>
    /// Static helper to check if damage is valid for a specific type.
    /// </summary>
    internal static bool IsDamageValid(short modifiersMask, string type,
      bool assassinateEnabled, bool baneEnabled, bool dsEnabled,
      bool finishingBlowEnabled, bool headshotEnabled, bool slayUndeadEnabled)
    {
      if (LineModifiersParser.IsAssassinate(modifiersMask) && !assassinateEnabled)
      {
        return false;
      }

      if (type == Labels.Bane && !baneEnabled)
      {
        return false;
      }

      if (type == Labels.Ds && !dsEnabled)
      {
        return false;
      }

      if (LineModifiersParser.IsFinishingBlow(modifiersMask) && !finishingBlowEnabled)
      {
        return false;
      }

      if (LineModifiersParser.IsHeadshot(modifiersMask) && !headshotEnabled)
      {
        return false;
      }

      if (LineModifiersParser.IsSlayUndead(modifiersMask) && !slayUndeadEnabled)
      {
        return false;
      }

      return true;
    }

    /// <summary>
    /// Validates if the damage record should be processed based on current settings.
    /// </summary>
    internal bool IsValid(DamageRecord record)
    {
      if (record is null)
      {
        return false;
      }

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
    /// Checks if damage validation is limited (any special damage type disabled).
    /// </summary>
    internal bool IsDamageLimited()
    {
      return !_assassinateEnabled || !_baneEnabled || !_dsEnabled || !_finishingBlowEnabled || !_headshotEnabled || !_slayUndeadEnabled;
    }

  }
}
