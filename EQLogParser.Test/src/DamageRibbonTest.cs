namespace EQLogParser
{
  /*
   * Event ribbon tests: the wording rules (named pets shorten, generic pets are ignored, mob deaths stay out)
   * and the ticker's ring behavior. Records go straight to the ribbon's handlers; no log lines are parsed, and
   * PlayerRegistry never loads - who counts as a player is a lambda.
   */
  [TestClass]
  public sealed class DamageRibbonTest
  {
    private static readonly string[] Players = ["Kizant", "Bob", "Sancus"];

    private static bool IsPlayer(string name) => name is not null && Players.Contains(name);

    private static DamageRibbon NewRibbon()
    {
      var ribbon = new DamageRibbon();
      ribbon.IsPlayerName = IsPlayer;
      return ribbon;
    }

    [TestMethod]
    public void FormatMezBreak_NamingTheBreaker()
    {
      Assert.AreEqual("Kizant breaks mez!", DamageRibbon.FormatMezBreak(new MezBreakRecord { Breaker = "Kizant", Awakened = "Enchant Mesmerize" }));
      // a mob breaking is the louder case: an AE woke up
      Assert.AreEqual("a skeleton breaks mez!", DamageRibbon.FormatMezBreak(new MezBreakRecord { Breaker = "a skeleton" }));
      Assert.IsNull(DamageRibbon.FormatMezBreak(null));
    }

    [TestMethod]
    public void FormatTaunt_OnlySuccessfulOnes()
    {
      Assert.AreEqual("Kizant taunts a skeleton", DamageRibbon.FormatTaunt(new TauntRecord { Player = "Kizant", Npc = "a skeleton", Success = true }));
      Assert.IsNull(DamageRibbon.FormatTaunt(new TauntRecord { Player = "Kizant", Npc = "a skeleton", Success = false }));
      // improved taunts land the same line; the meter does not grade them
      Assert.AreEqual("Kizant taunts a skeleton", DamageRibbon.FormatTaunt(new TauntRecord { Player = "Kizant", Npc = "a skeleton", Success = true, IsImproved = true }));
    }

    [TestMethod]
    public void FormatDeath_NamedPetShortensToPersonalName()
    {
      var death = new DeathRecord { Killer = "Kizant", Killed = "Sancus`s pet Puksu" };
      Assert.AreEqual("Kizant wills Puksu", DamageRibbon.FormatDeath(death, IsPlayer));
      // hand-built and cached names drift between the backtick and the apostrophe; both fold
      Assert.AreEqual("Kizant wills Puksu", DamageRibbon.FormatDeath(new DeathRecord { Killer = "Kizant", Killed = "Sancus's pet Puksu" }, IsPlayer));
    }

    [TestMethod]
    public void FormatDeath_GenericPetsAreIgnored()
    {
      Assert.IsNull(DamageRibbon.FormatDeath(new DeathRecord { Killer = "a ghoul", Killed = "Kizante`s pet" }, IsPlayer));
      Assert.IsNull(DamageRibbon.FormatDeath(new DeathRecord { Killer = "a ghoul", Killed = "Kizante`s pet   " }, IsPlayer));
    }

    [TestMethod]
    public void FormatDeath_PlayerVictims()
    {
      // a player kill reads as a will; the killer is the story
      Assert.AreEqual("Kizant wills Bob", DamageRibbon.FormatDeath(new DeathRecord { Killer = "Kizant", Killed = "Bob" }, IsPlayer));
      // a mob killing a player is still that player's death - who cares who dealt it
      Assert.AreEqual("Bob died", DamageRibbon.FormatDeath(new DeathRecord { Killer = "a reaper", Killed = "Bob" }, IsPlayer));
    }

    [TestMethod]
    public void FormatDeath_MobVictimsStayOutOfTheRibbon()
    {
      // every raid kill would join otherwise and drown the events that matter
      Assert.IsNull(DamageRibbon.FormatDeath(new DeathRecord { Killer = "Kizant", Killed = "a skeleton" }, IsPlayer));
      Assert.IsNull(DamageRibbon.FormatDeath(new DeathRecord { Killer = "Kizant", Killed = "" }, IsPlayer));
      Assert.IsNull(DamageRibbon.FormatDeath(null, IsPlayer));
    }

    [TestMethod]
    public void Handlers_FeedTheRingAndCapIt()
    {
      var ribbon = NewRibbon();
      for (var i = 0; i < DamageRibbon.Capacity + 5; i++)
      {
        ribbon.OnMezBreak(new MezBreakRecord { Breaker = "M" + i });
      }

      var lines = ribbon.Lines();
      Assert.AreEqual(DamageRibbon.Capacity, lines.Count);
      // oldest-first, the first five pushed off the top
      Assert.AreEqual("M5 breaks mez!", lines[0]);
      Assert.AreEqual($"M{DamageRibbon.Capacity + 4} breaks mez!", lines[^1]);
    }

    [TestMethod]
    public void Handlers_MixedSourcesKeepArrivalOrder()
    {
      var ribbon = NewRibbon();
      ribbon.OnMezBreak(new MezBreakRecord { Breaker = "Kizant" });
      ribbon.OnDeath(new DeathEvent { Record = new DeathRecord { Killer = "Kizant", Killed = "Sancus`s pet Puksu" } });
      ribbon.OnTaunt(new TauntEvent { Record = new TauntRecord { Player = "Kizant", Npc = "a skeleton", Success = true } });
      ribbon.OnTaunt(new TauntEvent { Record = new TauntRecord { Player = "Kizant", Npc = "a skeleton", Success = false } });

      CollectionAssert.AreEqual(new[] { "Kizant breaks mez!", "Kizant wills Puksu", "Kizant taunts a skeleton" }, ribbon.Lines().ToList());
    }

    [TestMethod]
    public void Clear_EmptiesTheHistory()
    {
      var ribbon = NewRibbon();
      ribbon.OnMezBreak(new MezBreakRecord { Breaker = "Kizant" });
      ribbon.Clear();
      Assert.AreEqual(0, ribbon.Lines().Count);
    }

    [TestMethod]
    public void PersonalName_PlainNamesPassThrough()
    {
      Assert.AreEqual("Kizant", DamageRibbon.PersonalName("Kizant"));
      Assert.AreEqual("a skeleton", DamageRibbon.PersonalName("a skeleton"));
      Assert.IsNull(DamageRibbon.PersonalName(null));
    }

    [TestMethod]
    public void PetPersonalName_IsThePetQuestionOnly()
    {
      // no pet marker at all is NOT a pet — the death of a plain mob name must not pass for a victim worth showing
      Assert.IsNull(DamageRibbon.PetPersonalName("a skeleton"));
      Assert.IsNull(DamageRibbon.PetPersonalName("Kizant"));
      Assert.AreEqual("Puksu", DamageRibbon.PetPersonalName("Sancus`s pet Puksu"));
      Assert.IsNull(DamageRibbon.PetPersonalName("Sancus`s pet"));
    }
  }
}
