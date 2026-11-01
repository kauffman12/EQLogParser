using System.Text.Json;

namespace EQLogParser
{
  [DoNotParallelize]
  [TestClass]
  public class WebhookSettingsTest : TempDirFixture
  {
    [TestMethod]
    public void NewAndLegacyTriggersDefaultToNoAggregation()
    {
      Assert.AreEqual(0, new Trigger().AggregateWebhooks);
      var legacy = JsonSerializer.Deserialize<Trigger>("{\"TextToSendToChat\":\"raid loot\"}");
      Assert.IsNotNull(legacy);
      Assert.AreEqual(0, legacy.AggregateWebhooks);
    }

    [TestMethod]
    public async Task DeletingAFolderRetiresAllItsWebhookSourcesAndErrorsImmediately()
    {
      await using var db = new TriggerStateDB(Path.Combine(NewTempDir(), "webhook-delete.db"), applyLegacyUpgrades: false);
      var root = (await db.GetTriggerTree("P1")).Root;
      var folder = (await db.CreateFolder(root.Id, "Loot", "P1")).Node;
      var child = (await db.CreateTrigger(folder.Id, "Send", "P1")).Node;
      using var client = new HttpClient();
      var queue = new WebhookDispatchQueue(new DiscordWebhookSender(client));
      var source = queue.UpdateSources("owner", "P1", [(child.Id, child.Name)])[child.Id];
      db.EventsTriggerStateChanged += queue.SetEnabled;
      await queue.EnqueueAsync(source, "invalid", "loot", 0);
      Assert.HasCount(1, queue.Status.Errors);
      await db.Delete(folder.Id);
      Assert.IsEmpty(queue.Status.Errors);
      await queue.EnqueueAsync(source, "invalid", "stale", 0);
      Assert.IsEmpty(queue.Status.Errors);
    }

    [TestMethod]
    public async Task FolderDisableAndCopiedStateNotifyEveryAffectedWebhookImmediately()
    {
      await using var db = new TriggerStateDB(Path.Combine(NewTempDir(), "webhook-state.db"), applyLegacyUpgrades: false);
      var root = (await db.GetTriggerTree("P1")).Root;
      var folder = (await db.CreateFolder(root.Id, "Raid loot", "P1")).Node;
      var first = (await db.CreateTrigger(folder.Id, "First", "P1")).Node;
      var second = (await db.CreateTrigger(folder.Id, "Second", "P1")).Node;
      await db.GetTriggerTree("P2");
      var events = new List<(string Character, string Id, bool Enabled)>();
      db.EventsTriggerStateChanged += (character, id, enabled) => events.Add((character, id, enabled));
      await db.SetState(["P1"], folder.Id, false);
      Assert.Contains(("P1", first.Id, false), events);
      Assert.Contains(("P1", second.Id, false), events);
      events.Clear();
      await db.SetState(["P1"], folder.Id, true);
      await db.CopyState(folder.Id, "P1", "P2");
      Assert.Contains(("P2", first.Id, true), events);
      Assert.Contains(("P2", second.Id, true), events);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    public async Task AggregationSurvivesSharingCopyingAndReopeningTheStore(int option)
    {
      var path = Path.Combine(NewTempDir(), "webhook-settings.db");
      var source = new ExportTriggerNode
      {
        Name = "Loot",
        TriggerData = new Trigger
        {
          Pattern = "raid loot",
          ChatWebhook = "https://discord.com/api/webhooks/123/test",
          AggregateWebhooks = option
        }
      };
      var shared = JsonSerializer.Deserialize<ExportTriggerNode>(JsonSerializer.Serialize(source));
      Assert.IsNotNull(shared);
      await using (var db = new TriggerStateDB(path, applyLegacyUpgrades: false))
      {
        var root = (await db.GetTriggerTree("P1")).Root;
        await db.ImportTriggers(root, [new ExportTriggerNode { Name = TriggerStateDB.Triggers, Nodes = [shared] }]);
        var imported = (await db.GetTriggerTree("P1")).Nodes.Single(node => node.Name == "Loot");
        Assert.AreEqual(option, imported.TriggerData.AggregateWebhooks);
        await db.Copy(imported, root);
      }

      await using var reopened = new TriggerStateDB(path, applyLegacyUpgrades: false);
      var triggers = (await reopened.GetTriggerTree("P1")).Nodes.Where(node => node.TriggerData != null).ToArray();
      Assert.HasCount(2, triggers);
      Assert.IsTrue(triggers.All(node => node.TriggerData.AggregateWebhooks == option));
    }
  }
}