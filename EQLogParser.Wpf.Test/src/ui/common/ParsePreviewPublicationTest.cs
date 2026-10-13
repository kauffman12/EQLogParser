using EQLogParser;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace EQLogParser.Wpf.Test;

[TestClass]
public sealed class ParsePreviewPublicationTest
{
  [TestMethod]
  public void AClearRejectsAnAnswerAlreadyQueuedForTheDispatcher()
  {
    var shown = -1;
    var retained = -1;
    Sta.Run(() =>
    {
      using var pane = CreatePane();
      Task.Run(() => BuildAnswer(pane, Labels.DamageParse)).GetAwaiter().GetResult();
      pane.ClearParses();
      Flush();
      shown = pane.parseList.Items.Count;
      retained = Retained(pane);
    });
    Assert.AreEqual(0, shown);
    Assert.AreEqual(0, retained, "discarding the UI callback must also discard its retained stats");
  }

  [TestMethod]
  public void ABuildStartedBeforeClearCannotRestampItsLateAnswer()
  {
    var shown = -1;
    var retained = -1;
    Sta.Run(() =>
    {
      using var pane = CreatePane();
      Task.Run(() => pane.EventsGenerationStatus(new StatsGenerationEvent
        { Type = Labels.DamageParse, State = "STARTED" })).GetAwaiter().GetResult();
      pane.ClearParses();
      Task.Run(() => pane.EventsGenerationStatus(new StatsGenerationEvent
        { Type = Labels.DamageParse, State = "COMPLETED" })).GetAwaiter().GetResult();
      Flush();
      shown = pane.parseList.Items.Count;
      retained = Retained(pane);
    });
    Assert.AreEqual(0, shown);
    Assert.AreEqual(0, retained);
  }

  [TestMethod]
  public void ANewAnswerSurvivesAnOlderQueuedAnswerAndClear()
  {
    var shown = new List<string>();
    var retained = -1;
    Sta.Run(() =>
    {
      using var pane = CreatePane();
      Task.Run(() => BuildAnswer(pane, Labels.DamageParse)).GetAwaiter().GetResult();
      pane.ClearParses();
      Task.Run(() => BuildAnswer(pane, Labels.HealParse)).GetAwaiter().GetResult();
      Flush();
      shown.AddRange(pane.parseList.Items.Cast<string>());
      retained = Retained(pane);
    });
    CollectionAssert.AreEqual(new[] { Labels.HealParse }, shown);
    Assert.AreEqual(1, retained, "only the current publication owns a retained answer");
  }

  [TestMethod]
  public void AStaleCaptureCannotPublishEvenIfItsBuildStartsAfterClear()
  {
    var shown = -1;
    var retained = -1;
    Sta.Run(() =>
    {
      using var pane = CreatePane();
      pane.ClearParses();
      Task.Run(() =>
      {
        pane.EventsGenerationStatus(new StatsGenerationEvent
          { Type = Labels.DamageParse, State = "STARTED", SessionId = int.MaxValue });
        pane.EventsGenerationStatus(new StatsGenerationEvent
          { Type = Labels.DamageParse, State = "COMPLETED", SessionId = int.MaxValue });
      }).GetAwaiter().GetResult();
      Flush();
      shown = pane.parseList.Items.Count;
      retained = Retained(pane);
    });
    Assert.AreEqual(0, shown);
    Assert.AreEqual(0, retained);
  }

  private static void BuildAnswer(ParsePreview pane, string type)
  {
    pane.EventsGenerationStatus(new StatsGenerationEvent { Type = type, State = "STARTED" });
    pane.EventsGenerationStatus(new StatsGenerationEvent { Type = type, State = "COMPLETED" });
  }

  private static int Retained(ParsePreview pane)
  {
    var field = typeof(ParsePreview).GetField("_parses", BindingFlags.Instance | BindingFlags.NonPublic)
      ?? throw new MissingFieldException("_parses");
    return field.GetValue(pane) is Dictionary<string, ParseData> parses
      ? parses.Count : throw new InvalidOperationException("The parse store was not a dictionary");
  }

  private static ParsePreview CreatePane()
  {
    var app = Application.Current ?? new Application();
    app.Resources["CustomCheckBoxTemplate"] ??= new ControlTemplate(typeof(CheckBox));
    return new ParsePreview();
  }

  private static void Flush() => Dispatcher.CurrentDispatcher.Invoke(new Action(() => { }), DispatcherPriority.Normal);
}
