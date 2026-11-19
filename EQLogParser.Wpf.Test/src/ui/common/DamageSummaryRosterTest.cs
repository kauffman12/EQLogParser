using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

#nullable enable annotations

namespace EQLogParser.Wpf.Test;

[TestClass]
public sealed class DamageSummaryRosterTest
{
  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void OpeningReadsRostersCapturedWhileThePaneWasHidden(bool reopen)
  {
    var refreshed = false;
    WithPane(pane =>
    {
      var parent = new Grid();
      parent.Children.Add(pane);
      try
      {
        if (reopen)
        {
          pane.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
          pane.HideContent();
        }

        CaptureRoster();
        pane.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        // Read before any queued board result or debounced roster notification can repair a stale list.
        refreshed = HasRoster(pane);
      }
      finally
      {
        pane.HideContent();
        parent.Children.Remove(pane);
      }
    });
    Assert.IsTrue(refreshed, "opening or reopening must read existing rosters without a damage build");
  }

  [TestMethod]
  public void EnteringGroupViewReadsTheStoreAgain()
  {
    var refreshed = false;
    WithPane(pane =>
    {
      CaptureRoster();
      var view = (ComboBox)pane.FindName("petOrPlayerList");
      view.SelectedIndex = view.Items.IndexOf(Labels.ByGroupOption);
      refreshed = HasRoster(pane)
        && ((ComboBox)pane.FindName("rosterSelector")).Visibility == Visibility.Visible;
    });
    Assert.IsTrue(refreshed, "showing the roster selector must refresh it even without a roster notification");
  }

  private static void CaptureRoster()
  {
    RaidRosterStore.Instance.CapturePlayer("Rosterplayer", 3,
      DateUtil.ToDotNetSeconds(new DateTime(2026, 1, 1, 12, 0, 0)));
    RaidRosterStore.Instance.FlushCurrentGroup();
  }

  private static bool HasRoster(DamageSummary pane)
  {
    var selector = (ComboBox)pane.FindName("rosterSelector");
    var roster = RaidRosterStore.Instance.GetRosterRecords().Single();
    return selector.IsEnabled && selector.Items.OfType<ComboBoxItem>()
      .Any(item => item.Tag?.ToString() == roster.BeginTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
  }

  private static void WithPane(Action<DamageSummary> body)
  {
    Sta.Run(() =>
    {
      var app = Application.Current ?? new Application();
      app.Resources["EQTitleStyle"] ??= new Style(typeof(Label));
      app.Resources["EQIconStyle"] ??= new Style(typeof(Image));
      foreach (var key in new[]
      {
        "HeaderTotalTemplateToolTip", "HeaderDPSTemplateToolTip", "HeaderSDPSTemplateToolTip",
        "HeaderSecondsTemplateToolTip", "HeaderBusyTemplateToolTip", "HeaderDamageHitsTemplateToolTip",
        "HeaderBestSecondTemplateToolTip", "HeaderMeleeAccRateTemplateToolTip", "HeaderMeleeHitRateTemplateToolTip",
        "HeaderSpecialTemplateToolTip"
      })
      {
        app.Resources[key] ??= new DataTemplate();
      }

      RaidRosterStore.Instance.Clear();
      var builder = DamageStatsBuilder.Instance;
      var cache = typeof(DamageStatsBuilder).GetField("_lastStatsEvent", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Damage builder's last event not found");
      var previous = cache.GetValue(builder);
      // Leave the damage build unfinished so its completion cannot repair the roster list for the test.
      cache.SetValue(builder, new StatsGenerationEvent
      {
        SessionId = DeriveEngine.Active?.SessionId ?? 0, Type = Labels.DamageParse, State = "STARTED"
      });

      DamageSummary? pane = null;
      try
      {
        pane = new DamageSummary();
        body(pane);
      }
      finally
      {
        if (pane is not null)
        {
          // Deliver the queued STARTED while the dispatcher still exists, then cancel its delayed busy state on hide.
          Dispatcher.CurrentDispatcher.Invoke(new Action(() => { }), DispatcherPriority.Normal);
          pane.HideContent();
          var theme = typeof(SummaryTable).GetMethod("EventsThemeChanged", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Summary theme handler not found");
          ThemeConfig.EventsThemeChanged -= theme.CreateDelegate<Action<string>>(pane);
        }
        cache.SetValue(builder, previous);
        RaidRosterStore.Instance.Clear();
      }
    });
  }
}
