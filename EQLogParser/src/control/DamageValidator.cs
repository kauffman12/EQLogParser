
namespace EQLogParser
{
  class DamageValidator
  {
    private bool assassinateEnabled;
    private bool baneEnabled;
    private bool finishingBlowEnabled;
    private bool headshotEnabled;
    private bool slayUndeadEnabled;

    public DamageValidator()
    {
      // save this up front so we work with a constant state for their values
      assassinateEnabled = MainWindow.IsAssassinateDamageEnabled;
      baneEnabled = MainWindow.IsBaneDamageEnabled;
      finishingBlowEnabled = MainWindow.IsFinishingBlowDamageEnabled;
      headshotEnabled = MainWindow.IsHeadshotDamageEnabled;
      slayUndeadEnabled = MainWindow.IsSlayUndeadDamageEnabled;
    }

    public bool IsValid(DamageRecord record)
    {
      if (LineModifiersParser.IsAssassinate(record.ModifiersMask) && !assassinateEnabled)
      {
        return false;
      }

      if (record.Type == Labels.BANE && !baneEnabled)
      {
        return false;
      }

      if (LineModifiersParser.IsFinishingBlow(record.ModifiersMask) && !finishingBlowEnabled)
      {
        return false;
      }

      if (LineModifiersParser.IsHeadshot(record.ModifiersMask) && !headshotEnabled)
      {
        return false;
      }

      if (LineModifiersParser.IsSlayUndead(record.ModifiersMask) && !slayUndeadEnabled)
      {
        return false;
      }

      return true;
    }

    public bool IsDamageLimited()
    {
      return !assassinateEnabled || !baneEnabled || !finishingBlowEnabled || !headshotEnabled || !slayUndeadEnabled;
    }
  }
}
