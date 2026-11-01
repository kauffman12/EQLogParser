using EQLogParser;
using System.Net;
using static EQLogParserTest.WebhookTestHandler;

namespace EQLogParserTest;

[TestClass]
public class WebhookFailureEpisodeTest
{
  private const string Url = "https://discord.com/api/webhooks/123/test";

  [TestMethod]
  public async Task SharedWebhookReportsEachCauseOnceUntilDeliverySucceeds()
  {
    var status = HttpStatusCode.NotFound;
    using var handler = new WebhookTestHandler((_, _) => Task.FromResult(Response(status)));
    using var client = new HttpClient(handler);
    var queue = new WebhookDispatchQueue(new DiscordWebhookSender(client));
    var sources = queue.UpdateSources("owner", "char", [("first", "First"), ("second", "Second")]);
    var reports = new List<WebhookError>();
    queue.EventsSendFailed += reports.Add;
    for (var i = 0; i < 10; i++)
    {
      await queue.EnqueueAsync(sources[i % 2 == 0 ? "first" : "second"],
        Url + (i % 2 == 0 ? "?wait=false" : "?thread_id=7"), "loot", 0);
    }

    Assert.HasCount(1, reports);
    Assert.HasCount(1, queue.Status.Errors);
    status = HttpStatusCode.Forbidden;
    await queue.EnqueueAsync(sources["first"], Url, "loot", 0);
    status = HttpStatusCode.NotFound;
    await queue.EnqueueAsync(sources["first"], Url, "loot", 0);
    Assert.HasCount(2, reports, "Switching back to an already reported cause is still the same broken episode.");

    status = HttpStatusCode.OK;
    await queue.EnqueueAsync(sources["first"], Url, "loot", 0);
    Assert.IsEmpty(queue.Status.Errors);
    status = HttpStatusCode.NotFound;
    await queue.EnqueueAsync(sources["second"], Url, "loot", 0);
    Assert.HasCount(3, reports);
  }

  [TestMethod]
  [DataRow(0)]
  [DataRow(1)]
  [DataRow(2)]
  public async Task RetiringASourceRemovesItsErrorAndPublishesEvenWithNoPendingWork(int door)
  {
    using var handler = new WebhookTestHandler((_, _) => Task.FromResult(Response(HttpStatusCode.NotFound)));
    using var client = new HttpClient(handler);
    var queue = new WebhookDispatchQueue(new DiscordWebhookSender(client));
    var source = queue.UpdateSources("owner", "char", [("trigger", "Loot")])["trigger"];
    await queue.EnqueueAsync(source, Url, "loot", 0);
    Assert.HasCount(1, queue.Status.Errors);
    Assert.AreEqual(0, queue.Status.Pending);
    var notices = 0;
    queue.EventsStatusChanged += () => notices++;
    Retire(queue, source, door);

    Assert.IsEmpty(queue.Status.Errors);
    Assert.AreEqual(1, notices);
    await queue.EnqueueAsync(source, Url, "stale", 0);
    Assert.IsEmpty(queue.Status.Errors);
  }

  [TestMethod]
  [DataRow(0, false)]
  [DataRow(0, true)]
  [DataRow(1, false)]
  [DataRow(1, true)]
  [DataRow(2, false)]
  [DataRow(2, true)]
  public async Task RetiringOneAffectedSourceKeepsTheSharedEpisodeAndNavigatesToASurvivor(int door, bool retireLatest)
  {
    var status = HttpStatusCode.NotFound;
    using var handler = new WebhookTestHandler((_, _) => Task.FromResult(Response(status)));
    using var client = new HttpClient(handler);
    var queue = new WebhookDispatchQueue(new DiscordWebhookSender(client));
    var first = queue.UpdateSources("owner1", "char1", [("loot", "First")])["loot"];
    var second = queue.UpdateSources("owner2", "char2", [("loot", "Second")])["loot"];
    var reports = new List<WebhookError>();
    queue.EventsSendFailed += reports.Add;
    await queue.EnqueueAsync(first, Url + "?thread_id=1", "first", 0);
    status = HttpStatusCode.Forbidden;
    await queue.EnqueueAsync(second, Url + "?thread_id=2", "second", 0);
    Assert.HasCount(2, reports);
    Assert.AreSame(second, queue.Status.Errors.Single().Source);
    // Both sources need the fallback after editing their URL, not just the source used for navigation.
    Assert.IsNotNull(queue.ErrorFor("new first URL", "char1", "loot"));
    Assert.IsNotNull(queue.ErrorFor("new second URL", "char2", "loot"));
    var removed = retireLatest ? second : first;
    var survivor = retireLatest ? first : second;
    var notices = 0;
    queue.EventsStatusChanged += () => notices++;
    Retire(queue, removed, door);
    Assert.AreEqual(1, notices, "Retirement must repaint even if it only changes error attribution.");
    Assert.HasCount(1, queue.Status.Errors);
    Assert.AreSame(survivor, queue.Status.Errors[0].Source);
    Assert.AreEqual(Url + (retireLatest ? "?thread_id=1" : "?thread_id=2"), queue.Status.Errors[0].Url);
    Assert.IsNotNull(queue.ErrorFor(Url, survivor.CharacterId, "loot"));
    Assert.IsNull(queue.ErrorFor("new URL", removed.CharacterId, "loot"));
    foreach (var failure in new[] { HttpStatusCode.NotFound, HttpStatusCode.Forbidden })
    {
      status = failure;
      await queue.EnqueueAsync(survivor, Url, "still broken", 0);
    }
    Assert.HasCount(2, reports, "Retirement must not reopen causes already reported for this endpoint.");
    status = HttpStatusCode.OK;
    await queue.EnqueueAsync(survivor, Url, "recovered", 0);
    Assert.IsEmpty(queue.Status.Errors);
    status = HttpStatusCode.NotFound;
    await queue.EnqueueAsync(survivor, Url, "a new episode", 0);
    Assert.HasCount(3, reports);
    notices = 0;
    Retire(queue, survivor, door);
    Assert.IsEmpty(queue.Status.Errors);
    Assert.AreEqual(1, notices);
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public async Task CorrectingOneSourcesUrlKeepsOtherSourcesOnTheBrokenEndpointFlagged(bool correctLatest)
  {
    using var handler = new WebhookTestHandler((request, _) => Task.FromResult(Response(
      request.RequestUri?.AbsolutePath.EndsWith("fixed", StringComparison.Ordinal) == true
        ? HttpStatusCode.OK : HttpStatusCode.NotFound)));
    using var client = new HttpClient(handler);
    var queue = new WebhookDispatchQueue(new DiscordWebhookSender(client));
    var sources = queue.UpdateSources("owner", "char", [("first", "First"), ("second", "Second")]);
    var reports = new List<WebhookError>();
    queue.EventsSendFailed += reports.Add;
    await queue.EnqueueAsync(sources["first"], Url, "first", 0);
    await queue.EnqueueAsync(sources["second"], Url, "second", 0);
    var corrected = sources[correctLatest ? "second" : "first"];
    var remaining = sources[correctLatest ? "first" : "second"];
    await queue.EnqueueAsync(corrected, Url + "-fixed", "corrected", 0);
    Assert.HasCount(1, queue.Status.Errors);
    Assert.AreSame(remaining, queue.Status.Errors[0].Source);
    Assert.IsNotNull(queue.ErrorFor("edited URL", "char", remaining.TriggerId));
    Assert.IsNull(queue.ErrorFor(Url + "-fixed", "char", corrected.TriggerId));
    await queue.EnqueueAsync(remaining, Url, "still broken", 0);
    Assert.HasCount(1, reports);
    await queue.EnqueueAsync(remaining, Url + "-fixed", "corrected too", 0);
    Assert.IsEmpty(queue.Status.Errors);
    await queue.EnqueueAsync(remaining, Url, "new episode", 0);
    Assert.HasCount(2, reports);
  }

  [TestMethod]
  public async Task ExceptionMessagesCannotLeakWebhookSecretsIntoFailureReports()
  {
    using var handler = new WebhookTestHandler((_, _) =>
      throw new InvalidOperationException("Secret URL: " + Url));
    using var client = new HttpClient(handler);
    var queue = new WebhookDispatchQueue(new DiscordWebhookSender(client));
    var source = queue.UpdateSources("owner", "char", [("trigger", "Loot")])["trigger"];
    var messages = new List<string>();
    queue.EventsSendFailed += error => messages.Add(error.Message);
    await queue.EnqueueAsync(source, Url, "loot", 0);
    Assert.HasCount(1, messages);
    Assert.IsTrue(messages[0].Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
    Assert.IsFalse(messages[0].Contains(Url, StringComparison.Ordinal));
    Assert.IsFalse(messages[0].Contains("Secret", StringComparison.Ordinal));
  }

  [TestMethod]
  public async Task ThrowingStatusAndErrorSubscribersCannotStrandWorkOrStarveOtherSubscribers()
  {
    using var handler = new WebhookTestHandler((_, _) => Task.FromResult(Response(HttpStatusCode.NotFound)));
    using var client = new HttpClient(handler);
    var queue = new WebhookDispatchQueue(new DiscordWebhookSender(client));
    var source = queue.UpdateSources("owner", "char", [("trigger", "Loot")])["trigger"];
    var statuses = 0;
    var reports = 0;
    queue.EventsStatusChanged += () => throw new InvalidOperationException("status subscriber");
    queue.EventsStatusChanged += () => statuses++;
    queue.EventsSendFailed += _ => throw new InvalidOperationException("error subscriber");
    queue.EventsSendFailed += _ => reports++;
    await queue.EnqueueAsync(source, Url, "loot", 0).WaitAsync(TimeSpan.FromSeconds(5));
    Assert.AreEqual(0, queue.Status.Pending);
    Assert.AreEqual(1, reports);
    Assert.IsTrue(statuses > 0);
  }

  [TestMethod]
  [DataRow("")]
  [DataRow("   ")]
  public async Task AnUnconfiguredUrlDoesNotQueueReportOrCallHttp(string url)
  {
    using var handler = new WebhookTestHandler((_, _) => throw new AssertFailedException("Must not send."));
    using var client = new HttpClient(handler);
    var queue = new WebhookDispatchQueue(new DiscordWebhookSender(client));
    var source = queue.UpdateSources("owner", "char", [("trigger", "Loot")])["trigger"];
    var statuses = 0;
    queue.EventsStatusChanged += () => statuses++;
    await queue.EnqueueAsync(source, url, "loot", 5);
    Assert.AreEqual(0, queue.Status.Pending);
    Assert.IsEmpty(queue.Status.Errors);
    Assert.AreEqual(0, statuses);
  }

  private static void Retire(WebhookDispatchQueue queue, WebhookSource source, int door)
  {
    switch (door)
    {
      case 0:
        queue.UpdateSources(source.Owner, source.CharacterId, []);
        break;
      case 1:
        queue.SetEnabled(source.CharacterId, source.TriggerId, false);
        break;
      case 2:
        queue.ClearOwner(source.Owner, removeSources: true);
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(door));
    }
  }
}