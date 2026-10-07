using EQLogParser;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParserWpf.Tests.UI.Common;

/*
 * The "Set <name> as" cascade is the identity verb for four panes, so the thing worth pinning is that there is ONE shape to it:
 * the words come from IdentityVocabulary (a fifth kind cannot appear in one pane and not another), the header names the subject the
 * click will write, and a pick carries the Kind it shows. All three are cheap to assert and each has already been divergent in this
 * application — the retired menu had four flat "Set as X" rows here and a different verb ("Set as Verified Player") in the summaries,
 * which is the drift these tests exist to make impossible to repeat.
 *
 * Like everything in this assembly it runs through Sta.Run: MenuItem is a FrameworkElement and WPF will not construct one on the MTA
 * thread MSTest hands out (AGENTS → "EQLogParser.Wpf.Test runs MTA"). No Application is needed — IdentityVerdictMenu's icon helper asks
 * for the theme style conditionally, which is what lets the menu be built in a windowless host.
 */
[TestClass]
public class IdentityVerdictMenuTest
{
  [TestMethod]
  public void TheCascadeOffersTheFourKindsInTheVocabularysOwnOrder()
  {
    Sta.Run(() =>
    {
      var root = new MenuItem { Header = IdentityVerdictMenu.BaseHeader };
      IdentityVerdictMenu.Populate(root, _ => { });
      var words = Words(root);

      Assert.AreEqual(4, words.Count, "the cascade is four kinds — Spell is a rule's answer and \"Clear claim\" needs a row's evidence");
      CollectionAssert.AreEqual(
        new[] { "Player", "Pet", "Mercenary", "NPC" },
        words,
        "the words are IdentityVocabulary's, in its order; retyping them per pane is how the panes stopped matching");

      // Filling twice must not add a second set (a pane's constructor is not the only caller, and eight children would render as a bug).
      IdentityVerdictMenu.Populate(root, _ => { });
      Assert.AreEqual(4, Words(root).Count, "Populate is idempotent");
    });
  }

  [TestMethod]
  public void TheHeaderSaysWhichNameTheClickWillWrite()
  {
    Sta.Run(() =>
    {
      var root = new MenuItem { Header = IdentityVerdictMenu.BaseHeader };
      IdentityVerdictMenu.Populate(root, _ => { });

      // Nothing selected: the verb with no object. Not "Set Unknown as", which is what the panes used to print (their placeholder name
      // leaked into a header) and reads like the application thinks there is a fighter called "Unknown".
      IdentityVerdictMenu.Present(root, null, 0);
      Assert.AreEqual("Set as", root.Header);

      IdentityVerdictMenu.Present(root, "Frostmaw", 1);
      Assert.AreEqual("Set Frostmaw as", root.Header);

      // A batch cannot be named by one row, and pretending otherwise is how a ctrl-click of twenty looked like a single-row edit.
      IdentityVerdictMenu.Present(root, "Frostmaw", 12);
      Assert.AreEqual("Set 12 names as", root.Header);

      Assert.IsTrue(root.IsEnabled, "a greyed verb is a verb nobody finds; an empty batch is refused by the write instead");
      Assert.IsTrue(Items(root).All(i => i.IsEnabled), "and that includes its four children");
    });
  }

  /*
   * Each entry carries the kind the word claims. This is the seam between a menu and identity-overrides.txt: a child wired to the wrong
   * IdentityKind would write a Pet for a click that said Player, save it, re-derive, and be believed — the same class of "two rules
   * disagreeing about one dropdown" every other rule here is written against.
   */
  [TestMethod]
  public void EachEntryWritesTheKindItsWordPromises()
    => Sta.Run(() =>
    {
      var written = new List<IdentityKind>();
      var root = new MenuItem { Header = IdentityVerdictMenu.BaseHeader };
      IdentityVerdictMenu.Populate(root, kind => written.Add(kind));

      foreach (var item in Items(root)) item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

      CollectionAssert.AreEqual(new[] { IdentityKind.Player, IdentityKind.Pet, IdentityKind.Merc, IdentityKind.Npc }, written,
        "Player / Pet / Mercenary / NPC in that order, each raising its own kind");
    });

  private static List<MenuItem> Items(MenuItem root) => [.. root.Items.OfType<MenuItem>()];

  // Materialized, because the assertions read it twice (count and order) and a menu's item collection is not a list.
  private static List<string> Words(MenuItem root) => [.. Items(root).Select(i => i.Header?.ToString() ?? string.Empty)];
}
