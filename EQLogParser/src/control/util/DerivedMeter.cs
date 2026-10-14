namespace EQLogParser
{
  internal static class DerivedMeter
  {
    // Opening and closing use the same eligibility as accounting, including a recently completed one-shot.
    internal static bool HasActivity(int damageMode) => DeriveEngine.Active?.HasOverlayActivity(damageMode) == true;

    internal static bool HasActivity() => HasActivity(ConfigUtil.GetSettingAsInteger("OverlayDamageMode"));
  }
}
