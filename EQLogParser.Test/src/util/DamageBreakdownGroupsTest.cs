namespace EQLogParser;

[TestClass]
public class DamageBreakdownGroupsTest
{
  [TestMethod]
  public void SpellCategoriesContainTheirOriginalRowsAndSortByDamage()
  {
    var player = new PlayerStats { Name = "Tank" };
    var dd = Add(player, "Blast", Labels.Dd, 100);
    var bane = Add(player, "Bane", Labels.Bane, 50);
    var dot = Add(player, "Burn", Labels.Dot, 200);
    var proc = Add(player, "Proc", Labels.Proc, 25);
    var melee = Add(player, "Slash", Labels.Melee, 300);
    var shield = Add(player, "Thorns", Labels.Ds, 10);
    var groups = Build(player);

    CollectionAssert.AreEqual(new[] { "Slash", Labels.Dot, Labels.Dd, Labels.Proc, "Thorns" },
      groups.ChildrenOf(player).Select(row => row.Name).ToArray());
    CollectionAssert.AreEqual(new[] { dd, bane }, groups.ChildrenOf(Group(groups, player, Labels.Dd)).ToArray());
    Assert.AreSame(dot, groups.ChildrenOf(Group(groups, player, Labels.Dot)).Single());
    Assert.AreSame(proc, groups.ChildrenOf(Group(groups, player, Labels.Proc)).Single());
    Assert.AreSame(melee, groups.ChildrenOf(player).First());
    Assert.AreSame(shield, groups.ChildrenOf(player).Last());
    Assert.AreEqual(player.Total, groups.ChildrenOf(player).Sum(row => row.Total));
  }

  [TestMethod]
  public void CategoriesMergeCountersAndExtremaThenCalculateRatesAgainstTheirPlayer()
  {
    var player = new PlayerStats { Name = "Tank", TotalSeconds = 20 };
    var first = Add(player, "First blast", Labels.Dd, 100, 2, 10);
    first.Min = 30;
    first.Max = 70;
    first.CritHits = 1;
    first.TotalCrit = 70;
    first.Resists = 1;
    var second = Add(player, "Second blast", Labels.Dd, 200, 4, 20);
    second.Min = 20;
    second.Max = 80;
    var bane = Add(player, "Bane", Labels.Bane, 50, 1, 5);
    bane.Min = 50;
    bane.Max = 50;
    bane.BaneHits = 1;
    Add(player, "Melee", Labels.Melee, 650);
    var groups = new DamageBreakdownGroups(new PlayerStats { Total = 5000, TotalSeconds = 100 }, [player]);
    var category = Group(groups, player, Labels.Dd);

    Assert.AreEqual(350L, category.Total);
    Assert.AreEqual(7u, category.Hits);
    Assert.AreEqual(1u, category.CritHits);
    Assert.AreEqual(70L, category.TotalCrit);
    Assert.AreEqual(1u, category.BaneHits);
    Assert.AreEqual(1, category.Resists);
    Assert.AreEqual(20L, category.Min);
    Assert.AreEqual(80L, category.Max);
    Assert.AreEqual(20d, category.TotalSeconds);
    Assert.AreEqual(17L, category.Dps);
    Assert.AreEqual(17L, category.Sdps, "breakdown SDPS uses the player's span, not the raid's");
    Assert.AreEqual(35f, category.Percent);
    Assert.AreEqual(50L, category.Avg);
    Assert.AreEqual(14.29f, category.CritRate);
  }

  [TestMethod]
  public void EmptySpellCategoriesHideButZeroDamageDefensesRemainVisible()
  {
    var player = new PlayerStats { Name = "Tank" };
    Add(player, "Resisted blast", Labels.Dd, 0, 0);
    var defense = Add(player, "Slash", Labels.Melee, 0, 0);
    defense.MeleeAttempts = 1;
    defense.RiposteHits = 1;
    var groups = Build(player);

    Assert.AreSame(defense, groups.ChildrenOf(player).Single());
    Assert.HasCount(0, groups.ChildrenOf(defense).ToArray());
  }

  [TestMethod]
  public void PositiveCategoryKeepsItsZeroDamageChildren()
  {
    var player = new PlayerStats { Name = "Tank" };
    var zero = Add(player, "Resisted blast", Labels.Dd, 0, 0);
    var hit = Add(player, "Blast", Labels.Dd, 20);
    var groups = Build(player);

    CollectionAssert.AreEqual(new[] { zero, hit }, groups.ChildrenOf(Group(groups, player, Labels.Dd)).ToArray());
  }

  [TestMethod]
  public void GroupingDoesNotMutateOrAccumulateIntoPublishedBoardRows()
  {
    var player = new PlayerStats { Name = "Tank" };
    var dd = Add(player, "Blast", Labels.Dd, 100, 2);
    dd.RecordHitTotal(40, false);
    dd.RecordHitTotal(60, false);
    var originalRows = player.SubStats.ToArray();
    var first = Build(player);
    var second = Build(player);

    Assert.AreEqual(100L, Group(first, player, Labels.Dd).Total);
    Assert.AreEqual(100L, Group(second, player, Labels.Dd).Total);
    Assert.AreNotSame(Group(first, player, Labels.Dd), Group(second, player, Labels.Dd));
    CollectionAssert.AreEqual(originalRows, player.SubStats);
    Assert.AreSame(dd, first.ChildrenOf(Group(first, player, Labels.Dd)).Single());
    Assert.AreEqual(100L, player.Total);
    Assert.AreEqual(100L, dd.Total);
    Assert.AreEqual(2u, dd.Hits);
    Assert.IsTrue(dd.HoldsRawHitTotals, "grouping must not materialize a chart's histogram");
    Assert.AreEqual(2, dd.RawHitTotals);

  }

  [TestMethod]
  public void EqualNamesDoNotCombineDifferentPlayerRows()
  {
    var first = new PlayerStats { Name = "Same" };
    var second = new PlayerStats { Name = "Same" };
    var firstSpell = Add(first, "Blast", Labels.Dd, 100);
    var secondSpell = Add(second, "Blast", Labels.Dd, 200);
    var groups = new DamageBreakdownGroups(new PlayerStats { TotalSeconds = 10 }, [first, second]);

    Assert.AreEqual(100L, Group(groups, first, Labels.Dd).Total);
    Assert.AreEqual(200L, Group(groups, second, Labels.Dd).Total);
    Assert.AreSame(firstSpell, groups.ChildrenOf(Group(groups, first, Labels.Dd)).Single());
    Assert.AreSame(secondSpell, groups.ChildrenOf(Group(groups, second, Labels.Dd)).Single());
  }

  [TestMethod]
  public void RootsAreCapturedAndLeavesHaveNoChildren()
  {
    var player = new PlayerStats { Name = "Tank" };
    var selected = new List<PlayerStats> { player };
    var groups = new DamageBreakdownGroups(new PlayerStats(), selected);
    selected.Clear();

    Assert.HasCount(1, groups.Players);
    Assert.AreSame(player, groups.ChildrenOf(null).Single());
    Assert.HasCount(0, groups.ChildrenOf(player).ToArray());
    Assert.HasCount(0, groups.ChildrenOf(new PlayerStats()).ToArray());
    Assert.HasCount(0, groups.ChildrenOf(new PlayerSubStats()).ToArray());
    Assert.HasCount(0, new DamageBreakdownGroups(new PlayerStats(), []).Players);
  }

  [TestMethod]
  public void WideTotalsArePreservedAndTiesKeepTheOutgoingCategoryOrder()
  {
    var player = new PlayerStats { Name = "Tank" };
    const long amount = 5_309_597_698;
    Add(player, "Proc", Labels.Proc, amount);
    Add(player, "Burn", Labels.Dot, amount);
    Add(player, "Blast", Labels.Dd, amount);
    var groups = Build(player);

    CollectionAssert.AreEqual(new[] { Labels.Dd, Labels.Dot, Labels.Proc },
      groups.ChildrenOf(player).Select(row => row.Name).ToArray());
    Assert.AreEqual(3 * amount, groups.ChildrenOf(player).Sum(row => row.Total));
  }

  private static DamageBreakdownGroups Build(PlayerStats player)
    => new(new PlayerStats { Total = player.Total, TotalSeconds = 10 }, [player]);

  private static SubStatsBreakdown Group(DamageBreakdownGroups groups, PlayerStats player, string type)
    => groups.ChildrenOf(player).OfType<SubStatsBreakdown>().Single(row => row.Type == type);

  private static PlayerSubStats Add(PlayerStats player, string name, string type, long total,
    uint hits = 1, double seconds = 10)
  {
    var row = player.SubStatOf(name, type);
    row.Total = total;
    row.Hits = hits;
    row.TotalSeconds = seconds;
    player.Total += total;
    return row;
  }
}
