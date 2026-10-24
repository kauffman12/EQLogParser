namespace EQLogParser
{
  internal static class DerivedMeter
  {
    /*
     * Opening and closing use the same eligibility as accounting, including a recently completed one-shot — filtered to the panels
     * this meter displays. A lane the operator does not watch may neither pop the window open nor hold it on screen, which is the
     * whole point of the choice; the numbers themselves stay computed either way.
     */
    internal static bool HasActivity(int damageMode, MeterPanels panels) =>
      DeriveEngine.Active?.HasOverlayActivity(damageMode, panels) == true;

    // No window alive to ask: DamageMeterConfigState is the one reader of every meter key, including this one.
    internal static bool HasActivity() =>
      HasActivity(DamageMeterConfigState.LoadResetMode(), DamageMeterConfigState.LoadPanels());
  }
}
