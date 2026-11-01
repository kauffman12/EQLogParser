using EQLogParser;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EQLogParserTest;

[TestClass]
public class WebhookMessageTest
{
  [TestMethod]
  [DataRow(false, "N")]
  [DataRow(true, "A")]
  public void SinglePartMessagesAlwaysCarryIdentityAndDigest(bool aggregated, string mode)
  {
    const string content = "loot\nmore loot";
    var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    var parts = WebhookMessage.Create(content, aggregated);
    Assert.HasCount(1, parts);
    Assert.HasCount(32, parts[0].BatchId);
    using var payload = JsonDocument.Parse(parts[0].Json);
    Assert.AreEqual(content, payload.RootElement.GetProperty("content").GetString());
    Assert.AreEqual($"EQLP1|{parts[0].BatchId}|1|1|{mode}|{digest}",
      payload.RootElement.GetProperty("embeds")[0].GetProperty("footer").GetProperty("text").GetString());
  }

  [TestMethod]
  [DataRow(false, "N")]
  [DataRow(true, "A")]
  public void EveryPartCarriesTheCompleteBatchIdentityAndDigest(bool aggregated, string mode)
  {
    var content = new string('a', 1400) + "\n" + new string('b', 1400);
    var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    var parts = WebhookMessage.Create(content, aggregated);
    Assert.HasCount(2, parts);
    Assert.HasCount(32, parts[0].BatchId);
    Assert.AreEqual(content, string.Concat(parts.Select(part => part.Content)));
    for (var i = 0; i < parts.Count; i++)
    {
      using var payload = JsonDocument.Parse(parts[i].Json);
      Assert.AreEqual($"EQLP1|{parts[0].BatchId}|{i + 1}|2|{mode}|{digest}",
        payload.RootElement.GetProperty("embeds")[0].GetProperty("footer").GetProperty("text").GetString());
    }
  }

  [TestMethod]
  public void PackingDoesNotCutLootLinesThatFitInOneRequest()
  {
    var lines = new[] { new string('a', 700), new string('b', 700), new string('c', 700) };
    var content = string.Join("\n", lines);
    var parts = WebhookMessage.Create(content, true);
    Assert.HasCount(2, parts);
    Assert.AreEqual(lines[0] + "\n" + lines[1] + "\n", parts[0].Content);
    Assert.AreEqual(lines[2], parts[1].Content);
    Assert.IsTrue(parts.All(part => part.Content.Length <= 2000));
    Assert.AreEqual(content, string.Concat(parts.Select(part => part.Content)));
  }

  [TestMethod]
  public void AnOversizedLineNeverCutsASurrogatePairOrCrLf()
  {
    var content = new string('a', 1999) + "🌳" + new string('b', 1998) + "\r\n" + new string('c', 2100);
    var parts = WebhookMessage.Create(content, false);
    Assert.AreEqual(content, string.Concat(parts.Select(part => part.Content)));
    Assert.IsTrue(parts.All(part => part.Content.Length <= 2000));
    Assert.IsTrue(parts.All(part => !char.IsHighSurrogate(part.Content[^1]) && part.Content[^1] != '\r'));
  }

  [TestMethod]
  public void ChoicesKeepTheForksStoredValuesAndOfferOneSecondWithoutChangingTheDefault()
  {
    Assert.AreEqual("No", WebhookAggregation.Options[0].Text);
    Assert.AreEqual(0, WebhookAggregation.Options[0].Value);
    Assert.AreEqual(0, WebhookAggregation.SecondsFor(0));
    for (var value = 1; value <= 6; value++)
    {
      Assert.AreEqual(value * 5, WebhookAggregation.SecondsFor(value));
    }

    Assert.AreEqual(1, WebhookAggregation.SecondsFor(WebhookAggregation.Options.Single(item => item.Text == "1s").Value));
    Assert.AreEqual(0, WebhookAggregation.SecondsFor(-1));
    Assert.AreEqual(0, WebhookAggregation.SecondsFor(int.MaxValue));
  }
}