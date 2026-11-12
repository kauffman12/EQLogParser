using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser.Wpf.Test;

[TestClass]
public sealed class FightStatsSelectionTest
{
  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void SettledSelectionRefreshesOnlyAnOpenSelectedScopeAndReattachesOnce(bool taunts)
  {
    var rowCounts = new List<int>();
    var subscriptions = new List<int>();
    Sta.Run(() =>
    {
      var app = Application.Current ?? new Application();
      var resources = app.Resources;
      resources["EQTitleStyle"] ??= new Style(typeof(Label));
      resources["EQIconStyle"] ??= new Style(typeof(Image));
      resources["CustomCheckBoxTemplate"] ??= new ControlTemplate(typeof(CheckBox));

      var hostField = Field(typeof(MainActions), "_mainWindow");
      var oldHost = hostField.GetValue(null);
      UserControl pane = taunts ? new TauntStatsViewer() : new SpellDamageStatsViewer();
      var document = (IDocumentContent)pane;
      var parent = new Grid();
      parent.Children.Add(pane);
      var scope = (ComboBox)pane.FindName("fightOption");
      IList rows = pane is TauntStatsViewer tauntPane ? tauntPane.TauntData
        : ((SpellDamageStatsViewer)pane).SpellDamageData;
      object previousRow = taunts ? new TauntRow { Name = "Previous tank", Children = [] }
        : new SpellDamageRow { Caster = "Previous caster", Spell = "Previous spell", Type = Labels.Dd };

      void Open()
      {
        pane.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        pane.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        subscriptions.Add(SelectionSubscriptions(pane));
      }

      void AnnounceAndCount()
      {
        rows.Clear();
        rows.Add(previousRow);
        MainActions.FireFightSelectionSettled();
        rowCounts.Add(rows.Count);
      }

      try
      {
        hostField.SetValue(null, null);
        Open();
        // All Fights keeps its snapshot when the fight-list selection changes.
        scope.SelectedIndex = 0;
        AnnounceAndCount();

        // No host means an empty selection. The actual notification must replace the previous answer with empty rows.
        scope.SelectedIndex = 1;
        AnnounceAndCount();

        document.HideContent();
        subscriptions.Add(SelectionSubscriptions(pane));
        AnnounceAndCount();

        Open();
        AnnounceAndCount();
      }
      finally
      {
        document.HideContent();
        parent.Children.Remove(pane);
        // These older panes subscribe to themes in their constructors, independently of the document lifecycle.
        var themeHandler = pane.GetType().GetMethod("EventsThemeChanged", BindingFlags.Instance | BindingFlags.NonPublic)
          ?? throw new InvalidOperationException("Theme handler not found");
        ThemeConfig.EventsThemeChanged -= themeHandler.CreateDelegate<Action<string>>(pane);
        hostField.SetValue(null, oldHost);
      }
    });

    CollectionAssert.AreEqual(new[] { 1, 0, 1, 0 }, rowCounts,
      "only an open Selected Fights pane may reload on a settled selection, including deselection");
    CollectionAssert.AreEqual(new[] { 1, 0, 1 }, subscriptions,
      "repeated Loaded events must not duplicate subscriptions, and Hide must detach");
  }

  private static int SelectionSubscriptions(object pane) =>
    Field(typeof(MainActions), "EventsFightSelectionSettled").GetValue(null) is Delegate handlers
      ? handlers.GetInvocationList().Count(handler => ReferenceEquals(handler.Target, pane)) : 0;

  private static FieldInfo Field(Type type, string name) =>
    type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)
      ?? throw new InvalidOperationException(name + " not found");
}
