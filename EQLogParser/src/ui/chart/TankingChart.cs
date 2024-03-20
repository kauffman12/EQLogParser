using System.Collections.Generic;

namespace EQLogParser
{
  internal class TankingChart : LineChart, IDocumentContent
  {
    private static readonly List<string> TankingChoices =
    [
      "Aggregate DPS",
      "Aggregate Av Hit",
      "Aggregate Damaged",
      "DPS",
      "Rolling DPS",
      "Rolling Damage",
      "# Attempts",
      "# Hits",
      "# Twincasts"
    ];

    private bool _ready;

    public TankingChart() : base(TankingChoices, false)
    {
      Loaded += ContentLoaded;
    }

    private void EventsClearedActiveData(bool cleared) => Clear();

    private void ContentLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
      if (VisualParent != null && !_ready)
      {
        DataManager.Instance.EventsClearedActiveData += EventsClearedActiveData;
        MainActions.FireChartOpened("Tanking");
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
