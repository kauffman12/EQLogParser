using EQLogParser;
using Moq;

namespace EQLogParserTest
{
  [TestClass]
  public class DamageLineParserTest
  {
    private Mock<IDataManager>? _mockManager;

    [TestInitialize]
    public void Setup()
    {
      ConfigUtil.PlayerName = "TestPlayer";
      _mockManager = new Mock<IDataManager>();
#pragma warning disable CS8603 // Possible null reference return.
      _mockManager.Setup(m => m.GetDamagingSpellByName(It.IsAny<string>())).Returns((string name) => null);
#pragma warning restore CS8603 // Possible null reference return.
      _mockManager.Setup(m => m.IsOldSpell(It.IsAny<string>())).Returns(false);
      _mockManager.Setup(m => m.AbbreviateSpellName(It.IsAny<string>())).Returns((string name) => name);
#pragma warning disable CS8603 // Possible null reference return.
      _mockManager.Setup(m => m.GetSpellByAbbrv(It.IsAny<string>())).Returns((string name) => null);
#pragma warning restore CS8603 // Possible null reference return.
      _mockManager.Setup(m => m.RemoveActiveFight(It.IsAny<string>()));
      _mockManager.Setup(m => m.ClearActiveAdps());
#pragma warning disable CS8603 // Possible null reference return.
      _mockManager.Setup(m => m.GetFight(It.IsAny<string>())).Returns((string name) => null);
#pragma warning restore CS8603 // Possible null reference return.
      DamageLineParser.DataManager = _mockManager.Object;
    }

    private static DamageRecord ParseAction(string action)
    {
      return DamageLineParser.ParseLine(action);
    }

    private static DamageRecord ParseEmuAction(string action)
    {
      MainWindow.IsEmuParsingEnabled = true;
      var result = DamageLineParser.ParseLine(action);
      MainWindow.IsEmuParsingEnabled = false;
      return result;
    }

    #region Basic Melee Damage

    [TestMethod]
    public void TestMelee_Crushes()
    {
      var record = ParseAction("Astralx crushes Sontalak for 126225 points of damage. (Strikethrough Critical)");
      Assert.IsNotNull(record);
      Assert.AreEqual("Astralx", record.Attacker);
      Assert.AreEqual("Sontalak", record.Defender);
      Assert.AreEqual((uint)126225, record.Total);
      Assert.AreEqual(Labels.Melee, record.Type);
      Assert.AreEqual("Crushes", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
      Assert.IsTrue(LineModifiersParser.IsStrikethrough(record.ModifiersMask));
      Assert.IsTrue(LineModifiersParser.IsCrit(record.ModifiersMask));
    }

    [TestMethod]
    public void TestMelee_Crushes_NoModifiers()
    {
      var record = ParseAction("Useless crushes an abyssal terror for 9022 points of damage.");
      Assert.IsNotNull(record);
      Assert.AreEqual("Useless", record.Attacker);
      Assert.AreEqual("An abyssal terror", record.Defender);
      Assert.AreEqual((uint)9022, record.Total);
      Assert.AreEqual(Labels.Melee, record.Type);
      Assert.AreEqual("Crushes", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
    }

    [TestMethod]
    public void TestMelee_Claws()
    {
      var record = ParseAction("Susarrak the Crusader claws Villette for 27699 points of damage. (Strikethrough Wild Rampage)");
      Assert.IsNotNull(record);
      Assert.AreEqual("Susarrak the Crusader", record.Attacker);
      Assert.AreEqual("Villette", record.Defender);
      Assert.AreEqual((uint)27699, record.Total);
      Assert.AreEqual(Labels.Melee, record.Type);
      Assert.AreEqual("Claws", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
      Assert.IsTrue(LineModifiersParser.IsStrikethrough(record.ModifiersMask));
      Assert.IsTrue(LineModifiersParser.IsRampage(record.ModifiersMask));
    }

    [TestMethod]
    public void TestMelee_Crushes_LuckyCritical()
    {
      var record = ParseAction("You crush Ogna, Artisan of War for 20581 points of damage. (Lucky Critical)");
      Assert.IsNotNull(record);
      Assert.AreEqual(ConfigUtil.PlayerName, record.Attacker);
      Assert.AreEqual("Ogna, Artisan of War", record.Defender);
      Assert.AreEqual((uint)20581, record.Total);
      Assert.AreEqual(Labels.Melee, record.Type);
      Assert.AreEqual("Crushes", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
      Assert.IsTrue(LineModifiersParser.IsCrit(record.ModifiersMask));
      Assert.IsTrue(LineModifiersParser.IsLucky(record.ModifiersMask));
    }

    [TestMethod]
    public void TestMelee_Pierces()
    {
      var record = ParseAction("Nniki pierces an ice giant for 101810 points of damage. (Critical)");
      Assert.IsNotNull(record);
      Assert.AreEqual("Nniki", record.Attacker);
      Assert.AreEqual("An ice giant", record.Defender);
      Assert.AreEqual((uint)101810, record.Total);
      Assert.AreEqual(Labels.Melee, record.Type);
      Assert.AreEqual("Pierces", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
      Assert.IsTrue(LineModifiersParser.IsCrit(record.ModifiersMask));
    }

    [TestMethod]
    public void TestMelee_Kicks()
    {
      var record = ParseAction("Nniki kicks an ice giant for 672875 points of damage. (Lucky Critical)");
      Assert.IsNotNull(record);
      Assert.AreEqual("Nniki", record.Attacker);
      Assert.AreEqual("An ice giant", record.Defender);
      Assert.AreEqual((uint)672875, record.Total);
      Assert.AreEqual(Labels.Melee, record.Type);
      Assert.AreEqual("Kicks", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
      Assert.IsTrue(LineModifiersParser.IsCrit(record.ModifiersMask));
      Assert.IsTrue(LineModifiersParser.IsLucky(record.ModifiersMask));
    }

    [TestMethod]
    public void TestMelee_Bashes()
    {
      var record = ParseAction("An ice giant bashes Shmid for 39969 points of damage. (Riposte Strikethrough)");
      Assert.IsNotNull(record);
      Assert.AreEqual("An ice giant", record.Attacker);
      Assert.AreEqual("Shmid", record.Defender);
      Assert.AreEqual((uint)39969, record.Total);
      Assert.AreEqual(Labels.Melee, record.Type);
      Assert.AreEqual("Bashes", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
      Assert.IsFalse(LineModifiersParser.IsRiposte(record.ModifiersMask));
      Assert.IsTrue(LineModifiersParser.IsStrikethrough(record.ModifiersMask));
    }

    [TestMethod]
    public void TestMelee_Bites()
    {
      var record = ParseAction("A bloodthirsty gnawer tries to bite Vandil, but Vandil parries!");
      Assert.IsNotNull(record);
      Assert.AreEqual("A bloodthirsty gnawer", record.Attacker);
      Assert.AreEqual("Vandil", record.Defender);
      Assert.AreEqual(Labels.Parry, record.Type);
      Assert.AreEqual("Bites", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
    }

    [TestMethod]
    public void TestMelee_Slashes()
    {
      var record = ParseAction("Drogbaa tries to slash Whirlrender Scout, but misses! (Strikethrough)");
      Assert.IsNotNull(record);
      Assert.AreEqual("Drogbaa", record.Attacker);
      Assert.AreEqual("Whirlrender Scout", record.Defender);
      Assert.AreEqual(Labels.Miss, record.Type);
      Assert.AreEqual("Slashes", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
      Assert.IsTrue(LineModifiersParser.IsStrikethrough(record.ModifiersMask));
    }

    #endregion

    #region Spell Damage

    [TestMethod]
    public void TestSpell_HitBy()
    {
      var record = ParseAction("Sonozen hit Jortreva the Crusader for 38948 points of fire damage by Burst of Flames. (Lucky Critical Twincast)");
      Assert.IsNotNull(record);
      Assert.AreEqual("Sonozen", record.Attacker);
      Assert.AreEqual("Jortreva the Crusader", record.Defender);
      Assert.AreEqual((uint)38948, record.Total);
      Assert.IsTrue(LineModifiersParser.IsCrit(record.ModifiersMask));
      Assert.IsTrue(LineModifiersParser.IsLucky(record.ModifiersMask));
      Assert.IsTrue(LineModifiersParser.IsTwincast(record.ModifiersMask));
    }

    [TestMethod]
    public void TestSpell_HitUnresistable()
    {
      var record = ParseAction("Piemastaj hit Boss for 176000 points of unresistable damage by Elemental Conversion VI.");
      Assert.IsNotNull(record);
      Assert.AreEqual("Piemastaj", record.Attacker);
      Assert.AreEqual("Boss", record.Defender);
      Assert.AreEqual((uint)176000, record.Total);
    }

    [TestMethod]
    public void TestSpell_HitYouTreant()
    {
      var record = ParseAction("You hit a treant for 1633489 points of magic damage by Chromospheric Vortex Rk. II. (Lucky Critical)");
      Assert.IsNotNull(record);
      Assert.AreEqual(ConfigUtil.PlayerName, record.Attacker);
      Assert.AreEqual("A treant", record.Defender);
      Assert.AreEqual((uint)1633489, record.Total);
      Assert.AreEqual(Labels.Dd, record.Type);
      Assert.AreEqual("Chromospheric Vortex Rk. II", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
      Assert.IsTrue(LineModifiersParser.IsCrit(record.ModifiersMask));
      Assert.IsTrue(LineModifiersParser.IsLucky(record.ModifiersMask));
    }

    [TestMethod]
    public void TestSpell_HitPetOwner()
    {
      var record = ParseEmuAction("Lobekn (Owner: Bulron) hit a wan ghoul knight for 311 points of non-melee damage. (Earthquake)");
      Assert.IsNotNull(record);
      Assert.AreEqual("Lobekn", record.Attacker);
      Assert.AreEqual("Bulron", record.AttackerOwner);
      Assert.AreEqual("A wan ghoul knight", record.Defender);
      Assert.AreEqual((uint)311, record.Total);
      Assert.AreEqual(Labels.Dd, record.Type);
      Assert.AreEqual("Earthquake", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
    }

    #endregion

    #region Non-Melee / Thorns Damage

    [TestMethod]
    public void TestNonMelee_PiercedByThorns()
    {
      var record = ParseAction("Tantor is pierced by Tolzol's thorns for 6718 points of non-melee damage.");
      Assert.IsNotNull(record);
      Assert.AreEqual("Tolzol", record.Attacker);
      Assert.AreEqual("Tantor", record.Defender);
      Assert.AreEqual((uint)6718, record.Total);
    }

    [TestMethod]
    public void TestNonMelee_TormentedByFrost()
    {
      var record = ParseAction("Honvar is tormented by Reisil's frost for 7809 points of non-melee damage.");
      Assert.IsNotNull(record);
      Assert.AreEqual("Reisil", record.Attacker);
      Assert.AreEqual("Honvar", record.Defender);
      Assert.AreEqual((uint)7809, record.Total);
    }

    [TestMethod]
    public void TestNonMelee_YourThorns()
    {
      var record = ParseAction("A failed reclaimer is pierced by YOUR thorns for 193 points of non-melee damage.");
      Assert.IsNotNull(record);
      Assert.AreEqual(ConfigUtil.PlayerName, record.Attacker);
      Assert.AreEqual("A failed reclaimer", record.Defender);
      Assert.AreEqual((uint)193, record.Total);
      Assert.AreEqual(Labels.Ds, record.Type);
      Assert.AreEqual(Labels.Ds, record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
    }

    [TestMethod]
    public void TestNonMelee_TestOneHundredThree()
    {
      var record = ParseAction("Test One Hundred Three is burned by YOUR flames for 5224 points of non-melee damage.");
      Assert.IsNotNull(record);
      Assert.AreEqual(ConfigUtil.PlayerName, record.Attacker);
      Assert.AreEqual("Test One Hundred Three", record.Defender);
      Assert.AreEqual((uint)5224, record.Total);
      Assert.AreEqual(Labels.Ds, record.Type);
      Assert.AreEqual(Labels.Ds, record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
    }

    [TestMethod]
    public void TestNonMelee_ChilledToTheBone()
    {
      var record = ParseAction("A dendridic shard was chilled to the bone for 410 points of non-melee damage.");
      Assert.IsNotNull(record);
      Assert.AreEqual("A dendridic shard", record.Defender);
      Assert.AreEqual((uint)410, record.Total);
    }

    [TestMethod]
    public void TestNonMelee_YOUChilledToTheBone()
    {
      var record = ParseAction("YOU are chilled to the bone for 2700 points of non-melee damage!");
      Assert.IsNotNull(record);
      Assert.AreEqual(ConfigUtil.PlayerName, record.Defender);
      Assert.AreEqual((uint)2700, record.Total);
      Assert.AreEqual(Labels.Ds, record.Type);
      Assert.AreEqual(Labels.Ds, record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
    }

    [TestMethod]
    public void TestNonMelee_DemonstratedDepletion()
    {
      var record = ParseAction("Demonstrated Depletion was hit by non-melee for 6734 points of damage.");
      Assert.IsNotNull(record);
      Assert.AreEqual("Demonstrated Depletion", record.Defender);
      Assert.AreEqual((uint)6734, record.Total);
    }

    [TestMethod]
    public void TestNonMelee_FallingDamage()
    {
      var record = ParseAction("You were hit by non-melee for 16 damage");
      Assert.IsNotNull(record);
      Assert.AreEqual(Labels.Unk, record.Attacker);
      Assert.AreEqual(ConfigUtil.PlayerName, record.Defender);
      Assert.AreEqual((uint)16, record.Total);
      Assert.AreEqual(Labels.Dd, record.Type);
      Assert.AreEqual(Labels.Dd, record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
    }

    #endregion

    #region Taken Damage Format

    [TestMethod]
    public void TestTaken_DamageFromSpell()
    {
      var record = ParseAction("Dovhesi has taken 173674 damage from Curse of the Shrine by Grendish the Crusader.");
      Assert.IsNotNull(record);
      Assert.AreEqual("Grendish the Crusader", record.Attacker);
      Assert.AreEqual("Dovhesi", record.Defender);
      Assert.AreEqual((uint)173674, record.Total);
    }

    [TestMethod]
    public void TestTaken_DamageFromSpell_LuckyCritical()
    {
      var record = ParseAction("Grendish the Crusader has taken 1003231 damage from Pyre of Klraggek Rk. III by Atvar. (Lucky Critical)");
      Assert.IsNotNull(record);
      Assert.AreEqual("Atvar", record.Attacker);
      Assert.AreEqual("Grendish the Crusader", record.Defender);
      Assert.AreEqual((uint)1003231, record.Total);
      Assert.IsTrue(LineModifiersParser.IsCrit(record.ModifiersMask));
      Assert.IsTrue(LineModifiersParser.IsLucky(record.ModifiersMask));
    }

    [TestMethod]
    public void TestTaken_YouDamageFromSpell()
    {
      var record = ParseAction("You have taken 4852 damage from Nectar of Misery by Commander Gartik.");
      Assert.IsNotNull(record);
      Assert.AreEqual("Commander Gartik", record.Attacker);
      Assert.AreEqual(ConfigUtil.PlayerName, record.Defender);
      Assert.AreEqual((uint)4852, record.Total);
      Assert.AreEqual(Labels.Dot, record.Type);
      Assert.AreEqual("Nectar of Misery", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
    }

    [TestMethod]
    public void TestTaken_NonPlayerAttacker()
    {
      var record = ParseAction("A gnoll has taken 108790 damage from your Mind Coil Rk. II.");
      Assert.IsNotNull(record);
      Assert.AreEqual(ConfigUtil.PlayerName, record.Attacker);
      Assert.AreEqual("A gnoll", record.Defender);
      Assert.AreEqual((uint)108790, record.Total);
      Assert.AreEqual(Labels.Dot, record.Type);
      Assert.AreEqual("Mind Coil Rk. II", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
    }

    [TestMethod]
    public void TestTaken_YouDamageFromSpellNoBy()
    {
      // TODO should attacker be spell?
      var record = ParseAction("You have taken 2354 damage from Flashbroil Singe III.");
      Assert.IsNotNull(record);
      Assert.AreEqual("Flashbroil Singe III", record.Attacker);
      Assert.AreEqual(ConfigUtil.PlayerName, record.Defender);
      Assert.AreEqual((uint)2354, record.Total);
      Assert.AreEqual(Labels.OtherDmg, record.Type);
      Assert.AreEqual("Flashbroil Singe III", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
    }

    [TestMethod]
    public void TestTaken_DamageFromSpellWithEmptyAttacker()
    {
      var record = ParseAction("Goratoar has taken 18724 damage from Slicing Energy by .");
      Assert.IsNotNull(record);
      Assert.AreEqual("Goratoar", record.Defender);
      Assert.AreEqual((uint)18724, record.Total);
    }

    [TestMethod]
    public void TestTaken_OldEqemu()
    {
      var record = ParseEmuAction("Pixtt Invi Mal has taken 189 damage from Goanna by Tuyen`s Chant of Fire.");
      Assert.IsNotNull(record);
      Assert.AreEqual("Goanna", record.Attacker);
      Assert.AreEqual("Tuyen`s Chant of Fire", record.SubType);
      Assert.AreEqual("Pixtt Invi Mal", record.Defender);
      Assert.AreEqual((uint)189, record.Total);
    }

    [TestMethod]
    public void TestTaken_YouOldEqemu()
    {
      var record = ParseEmuAction("You have taken 1 damage from a bonecrawler hatchling by Feeble Poison");
      Assert.IsNotNull(record);
      Assert.AreEqual("A bonecrawler hatchling", record.Attacker);
      Assert.AreEqual(ConfigUtil.PlayerName, record.Defender);
      Assert.AreEqual((uint)1, record.Total);
      Assert.AreEqual(Labels.Dot, record.Type);
      Assert.AreEqual("Feeble Poison", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
    }

    [TestMethod]
    public void TestTaken_WispExplosion()
    {
      var record = ParseAction("Lawlstryke has taken 216717 damage by Wisp Explosion.");
      Assert.IsNotNull(record);
      Assert.AreEqual("Wisp Explosion", record.Attacker);
      Assert.AreEqual("Lawlstryke", record.Defender);
      Assert.AreEqual((uint)216717, record.Total);
      Assert.AreEqual(Labels.OtherDmg, record.Type);
      Assert.AreEqual("Wisp Explosion", record.SubType);
      Assert.IsTrue(record.AttackerIsSpell);
    }

    #endregion

    #region Old EQEMU Format

    [TestMethod]
    public void TestOldEqemu_HitForDamage()
    {
      var record = ParseEmuAction("Jaun hit Pixtt Invi Mal for 150 points of non-melee damage.");
      Assert.IsNotNull(record);
      Assert.AreEqual("Jaun", record.Attacker);
      Assert.AreEqual("Pixtt Invi Mal", record.Defender);
      Assert.AreEqual((uint)150, record.Total);
      Assert.AreEqual(Labels.Dd, record.Type);
      Assert.AreEqual(Labels.Dd, record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
    }

    #endregion

    #region Critical Melee (Old Style)

    [TestMethod]
    public void TestOldCrit_ScoresCriticalHit()
    {
      var record1 = ParseEmuAction("Vorgash scores a critical hit!");
      Assert.IsNull(record1);

      var record2 = ParseEmuAction("Vorgash hits a target for 780 points of damage.");
      Assert.IsNotNull(record2);
      Assert.AreEqual("A target", record2.Defender);
      Assert.AreEqual(Labels.Melee, record2.Type);
      Assert.AreEqual("Hits", record2.SubType);
      Assert.AreEqual((uint)780, record2.Total);
      Assert.IsFalse(record2.AttackerIsSpell);
    }

    [TestMethod]
    public void TestOldCrit_CripplingBlow()
    {
      var record1 = ParseEmuAction("Arilyn lands a Crippling Blow!(244)");
      Assert.IsNull(record1);

      var record2 = ParseEmuAction("Arilyn hits a target for 280 points of damage.");
      Assert.IsNotNull(record2);
      Assert.AreEqual("A target", record2.Defender);
      Assert.AreEqual(Labels.Melee, record2.Type);
      Assert.AreEqual("Hits", record2.SubType);
      Assert.AreEqual((uint)280, record2.Total);
      Assert.IsFalse(record2.AttackerIsSpell);
    }

    #endregion

    #region Edge Cases

    [TestMethod]
    public void TestParseLine_EmptyAction()
    {
      var record = ParseAction("");
      Assert.IsNull(record);
    }

    [TestMethod]
    public void TestParseLine_NullAction()
    {
      var record = ParseAction(null!);
      Assert.IsNull(record);
    }

    [TestMethod]
    public void TestParseLine_ShortAction()
    {
      var record = ParseAction("hi");
      Assert.IsNull(record);
    }

    [TestMethod]
    public void TestParseLine_MultiWordNameApostrophe()
    {
      var record = ParseAction("Kizante`s pet was slain by a rockborn!");
      Assert.IsNull(record);
    }

    [TestMethod]
    public void TestParseLine_MultiWordNamePetSlain()
    {
      var record = ParseAction("Strangle`s pet has been slain by Kzerk!");
      Assert.IsNull(record);
    }

    [TestMethod]
    public void TestParseLine_YouSlainByArmedFlyer()
    {
      var record = ParseAction("You have been slain by an armed flyer!");
      Assert.IsNull(record);
    }

    [TestMethod]
    public void TestParseLine_KizantesPetSlain()
    {
      var record = ParseAction("Kizante`s pet was slain by a rockborn!");
      Assert.IsNull(record);
    }

    [TestMethod]
    public void TestParseLine_StranglesPetSlain()
    {
      var record = ParseAction("Strangle`s pet has been slain by Kzerk!");
      Assert.IsNull(record);
    }

    #endregion

    #region Taunt / Attention

    [TestMethod]
    public void TestTaunt_CapturedAttention()
    {
      var record = ParseAction("Goodurden has captured liquid shadow's attention!");
      Assert.IsNull(record);
    }

    [TestMethod]
    public void TestTaunt_FailedTaunt()
    {
      var record = ParseAction("Foob failed to taunt Doomshade.");
      Assert.IsNull(record);
    }

    [TestMethod]
    public void TestTaunt_ImprovedTaunt()
    {
      var record = ParseAction("A war beast is focused on attacking Rorcal due to an improved taunt.");
      Assert.IsNull(record);
    }

    [TestMethod]
    public void TestTaunt_YouCaptureAttention()
    {
      var record = ParseAction("You capture a slithering adder's attention!");
      Assert.IsNull(record);
    }

    #endregion

    #region Absorb / Shield

    [TestMethod]
    public void TestAbsorb_MagicalSkin()
    {
      var record = ParseAction("Fllint's magical skin absorbs the damage of Firethorn's thorns.");
      Assert.IsNotNull(record);
      Assert.AreEqual("Firethorn", record.Attacker);
      Assert.AreEqual("Fllint", record.Defender);
      Assert.AreEqual(Labels.Absorb, record.Type);
      Assert.AreEqual("Hits", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
    }

    [TestMethod]
    public void TestAbsorb_YourMagicalSkin()
    {
      var record = ParseAction("YOUR magical skin absorbs the damage of Herald of the Outer Brood's thorns.");
      Assert.IsNotNull(record);
      Assert.AreEqual("Herald of the Outer Brood", record.Attacker);
      Assert.AreEqual(ConfigUtil.PlayerName, record.Defender);
      Assert.AreEqual(Labels.Absorb, record.Type);
      Assert.AreEqual("Hits", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
    }

    [TestMethod]
    public void TestAbsorb_Spellshield()
    {
      // TODO - Parse the difference as damage?
      var record = ParseEmuAction("The Spellshield absorbed 132 of 162 points of damage");
      Assert.IsNotNull(record);
      Assert.AreEqual(Labels.Unk, record.Attacker);
      Assert.AreEqual(ConfigUtil.PlayerName, record.Defender);
      Assert.AreEqual((uint)0, record.Total);
      Assert.AreEqual(Labels.Absorb, record.Type);
      Assert.AreEqual("Hits", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
    }

    [TestMethod]
    public void TestAbsorb_ShieldedItself()
    {
      var record = ParseEmuAction("Gaber (Owner: Claus) has shielded itself from 116 points of damage. (Rune II)");
      Assert.IsNotNull(record);
      Assert.AreEqual(Labels.Unk, record.Attacker);
      Assert.AreEqual("Gaber", record.Defender);
      Assert.AreEqual((uint)0, record.Total);
      Assert.AreEqual(Labels.Absorb, record.Type);
      Assert.AreEqual("Hits", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
    }

    [TestMethod]
    public void TestAbsorb_ShieldedHerself()
    {
      var record = ParseEmuAction("Leela has shielded herself from 658 points of damage. (Manaskin)");
      Assert.IsNotNull(record);
      Assert.AreEqual("Leela", record.Defender);
      Assert.AreEqual(Labels.Unk, record.Attacker);
      Assert.AreEqual((uint)0, record.Total);
      Assert.AreEqual(Labels.Absorb, record.Type);
      Assert.AreEqual("Hits", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
    }

    #endregion

    #region Dodge / Block / Parry / Miss

    [TestMethod]
    public void TestDodge_TriesToCrushDodges()
    {
      var record = ParseAction("Test One Hundred Three tries to punch Kazint, but misses!");
      Assert.IsNotNull(record);
      Assert.AreEqual("Test One Hundred Three", record.Attacker);
      Assert.AreEqual("Kazint", record.Defender);
      Assert.AreEqual(Labels.Miss, record.Type);
      Assert.AreEqual("Punches", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
    }

    [TestMethod]
    public void TestDodge_TriesToPunchDodges()
    {
      var record = ParseAction("Test One Hundred Three tries to punch Kazint, but Kazint dodges!");
      Assert.IsNotNull(record);
      Assert.AreEqual("Test One Hundred Three", record.Attacker);
      Assert.AreEqual("Kazint", record.Defender);
      Assert.AreEqual(Labels.Dodge, record.Type);
      Assert.AreEqual("Punches", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
    }

    [TestMethod]
    public void TestDodge_TriesToPunchYouDodges()
    {
      var record = ParseAction("Test One Hundred Three tries to punch YOU, but YOU dodge!");
      Assert.IsNotNull(record);
      Assert.AreEqual("Test One Hundred Three", record.Attacker);
      Assert.AreEqual(ConfigUtil.PlayerName, record.Defender);
      Assert.AreEqual(Labels.Dodge, record.Type);
      Assert.AreEqual("Punches", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
    }

    [TestMethod]
    public void TestDodge_CrushDodges()
    {
      var record = ParseAction("Kazint tries to crush Test One Hundred Three, but Test One Hundred Three dodges!");
      Assert.IsNotNull(record);
      Assert.AreEqual("Kazint", record.Attacker);
      Assert.AreEqual("Test One Hundred Three", record.Defender);
      Assert.AreEqual(Labels.Dodge, record.Type);
      Assert.AreEqual("Crushes", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
    }

    [TestMethod]
    public void TestParry_TriesToCrushParries()
    {
      var record = ParseAction("You try to crush a primal guardian, but a primal guardian parries!");
      Assert.IsNotNull(record);
      Assert.AreEqual(ConfigUtil.PlayerName, record.Attacker);
      Assert.AreEqual("A primal guardian", record.Defender);
      Assert.AreEqual(Labels.Parry, record.Type);
      Assert.AreEqual("Crushes", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
    }

    [TestMethod]
    public void TestParry_TriesToBiteParries()
    {
      var record = ParseAction("A bloodthirsty gnawer tries to bite Vandil, but Vandil parries!");
      Assert.IsNotNull(record);
      Assert.AreEqual("A bloodthirsty gnawer", record.Attacker);
      Assert.AreEqual("Vandil", record.Defender);
      Assert.AreEqual(Labels.Parry, record.Type);
      Assert.AreEqual("Bites", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
    }

    [TestMethod]
    public void TestParry_BashParries()
    {
      var record = ParseAction("Romance tries to bash Vulak`Aerr, but Vulak`Aerr parries!");
      Assert.IsNotNull(record);
      Assert.AreEqual("Romance", record.Attacker);
      Assert.AreEqual("Vulak`Aerr", record.Defender);
      Assert.AreEqual(Labels.Parry, record.Type);
      Assert.AreEqual("Bashes", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
    }

    [TestMethod]
    public void TestBlock_TriesToCrushBlocks()
    {
      var record = ParseAction("You try to crush a desert madman, but a desert madman blocks!");
      Assert.IsNotNull(record);
      Assert.AreEqual(ConfigUtil.PlayerName, record.Attacker);
      Assert.AreEqual("A desert madman", record.Defender);
      Assert.AreEqual(Labels.Block, record.Type);
      Assert.AreEqual("Crushes", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
    }

    [TestMethod]
    public void TestBlock_TriesToHitBlocks()
    {
      var record = ParseAction("An ancient warden tries to hit Reisil, but Reisil blocks with his shield!");
      Assert.IsNotNull(record);
      Assert.AreEqual("An ancient warden", record.Attacker);
      Assert.AreEqual("Reisil", record.Defender);
      Assert.AreEqual(Labels.Block, record.Type);
      Assert.AreEqual("Hits", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
    }

    [TestMethod]
    public void TestBlock_TriesToSmashBlocks()
    {
      var record = ParseAction("A windchill sprite tries to smash YOU, but YOU block with your staff!");
      Assert.IsNotNull(record);
      Assert.AreEqual("A windchill sprite", record.Attacker);
      Assert.AreEqual(ConfigUtil.PlayerName, record.Defender);
      Assert.AreEqual(Labels.Block, record.Type);
      Assert.AreEqual("Smashes", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
    }

    [TestMethod]
    public void TestBlock_Invuln()
    {
      var record = ParseAction("Tolzol tries to crush Dendritic Golem, but Dendritic Golem is INVULNERABLE!");
      Assert.IsNotNull(record);
      Assert.AreEqual("Tolzol", record.Attacker);
      Assert.AreEqual("Dendritic Golem", record.Defender);
      Assert.AreEqual(Labels.Invulnerable, record.Type);
      Assert.AreEqual("Crushes", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
    }

    [TestMethod]
    public void TestBlock_SkinnedBlow()
    {
      var record = ParseAction("A failed reclaimer tries to punch YOU, but YOUR magical skin absorbs the blow!");
      Assert.IsNotNull(record);
      Assert.AreEqual("A failed reclaimer", record.Attacker);
      Assert.AreEqual(ConfigUtil.PlayerName, record.Defender);
    }

    [TestMethod]
    public void TestBlock_ParryBlow()
    {
      var record = ParseAction("An enchanted Syldon stalker tries to crush YOU, but YOU parry!");
      Assert.IsNotNull(record);
      Assert.AreEqual("An enchanted Syldon stalker", record.Attacker);
      Assert.AreEqual(ConfigUtil.PlayerName, record.Defender);
      Assert.AreEqual("Parry", record.Type);
      Assert.AreEqual("Crushes", record.SubType);
    }

    [TestMethod]
    public void TestBlock_RiposteOfStrikethroughYou()
    {
      var record = ParseAction("An enchanted Syldon stalker tries to crush YOU, but YOU riposte! (Strikethrough)");
      Assert.IsNull(record);
    }

    [TestMethod]
    public void TestBlock_RiposteOfStrikethroughOther()
    {
      var record = ParseAction("Zelnithak tries to hit Fllint, but Fllint ripostes! (Strikethrough)");
      Assert.IsNull(record);
    }

    [TestMethod]
    public void TestBlock_MissRiposteStrikethrough()
    {
      var record = ParseAction("An enchanted Syldon stalker tries to crush YOU, but misses! (Strikethrough)");
      Assert.IsNotNull(record);
      Assert.AreEqual("An enchanted Syldon stalker", record.Attacker);
      Assert.AreEqual(ConfigUtil.PlayerName, record.Defender);
      Assert.AreEqual(Labels.Miss, record.Type);
      Assert.AreEqual("Crushes", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
    }

    [TestMethod]
    public void TestBlock_YouRiposteStrikethrough()
    {
      var record = ParseAction("You try to crush a Kar`Zok soldier, but miss! (Riposte Strikethrough)");
      Assert.IsNotNull(record);
      Assert.AreEqual(ConfigUtil.PlayerName, record.Attacker);
      Assert.AreEqual("A Kar`Zok soldier", record.Defender);
      Assert.AreEqual(Labels.Miss, record.Type);
      Assert.AreEqual("Crushes", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
    }

    #endregion

    #region Immolation / DoT

    [TestMethod]
    public void TestImmolation()
    {
      var record = ParseEmuAction("You are immolated by raging energy.  You have taken 179 points of damage.");
      Assert.IsNotNull(record);
      Assert.AreEqual(ConfigUtil.PlayerName, record.Defender);
      Assert.AreEqual(Labels.Unk, record.Attacker);
      Assert.AreEqual((uint)179, record.Total);
      Assert.AreEqual(Labels.Dot, record.Type);
      Assert.AreEqual(Labels.Dot, record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
    }

    #endregion

    #region Real Log Data from eqlog_Kizant_xegony-selected.txt

    [TestMethod]
    public void TestRealLog_NnikiFlurry()
    {
      var record = ParseAction("Nniki pierces an ice giant for 55114 points of damage. (Lucky Critical Flurry)");
      Assert.IsNotNull(record);
      Assert.AreEqual("Nniki", record.Attacker);
      Assert.AreEqual("An ice giant", record.Defender);
      Assert.AreEqual((uint)55114, record.Total);
      Assert.AreEqual(Labels.Melee, record.Type);
      Assert.AreEqual("Pierces", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
      Assert.IsTrue(LineModifiersParser.IsCrit(record.ModifiersMask));
      Assert.IsTrue(LineModifiersParser.IsLucky(record.ModifiersMask));
      Assert.IsTrue(LineModifiersParser.IsFlurry(record.ModifiersMask));
    }

    [TestMethod]
    public void TestRealLog_NnikiFlurry2()
    {
      var record = ParseAction("Nniki pierces an ice giant for 156037 points of damage. (Lucky Critical Flurry)");
      Assert.IsNotNull(record);
      Assert.AreEqual("Nniki", record.Attacker);
      Assert.AreEqual("An ice giant", record.Defender);
      Assert.AreEqual((uint)156037, record.Total);
      Assert.AreEqual(Labels.Melee, record.Type);
      Assert.AreEqual("Pierces", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
      Assert.IsTrue(LineModifiersParser.IsCrit(record.ModifiersMask));
      Assert.IsTrue(LineModifiersParser.IsLucky(record.ModifiersMask));
      Assert.IsTrue(LineModifiersParser.IsFlurry(record.ModifiersMask));
    }

    [TestMethod]
    public void TestRealLog_NnikiFlurryOnly()
    {
      var record = ParseAction("Nniki pierces an ice giant for 21076 points of damage. (Flurry)");
      Assert.IsNotNull(record);
      Assert.AreEqual("Nniki", record.Attacker);
      Assert.AreEqual("An ice giant", record.Defender);
      Assert.AreEqual((uint)21076, record.Total);
      Assert.AreEqual("Pierces", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
      Assert.IsTrue(LineModifiersParser.IsFlurry(record.ModifiersMask));
      Assert.IsFalse(LineModifiersParser.IsCrit(record.ModifiersMask));
    }

    [TestMethod]
    public void TestRealLog_IceGiantRiposteStrikethrough()
    {
      var record = ParseAction("An ice giant hits Shmid for 172275 points of damage. (Riposte Strikethrough)");
      Assert.IsNotNull(record);
      Assert.AreEqual("An ice giant", record.Attacker);
      Assert.AreEqual("Shmid", record.Defender);
      Assert.AreEqual((uint)172275, record.Total);
      Assert.AreEqual(Labels.Melee, record.Type);
      Assert.AreEqual("Hits", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
      Assert.IsFalse(LineModifiersParser.IsRiposte(record.ModifiersMask));
      Assert.IsTrue(LineModifiersParser.IsStrikethrough(record.ModifiersMask));
    }

    [TestMethod]
    public void TestRealLog_IceGiantBashRiposteStrikethrough()
    {
      var record = ParseAction("An ice giant bashes Shmid for 39969 points of damage. (Riposte Strikethrough)");
      Assert.IsNotNull(record);
      Assert.AreEqual("An ice giant", record.Attacker);
      Assert.AreEqual("Shmid", record.Defender);
      Assert.AreEqual((uint)39969, record.Total);
      Assert.AreEqual(Labels.Melee, record.Type);
      Assert.AreEqual("Bashes", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
      Assert.IsFalse(LineModifiersParser.IsRiposte(record.ModifiersMask));
      Assert.IsTrue(LineModifiersParser.IsStrikethrough(record.ModifiersMask));
    }

    [TestMethod]
    public void TestRealLog_HacketLuckyCritical()
    {
      var record = ParseAction("Hacket hit an ice giant for 303431 points of magic damage by Crush of the Crying Seas X Rk. II. (Lucky Critical)");
      Assert.IsNotNull(record);
      Assert.AreEqual("Hacket", record.Attacker);
      Assert.AreEqual("An ice giant", record.Defender);
      Assert.AreEqual((uint)303431, record.Total);
      Assert.AreEqual(Labels.Dd, record.Type);
      Assert.AreEqual("Crush of the Crying Seas X Rk. II", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
      Assert.IsTrue(LineModifiersParser.IsCrit(record.ModifiersMask));
      Assert.IsTrue(LineModifiersParser.IsLucky(record.ModifiersMask));
    }

    [TestMethod]
    public void TestRealLog_NnikiLuckyCritical()
    {
      var record = ParseAction("Nniki crushes an ice giant for 148792 points of damage. (Lucky Critical)");
      Assert.IsNotNull(record);
      Assert.AreEqual("Nniki", record.Attacker);
      Assert.AreEqual("An ice giant", record.Defender);
      Assert.AreEqual((uint)148792, record.Total);
      Assert.AreEqual(Labels.Melee, record.Type);
      Assert.AreEqual("Crushes", record.SubType);
      Assert.IsFalse(record.AttackerIsSpell);
      Assert.IsTrue(LineModifiersParser.IsCrit(record.ModifiersMask));
      Assert.IsTrue(LineModifiersParser.IsLucky(record.ModifiersMask));
    }

    #endregion
  }
}
