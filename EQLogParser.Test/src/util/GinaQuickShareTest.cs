using System.IO.Compression;
using System.Text;

namespace EQLogParser
{
  /* Quick-share/GINA domain tests, runnable without WPF: chat detection into the shared state,
   * the state's own rules, and an end-to-end convert + store import of a synthetic GINA package.
   * No network: CheckGina only starts downloads when TriggersWatchForQuickShare is enabled, which
   * these tests never do (fresh ConfigUtil settings). */
  [TestClass]
  public sealed class GinaQuickShareTest
  {
    private readonly List<string> _dirs = [];

    [TestCleanup]
    public void Cleanup()
    {
      foreach (var dir in _dirs)
      {
        try
        {
          Directory.Delete(dir, true);
        }
        catch
        {
          // best effort
        }
      }
    }

    private static QuickShareRecord FindRecord(string key) =>
      QuickShareState.Instance.Snapshot().FirstOrDefault(r => r.Key == key);

    [TestMethod]
    public void CheckGina_GroupChatFromTrustedPlayer_AddsRecord()
    {
      var key = Guid.NewGuid().ToString("N");
      var chatType = new ChatType(ChatChannels.Group, "Somesender", 0) { SenderIsYou = false };

      GinaUtil.CheckGina([new TrustedPlayer { Name = "somesender" }], chatType, $"hello {{GINA:{key}}}", 1234.0, "P1", "MyChar");

      var record = FindRecord($"{{GINA:{key}}}");
      Assert.IsNotNull(record, "expected a quick-share record in the shared state");
      Assert.AreEqual("Somesender", record.From);
      Assert.AreEqual("Group", record.To);
      Assert.IsFalse(record.IsMine);
      Assert.AreEqual("GINA", record.Type);
    }

    [TestMethod]
    public void CheckGina_TellFromOtherPlayer_AddsRecordAddressedToCharacter()
    {
      var key = Guid.NewGuid().ToString("N");
      var chatType = new ChatType(ChatChannels.Tell, "Friend", 0) { SenderIsYou = false };

      GinaUtil.CheckGina([], chatType, $"{{GINA:{key}}}", 1234.0, "P1", "MyChar");

      // Tell channel: To is the receiving character (processorName) when not importing to the default user.
      var record = FindRecord($"{{GINA:{key}}}");
      Assert.IsNotNull(record);
      Assert.AreEqual("MyChar", record.To);
    }

    [TestMethod]
    public void CheckGina_YourOwnTell_AddsOwnRecord()
    {
      var key = Guid.NewGuid().ToString("N");
      var chatType = new ChatType(ChatChannels.Tell, "You", 0) { SenderIsYou = true };

      GinaUtil.CheckGina([], chatType, $"{{GINA:{key}}}", 1234.0, "P1", null);

      // Your own shares are recorded (marked IsMine) but never auto-processed.
      var record = FindRecord($"{{GINA:{key}}}");
      Assert.IsNotNull(record);
      Assert.IsTrue(record.IsMine);
      Assert.AreEqual("You", record.To);
    }

    [TestMethod]
    public void CheckGina_SayChannel_AddsHistoryButIsNotAutoProcessed()
    {
      var key = Guid.NewGuid().ToString("N");
      var chatType = new ChatType(ChatChannels.Say, "Somesender", 0) { SenderIsYou = false };

      GinaUtil.CheckGina([], chatType, $"{{GINA:{key}}}", 1234.0, "P1", null);

      // Every GINA chat lands in history (channel names capitalized); only group/guild/raid/tell
      // are eligible for auto-import.
      var record = FindRecord($"{{GINA:{key}}}");
      Assert.IsNotNull(record);
      Assert.AreEqual("Say", record.To);
    }

    [TestMethod]
    public void QuickShareState_ConsecutiveSameKey_BecomesOneRecord()
    {
      var state = new QuickShareState();
      var record = new QuickShareRecord { Key = "{GINA:dedupe}", BeginTime = 1.0, From = "A", IsMine = true };

      Assert.IsTrue(state.Add(record));
      Assert.IsFalse(state.Add(new QuickShareRecord { Key = "{GINA:dedupe}", BeginTime = 1.0, From = "A" }), "same key+time right after must not duplicate");
      Assert.IsTrue(state.Add(new QuickShareRecord { Key = "{GINA:other}", BeginTime = 2.0, From = "B" }));

      Assert.AreEqual(2, state.Snapshot().Count);
      Assert.IsTrue(state.IsMine("{GINA:dedupe}"));
      Assert.IsFalse(state.IsMine("{GINA:other}"));
    }

    [TestMethod]
    public async Task GinaPackage_ConvertAndImport_RoundTrips()
    {
      const string xml = """
        <SharedData>
          <TriggerGroup>
            <Name>Gina Test Folder</Name>
            <Triggers>
              <Trigger>
                <Name>Fireball Cast</Name>
                <TriggerText>spellcast:Fireball</TriggerText>
                <UseText>true</UseText>
                <DisplayText>Fireball!</DisplayText>
              </Trigger>
            </Triggers>
          </TriggerGroup>
        </SharedData>
        """;

      var nodes = GinaUtil.CovertToTriggerNodes(MakeGinaPackage(xml));

      // GINA conversion produces the same root-wrapped shape Import expects.
      Assert.AreEqual(1, nodes.Count);
      var folder = nodes[0].Nodes?.FirstOrDefault();
      Assert.IsNotNull(folder, "expected the TriggerGroup to become a folder node");
      Assert.AreEqual("Gina Test Folder", folder.Name);
      var trigger = folder.Nodes?.FirstOrDefault();
      Assert.IsNotNull(trigger);
      Assert.AreEqual("spellcast:Fireball", trigger.TriggerData.Pattern);
      Assert.AreEqual("Fireball!", trigger.TriggerData.TextToDisplay);

      // End-to-end into a real (temporary) store, the same call GINA's import path makes.
      var dir = Directory.CreateDirectory(Path.Combine(TestTemp.Root, Guid.NewGuid().ToString("N"))).FullName;
      _dirs.Add(dir);
      await using var db = new TriggerStateDB(Path.Combine(dir, "test.db"), applyLegacyUpgrades: false);

      await db.ImportTriggers("", nodes, new HashSet<string> { "P1" });

      var (_, allNodes, _) = await db.GetTriggerTree("P1");
      var imported = allNodes.FirstOrDefault(n => n.Name == "Fireball Cast");
      Assert.IsNotNull(imported, "GINA trigger did not reach the store");
      Assert.AreEqual("Fireball!", imported.TriggerData.TextToDisplay);
    }

    /// <summary>GINA packages are zip files; ReadXml takes the first entry as UTF-8 XML text.</summary>
    private static byte[] MakeGinaPackage(string xml)
    {
      var ms = new MemoryStream();
      using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
      {
        var entry = zip.CreateEntry("data.xml");
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(xml);
      }

      return ms.ToArray();
    }
  }
}
