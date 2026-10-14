namespace EQLogParser
{
  [TestClass]
  public class ChatLineParserTest
  {
    [TestMethod]
    public void TestOtherArrow()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test -> Test2:");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Tell, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual("Test2", chatType.Receiver);
      Assert.AreEqual(40, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test -> Test2: hello");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Tell, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual("Test2", chatType.Receiver);
      Assert.AreEqual(40, chatType.TextStart);
    }

    [TestMethod]
    public void TestOtherAuction()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test auctions, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Auction, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(43, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test auctions, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Auction, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(43, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test auctions,  'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Auction, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(44, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test auctions, in an unknown tongue, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Auction, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(65, chatType.TextStart);
    }

    [TestMethod]
    public void TestOtherSay()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test says, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Say, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(39, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test says, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Say, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(39, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test says,  'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Say, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(40, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test says, in an unknown tongue, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Say, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(61, chatType.TextStart);
    }

    [TestMethod]
    public void TestOtherSayOutOfCharacter()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test says out of character, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Ooc, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(56, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test says out of character, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Ooc, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(56, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test says out of character,  'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Ooc, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(57, chatType.TextStart);
    }

    [TestMethod]
    public void TestOtherSayPetLeader()
    {
      // EMU pet leader
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test says 'My leader is hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Say, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(38, chatType.TextStart);
    }

    [TestMethod]
    public void TestOtherTellsFellowship()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells the fellowship, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Fellowship, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(55, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells the fellowship, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Fellowship, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(55, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells the fellowship,  'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Fellowship, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(56, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells the fellowship, in an unknown tongue, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Fellowship, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(77, chatType.TextStart);
    }

    [TestMethod]
    public void TestOtherTellsGroup()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells the group, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Group, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(50, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells the group, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Group, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(50, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells the group,  'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Group, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(51, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells the group, in an unknown tongue, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Group, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(72, chatType.TextStart);
    }

    [TestMethod]
    public void TestOtherTellsGuild()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells the guild, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Guild, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(50, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells the guild, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Guild, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(50, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells the guild,  'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Guild, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(51, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells the guild, in an unknown tongue, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Guild, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(72, chatType.TextStart);
    }

    [TestMethod]
    public void TestOtherTellsRaid()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells the raid, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Raid, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(49, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells the raid, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Raid, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(49, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells the raid,  'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Raid, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(50, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells the raid, in an unknown tongue, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Raid, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(71, chatType.TextStart);
    }

    [TestMethod]
    public void TestOtherTellsOther()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells Test.test:34, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual("test.test", chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(53, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells Test.test:34, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual("test.test", chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(53, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells Test.test:34, in an unknown tongue, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual("test.test", chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(75, chatType.TextStart);
    }

    [TestMethod]
    public void TestOtherTellsYou()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells you, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Tell, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual("You", chatType.Receiver);
      Assert.AreEqual(44, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells you, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Tell, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual("You", chatType.Receiver);
      Assert.AreEqual(44, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells you, in an unknown tongue, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Tell, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual("You", chatType.Receiver);
      Assert.AreEqual(66, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test.test2 tells you, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Tell, chatType.Channel);
      Assert.AreEqual("test2", chatType.Sender);
      Assert.AreEqual("You", chatType.Receiver);
      Assert.AreEqual(50, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test.test2 tells you, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Tell, chatType.Channel);
      Assert.AreEqual("test2", chatType.Sender);
      Assert.AreEqual("You", chatType.Receiver);
      Assert.AreEqual(50, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test.test2 tells you, in an unknown tongue, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Tell, chatType.Channel);
      Assert.AreEqual("test2", chatType.Sender);
      Assert.AreEqual("You", chatType.Receiver);
      Assert.AreEqual(72, chatType.TextStart);
    }

    [TestMethod]
    public void TestOtherToldYou()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test.Test told you, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Tell, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(48, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test.Test told you, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Tell, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(48, chatType.TextStart);
    }

    [TestMethod]
    public void TestOtherShouts()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test shouts, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Shout, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(41, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test shouts, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Shout, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(41, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test shouts,  'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Shout, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(42, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test shouts, in an unknown tongue, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Shout, chatType.Channel);
      Assert.AreEqual("Test", chatType.Sender);
      Assert.AreEqual(63, chatType.TextStart);
    }

    [TestMethod]
    public void TestYouAuction()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] You auction, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Auction, chatType.Channel);
      Assert.AreEqual(ChatType.You, chatType.Sender);
      Assert.AreEqual(41, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You auction, 'test'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Auction, chatType.Channel);
      Assert.AreEqual(ChatType.You, chatType.Sender);
      Assert.AreEqual(41, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You auction,  'test'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Auction, chatType.Channel);
      Assert.AreEqual(ChatType.You, chatType.Sender);
      Assert.AreEqual(42, chatType.TextStart);
    }

    [TestMethod]
    public void TestYouSay()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] You say, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Say, chatType.Channel);
      Assert.AreEqual(ChatType.You, chatType.Sender);
      Assert.AreEqual(37, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You say, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Say, chatType.Channel);
      Assert.AreEqual(ChatType.You, chatType.Sender);
      Assert.AreEqual(37, chatType.TextStart);
    }

    [TestMethod]
    public void TestYouSayGuild()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] You say to your guild, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Guild, chatType.Channel);
      Assert.AreEqual(ChatType.You, chatType.Sender);
      Assert.AreEqual(51, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You say to your guild, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Guild, chatType.Channel);
      Assert.AreEqual(ChatType.You, chatType.Sender);
      Assert.AreEqual(51, chatType.TextStart);

      // old/bad message
      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You say to your guild,  'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Guild, chatType.Channel);
      Assert.AreEqual(ChatType.You, chatType.Sender);
      Assert.AreEqual(52, chatType.TextStart);
    }

    [TestMethod]
    public void TestYouSayFellowship()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] You say to your fellowship, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Fellowship, chatType.Channel);
      Assert.AreEqual(ChatType.You, chatType.Sender);
      Assert.AreEqual(56, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You say to your fellowship, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Fellowship, chatType.Channel);
      Assert.AreEqual(ChatType.You, chatType.Sender);
      Assert.AreEqual(56, chatType.TextStart);

      // old/bad message
      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You say to your fellowship,  'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Fellowship, chatType.Channel);
      Assert.AreEqual(ChatType.You, chatType.Sender);
      Assert.AreEqual(57, chatType.TextStart);
    }

    [TestMethod]
    public void TestYouSayOutOfCharacter()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] You say out of character, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Ooc, chatType.Channel);
      Assert.AreEqual(ChatType.You, chatType.Sender);
      Assert.AreEqual(54, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You say out of character, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Ooc, chatType.Channel);
      Assert.AreEqual(ChatType.You, chatType.Sender);
      Assert.AreEqual(54, chatType.TextStart);

      // old/bad message
      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You say out of character,  'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Ooc, chatType.Channel);
      Assert.AreEqual(ChatType.You, chatType.Sender);
      Assert.AreEqual(55, chatType.TextStart);
    }

    [TestMethod]
    public void TestYouShout()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] You shout, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Shout, chatType.Channel);
      Assert.AreEqual(ChatType.You, chatType.Sender);
      Assert.AreEqual(39, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You shout, 'test'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Shout, chatType.Channel);
      Assert.AreEqual(ChatType.You, chatType.Sender);
      Assert.AreEqual(39, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You shout,  'test'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Shout, chatType.Channel);
      Assert.AreEqual(ChatType.You, chatType.Sender);
      Assert.AreEqual(40, chatType.TextStart);
    }

    [TestMethod]
    public void TestYouTell()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] You tell test, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual("test", chatType.Channel);
      Assert.AreEqual(ChatType.You, chatType.Sender);
      Assert.AreEqual(43, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You tell test:22, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual("test", chatType.Channel);
      Assert.AreEqual(ChatType.You, chatType.Sender);
      Assert.AreEqual(46, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You tell Test.test:34, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual("test.test", chatType.Channel);
      Assert.AreEqual(ChatType.You, chatType.Sender);
      Assert.AreEqual(51, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You tell Test.test:34, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual("test.test", chatType.Channel);
      Assert.AreEqual(ChatType.You, chatType.Sender);
      Assert.AreEqual(51, chatType.TextStart);

      // old/bad message
      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You tell Test.test:34,  'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual("test.test", chatType.Channel);
      Assert.AreEqual(ChatType.You, chatType.Sender);
      Assert.AreEqual(52, chatType.TextStart);
    }

    [TestMethod]
    public void TestYouTellGroup()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] You tell your party, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Group, chatType.Channel);
      Assert.AreEqual(ChatType.You, chatType.Sender);
      Assert.AreEqual(49, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You tell your party, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Group, chatType.Channel);
      Assert.AreEqual(ChatType.You, chatType.Sender);
      Assert.AreEqual(49, chatType.TextStart);

      // old/bad message
      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You tell your party,  'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Group, chatType.Channel);
      Assert.AreEqual(ChatType.You, chatType.Sender);
      Assert.AreEqual(50, chatType.TextStart);
    }

    [TestMethod]
    public void TestYouTellRaid()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] You tell your raid, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Raid, chatType.Channel);
      Assert.AreEqual(ChatType.You, chatType.Sender);
      Assert.AreEqual(48, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You tell your raid, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Raid, chatType.Channel);
      Assert.AreEqual(ChatType.You, chatType.Sender);
      Assert.AreEqual(48, chatType.TextStart);

      // old/bad message
      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You tell your raid,  'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Raid, chatType.Channel);
      Assert.AreEqual(ChatType.You, chatType.Sender);
      Assert.AreEqual(49, chatType.TextStart);
    }

    [TestMethod]
    public void TestYouTold()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] You told test, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Tell, chatType.Channel);
      Assert.AreEqual(ChatType.You, chatType.Sender);
      Assert.AreEqual("test", chatType.Receiver);
      Assert.AreEqual(43, chatType.TextStart);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You told test, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Tell, chatType.Channel);
      Assert.AreEqual(ChatType.You, chatType.Sender);
      Assert.AreEqual("test", chatType.Receiver);
      Assert.AreEqual(43, chatType.TextStart);

      // in queue (text start is a little early but that's ok)
      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You told test '[queued], test'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Tell, chatType.Channel);
      Assert.AreEqual(ChatType.You, chatType.Sender);
      Assert.AreEqual("test", chatType.Receiver);
      Assert.AreEqual(42, chatType.TextStart);

      // old/bad message
      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You told test ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Tell, chatType.Channel);
      Assert.AreEqual(ChatType.You, chatType.Sender);
      Assert.AreEqual("test", chatType.Receiver);
      Assert.AreEqual(42, chatType.TextStart);

      // with server name
      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You told Test.test, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Tell, chatType.Channel);
      Assert.AreEqual(ChatType.You, chatType.Sender);
      Assert.AreEqual("test", chatType.Receiver);
      Assert.AreEqual(48, chatType.TextStart);

      // with server name
      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You told Test.test, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Tell, chatType.Channel);
      Assert.AreEqual(ChatType.You, chatType.Sender);
      Assert.AreEqual("test", chatType.Receiver);
      Assert.AreEqual(48, chatType.TextStart);

      // old/bad message
      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You told Test.test ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(ChatChannels.Tell, chatType.Channel);
      Assert.AreEqual(ChatType.You, chatType.Sender);
      Assert.AreEqual("test", chatType.Receiver);
      Assert.AreEqual(47, chatType.TextStart);
    }

    /*
     * The marker gate (one SearchValues scan in ChatLineParser) is allowed to be wrong in only one direction: a line carrying no marker may be
     * refused without looking further, and a line carrying one must reach the recognizer that actually decides. So each of these fixtures is the
     * shape ITS marker exists for, with the channel it must produce — a fixture the recognizer cannot read would prove nothing (it would agree even
     * if the gate refused it), which is how a first cut of this test passed while a needle was missing from the list.
     *
     * " auctions" and " auction" overlap by construction (one contains the other, as in the eight-call chain this replaced), so no single fixture
     * can prove both; removing either still leaves the auction line reaching the recognizer, which is the behaviour that matters.
     */
    [TestMethod]
    public void MarkerGateRefusesNothingItShouldNot()
    {
      (string Action, string Channel)[] chatLines = [
        ("Test says, 'hello'", ChatChannels.Say),                                       // " says"
        ("Test says out of character, 'hello'", ChatChannels.Ooc),                        // " says"
        ("Useless says, 'My leader is Incogitable.'", ChatChannels.Say),                  // "'My leader is"
        ("Test tells the guild, 'hello'", ChatChannels.Guild),                            // " tells "
        ("Test.Test told you, 'hello'", ChatChannels.Tell),                               // " told "
        ("Test shouts, 'hello'", ChatChannels.Shout),                                     // " shouts"
        ("Test auctions, 'hello'", ChatChannels.Auction),                                 // " auction(s)"
        ("Test -> Test2: hello", ChatChannels.Tell),                                      // " -> "
      ];

      foreach (var (action, channel) in chatLines)
      {
        var decided = ChatLineParser.CheckOtherCriteria(action.AsSpan());
        var gated = ChatLineParser.ParseChatType(action);

        Assert.IsNotNull(decided, $"{action}: the fixture itself no longer reaches a recognizer — it proves nothing about the gate");
        Assert.IsNotNull(gated, $"{action}: the marker gate refused a line the recognizer reads as {decided.Channel} chat");
        Assert.AreEqual(channel, gated.Channel);
        Assert.AreEqual(decided.TextStart, gated.TextStart);
        Assert.AreEqual(decided.Sender, gated.Sender);
      }

      // Combat and system shapes: none of them carries a marker, so all of them must fall through to the real parsers untouched.
      string[] notChat = [
        "Goratoar bites Kizant for 137 damage.",
        "Kizant is buffed by Cumlaud. (Affirmation of the Hero)",
        "You begin casting Cancel Magic.",
        "~ Kizant wonders.",
        "Vex has been slain by a ghoul.",
        "You have taken 4852 damage from Nectar of Misery by Commander Gartik.",
        "Your pet misses a ghoul.",
        "Kizant's pet has been slain by a ghoul.",
      ];

      foreach (var action in notChat)
      {
        Assert.IsNull(ChatLineParser.ParseChatType(action), $"{action}: claimed as chat by the marker gate");
      }
    }

    private static ChatType ParseText(string line)
    {
      var action = line[27..];
      return ChatLineParser.ParseChatType(action);
    }
  }
}