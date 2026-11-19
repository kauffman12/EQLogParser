using Syncfusion.UI.Xaml.TreeGrid;
using System.Collections.Concurrent;
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
        // Read before any queued board result or roster notification can repair a stale list.
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

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void NewCompletedRostersAutomaticallyReplaceTheCurrentChoiceButPartialOnesDoNot(bool groupView)
  {
    var groups = new List<int>();
    var absentGroups = new List<int>();
    var partialHidden = false;
    WithPane(pane =>
    {
      var parent = new Grid();
      parent.Children.Add(pane);
      pane.CurrentStats = Board();
      if (groupView)
      {
        var view = (ComboBox)pane.FindName("petOrPlayerList");
        view.SelectedIndex = view.Items.IndexOf(Labels.ByGroupOption);
      }
      var selector = (ComboBox)pane.FindName("rosterSelector");
      void Observe()
      {
        groups.Add(pane.CurrentStats.StatsList[0].AssignedGroup);
        absentGroups.Add(pane.CurrentStats.StatsList[1].AssignedGroup);
      }

      try
      {
        var store = RaidRosterStore.Instance;
        var capture = store.CaptureVersion;
        store.CapturePlayer(capture, "Absentplayer", 2, Begin);
        CaptureRoster(group: 1, playerCount: 2);
        pane.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Flush();
        Observe();

        store.CapturePlayer(capture, "Rosterplayer", 5, Begin + 10);
        Flush();
        partialHidden = selector.Items.Count == 2;
        Observe();

        store.FlushCurrentGroup(capture, 1);
        Flush();
        Observe();

        selector.SelectedIndex = 2; // An older manual choice survives ordinary refreshes, not a new completed roster.
        Invoke(pane, "PopulateRosterSelector", false);
        Observe();

        CaptureRoster(group: 6, offset: 10); // A second completion in the same log second still takes over.
        Flush();
        Observe();

        selector.SelectedIndex = 0;
        Observe();
        CaptureRoster(group: 8, offset: 20);
        Flush();
        Observe();
      }
      finally
      {
        pane.HideContent();
        parent.Children.Remove(pane);
      }
    });
    Assert.IsTrue(partialHidden);
    CollectionAssert.AreEqual(new[] { 1, 1, 5, 1, 6, 0, 8 }, groups);
    CollectionAssert.AreEqual(new[] { 2, 2, 0, 2, 0, 0, 0 }, absentGroups,
      "names absent from a replacement roster must lose their previous group");
  }

  [TestMethod]
  public void TheSelectedRosterAlsoAppliesToSubsequentBoardResults()
  {
    var assignments = new List<int>();
    var cachedAbsentGroup = -1;
    WithPane(pane =>
    {
      var parent = new Grid();
      parent.Children.Add(pane);
      try
      {
        CaptureRoster();
        pane.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        foreach (var staleGroup in new[] { 0, 7 })
        {
          var board = Board();
          foreach (var row in board.StatsList.Concat(board.ExpandedStatsList))
          {
            row.AssignedGroup = staleGroup;
          }
          PublishBoard(pane, board);
          assignments.AddRange(board.StatsList.Select(row => row.AssignedGroup));
          assignments.AddRange(board.ExpandedStatsList.Select(row => row.AssignedGroup));
        }
        cachedAbsentGroup = GroupAssignments()["Absentplayer"];
      }
      finally
      {
        pane.HideContent();
        parent.Children.Remove(pane);
      }
    });
    CollectionAssert.AreEqual(new[] { 3, 0, 3, 0, 3, 0, 3, 0 }, assignments);
    Assert.AreEqual(0, cachedAbsentGroup, "clearing an absent name must update the builder's group cache too");
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void AManualGroupEditSurvivesBoardRefreshUntilTheNextRoster(bool clearRoster)
  {
    var assignments = new List<int>();
    var keptChoice = false;
    WithPane(pane =>
    {
      var parent = new Grid();
      parent.Children.Add(pane);
      try
      {
        CaptureRoster();
        pane.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        PublishBoard(pane, Board());
        var selector = (ComboBox)pane.FindName("rosterSelector");
        if (clearRoster) selector.SelectedIndex = 0;
        Invoke(pane, "SetManualGroup", pane.CurrentStats.ExpandedStatsList[0], 5);
        ObservePlayerRows();

        PublishBoard(pane, Board()); // A result constructed before the manual edit has no group assignment yet.
        ObservePlayerRows();
        keptChoice = selector.SelectedIndex == (clearRoster ? 0 : 1);

        CaptureRoster(group: 8); // Replacement in the same second is still a new roster.
        Flush();
        ObservePlayerRows();

        void ObservePlayerRows()
        {
          assignments.Add(pane.CurrentStats.StatsList[0].AssignedGroup);
          assignments.Add(pane.CurrentStats.ExpandedStatsList[0].AssignedGroup);
        }
      }
      finally
      {
        pane.HideContent();
        parent.Children.Remove(pane);
      }
    });
    Assert.IsTrue(keptChoice);
    CollectionAssert.AreEqual(new[] { 5, 5, 5, 5, 8, 8 }, assignments);
  }

  [TestMethod]
  public void AQueuedRosterNotificationCannotChangeTheNextCapturesRosterChoice()
  {
    var selectedIndex = -1;
    WithPane(pane =>
    {
      var parent = new Grid();
      parent.Children.Add(pane);
      try
      {
        pane.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        CaptureRoster(); // Queue the old capture's notification without pumping the dispatcher.
        RaidRosterStore.Instance.Clear(serverChanged: false);
        Invoke(pane, "PopulateRosterSelector", false);
        var selector = (ComboBox)pane.FindName("rosterSelector");
        selector.SelectedIndex = 0;
        Flush();
        selectedIndex = selector.SelectedIndex;
      }
      finally
      {
        pane.HideContent();
        parent.Children.Remove(pane);
      }
    });
    Assert.AreEqual(0, selectedIndex, "a queued notification must not reselect a retained roster after a capture switch");
  }

  [TestMethod]
  public void RosterUpdatesKeepPlayerSelectionOutsideGroupView()
  {
    var selectedBefore = false;
    var selectedAfter = false;
    var groupAfter = -1;
    WithPane(pane =>
    {
      var parent = new Grid();
      parent.Children.Add(pane);
      try
      {
        pane.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        var board = Board();
        PublishBoard(pane, board);
        var grid = (SfTreeGrid)pane.FindName("dataGrid");
        var player = board.StatsList[0];
        grid.SelectedItems.Add(player);
        selectedBefore = grid.SelectedItems.Contains(player);

        CaptureRoster();
        Flush();
        groupAfter = player.AssignedGroup;
        selectedAfter = grid.SelectedItems.Contains(player);
      }
      finally
      {
        pane.HideContent();
        parent.Children.Remove(pane);
      }
    });
    Assert.IsTrue(selectedBefore, "the grid must actually have a selection to protect");
    Assert.AreEqual(3, groupAfter, "the roster still updates assignments outside Group View");
    Assert.IsTrue(selectedAfter, "applying a roster must not clear the selected player");
  }

  private static readonly double Begin = DateUtil.ToDotNetSeconds(new DateTime(2026, 1, 1, 12, 0, 0));

  private static void CaptureRoster(int group = 3, int offset = 0, uint playerCount = 1)
  {
    var store = RaidRosterStore.Instance;
    var capture = store.CaptureVersion;
    store.CapturePlayer(capture, "Rosterplayer", group, Begin + offset);
    store.FlushCurrentGroup(capture, playerCount);
  }

  private static void PublishBoard(DamageSummary pane, CombinedStats board)
  {
    Invoke(pane, "EventsGenerationStatus", new StatsGenerationEvent
    {
      SessionId = DeriveEngine.Active?.SessionId ?? 0, State = "COMPLETED",
      Type = Labels.DamageParse, CombinedStats = board
    });
    Flush();
  }

  private static CombinedStats Board()
  {
    var board = new CombinedStats
    {
      RaidStats = StatsUtil.CreatePlayerStats(Labels.RaidTotals), PlayerClasses = [], FullTitle = "Selected fights"
    };
    board.StatsList.Add(StatsUtil.CreatePlayerStats("Rosterplayer +Pets", "Rosterplayer"));
    board.StatsList.Add(StatsUtil.CreatePlayerStats("Absentplayer +Pets", "Absentplayer"));
    board.ExpandedStatsList.Add(StatsUtil.CreatePlayerStats("Rosterplayer"));
    board.ExpandedStatsList.Add(StatsUtil.CreatePlayerStats("Absentplayer"));
    return board;
  }

  private static void Flush() => Dispatcher.CurrentDispatcher.Invoke(new Action(() => { }), DispatcherPriority.Normal);

  private static void Invoke(DamageSummary pane, string name, params object[] args)
  {
    var method = typeof(DamageSummary).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
      ?? throw new InvalidOperationException(name + " not found");
    method.Invoke(pane, args);
  }

  private static ConcurrentDictionary<string, int> GroupAssignments() =>
    typeof(DamageStatsBuilder).GetField("_playerGroupAssignments", BindingFlags.Instance | BindingFlags.NonPublic)
      ?.GetValue(DamageStatsBuilder.Instance) is ConcurrentDictionary<string, int> groups ? groups
      : throw new InvalidOperationException("Damage builder's group assignments not found");

  private static bool HasRoster(DamageSummary pane)
  {
    var selector = (ComboBox)pane.FindName("rosterSelector");
    var roster = RaidRosterStore.Instance.GetRosterRecords().Single();
    return selector.IsEnabled && selector.SelectedItem is ComboBoxItem selected && ReferenceEquals(selected.Tag, roster);
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
      var previousGroups = GroupAssignments().ToArray();
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
        var groups = GroupAssignments();
        groups.Clear();
        foreach (var entry in previousGroups) groups[entry.Key] = entry.Value;
        RaidRosterStore.Instance.Clear();
      }
    });
  }
}
