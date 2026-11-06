using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using EQLogParser;

namespace EQLogParser.Wpf.Test;

[TestClass]
public sealed class TriggersLogViewTest
{
  private (string? Family, double Size, double DateWidth, double NameWidth, double MediumWidth) _savedTheme;

  [TestInitialize]
  public void Setup()
  {
    _savedTheme = (ThemeConfig.CurrentFontFamily, ThemeConfig.CurrentFontSize, ThemeConfig.CurrentDateTimeWidth,
      ThemeConfig.CurrentNameWidth, ThemeConfig.CurrentMediumWidth);
    // The test host does not run App startup; column generation still needs the normal font and width inputs.
    ThemeConfig.CurrentFontFamily = "Segoe UI";
    ThemeConfig.CurrentFontSize = 12;
    ThemeConfig.CurrentDateTimeWidth = (10 * ThemeConfig.CurrentFontSize) - 10;
    ThemeConfig.CurrentNameWidth = (10 * ThemeConfig.CurrentFontSize) + 25;
    ThemeConfig.CurrentMediumWidth = 6.5 * ThemeConfig.CurrentFontSize;
  }

  [TestCleanup]
  public void Cleanup()
  {
    (ThemeConfig.CurrentFontFamily, ThemeConfig.CurrentFontSize, ThemeConfig.CurrentDateTimeWidth,
      ThemeConfig.CurrentNameWidth, ThemeConfig.CurrentMediumWidth) = _savedTheme;
  }

  [TestMethod]
  public void SettingTheInitialWatermarkDoesNotThrow()
  {
    Sta.Run(() =>
    {
      EnsureResources();
      var pane = new TriggersLogView();
      try
      {
        Assert.AreEqual(Resource.TRIGGER_LOG_FILTER_TEXT, pane.filterBox.Text);
        Assert.AreEqual(FontStyles.Italic, pane.filterBox.FontStyle);
        pane.filterBox.RaiseEvent(new RoutedEventArgs(UIElement.GotFocusEvent));
        Assert.AreEqual(string.Empty, pane.filterBox.Text);
        pane.filterBox.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
        Assert.AreEqual(Resource.TRIGGER_LOG_FILTER_TEXT, pane.filterBox.Text);
      }
      finally
      {
        pane.HideContent();
      }
    });
  }

  [TestMethod]
  [DataRow("hEaL", "Healing Alert")]
  [DataRow("CRITICAL", "Damage Alert")]
  [DataRow("[", "Literal Alert")]
  [DataRow("missing", "")]
  public void TheDebouncedFilterMatchesNameOrLineIgnoringCase(string query, string expectedName)
  {
    WithPane(pane =>
    {
      var entries = Entries();
      pane.dataGrid.ItemsSource = entries;
      pane.dataGrid.SelectedItem = entries[0];
      pane.filterBox.Text = query;
      Assert.AreEqual(entries.Count, VisibleEntries(pane).Length, "typing is debounced, not a per-key rebuild");

      PumpFor(450);

      var expected = expectedName.Length == 0 ? Array.Empty<string>() : new[] { expectedName };
      CollectionAssert.AreEqual(expected, VisibleEntries(pane).Select(entry => entry.Name).ToArray());
      Assert.AreEqual(0, pane.dataGrid.SelectedItems.Count, "a new query clears the old selection");
      Assert.AreEqual(4, entries.Count, "filtering does not delete captured log entries");
    });
  }

  [TestMethod]
  public void OnlyTheLatestQueryAppliesAndEmptyTextRestoresAllRows()
  {
    WithPane(pane =>
    {
      pane.dataGrid.ItemsSource = Entries();
      pane.filterBox.Text = "Healing";
      pane.filterBox.Text = "critical";
      PumpFor(450);
      Assert.AreEqual("Damage Alert", VisibleEntries(pane).Single().Name);

      pane.filterBox.Text = string.Empty;
      PumpFor(450);
      Assert.AreEqual(4, VisibleEntries(pane).Length);
    });
  }

  [TestMethod]
  public void TheFilterSurvivesLogReplacementAndAppliesToLiveBatches()
  {
    WithPane(pane =>
    {
      pane.dataGrid.ItemsSource = Entries();
      pane.filterBox.Text = "critical";
      PumpFor(450);

      // Log selection replaces ItemsSource; the new view must receive the active predicate too.
      var replacement = new BulkObservableCollection<TriggerLogEntry>
      {
        new() { Name = "Another Alert", Line = "A critical strike lands." },
        new() { Name = "Unrelated Alert", Line = "You sit down." }
      };
      pane.dataGrid.ItemsSource = replacement;
      Assert.AreEqual("Another Alert", VisibleEntries(pane).Single().Name);

      // Production arrivals use AddRange's Reset notification, not individual Add notifications.
      replacement.AddRange([
        new TriggerLogEntry { Name = "CRITICAL Alert", Line = "A new match." },
        new TriggerLogEntry { Name = "Ignored Alert", Line = "No match." }]);
      Flush();
      CollectionAssert.AreEquivalent(new[] { "Another Alert", "CRITICAL Alert" },
        VisibleEntries(pane).Select(entry => entry.Name).ToArray());

      replacement.Clear();
      Flush();
      Assert.AreEqual(0, VisibleEntries(pane).Length);
    });
  }

  [TestMethod]
  public void EscapeClearsImmediatelyAndCancelsThePendingQuery()
  {
    WithPane(pane =>
    {
      pane.dataGrid.ItemsSource = Entries();
      pane.filterBox.Focus();
      pane.filterBox.Text = "Healing";
      PumpFor(450);
      Assert.AreEqual(1, VisibleEntries(pane).Length);
      pane.filterBox.Text = "critical";

      var source = PresentationSource.FromVisual(pane.filterBox)
        ?? throw new InvalidOperationException("the filter must be hosted for keyboard input");
      var escape = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
      { RoutedEvent = Keyboard.PreviewKeyDownEvent };
      pane.filterBox.RaiseEvent(escape);

      Assert.IsTrue(escape.Handled);
      Assert.AreEqual(4, VisibleEntries(pane).Length, "Escape clears without waiting for the debounce");
      Assert.AreEqual(Resource.TRIGGER_LOG_FILTER_TEXT, pane.filterBox.Text);
      Assert.IsFalse(pane.filterBox.IsKeyboardFocusWithin);
      PumpFor(450);
      Assert.AreEqual(4, VisibleEntries(pane).Length, "the pending query cannot reinstate the filter");
    });
  }

  private static BulkObservableCollection<TriggerLogEntry> Entries() =>
  [
    new() { Name = "Healing Alert", Line = "You recover hit points." },
    new() { Name = "Damage Alert", Line = "A critical strike lands." },
    new() { Name = "Literal Alert", Line = "[Combat] begins." },
    new()
  ];

  private static TriggerLogEntry[] VisibleEntries(TriggersLogView pane)
    => pane.dataGrid.View?.Records.Select(record => (TriggerLogEntry)record.Data).ToArray()
      ?? throw new InvalidOperationException("the loaded grid must have a view");

  private static void WithPane(Action<TriggersLogView> body)
  {
    Sta.Run(() =>
    {
      EnsureResources();
      var pane = new TriggersLogView();
      var window = new Window { Content = pane, Width = 1000, Height = 500 };
      try
      {
        window.Show();
        window.UpdateLayout();
        PumpFor(1);
        Flush();
        body(pane);
      }
      finally
      {
        pane.HideContent();
        window.Close();
      }
    });
  }

  private static void EnsureResources()
  {
    var app = Application.Current ?? new Application();
    app.Resources["EQTitleStyle"] ??= new Style(typeof(Label));
    app.Resources["EQIconStyle"] ??= new Style(typeof(Image));
  }

  private static void Flush()
    => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Normal);

  private static void PumpFor(int milliseconds)
  {
    var frame = new DispatcherFrame();
    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
    timer.Tick += (_, _) =>
    {
      timer.Stop();
      frame.Continue = false;
    };
    timer.Start();
    Dispatcher.PushFrame(frame);
  }
}
