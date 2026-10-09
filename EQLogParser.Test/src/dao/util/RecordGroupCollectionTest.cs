using System.Reflection;

using EQLogParser;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser;

/*
 * The record→DataPoint walk, and the two things it now promises.
 *
 * A walk used to allocate a `RecordWrapper` and a `DataPoint` for EVERY record: on a whole-capture selection that is 4.6 M
 * pairs — 960 MB of garbage measured over one damage-board build (docs/DesignNotes.md → the board-cost section) — plus an
 * identity-store lookup per record to answer a question with dozens of distinct answers. Both are gone, and what replaced
 * them is aliasing, so these tests are the seam's contract:
 *
 *  - **one instance for the whole walk**, handed out again per record (`TheWalkHandsOutOneDataPointInstance`), because that
 *    is what a consumer must assume when it decides whether it may keep a value;
 *  - **every field written or reset per record** (`AnUntouchedFieldNeverSurvivesIntoTheNextRecord`), checked by reflection so a
 *    new `DataPoint` property is covered the day somebody adds it — a stale carry-over would otherwise be a value from the
 *    previous record wearing the current one;
 *  - **no per-record allocation** (`TheWalkAllocatesForTheWalkRatherThanForTheRecords`), with a control loop that proves the
 *    probe can see an allocation, so regressing to `new DataPoint` fails by name instead of quietly printing 960 MB again;
 *  - **placement unchanged**: pet owner from the store first and the line's own possessive word second, memoized per walk but
 *    never across walks (`TheOwnerMemoAnswersPerNameAndNeverGoesStaleAcrossWalks`).
 *
 * Board totals themselves are pinned elsewhere — DamageBoardGoldenTest/TankingBoardGoldenTest cover the same walk through
 * the real builder, and they are what says this is a performance change rather than a numbers change.
 */
[TestClass]
[DoNotParallelize]
public class RecordGroupCollectionTest
{
  private const double T0 = FixtureTime.Base;

  [TestInitialize]
  public void Setup() => PlayerRegistry.Instance.Clear();

  [TestCleanup]
  public void Cleanup()
  {
    PlayerRegistry.Instance.Clear();
    IdentityLookup.LiveOwner = null;
  }

  // ── one instance, every field fresh ───────────────────────────────────

  [TestMethod]
  public void TheWalkHandsOutOneDataPointInstance()
  {
    var groups = Groups(
      Block(T0, Damage("Reisil", "an ice lion", 100)),
      Block(T0 + 2, Damage("Kilsa", "an ice lion", 200)),
      Block(T0 + 4, Damage("Rune", "an ice lion", 300)));

    var seen = new List<DataPoint>();
    foreach (var point in new DamageGroupCollection(groups)) seen.Add(point);

    Assert.AreEqual(3, seen.Count, "every record should reach the walk");
    Assert.AreSame(seen[0], seen[1], "the walk reuses one DataPoint; a consumer must not retain what it is handed");
    Assert.AreSame(seen[1], seen[2]);

    /* And the last write wins on that shared instance, which is the aliasing law stated as an observable. */
    Assert.AreEqual("Rune", seen[0].Name);
  }

  [TestMethod]
  public void AnUntouchedFieldNeverSurvivesIntoTheNextRecord()
  {
    /* Fields a damage walk answers with. Everything else on the DTO has to read default on every record it yields. */
    var written = new HashSet<string>
    {
      nameof(DataPoint.Name), nameof(DataPoint.PlayerName), nameof(DataPoint.Type),
      nameof(DataPoint.Total), nameof(DataPoint.ModifiersMask), nameof(DataPoint.CurrentTime),
    };

    var properties = typeof(DataPoint).GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && !written.Contains(p.Name))
        .ToArray();

    Assert.IsTrue(properties.Length > 10, "the reflection sweep should cover most of the DTO, not nothing");

    var groups = Groups(
        Block(T0, new DamageRecord { Attacker = "Reisil", Defender = "an ice lion", Total = 5, Type = Labels.Melee, SubType = Labels.Melee, ModifiersMask = LineModifiersParser.Crit }),
        Block(T0 + 1, new DamageRecord { Attacker = "Kilsa", Defender = "an ice lion", Total = 6, Type = Labels.Bane, SubType = Labels.Bane }));

    var walked = 0;
    foreach (var point in new DamageGroupCollection(groups))
    {
      walked++;
      foreach (var property in properties)
      {
        var value = property.GetValue(point);
        var isDefault = value is null
                        || value.Equals(0L) || value.Equals(0) || value.Equals(0u)
                        || value.Equals(0.0) || value.Equals(default(DateTime));
        Assert.IsTrue(isDefault, $"{property.Name} carried {value} into record {walked} from an earlier one");
      }
    }

    Assert.AreEqual(2, walked);
  }

  [TestMethod]
  public void TheWalkAllocatesForTheWalkRatherThanForTheRecords()
  {
    const int records = 200_000;
    var groups = Groups(Block(T0, Enumerable.Range(0, records)
        .Select(i => Damage(i % 50 == 0 ? "Reisil" : "Kilsa", "an ice lion", (uint)(i + 1)))
        .ToArray()));

    /* The control: an allocation the probe MUST see, published so nothing elides it (same reason as FctPlacementAllocationTest). */
    var sink = new List<object>();
    var control0 = GC.GetTotalAllocatedBytes(precise: true);
    for (var i = 0; i < records; i++) sink.Add(new byte[64]);
    var controlBytes = GC.GetTotalAllocatedBytes(precise: true) - control0;
    Assert.IsTrue(controlBytes > records * 32, $"the probe could not see {records} allocations ({controlBytes} bytes) — it cannot clear the walk either");

    GC.KeepAlive(sink);

    var walk0 = GC.GetTotalAllocatedBytes(precise: true);
    long total = 0;
    foreach (var point in new DamageGroupCollection(groups)) total += point.Total;
    var walkBytes = GC.GetTotalAllocatedBytes(precise: true) - walk0;

    Assert.AreEqual(Enumerable.Range(1, records).Sum(i => (long)i), total, "the walk still adds up");

    /*
     * The state machine and the collection itself are allowed; what is not allowed is cost that scales with records at anything
     * like object size. Before this seam was fixed the same loop cost ~120 B/record (a wrapper plus a DTO).
     */
    var perRecord = walkBytes / (double)records;
    Assert.IsTrue(perRecord < 8, $"the walk costs {perRecord:F1} bytes per record — that looks like per-record allocation again");
  }

  // ── placement: same answer, fewer questions ───────────────────────────

  [TestMethod]
  public void TheOwnerMemoAnswersPerNameAndNeverGoesStaleAcrossWalks()
  {
    var groups = Groups(Block(T0, Damage("Fido", "an ice lion", 100)));

    /* No opinion anywhere: the line's own possessive word is what places it. */
    foreach (var point in new DamageGroupCollection(Groups(Block(T0,
       new DamageRecord { Attacker = "Fido", AttackerOwner = "Reisil", Defender = "an ice lion", Total = 100, Type = Labels.Melee, SubType = Labels.Melee }))))
    {
      Assert.AreEqual("Reisil", point.PlayerName, "the record's own owner word has to place the pet");
    }

    /* The store answers first when it has an answer, and one name is asked once per walk. */
    var asks = 0;
    IdentityLookup.LiveOwner = pet =>
    {
      asks++;
      return pet.Equals("Fido", StringComparison.OrdinalIgnoreCase) ? "Kilsa" : null;
    };

    var twiceRepeating = Groups(Block(T0,
        new DamageRecord { Attacker = "Fido", AttackerOwner = "Reisil", Defender = "an ice lion", Total = 100, Type = Labels.Melee, SubType = Labels.Melee },
        new DamageRecord { Attacker = "Fido", AttackerOwner = "Reisil", Defender = "an ice lion", Total = 50, Type = Labels.Melee, SubType = Labels.Melee }));

    foreach (var point in new DamageGroupCollection(twiceRepeating))
    {
      Assert.AreEqual("Kilsa", point.PlayerName, "the store's answer outranks the line's, as it always did");
    }

    Assert.AreEqual(1, asks, "one name is one question per walk, not one per record");

    /* A walk started after the world changed must not inherit the memo of the walk before it. */
    IdentityLookup.LiveOwner = pet => null;
    foreach (var point in new DamageGroupCollection(twiceRepeating))
    {
      Assert.AreEqual("Reisil", point.PlayerName, "a fresh walk re-asks: the memo lives on the collection, not the type");
    }
  }

  [TestMethod]
  public void ARecordOfTheWrongKindReachesNoPointAtAll()
  {
    var damageBlock = Block(T0, Damage("Reisil", "an ice lion", 100));
    damageBlock.Actions.Add(new HealRecord { Healer = "Kilsa", Healed = "Reisil", Total = 900, Type = Labels.Heal, SubType = Labels.Heal });
    var mixed = new List<List<ActionGroup>> { new() { damageBlock, new ActionGroup { BeginTime = T0 + 1 } } };

    var names = new List<string>();
    foreach (var point in new DamageGroupCollection(mixed)) names.Add(point.Name);

    CollectionAssert.AreEqual(new[] { "Reisil" }, names, "a heal line must not appear in a damage walk, and must not leak the previous record's fields");

    /* The tanking walk reads the DEFENDER of the same records — pinned so the two seams cannot drift apart. */
    var taken = new List<string>();
    var tankGroups = new List<List<ActionGroup>> { new() { Block(T0, Damage("Reisil", "an ice lion", 100)) } };
    foreach (var point in new TankGroupCollection(tankGroups, 0)) taken.Add(point.Name);
    CollectionAssert.AreEqual(new[] { "an ice lion" }, taken);
  }

  // ── fixtures ──────────────────────────────────────────────────────────

  private static DamageRecord Damage(string attacker, string defender, uint total) => new()
  {
    Attacker = attacker,
    Defender = defender,
    Total = total,
    Type = Labels.Melee,
    SubType = Labels.Melee,
  };

  private static ActionGroup Block(double beginTime, params DamageRecord[] records)
  {
    var block = new ActionGroup { BeginTime = beginTime };
    foreach (var record in records) block.Actions.Add(record);
    return block;
  }

  private static List<List<ActionGroup>> Groups(params ActionGroup[] blocks) => [new List<ActionGroup>(blocks)];
}
