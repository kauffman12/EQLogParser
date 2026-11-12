#nullable enable annotations
namespace EQLogParser;

[TestClass]
public sealed class NpcStatsBuildKeyTest
{
  [TestMethod]
  public void EveryBuildInputAndEveryInvalidationChangesTheQuestion()
  {
    var original = Key();
    var changed = new[]
    {
      Key(session: 2),
      Key(stamp: 101),
      Key(damage: 11),
      Key(heals: 21),
      Key(zones: 3),
      Key(selected: []),
      Key(selected: [new DerivedFight { Id = 1 }]),
      Key(generation: 1),
      Key(refresh: 1)
    };
    foreach (var key in changed) Assert.AreNotEqual(original, key);
  }

  [TestMethod]
  public void IdenticalSelectionsHaveTheSameKeyWithoutRetainingTheirRows()
  {
    var first = Key(selected: [new DerivedFight { Id = 1 }, new DerivedFight { Id = 2 }]);
    Assert.AreEqual(first, Key(selected: [new DerivedFight { Id = 1 }, new DerivedFight { Id = 2 }]));
    Assert.AreNotEqual(first, Key(selected: [new DerivedFight { Id = 1 }]));
    Assert.AreNotEqual(first, Key(selected: [new DerivedFight { Id = 2 }]));
  }

  [TestMethod]
  public void ANewCaptureOrAReopenedPaneCannotReuseTheClearedGridsOldAnswer()
  {
    var first = Key(selected: [new DerivedFight { Id = 1 }]);
    Assert.AreNotEqual(first, Key(session: 2, selected: [new DerivedFight { Id = 1 }]));
    Assert.AreNotEqual(first, Key(generation: 1, selected: [new DerivedFight { Id = 1 }]));
    Assert.AreNotEqual(first, Key(refresh: 1, selected: [new DerivedFight { Id = 1 }]));
  }

  private static long Key(int session = 1, long stamp = 100, int damage = 10, int heals = 20, int zones = 2,
    IReadOnlyList<DerivedFight>? selected = null, long generation = 0, long refresh = 0)
    => NpcStatsBuildKey.For(session, stamp, damage, heals, zones, selected, generation, refresh);
}
