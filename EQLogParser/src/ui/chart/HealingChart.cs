﻿using System.Collections.Generic;

namespace EQLogParser
{
  internal class HealingChart : LineChart
  {
    private static readonly List<string> HealingChoices = new()
    {
      "Aggregate HPS",
      "Aggregate Av Heal",
      "Aggregate Healing",
      "Aggregate Crit Rate",
      "Aggregate Twincast Rate",
      "HPS",
      "Rolling HPS",
      "Rolling Healing",
      "# Crits",
      "# Heals",
      "# Twincasts"
    };

    private bool Ready;

    public HealingChart() : base(HealingChoices, false)
    {
      Loaded += ContentLoaded;
    }

    private void EventsClearedActiveData(bool cleared) => Clear();

    private void ContentLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
      if (VisualParent != null && !Ready)
      {
        DataManager.Instance.EventsClearedActiveData += EventsClearedActiveData;
        MainActions.FireChartOpened("Healing");
        Ready = true;
      }
    }

    public void HideContent()
    {
      DataManager.Instance.EventsClearedActiveData -= EventsClearedActiveData;
      Ready = false;
    }
  }
}