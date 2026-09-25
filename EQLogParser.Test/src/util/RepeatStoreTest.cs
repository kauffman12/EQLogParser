using EQLogParser;

namespace EQLogParserTest
{
  /*
   * RepeatStore is where a raid's records get shared, and sharing by value is the part that can hurt a player
   * if it goes wrong: hand back an instance for a *different* value and two events in the same fight read the
   * same numbers. So the tests here are mostly about what it must never do — merge two values, or substitute
   * an instance the caller cannot tell apart from its own by value.
   *
   * The other half of the contract is the frugality: a value seen once takes no entry, because four out of
   * five are never restated (docs/DesignNotes.md → What a loaded raid costs in memory). That is a deliberate
   * behavior change from the dictionary this replaced, which meant to remember every value it ever met.
   */
  [TestClass]
  public sealed class RepeatStoreTest
  {
    /* Any type with value equality will do: what the store keys on is Equals/GetHashCode, which is exactly
     * what DamageRecord and HealRecord write by hand. The two record types get their own tests below. */
    private sealed record Widget(string Name, int Amount);

    [TestMethod]
    public void TryGet_EmptyStore_HasNothingToGive()
    {
      var store = new RepeatStore<Widget>(10_000);
      Assert.IsFalse(store.TryGet(new Widget("sword", 10), out _));
    }

    [TestMethod]
    public void Offer_FirstSightingTakesNoEntry()
    {
      var store = new RepeatStore<Widget>(10_000);
      store.Offer(new Widget("sword", 10));

      Assert.AreEqual(0, store.Held, "a value seen once is not worth an entry — that is the whole design");
      Assert.AreEqual(1, store.LetGo);
      Assert.IsFalse(store.TryGet(new Widget("sword", 10), out _));
    }

    [TestMethod]
    public void Offer_SecondSightingBuysTheEntry()
    {
      var store = new RepeatStore<Widget>(10_000);
      var first = new Widget("sword", 10);
      var second = new Widget("sword", 10);

      store.Offer(first);
      Assert.AreEqual(0, store.Held);

      store.Offer(second);
      Assert.AreEqual(1, store.Held, "the repeat is what pays for the entry");

      Assert.IsTrue(store.TryGet(new Widget("sword", 10), out var cached));
      Assert.AreSame(second, cached, "the instance kept is the one that was offered, not a copy");
      Assert.AreEqual(1, store.LetGo, "only the first sighting was let go");
    }

    [TestMethod]
    public void TryGet_NeverHandsOutADifferentValue()
    {
      // The safety property. Distinct values offered in a repeating pattern — with a filter that can say
      // "seen" about values it has not kept, and an entry taken for some of them: whatever comes back out
      // has to be the caller's own instance or one equal to it, always.
      var store = new RepeatStore<Widget>(20_000);
      const int Kinds = 5_000;
      var offered = new Widget[Kinds];
      for (var i = 0; i < Kinds; i++)
      {
        offered[i] = new Widget($"item-{i}", i);
        store.Offer(offered[i]);
      }

      // Two more passes so nearly every kind has bought an entry, and every one of them is now a lookup.
      for (var pass = 0; pass < 2; pass++)
      {
        for (var i = 0; i < Kinds; i++)
        {
          store.Offer(offered[i]);

          if (store.TryGet(new Widget($"item-{i}", i), out var cached))
          {
            Assert.AreEqual(offered[i], cached, $"kind {i} came back as something else");
            Assert.AreSame(offered[i], cached, "the store may only hand out an instance it was given");
          }
        }
      }

      Assert.AreEqual(Kinds, store.Held, "with three sightings every kind should have earned an entry");
    }

    [TestMethod]
    public void Offer_KeepsValuesThatDifferInOneFieldApart()
    {
      var store = new RepeatStore<Widget>(10_000);
      var sword = new Widget("sword", 10);
      var biggerSword = new Widget("sword", 11);

      // Two sightings each so both are past the first-sighting gate and actually competing for entries.
      store.Offer(sword);
      store.Offer(biggerSword);
      store.Offer(sword);
      store.Offer(biggerSword);

      Assert.AreEqual(2, store.Held);
      Assert.IsTrue(store.TryGet(sword, out var foundSword));
      Assert.AreSame(sword, foundSword);
      Assert.IsTrue(store.TryGet(biggerSword, out var foundBigger));
      Assert.AreSame(biggerSword, foundBigger);
    }

    [TestMethod]
    public void Clear_DropsEntriesAndTheSightingHistory()
    {
      var store = new RepeatStore<Widget>(10_000);
      var widget = new Widget("sword", 10);
      store.Offer(widget);
      store.Offer(new Widget("sword", 10));
      Assert.AreEqual(1, store.Held);

      store.Clear();

      Assert.AreEqual(0, store.Held);
      Assert.AreEqual(0, store.LetGo);
      Assert.IsFalse(store.TryGet(widget, out _));

      // Cleared history means the next sighting is a first sighting again: an entry is not taken for it.
      store.Offer(new Widget("sword", 10));
      Assert.AreEqual(0, store.Held);
      Assert.AreEqual(1, store.LetGo);
    }

    [TestMethod]
    public void HealRecords_EqualByValueAcrossInstances()
    {
      // What the parsers lean on: two heal lines that say the same thing produce separate objects and have
      // to be recognized as one value, whatever the string instances inside them are.
      var store = new RepeatStore<HealRecord>(10_000);
      var first = new HealRecord { Healer = "Fllint", Healed = "Foob", Total = 11820, Type = Labels.Heal, SubType = "Blessing of the Ancients III" };
      var second = new HealRecord { Healer = "Fllint", Healed = "Foob", Total = 11820, Type = Labels.Heal, SubType = "Blessing of the Ancients III" };

      store.Offer(first);
      store.Offer(second);

      Assert.IsTrue(store.TryGet(new HealRecord { Healer = "Fllint", Healed = "Foob", Total = 11820, Type = Labels.Heal, SubType = "Blessing of the Ancients III" }, out var cached));
      Assert.AreSame(second, cached);
    }

    [TestMethod]
    public void DamageRecords_DifferingInAnyFieldStaySeparate()
    {
      // The damage cache may only collapse events that agree on every field the record compares. Amount,
      // spell and modifiers each have to keep their own instance, or a grid would add one hit twice.
      var store = new RepeatStore<DamageRecord>(10_000);
      var hit = new DamageRecord { Attacker = "xegony", Defender = "Guk", Total = 100, Type = Labels.Dd, SubType = "Sword" };
      var moreDamage = new DamageRecord { Attacker = "xegony", Defender = "Guk", Total = 101, Type = Labels.Dd, SubType = "Sword" };
      var otherSpell = new DamageRecord { Attacker = "xegony", Defender = "Guk", Total = 100, Type = Labels.Dd, SubType = "Axe" };
      var otherTarget = new DamageRecord { Attacker = "xegony", Defender = "Ukun", Total = 100, Type = Labels.Dd, SubType = "Sword" };

      // Three sightings apiece: past the gate, so all four are held and could have been merged.
      foreach (var record in new[] { hit, moreDamage, otherSpell, otherTarget })
      {
        store.Offer(record);
        store.Offer(record);
        store.Offer(record);
      }

      Assert.AreEqual(4, store.Held);
      Assert.IsTrue(store.TryGet(hit, out var foundHit));
      Assert.AreSame(hit, foundHit);
      Assert.IsFalse(object.ReferenceEquals(foundHit, moreDamage));
      Assert.IsFalse(object.ReferenceEquals(foundHit, otherSpell));
      Assert.IsFalse(object.ReferenceEquals(foundHit, otherTarget));
    }

    [TestMethod]
    public void FilterBytes_ReportsWhatTheFrugalityCosts()
    {
      // The filter is memory spent to save memory; a caller has to be able to see the price. Four million
      // offers costs about 5 MB, which bought 77 MB on the log the change was measured against.
      var store = new RepeatStore<Widget>(4_000_000);
      Assert.IsTrue(store.FilterBytes is > 4_000_000 and < 6_000_000, $"{store.FilterBytes} bytes");
    }
  }
}
