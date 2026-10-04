#nullable disable
using EQLogParser;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser.Wpf.Test.src.ui.common;

/*
 * The "Tanking" dial ported from the legacy fight table ("Include Fights with only Tanking data"). Legacy
 * expressed it by swapping whole lists (_fights vs _nonTankingFights); the derived grid says the same thing
 * with one predicate over the row's direction split, so the predicate is what gets pinned - the grid seam
 * (checkbox -> ApplyFilter) is the same mechanism the inactivity dial already proves at startup.
 *
 * DamageToOwner is the raid's output ON the row's anchor; zero means the row exists only because the anchor
 * was hitting us, which is exactly what the dial off is for.
 */
[TestClass]
public class FightTableTankingFilterTest
{
  [TestMethod]
  public void ARowTheRaidNeverTouched_HidesWhenTankingIsOff()
  {
    var tankingOnly = new DerivedFight { DamageToOwner = 0, DamageByOwner = 5000 };
    Assert.IsFalse(FightTable.ShownWhenTankingHidden(tankingOnly));
  }

  [TestMethod]
  public void ARowTheRaidDamaged_StaysWhateverTheDialSays()
  {
    var fought = new DerivedFight { DamageToOwner = 1, DamageByOwner = 5000 };
    Assert.IsTrue(FightTable.ShownWhenTankingHidden(fought));

    var outputOnly = new DerivedFight { DamageToOwner = 12000, DamageByOwner = 0 };
    Assert.IsTrue(FightTable.ShownWhenTankingHidden(outputOnly));
  }

  [TestMethod]
  public void ARowWithNoFightBehindIt_IsNotSwallowed()
  {
    // Defensive half: a row with no fight object is nobody's "tanking only" evidence, so the dial must not
    // eat it - absence that deletes rows is how data goes missing without an error.
    Assert.IsTrue(FightTable.ShownWhenTankingHidden(null));
  }
}
