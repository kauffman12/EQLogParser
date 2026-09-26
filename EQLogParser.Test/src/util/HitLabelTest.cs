using EQLogParser;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace EQLogParserTest
{
  /*
   * A damage or heal record states its kind as one byte instead of a string pointer, because there are only ever
   * sixteen words it can be (fourteen damage, two heal) and the parser picks them from constants rather than from
   * line text. That trade has three failure modes worth pinning: the backing type drifting back to int (which puts
   * the record over the size step it came down — 48 bytes instead of 40, for nothing), a word being missing from
   * the table (the record would read as unlabeled and its amount would vanish from every view built on the label),
   * and an unknown word being guessed at (which moves an event between columns silently). Numbers and reasoning:
   * docs/DesignNotes.md → What a loaded raid costs in memory; EQLogParser.Core/src/dao/model/HitLabel.cs.
   */
  [TestClass]
  public sealed class HitLabelTest
  {
    // The vocabulary, as the parser's branches actually produce it. A new word arrives with an entry here, an entry
    // in HitLabels' table, and a settings/panel change like everything else — this refuses one smuggled in.
    private static readonly (HitLabel Label, string Text)[] Expected =
    [
      (HitLabel.Melee, Labels.Melee),
      (HitLabel.Dd, Labels.Dd),
      (HitLabel.Dot, Labels.Dot),
      (HitLabel.Proc, Labels.Proc),
      (HitLabel.Bane, Labels.Bane),
      (HitLabel.Ds, Labels.Ds),
      (HitLabel.OtherDmg, Labels.OtherDmg),
      (HitLabel.Absorb, Labels.Absorb),
      (HitLabel.Block, Labels.Block),
      (HitLabel.Dodge, Labels.Dodge),
      (HitLabel.Miss, Labels.Miss),
      (HitLabel.Parry, Labels.Parry),
      (HitLabel.Riposte, Labels.Riposte),
      (HitLabel.Invulnerable, Labels.Invulnerable),
      (HitLabel.Heal, Labels.Heal),
      (HitLabel.Hot, Labels.Hot),
    ];

    [TestMethod]
    public void ALabelIsOneByteAndARecordCarriesNoStrings()
    {
      // An enum without an explicit backing type is an int, and four bytes here puts a damage record back over the
      // allocator's step: 48 bytes rather than 40, for a word that fits in a fifth of a byte.
      Assert.AreEqual(1, Unsafe.SizeOf<HitLabel>());

      // The reason the step exists at all: a record holds ids and this label, no references. A string field creeping
      // back onto either record costs 8 bytes a record to point at text that is already shared.
      foreach (var type in new[] { typeof(HitRecord), typeof(DamageRecord), typeof(HealRecord) })
      {
        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
        {
          Assert.AreNotEqual(typeof(string), field.FieldType, $"{type.Name}.{field.Name} stores a string reference");
        }
      }
    }

    [TestMethod]
    public void EveryLabelHasItsWordAndNoTwoShareOne()
    {
      var labels = (HitLabel[])Enum.GetValues(typeof(HitLabel));

      // None plus the sixteen. A word added to the enum without the pairing in HitLabels reads back as null, which
      // is why adding one is a deliberate act rather than an extra line in a file nobody re-reads.
      Assert.AreEqual(Expected.Length + 1, labels.Length);

      var words = new HashSet<string>(StringComparer.Ordinal);
      foreach (var (label, text) in Expected)
      {
        Assert.AreEqual(text, HitLabels.Text(label), $"{label} reads back as the wrong word");
        Assert.IsTrue(words.Add(text), $"{text} is the word for two labels");

        // Setting a record's Type must land on this label, and reading it must hand back the same string instance
        // every `record.Type == Labels.X` comparison has always been handed.
        var record = new DamageRecord { Type = text };
        Assert.AreEqual(label, record.Label);
        Assert.AreSame(text, record.Type);
      }

      // None is the absence of a word, and it must stay that way — see NoneReadsBackAsNoLabelAtAll.
      Assert.IsNull(HitLabels.Text(HitLabel.None));
    }

    [TestMethod]
    public void TheVocabularyRoundTripsThroughARecord()
    {
      // A heal can only be Direct Heal or HoT Tick, but the whole table has to map in both directions for whoever
      // builds a record by hand (the delay-crit path, tests, tools).
      foreach (var (label, text) in Expected)
      {
        var record = new HealRecord { Type = text };
        Assert.AreEqual(label, record.Label, $"{text} mapped to the wrong label");
        Assert.AreEqual(text, record.Type);
      }
    }

    [TestMethod]
    public void NoneReadsBackAsNoLabelAtAll()
    {
      // SpellCast, ReceivedSpell, CastTimeRecord and TauntRecord never set a label. Their Type has read null since
      // these fields were ids (StringCache.GetName(0) is null so an unset field stays unset), and nothing may start
      // seeing the word "Unknown" — or worse, a label belonging to a different event.
      Assert.IsNull(new DamageRecord().Type);
      Assert.IsNull(new HealRecord().Type);
      Assert.AreEqual(HitLabel.None, new DamageRecord().Label);
      Assert.AreEqual(HitLabel.None, HitLabels.Parse(null));

      // None must also never be produced by a word that is in the table.
      foreach (var (label, text) in Expected)
      {
        Assert.AreNotEqual(HitLabel.None, HitLabels.Parse(text), $"{text} maps to None");
      }
    }

    [TestMethod]
    public void AWordOutsideTheTableIsCountedAndNotGuessedAt()
    {
      var before = HitLabels.Unmapped;

      // Ordinal only. "direct damage" is not a label, and folding it into Direct Damage would move those events into
      // a column of every view without anything ever failing.
      Assert.AreEqual(HitLabel.None, HitLabels.Parse("direct damage"));
      Assert.AreEqual(HitLabel.None, HitLabels.Parse("Rune Strike"));
      Assert.IsNull(new DamageRecord { Type = "direct damage" }.Type);

      // A process-global counter, so this asserts movement rather than a value: other tests in the run may have fed
      // it too. It should never move in a normal session, and this is the only thing that would notice if it did.
      // Three here — two Parse calls plus the record assignment, which goes through the same door. A word is counted
      // per attempt rather than deduplicated, because a session that produced one of these per event should not look
      // like one that produced it once.
      Assert.AreEqual(before + 3, HitLabels.Unmapped);
    }

    [TestMethod]
    public void TwoEventsThatDifferOnlyByLabelStayTwoEvents()
    {
      // The label is in Equals and GetHashCode, where it has to stay: an Absorb of 1200 and a Melee of 1200 must not
      // collapse onto one instance in FightManager's repeat store.
      var absorb = new DamageRecord { Attacker = "Zomk", Defender = "Fen Claw", SubType = "Spirit of Barbs", Type = Labels.Absorb, Total = 1200 };
      var melee = new DamageRecord { Attacker = "Zomk", Defender = "Fen Claw", SubType = "Spirit of Barbs", Type = Labels.Melee, Total = 1200 };

      Assert.IsFalse(absorb.Equals(melee));
      Assert.AreNotEqual(absorb.GetHashCode(), melee.GetHashCode());
      Assert.IsTrue(absorb.Equals(new DamageRecord { Attacker = "Zomk", Defender = "Fen Claw", SubType = "Spirit of Barbs", Type = Labels.Absorb, Total = 1200 }));

      var heal = new HealRecord { Healer = "Cureqa", Healed = "Zomk", SubType = "Brell's Blessing", Type = Labels.Heal, Total = 900, OverTotal = 100 };
      var hot = new HealRecord { Healer = "Cureqa", Healed = "Zomk", SubType = "Brell's Blessing", Type = Labels.Hot, Total = 900, OverTotal = 100 };
      Assert.IsFalse(heal.Equals(hot));

      // A record whose label nobody recognised is still equal to its own kind: None compares like any other value,
      // it does not act as a wildcard.
      Assert.IsFalse(new DamageRecord { Type = "nonsense" }.Equals(new DamageRecord { Type = Labels.Melee, Total = 1 }));
      Assert.IsTrue(new DamageRecord { Attacker = "Zomk", Total = 5 }.Equals(new DamageRecord { Attacker = "Zomk", Total = 5, Type = "nonsense" }));
    }
    [TestMethod]
    public void TheWholeTableIsWordsTheParserCanProduce()
    {
      // DamageLineParserTest asserts record.Type against Labels for the lines the parser knows (54 of them, covering
      // eleven of these words), which is the real coverage that the table has no gaps. This holds the other half: the
      // count is what the parsers' own branches add up to, so a word nobody can produce shows up here.
      Assert.AreEqual(14, Expected.Length - 2, "fourteen damage labels plus the two heal labels");
      CollectionAssert.Contains(Expected.Select(e => e.Label).ToList(), HitLabel.Heal);
      CollectionAssert.Contains(Expected.Select(e => e.Label).ToList(), HitLabel.Hot);
    }
  }
}
