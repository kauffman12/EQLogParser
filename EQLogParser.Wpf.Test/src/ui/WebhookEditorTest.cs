using FontAwesome5;
using Syncfusion.Windows.PropertyGrid;
using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace EQLogParser.Wpf.Test
{
  [TestClass]
  public class WebhookEditorTest
  {
    [TestMethod]
    public void WebhookFieldKeepsErrorWhileEditingAndClearsAfterSuccess()
    {
      var initialText = "";
      var initiallyFlagged = false;
      var flaggedAfterEdit = false;
      var clearedAfterSuccess = false;
      Sta.Run(() =>
      {
        using var client = new HttpClient(new SuccessfulHandler());
        var queue = new WebhookDispatchQueue(new DiscordWebhookSender(client));
        var source = queue.UpdateSources("owner", "character", [("trigger", "Loot")])["trigger"];
        queue.EnqueueAsync(source, "invalid", "loot", 0).GetAwaiter().GetResult();
        var editor = new WebhookEditor(queue, () => ("character", "trigger"), () => { });
        var grid = (Grid)editor.Create(TypeDescriptor.GetProperties(typeof(WebhookField))[nameof(WebhookField.Url)]);
        editor.Attach(new PropertyViewItem(), new PropertyItem { Value = "invalid" });
        var text = grid.Children.OfType<TextBox>().Single();
        var error = grid.Children.OfType<ImageAwesome>().Single();
        PumpDispatcher();
        initialText = text.Text;
        initiallyFlagged = error.Visibility == Visibility.Visible;
        text.Text = "https://discord.com/api/webhooks/123/fixed";
        PumpDispatcher();
        flaggedAfterEdit = error.Visibility == Visibility.Visible;
        queue.EnqueueAsync(source, text.Text, "works", 0).GetAwaiter().GetResult();
        PumpDispatcher();
        clearedAfterSuccess = error.Visibility == Visibility.Collapsed && text.ToolTip is null;
        editor.Detach(new PropertyViewItem());
      });
      Assert.AreEqual("invalid", initialText);
      Assert.IsTrue(initiallyFlagged);
      Assert.IsTrue(flaggedAfterEdit);
      Assert.IsTrue(clearedAfterSuccess);
    }

    [TestMethod]
    public void InlineAggregationWritesItsSiblingPropertyWithoutDirtyingAttachment()
    {
      var attachmentWasQuiet = false;
      var editsReachedModel = false;
      var revertingWasClean = false;
      Sta.Run(() =>
      {
        using var client = new HttpClient(new SuccessfulHandler());
        var queue = new WebhookDispatchQueue(new DiscordWebhookSender(client));
        var saved = new Trigger { AggregateWebhooks = 1 };
        var model = new TriggerPropertyModel
        {
          AggregateWebhooks = saved.AggregateWebhooks,
          Node = new TriggerNode { TriggerData = saved }
        };
        var notices = new List<bool>();
        var editor = new WebhookEditor(queue, () => ("char", "trigger"),
          () => notices.Add(TriggersView.WebhookOptionsUnsaved(model)));
        var grid = (Grid)editor.Create(typeof(TriggerPropertyModel).GetProperty(nameof(Trigger.ChatWebhook)));
        editor.Attach(new PropertyViewItem(), new PropertyItem
        {
          PropertyInformation = typeof(TriggerPropertyModel).GetProperty(nameof(Trigger.ChatWebhook)),
          SelectedObject = model
        });
        PumpDispatcher();
        var aggregation = grid.Children.OfType<ComboBox>().Single();
        attachmentWasQuiet = notices.Count == 0 && Equals(1, aggregation.SelectedValue);
        aggregation.SelectedValue = 7;
        PumpDispatcher();
        editsReachedModel = model.AggregateWebhooks == 7 && notices.Count > 0 && notices.All(dirty => dirty);
        aggregation.SelectedValue = 1;
        PumpDispatcher();
        revertingWasClean = notices.Count > 0 && !notices[^1];
        editor.Detach(new PropertyViewItem());
      });
      Assert.IsTrue(attachmentWasQuiet);
      Assert.IsTrue(editsReachedModel);
      Assert.IsTrue(revertingWasClean);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(6)]
    [DataRow(7)]
    public void LoadingStoredAggregationIsNotUnsavedWork(int aggregation)
    {
      var model = new TriggerPropertyModel
      {
        AggregateWebhooks = aggregation,
        Node = new TriggerNode
        {
          TriggerData = new Trigger
          {
            AggregateWebhooks = aggregation
          }
        }
      };
      Assert.IsFalse(TriggersView.WebhookOptionsUnsaved(model));
      model.AggregateWebhooks = aggregation == 0 ? 1 : 0;
      Assert.IsTrue(TriggersView.WebhookOptionsUnsaved(model));
      model.AggregateWebhooks = aggregation;
      Assert.IsFalse(TriggersView.WebhookOptionsUnsaved(model));
    }

    private static void PumpDispatcher()
    {
      var frame = new DispatcherFrame();
      Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => frame.Continue = false));
      Dispatcher.PushFrame(frame);
    }

    private sealed class SuccessfulHandler : HttpMessageHandler
    {
      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    public sealed class WebhookField
    {
      public string Url { get; set; } = "invalid";
    }
  }
}