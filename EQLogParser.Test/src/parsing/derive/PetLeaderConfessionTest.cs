namespace EQLogParser.Tests.Derive;

/*
 * R5-leader: ``Mgsk says, 'My leader is Xxuro.'`` — a summon naming its own master.
 *
 * The law this holds: the sentence is captured as a fact (EvPetLeader, written by CombatCapture on ANY host, not only by
 * the WPF ChatDB) and claimed at Certain with R24's guard, so it outranks `Targeted (NPC)` — which the client prints for
 * every mercenary and pet — while a name this capture saw as a person keeps being one. Before the rule existed, the pair
 * reached classification only as a strength-8 RegistrySeed seed: measured, `Useless` read Npc with this line in the log and
 * `Mgsk` read Unplaced across 726 group heal lines while naming Xxuro.
 */
[TestClass]
[DoNotParallelize]
public sealed class PetLeaderConfessionTest
{
  private const double T0 = 1_700_000_000d;

  [TestInitialize]
  public void Setup() => PlayerRegistry.Instance.Clear();

  [TestCleanup]
  public void Cleanup() => PlayerRegistry.Instance.Clear();

  // ---- the recognizer ----

  [TestMethod]
  [DataRow("Mgsk says, 'My leader is Xxuro.'", "Mgsk", "Xxuro")]
  [DataRow("Mgsk says, 'My leader is Xxuro'", "Mgsk", "Xxuro")]
  [DataRow("Test says 'My leader is Super.' ", "Test", "Super")]
  [DataRow("Test says 'My leader is hello'", "Test", "hello")]
  [DataRow("Brozoll`s warder says, 'My leader is Beheleast.'", "Brozoll`s warder", "Beheleast")]
  [DataRow("A fluffy pet says 'My leader is Super'", "A fluffy pet", "Super")]
  [DataRow("Useless tells you, 'My leader is Incogitable.'", "Useless", "Incogitable")]
  [DataRow("Useless tells you, 'My leader is Incogitable'", "Useless", "Incogitable")]
  [DataRow("Useless told you, 'My leader is Incogitable.'", "Useless", "Incogitable")]
  [DataRow("Useless told you, 'My leader is Incogitable'", "Useless", "Incogitable")]
  public void ConfessionKeepsOnlyTheOwnerAcrossLiveAndLegacyChatForms(string line, string sender, string expectedOwner)
  {
    var chat = ParseChat(line);
    Assert.IsNotNull(chat, $"the chat parser dropped [{line}]");
    Assert.AreEqual(sender, chat.Sender);
    Assert.IsTrue(ChatLineParser.TryPetLeaderOwner(chat, out var owner), "no confession read from a confession line");
    Assert.AreEqual(expectedOwner, owner, "neither sentence punctuation nor the chat quote belongs to the owner");
  }

  [TestMethod]
  [DataRow("Mgsk says, 'ready'")]
  [DataRow("Soell says, 'Plane of Love'")]
  [DataRow("Mgsk says, 'My leader is '")]
  [DataRow("Mgsk says, 'My leader is .'")]
  public void OrdinaryChatIsNotAConfession(string line)
  {
    var chat = ParseChat(line);
    Assert.IsNotNull(chat);
    Assert.IsFalse(ChatLineParser.TryPetLeaderOwner(chat, out _), $"[{line}] is not a summon naming its master");
  }

  [TestMethod]
  [DataRow("Mgsk says, 'My leader is Xxuro.'", "Mgsk", "Xxuro")]
  [DataRow("Test says 'My leader is Super.' ", "Test", "Super")]
  [DataRow("Test says 'My leader is hello'", "Test", "hello")]
  [DataRow("Brozoll`s warder says, 'My leader is Beheleast.'", "Brozoll`s warder", "Beheleast")]
  [DataRow("A fluffy pet says 'My leader is Super'", "A fluffy pet", "Super")]
  [DataRow("Useless tells you, 'My leader is Incogitable'", "Useless", "Incogitable")]
  [DataRow("Useless told you, 'My leader is Incogitable'", "Useless", "Incogitable")]
  public void TheCaptureWritesAndClassifiesTheConfessionWithoutChatPunctuation(string line, string sender,
    string expectedOwner)
  {
    PipelineHarness.EnsureDataStore();
    var facts = new DamageFactTable(16);
    var capture = new CombatCapture(facts);
    var chat = ParseChat(line);
    Assert.IsNotNull(chat);
    capture.HandleChat(chat);

    var evidence = facts.Evidence.ToArray();
    var confession = evidence.Where(e => e.Kind == EvidenceFact.EvPetLeader).ToList();
    Assert.AreEqual(1, confession.Count, "exactly one fact for the sentence");
    Assert.AreEqual(sender, facts.NameOf(confession[0].NameIdx), "the SPEAKER is the summon, not the master it named");
    Assert.AreEqual(expectedOwner, facts.AuxOf(confession[0].AuxIdx));

    // The same line stays a chat event: R3's channel law still gets to read it.
    Assert.IsTrue(evidence.Any(e => e.Kind == EvidenceFact.EvChat), "the chat sighting itself is still captured");
    var timeline = new EntityTimeline();
    ClassificationRules.Apply(facts, timeline);
    Assert.AreEqual(IdentityKind.Pet, timeline.Identity(sender));
    Assert.AreEqual(expectedOwner, timeline.OwnerOf(sender, T0));
    Assert.IsFalse(timeline.NamesWithIdentity().Any(name => name.EndsWith('\'')),
      "a chat delimiter must not become a second owner name");
  }

  // ---- the verdict ----

  [TestMethod]
  public void TheConfessionMakesTheSummonAPetAndNamesItsOwner()
  {
    var timeline = Classify(("Mgsk", EvidenceFact.EvPetLeader, "Xxuro"));

    Assert.AreEqual(IdentityKind.Pet, timeline.IdentityAt("Mgsk", double.PositiveInfinity, out _, out var source));
    StringAssert.StartsWith(source, "R5-leader");
    // Asked at a log time, not at +inf: an ownership interval runs to +inf, so the far end of it is not inside it.
    Assert.AreEqual("Xxuro", timeline.OwnerOf("Mgsk", T0));

    // The line proves the summon and the pair; the master is corroborated only, exactly as R5-owner does.
    Assert.AreEqual(IdentityKind.Player, timeline.IdentityAt("Xxuro", double.PositiveInfinity, out var ownerStrength, out _));
    Assert.AreEqual(RuleStrength.Medium, ownerStrength, "a named master is corroborated, not proven");
  }

  // The case the rule exists for: the client's frame says "not a player" and cannot tell a pet from a hostile.
  [TestMethod]
  public void TheConfessionOutranksAnNpcFrame()
  {
    var timeline = Classify(("Useless", EvidenceFact.EvPetLeader, "Incogitable"),
                            ("Useless", EvidenceFact.EvTargetedNpc, null));

    Assert.AreEqual(IdentityKind.Pet, timeline.IdentityAt("Useless", double.PositiveInfinity, out var strength, out _));
    Assert.AreEqual(RuleStrength.Certain, strength);
  }

  // …and its guard, R24's law unchanged: a person is not a summon.
  [TestMethod]
  public void ANameTheCaptureSawAsAPersonIsNotTurnedIntoASummon()
  {
    var timeline = Classify(("Lafayette", EvidenceFact.EvPetLeader, "Kymidei"),
                            ("Lafayette", EvidenceFact.EvTargetedPlayer, null));

    Assert.AreEqual(IdentityKind.Player, timeline.IdentityAt("Lafayette", double.PositiveInfinity, out _, out var source));
    Assert.AreNotEqual("R5-leader", source, "the player frame answers for the row");
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void MercenaryEvidenceWinsRegardlessOfArrivalOrder(bool leaderFirst)
  {
    var leader = ("Probefoo", EvidenceFact.EvPetLeader, (string?)"Probeowner");
    var merc = ("Probefoo", EvidenceFact.EvMercJoinedGroup, (string?)null);
    var timeline = leaderFirst ? Classify(leader, merc) : Classify(merc, leader);
    Assert.AreEqual(IdentityKind.Merc, timeline.Identity("Probefoo"));
    Assert.IsNull(timeline.OwnerOf("Probefoo", T0));
  }

  [TestMethod]
  public void AListedMercenaryIsNotMadeAPetByItsLeaderSentence()
  {
    var timeline = Classify(("Candlefolk Mender", EvidenceFact.EvPetLeader, "Probeowner"));
    Assert.AreEqual(IdentityKind.Merc, timeline.Identity("Candlefolk Mender"));
    Assert.IsNull(timeline.OwnerOf("Candlefolk Mender", T0));
  }

  [TestMethod]
  public void LaterPersonEvidenceRetractsTheGuardedClaimOnACarriedPass()
  {
    PipelineHarness.EnsureDataStore();
    var facts = new DamageFactTable(16);
    var speaker = facts.InternName("Probefoo");
    facts.AddEvidence(new EvidenceFact(1, (long)T0, speaker, EvidenceFact.EvPetLeader, facts.InternAux("Probeowner")));
    var state = new ClassificationState();
    var first = new EntityTimeline();
    ClassificationRules.Apply(facts, first, state: state);
    Assert.AreEqual(IdentityKind.Pet, first.Identity("Probefoo"));
    facts.AddEvidence(new EvidenceFact(2, (long)T0 + 1, speaker, EvidenceFact.EvJoinedRaid, -1));
    var carried = new EntityTimeline();
    ClassificationRules.Apply(facts, carried, state: state);
    var fresh = new EntityTimeline();
    ClassificationRules.Apply(facts, fresh);
    Assert.AreEqual(IdentityKind.Player, carried.Identity("Probefoo"));
    Assert.IsNull(carried.OwnerOf("Probefoo", T0));
    Assert.AreEqual(fresh.StateStamp(), carried.StateStamp(), "a guarded claim is not an unconditional replay");
    state.Reset();
    Assert.AreEqual(0, state.PetLeaders.Count);
  }

  [TestMethod]
  public void ACustomNamedPetsOwnerChangeDoesNotRewriteItsEarlierOwnership()
  {
    var timeline = Classify(("Fluffy", EvidenceFact.EvPetLeader, "Firstowner"),
      ("Fluffy", EvidenceFact.EvPetLeader, "Secondowner"));
    Assert.AreEqual("Firstowner", timeline.OwnerOf("Fluffy", T0));
    Assert.AreEqual("Secondowner", timeline.OwnerOf("Fluffy", T0 + 1));
  }

  // `Akini, Xanathan`s Warder` = one summon, two masters: the pet claim holds, no raider is invented.
  [TestMethod]
  public void ACommaInOwnerTextClaimsThePetButInventsNoRaider()
  {
    var timeline = Classify(("Akini", EvidenceFact.EvPetLeader, "Akini, Xanathan"));

    Assert.AreEqual(IdentityKind.Pet, timeline.IdentityAt("Akini", double.PositiveInfinity, out _, out _));
    Assert.IsFalse(timeline.ClaimsOf("Akini, Xanathan").Count > 0, "a two-master text must not become a roster member");
    Assert.IsNull(timeline.OwnerOf("Akini", T0), "no owner can be picked out of a two-master text");
  }

  // ---- the rung ----

  /*
   * The frame's number is chosen so it shares no rung: thirteen rules claim at Strong and a tie settles on write order, not
   * on the rule book. This asserts the arithmetic rather than the constant, so moving any of these three numbers without
   * thinking about the ladder fails here. docs/DesignNotes.md → "A summon's own sentence about its master".
   */
  [TestMethod]
  public void TheNpcFrameSitsOnARungOfItsOwn()
  {
    var ladder = new[] { RuleStrength.Weak, RuleStrength.Listed, RuleStrength.Medium, RuleStrength.Strong,
                         ClassificationRules.TargetedNpcStrength, ClassificationRules.MercDatabaseStrength, RuleStrength.Certain };

    Assert.AreEqual(ladder.Length, ladder.Distinct().Count(),
                    "two of these share a rung; a tie settles on write order, not on the rule book");
    CollectionAssert.AreEqual(ladder.OrderBy(x => x).ToList(), ladder.ToList(),
                              "the frame sits between behaviour and the bot table");

    // The ordering that matters is asserted as behaviour, not as arithmetic: TheConfessionOutranksAnNpcFrame (Certain beats the
    // frame) and TargetFrameStrengthTest (the frame still beats a Strong behaviour claim).
  }

  // ---- helpers ----

  /*
   * The way LogProcessor builds one: the parser sees the line with its 27-character timestamp header stripped, while
   * Text carries the WHOLE line — which is why every offset in ChatLineParser starts at 27 and why a test that hands it a
   * bare message reads the confession from the wrong place.
   */
  private const string Header = "[Sun Oct 11 21:53:56 2026] ";

  private static ChatType? ParseChat(string body)
  {
    Assert.AreEqual(27, Header.Length, "the log's timestamp header is 27 characters; ChatLineParser's offsets assume it");
    var line = Header + body;
    var chat = ChatLineParser.ParseChatType(line[Header.Length..]);
    if (chat is not null)
    {
      chat.Text = line;
      chat.BeginTime = T0;
    }

    return chat;
  }

  private static EntityTimeline Classify(params (string Name, byte Kind, string? Aux)[] evidence)
  {
    PipelineHarness.EnsureDataStore();
    var facts = new DamageFactTable(16);
    for (var i = 0; i < evidence.Length; i++)
    {
      var (name, kind, aux) = evidence[i];
      facts.AddEvidence(new EvidenceFact(i, (long)(T0 + i), facts.InternName(name), kind,
                                         aux is null ? -1 : facts.InternAux(aux)));
    }

    var timeline = new EntityTimeline();
    ClassificationRules.Apply(facts, timeline);
    return timeline;
  }
}
