
namespace EQLogParser
{
  internal class DamageValidator
  {
    private readonly bool _assassinateEnabled = MainWindow.IsAssassinateDamageEnabled;
    private readonly bool _baneEnabled = MainWindow.IsBaneDamageEnabled;
    private readonly bool _dsEnabled = MainWindow.IsDamageShieldDamageEnabled;
    private readonly bool _finishingBlowEnabled = MainWindow.IsFinishingBlowDamageEnabled;
    private readonly bool _headshotEnabled = MainWindow.IsHeadshotDamageEnabled;
    private readonly bool _slayUndeadEnabled = MainWindow.IsSlayUndeadDamageEnabled;

    // save this up front. we work with a constant state for their values

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

    public bool IsDamageLimited()
    {
      return !_dsEnabled || !_assassinateEnabled || !_baneEnabled || !_finishingBlowEnabled || !_headshotEnabled || !_slayUndeadEnabled;
    }
  }
}
