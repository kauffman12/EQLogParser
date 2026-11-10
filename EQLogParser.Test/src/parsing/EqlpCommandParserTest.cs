#nullable enable annotations

namespace EQLogParser;

[TestClass]
public class EqlpCommandParserTest
{
  private string? _savedPlayerName;

  [TestInitialize]
  public void Initialize()
  {
    _savedPlayerName = ConfigUtil.PlayerName;
    ConfigUtil.PlayerName = "Otherplayer";
  }

  [TestCleanup]
  public void Cleanup()
  {
    ConfigUtil.PlayerName = _savedPlayerName;
  }

  [TestMethod]
  [DataRow("You tell Soandso, '{EQLP:Stop}'")]
  [DataRow("You told Soandso, '{EQLP:Stop}'")]
  [DataRow("You tell Server.Soandso, '{EQLP:Stop}'")]
  [DataRow("You told Server.Soandso, '{EQLP:Stop}'")]
  [DataRow("You say, '{EQLP:Stop}'")]
  [DataRow("You tell your party, '{EQLP:Stop}'")]
  [DataRow("You tell your raid, '{EQLP:Stop}'")]
  [DataRow("You shout, '{EQLP:Stop}'")]
  [DataRow("You tell Soandso, '{eqlp:stop}'")]
  [DataRow("You tell Soandso, '{EQLP:STOP} trailing words'")]
  public void OutgoingCommandsKeepTheirExistingSyntax(string action)
  {
    Assert.AreEqual(EqlpCommand.Stop, Match(action));
    Assert.AreEqual(EqlpCommand.Stop, Match(action, null));
    Assert.AreEqual(EqlpCommand.Stop, Match(action, ""));
  }

  [TestMethod]
  [DataRow(null)]
  [DataRow("")]
  [DataRow("Otherplayer")]
  [DataRow("Soandso")]
  public void IncomingSelfTellUsesMonitoredCharacterNotGlobalPlayer(string? globalPlayer)
  {
    ConfigUtil.PlayerName = globalPlayer;
    foreach (var sender in new[] { "Soandso", "soandso", "SOANDSO" })
    {
      Assert.AreEqual(EqlpCommand.Stop, Match($"{sender} tells you, '{{EQLP:Stop}}'"));
    }
  }

  [TestMethod]
  [DataRow("Server.Soandso tells you, '{EQLP:Stop}'")]
  [DataRow("Server.Soandso told you, '{EQLP:Stop}'")]
  [DataRow("Server.soandso tells you, '{eqlp:stop}'")]
  public void ServerQualifiedSelfTellUsesTheParsedPlayerName(string action)
  {
    var chat = ChatLineParser.ParseChatType(action);
    Assert.IsNotNull(chat);
    Assert.AreEqual("Soandso", chat.Sender, true);
    Assert.IsTrue(chat.IsFrom("Soandso"));
    Assert.AreEqual(EqlpCommand.Stop, EqlpCommandParser.Match(chat, action, "Soandso"));
    Assert.AreEqual(EqlpCommand.None, EqlpCommandParser.Match(chat, action, "Otherplayer"));
  }

  [TestMethod]
  [DataRow("You tell Soandso, '{EQLP:CLEAR}'")]
  [DataRow("You say, '{eqlp:clear}'")]
  [DataRow("Soandso tells you, '{EQLP:Clear}'")]
  public void ClearUsesTheSameSenderAndSyntaxRules(string action)
  {
    Assert.AreEqual(EqlpCommand.Clear, Match(action));
  }

  [TestMethod]
  [DataRow("Otherplayer tells you, '{EQLP:STOP}'")]
  [DataRow("Otherplayer tells you, '{EQLP:CLEAR}'")]
  public void GlobalPlayerIsNotAuthorizedInAnotherCharactersLog(string action)
  {
    var chat = ChatLineParser.ParseChatType(action);
    Assert.IsNotNull(chat);
    Assert.IsTrue(chat.SenderIsYou, "The shared flag reflects the global player, not this processor.");
    Assert.AreEqual(EqlpCommand.None, EqlpCommandParser.Match(chat, action, "Soandso"));
  }

  [TestMethod]
  [DataRow("Stranger tells you, '{EQLP:STOP}'")]
  [DataRow("Stranger tells you, '{EQLP:CLEAR}'")]
  [DataRow("You say, ' {EQLP:STOP}'")]
  [DataRow("You say, 'prefix {EQLP:STOP}'")]
  [DataRow("You say, '{EQLP: STOP}'")]
  [DataRow("You say, '{EQLP:STOP'")]
  [DataRow("You say, '{EQLP:STOPPED}'")]
  [DataRow("You say, '{EQLP:UNKNOWN}'")]
  [DataRow("You say, ' {EQLP:CLEAR}'")]
  [DataRow("You say, 'prefix {EQLP:CLEAR}'")]
  [DataRow("You say, '{EQLP: CLEAR}'")]
  public void OtherSendersAndNonleadingOrMalformedCommandsRemainRefused(string action)
  {
    Assert.AreEqual(EqlpCommand.None, Match(action));
  }

  [TestMethod]
  [DataRow(null)]
  [DataRow("")]
  public void IncomingTellNeedsAKnownMonitoredPlayer(string? currentPlayer)
  {
    Assert.AreEqual(EqlpCommand.None, Match("Otherplayer tells you, '{EQLP:STOP}'", currentPlayer));
  }

  // The accepted counterpart to InvalidOffsetsAreRefused: without it, a matcher rejecting every hand-built ChatType
  // would still pass, since all other cases reach it through the real chat parser.
  [TestMethod]
  [DataRow("  {EQLP:STOP}", 29, false)]
  [DataRow("  {EQLP:CLEAR}", 29, true)]
  public void OffsetIsTakenRelativeToTheTimestampStrippedAction(string action, int textStart, bool isClear)
  {
    var chat = new ChatType(ChatChannels.Tell, ChatType.You, textStart);
    var expected = isClear ? EqlpCommand.Clear : EqlpCommand.Stop;
    Assert.AreEqual(expected, EqlpCommandParser.Match(chat, action, "Soandso"));
  }

  [TestMethod]
  public void OffsetIsNotIgnoredWhenItLandsInsideTheCommand()
  {
    var chat = new ChatType(ChatChannels.Tell, ChatType.You, 28);
    Assert.AreEqual(EqlpCommand.None, EqlpCommandParser.Match(chat, "{EQLP:STOP}", "Soandso"));
  }

  [TestMethod]
  [DataRow(int.MinValue)]
  [DataRow(-1)]
  [DataRow(0)]
  [DataRow(27)]
  [DataRow(37)]
  [DataRow(38)]
  [DataRow(int.MaxValue)]
  public void InvalidOffsetsAreRefused(int textStart)
  {
    const string action = "{EQLP:STOP}";
    var chat = new ChatType(ChatChannels.Tell, ChatType.You, textStart);
    Assert.AreEqual(EqlpCommand.None, EqlpCommandParser.Match(chat, action, "Soandso"));
  }

  [TestMethod]
  public void MissingInputsAreRefused()
  {
    const string action = "You say, '{EQLP:STOP}'";
    var chat = ChatLineParser.ParseChatType(action);
    Assert.IsNotNull(chat);
    Assert.AreEqual(EqlpCommand.None, EqlpCommandParser.Match(null, action, "Soandso"));
    Assert.AreEqual(EqlpCommand.None, EqlpCommandParser.Match(chat, null, "Soandso"));
    Assert.AreEqual(EqlpCommand.None, EqlpCommandParser.Match(new ChatType(), action, "Soandso"));
    Assert.AreEqual(EqlpCommand.None, Match("You say, ''"));
    Assert.AreEqual(EqlpCommand.None,
      EqlpCommandParser.Match(ChatLineParser.ParseChatType("Not a chat line"), "Not a chat line", "Soandso"));
  }

  private static EqlpCommand Match(string action, string? currentPlayer = "Soandso")
  {
    var chat = ChatLineParser.ParseChatType(action);
    Assert.IsNotNull(chat, action);
    return EqlpCommandParser.Match(chat, action, currentPlayer);
  }
}
