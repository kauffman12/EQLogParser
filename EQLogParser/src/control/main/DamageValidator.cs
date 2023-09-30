
namespace EQLogParser
{
  class DamageValidator
  {
    private bool AssassinateEnabled;
    private bool BaneEnabled;
    private bool DSEnabled;
    private bool FinishingBlowEnabled;
    private bool HeadshotEnabled;
    private bool SlayUndeadEnabled;

    public DamageValidator()
    {
      // save this up front so we work with a constant state for their values
      AssassinateEnabled = MainWindow.IsAssassinateDamageEnabled;
      BaneEnabled = MainWindow.IsBaneDamageEnabled;
      DSEnabled = MainWindow.IsDamageShieldDamageEnabled;
      FinishingBlowEnabled = MainWindow.IsFinishingBlowDamageEnabled;
      HeadshotEnabled = MainWindow.IsHeadshotDamageEnabled;
      SlayUndeadEnabled = MainWindow.IsSlayUndeadDamageEnabled;
    }

    public bool IsValid(DamageRecord record)
    {
      if (LineModifiersParser.IsAssassinate(record.ModifiersMask) && !AssassinateEnabled)
      {
        return false;
      }

      if (record.Type == Labels.BANE && !BaneEnabled)
      {
        return false;
      }

      if (record.Type == Labels.DS && !DSEnabled)
      {
        return false;
      }

      if (LineModifiersParser.IsFinishingBlow(record.ModifiersMask) && !FinishingBlowEnabled)
      {
        return false;
      }

      if (LineModifiersParser.IsHeadshot(record.ModifiersMask) && !HeadshotEnabled)
      {
        return false;
      }

      if (LineModifiersParser.IsSlayUndead(record.ModifiersMask) && !SlayUndeadEnabled)
      {
        return false;
      }

      return true;
    }

    public bool IsDamageLimited()
    {
      return !AssassinateEnabled || !BaneEnabled || !FinishingBlowEnabled || !HeadshotEnabled || !SlayUndeadEnabled;
    }
  }
}
