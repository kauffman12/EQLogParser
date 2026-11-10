#nullable enable annotations

namespace EQLogParser;

/*
 * ChatType.IsFrom answers "did the character this caller speaks for send this line", so a trigger processor watching
 * one character's log stops asking the main window. If it drifts from these rows, commands and quick-share ownership
 * land on the wrong character again.
 */
[TestClass]
public class ChatTypeOwnershipTest
{
  private string? _savedPlayerName;

  [TestInitialize]
  public void Setup()
  {
    _savedPlayerName = ConfigUtil.PlayerName;
    ConfigUtil.PlayerName = null;
  }

  [TestCleanup]
  public void Cleanup()
  {
    ConfigUtil.PlayerName = _savedPlayerName;
  }

  [TestMethod]
  [DataRow("You", "Soandso", true)]
  [DataRow("You", "", true)]
  [DataRow("You", null, true)]
  [DataRow("Soandso", "Soandso", true)]
  [DataRow("soandso", "Soandso", true)]
  [DataRow("SOANDSO", "Soandso", true)]
  [DataRow("Soandso", null, false)]
  [DataRow("Soandso", "", false)]
  [DataRow("Stranger", "Soandso", false)]
  public void OwnershipIsTheLiteralYouOrTheCallerOwnName(string sender, string? currentPlayer, bool expected)
  {
    var chat = new ChatType(ChatChannels.Tell, sender, 0);
    Assert.AreEqual(expected, chat.IsFrom(currentPlayer));
  }

  [TestMethod]
  public void NullSenderIsNobodyOwnsIt()
  {
    Assert.IsFalse(new ChatType().IsFrom("Soandso"));
    Assert.IsFalse(new ChatType().IsFrom(null));
  }

  // The two questions have to stay separate: SenderIsYou still serves the main window's own chat surfaces.
  [TestMethod]
  [DataRow("Someonelse", "Soandso", "Soandso", false, true)]
  [DataRow("Soandso", "Soandso", "Kizant", true, false)]
  public void SharedFlagAndPerLaneQuestionMayDiffer(string globalPlayer, string sender, string askingPlayer,
    bool sharedAnswer, bool laneAnswer)
  {
    ConfigUtil.PlayerName = globalPlayer;
    var chat = new ChatType(ChatChannels.Tell, sender, 0);
    Assert.AreEqual(sharedAnswer, chat.SenderIsYou);
    Assert.AreEqual(laneAnswer, chat.IsFrom(askingPlayer));
  }
}
