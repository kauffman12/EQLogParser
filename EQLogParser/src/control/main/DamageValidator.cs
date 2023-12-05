
namespace EQLogParser
{
  class DamageValidator
  {
    private readonly bool _assassinateEnabled;
    private readonly bool _baneEnabled;
    private readonly bool _dsEnabled;
    private readonly bool _finishingBlowEnabled;
    private readonly bool _headshotEnabled;
    private readonly bool _slayUndeadEnabled;

    public DamageValidator()
    {
      // save this up front so we work with a constant state for their values
      _assassinateEnabled = MainWindow.IsAssassinateDamageEnabled;
      _baneEnabled = MainWindow.IsBaneDamageEnabled;
      _dsEnabled = MainWindow.IsDamageShieldDamageEnabled;
      _finishingBlowEnabled = MainWindow.IsFinishingBlowDamageEnabled;
      _headshotEnabled = MainWindow.IsHeadshotDamageEnabled;
      _slayUndeadEnabled = MainWindow.IsSlayUndeadDamageEnabled;
    }

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
      return !_assassinateEnabled || !_baneEnabled || !_finishingBlowEnabled || !_headshotEnabled || !_slayUndeadEnabled;
    }
  }
}
