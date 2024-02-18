using EQLogParser;

namespace EQLogParserTest
{
  [TestClass]
  public class ChatLineParserTest
  {
    [TestMethod]
    public void TestOtherArrow()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test -> Test2:");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Tell);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.Receiver, "Test2");
      Assert.AreEqual(chatType.TextStart, 40);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test -> Test2: hello");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Tell);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.Receiver, "Test2");
      Assert.AreEqual(chatType.TextStart, 40);
    }

    [TestMethod]
    public void TestOtherAuction()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test auctions, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Auction);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 43);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test auctions, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Auction);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 43);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test auctions,  'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Auction);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 44);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test auctions, in an unknown tongue, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Auction);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 65);
    }

    [TestMethod]
    public void TestOtherSay()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test says, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Say);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 39);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test says, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Say);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 39);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test says,  'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Say);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 40);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test says, in an unknown tongue, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Say);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 61);
    }

    [TestMethod]
    public void TestOtherSayOutOfCharacter()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test says out of character, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Ooc);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 56);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test says out of character, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Ooc);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 56);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test says out of character,  'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Ooc);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 57);
    }

    [TestMethod]
    public void TestOtherSayPetLeader()
    {
      // EMU pet leader
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test says 'My leader is hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Say);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 38);
    }

    [TestMethod]
    public void TestOtherTellsFellowship()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells the fellowship, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Fellowship);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 55);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells the fellowship, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Fellowship);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 55);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells the fellowship,  'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Fellowship);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 56);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells the fellowship, in an unknown tongue, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Fellowship);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 77);
    }

    [TestMethod]
    public void TestOtherTellsGroup()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells the group, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Group);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 50);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells the group, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Group);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 50);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells the group,  'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Group);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 51);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells the group, in an unknown tongue, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Group);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 72);
    }

    [TestMethod]
    public void TestOtherTellsGuild()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells the guild, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Guild);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 50);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells the guild, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Guild);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 50);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells the guild,  'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Guild);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 51);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells the guild, in an unknown tongue, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Guild);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 72);
    }

    [TestMethod]
    public void TestOtherTellsRaid()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells the raid, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Raid);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 49);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells the raid, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Raid);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 49);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells the raid,  'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Raid);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 50);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells the raid, in an unknown tongue, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Raid);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 71);
    }

    [TestMethod]
    public void TestOtherTellsOther()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells Test.test:34, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, "test.test");
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 53);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells Test.test:34, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, "test.test");
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 53);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells Test.test:34, in an unknown tongue, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, "test.test");
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 75);
    }

    [TestMethod]
    public void TestOtherTellsYou()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells you, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Tell);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.Receiver, "You");
      Assert.AreEqual(chatType.TextStart, 44);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells you, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Tell);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.Receiver, "You");
      Assert.AreEqual(chatType.TextStart, 44);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test tells you, in an unknown tongue, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Tell);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.Receiver, "You");
      Assert.AreEqual(chatType.TextStart, 66);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test.test2 tells you, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Tell);
      Assert.AreEqual(chatType.Sender, "test2");
      Assert.AreEqual(chatType.Receiver, "You");
      Assert.AreEqual(chatType.TextStart, 50);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test.test2 tells you, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Tell);
      Assert.AreEqual(chatType.Sender, "test2");
      Assert.AreEqual(chatType.Receiver, "You");
      Assert.AreEqual(chatType.TextStart, 50);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test.test2 tells you, in an unknown tongue, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Tell);
      Assert.AreEqual(chatType.Sender, "test2");
      Assert.AreEqual(chatType.Receiver, "You");
      Assert.AreEqual(chatType.TextStart, 72);
    }

    [TestMethod]
    public void TestOtherToldYou()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test.Test told you, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Tell);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 48);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test.Test told you, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Tell);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 48);
    }

    [TestMethod]
    public void TestOtherShouts()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test shouts, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Shout);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 41);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test shouts, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Shout);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 41);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test shouts,  'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Shout);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 42);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] Test shouts, in an unknown tongue, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Shout);
      Assert.AreEqual(chatType.Sender, "Test");
      Assert.AreEqual(chatType.TextStart, 63);
    }

    [TestMethod]
    public void TestYouAuction()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] You auction, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Auction);
      Assert.AreEqual(chatType.Sender, ChatType.You);
      Assert.AreEqual(chatType.TextStart, 41);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You auction, 'test'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Auction);
      Assert.AreEqual(chatType.Sender, ChatType.You);
      Assert.AreEqual(chatType.TextStart, 41);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You auction,  'test'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Auction);
      Assert.AreEqual(chatType.Sender, ChatType.You);
      Assert.AreEqual(chatType.TextStart, 42);
    }

    [TestMethod]
    public void TestYouSay()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] You say, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Say);
      Assert.AreEqual(chatType.Sender, ChatType.You);
      Assert.AreEqual(chatType.TextStart, 37);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You say, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Say);
      Assert.AreEqual(chatType.Sender, ChatType.You);
      Assert.AreEqual(chatType.TextStart, 37);
    }

    [TestMethod]
    public void TestYouSayGuild()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] You say to your guild, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Guild);
      Assert.AreEqual(chatType.Sender, ChatType.You);
      Assert.AreEqual(chatType.TextStart, 51);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You say to your guild, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Guild);
      Assert.AreEqual(chatType.Sender, ChatType.You);
      Assert.AreEqual(chatType.TextStart, 51);

      // old/bad message
      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You say to your guild,  'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Guild);
      Assert.AreEqual(chatType.Sender, ChatType.You);
      Assert.AreEqual(chatType.TextStart, 52);
    }

    [TestMethod]
    public void TestYouSayFellowship()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] You say to your fellowship, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Fellowship);
      Assert.AreEqual(chatType.Sender, ChatType.You);
      Assert.AreEqual(chatType.TextStart, 56);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You say to your fellowship, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Fellowship);
      Assert.AreEqual(chatType.Sender, ChatType.You);
      Assert.AreEqual(chatType.TextStart, 56);

      // old/bad message
      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You say to your fellowship,  'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Fellowship);
      Assert.AreEqual(chatType.Sender, ChatType.You);
      Assert.AreEqual(chatType.TextStart, 57);
    }

    [TestMethod]
    public void TestYouSayOutOfCharacter()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] You say out of character, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Ooc);
      Assert.AreEqual(chatType.Sender, ChatType.You);
      Assert.AreEqual(chatType.TextStart, 54);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You say out of character, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Ooc);
      Assert.AreEqual(chatType.Sender, ChatType.You);
      Assert.AreEqual(chatType.TextStart, 54);

      // old/bad message
      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You say out of character,  'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Ooc);
      Assert.AreEqual(chatType.Sender, ChatType.You);
      Assert.AreEqual(chatType.TextStart, 55);
    }

    [TestMethod]
    public void TestYouShout()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] You shout, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Shout);
      Assert.AreEqual(chatType.Sender, ChatType.You);
      Assert.AreEqual(chatType.TextStart, 39);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You shout, 'test'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Shout);
      Assert.AreEqual(chatType.Sender, ChatType.You);
      Assert.AreEqual(chatType.TextStart, 39);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You shout,  'test'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Shout);
      Assert.AreEqual(chatType.Sender, ChatType.You);
      Assert.AreEqual(chatType.TextStart, 40);
    }

    [TestMethod]
    public void TestYouTell()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] You tell test, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, "test");
      Assert.AreEqual(chatType.Sender, ChatType.You);
      Assert.AreEqual(chatType.TextStart, 43);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You tell test:22, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, "test");
      Assert.AreEqual(chatType.Sender, ChatType.You);
      Assert.AreEqual(chatType.TextStart, 46);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You tell Test.test:34, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, "test.test");
      Assert.AreEqual(chatType.Sender, ChatType.You);
      Assert.AreEqual(chatType.TextStart, 51);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You tell Test.test:34, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, "test.test");
      Assert.AreEqual(chatType.Sender, ChatType.You);
      Assert.AreEqual(chatType.TextStart, 51);

      // old/bad message
      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You tell Test.test:34,  'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, "test.test");
      Assert.AreEqual(chatType.Sender, ChatType.You);
      Assert.AreEqual(chatType.TextStart, 52);
    }

    [TestMethod]
    public void TestYouTellGroup()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] You tell your party, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Group);
      Assert.AreEqual(chatType.Sender, ChatType.You);
      Assert.AreEqual(chatType.TextStart, 49);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You tell your party, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Group);
      Assert.AreEqual(chatType.Sender, ChatType.You);
      Assert.AreEqual(chatType.TextStart, 49);

      // old/bad message
      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You tell your party,  'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Group);
      Assert.AreEqual(chatType.Sender, ChatType.You);
      Assert.AreEqual(chatType.TextStart, 50);
    }

    [TestMethod]
    public void TestYouTellRaid()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] You tell your raid, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Raid);
      Assert.AreEqual(chatType.Sender, ChatType.You);
      Assert.AreEqual(chatType.TextStart, 48);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You tell your raid, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Raid);
      Assert.AreEqual(chatType.Sender, ChatType.You);
      Assert.AreEqual(chatType.TextStart, 48);

      // old/bad message
      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You tell your raid,  'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Raid);
      Assert.AreEqual(chatType.Sender, ChatType.You);
      Assert.AreEqual(chatType.TextStart, 49);
    }

    [TestMethod]
    public void TestYouTold()
    {
      var chatType = ParseText("[Sun Oct 08 20:07:10 2023] You told test, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Tell);
      Assert.AreEqual(chatType.Sender, ChatType.You);
      Assert.AreEqual(chatType.Receiver, "test");
      Assert.AreEqual(chatType.TextStart, 43);

      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You told test, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Tell);
      Assert.AreEqual(chatType.Sender, ChatType.You);
      Assert.AreEqual(chatType.Receiver, "test");
      Assert.AreEqual(chatType.TextStart, 43);

      // in queue (text start is a little early but that's ok)
      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You told test '[queued], test'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Tell);
      Assert.AreEqual(chatType.Sender, ChatType.You);
      Assert.AreEqual(chatType.Receiver, "test");
      Assert.AreEqual(chatType.TextStart, 42);

      // old/bad message
      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You told test ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Tell);
      Assert.AreEqual(chatType.Sender, ChatType.You);
      Assert.AreEqual(chatType.Receiver, "test");
      Assert.AreEqual(chatType.TextStart, 42);

      // with server name
      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You told Test.test, ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Tell);
      Assert.AreEqual(chatType.Sender, ChatType.You);
      Assert.AreEqual(chatType.Receiver, "test");
      Assert.AreEqual(chatType.TextStart, 48);

      // with server name
      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You told Test.test, 'hello'");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Tell);
      Assert.AreEqual(chatType.Sender, ChatType.You);
      Assert.AreEqual(chatType.Receiver, "test");
      Assert.AreEqual(chatType.TextStart, 48);

      // old/bad message
      chatType = ParseText("[Sun Oct 08 20:07:10 2023] You told Test.test ''");
      Assert.IsNotNull(chatType);
      Assert.AreEqual(chatType.Channel, ChatChannels.Tell);
      Assert.AreEqual(chatType.Sender, ChatType.You);
      Assert.AreEqual(chatType.Receiver, "test");
      Assert.AreEqual(chatType.TextStart, 47);
    }

    private static ChatType ParseText(string line)
    {
      var action = line[27..];
      return ChatLineParser.ParseChatType(action);
    }
  }
}