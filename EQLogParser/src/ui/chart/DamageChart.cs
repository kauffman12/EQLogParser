using System.Collections.Generic;

namespace EQLogParser
{
  internal class DamageChart : LineChart, IDocumentContent
  {
    private static readonly List<string> DamageChoices = new()
    {
      "Aggregate DPS",
      "Aggregate Av Hit",
      "Aggregate Damage",
      "Aggregate Crit Rate",
      "Aggregate Twincast Rate",
      "DPS",
      "Rolling DPS",
      "Rolling Damage",
      "# Attempts",
      "# Crits",
      "# Hits",
      "# Twincasts"
    };

    private bool _ready;

    public DamageChart() : base(DamageChoices, true)
    {
      Loaded += ContentLoaded;
    }

    private void EventsClearedActiveData(bool cleared) => Clear();

    private void ContentLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
      if (VisualParent != null && !_ready)
      {
        DataManager.Instance.EventsClearedActiveData += EventsClearedActiveData;
        MainActions.FireChartOpened("Damage");
        _ready = true;
      }
    }

    public void HideContent()
    {
      DataManager.Instance.EventsClearedActiveData -= EventsClearedActiveData;
      _ready = false;
    }
  }
}
