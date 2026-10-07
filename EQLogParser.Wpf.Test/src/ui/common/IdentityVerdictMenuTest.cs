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

  /*
   * The one-row law, both halves. `Present` receives "the single selected name or null" — every pane computes that itself (Count == 1 and a
   * name), so what is pinned here is what a null/empty does to the menu: it goes OFF rather than offering to write something. Multi-selection
   * refusal lives in those callers on purpose, because the refusal has to be computed from each grid's own selection; this class's job is to be
   * unclickable once they say "no subject".
   */
  [TestMethod]
  public void OnlyASingleSelectedNameEnablesTheVerb()
  {
    Sta.Run(() =>
    {
      var root = new MenuItem { Header = IdentityVerdictMenu.BaseHeader };
      IdentityVerdictMenu.Populate(root, _ => { });

      IdentityVerdictMenu.Present(root, "Frostmaw");
      Assert.AreEqual("Set Frostmaw as", root.Header, "the header names the subject of the click");
      Assert.IsTrue(root.IsEnabled);
      Assert.IsTrue(Items(root).All(i => i.IsEnabled));

      // Nothing selected: an action with no object, and it is off. (The old summaries printed their placeholder over a name — a fighter
      // called "Unknown" on screen — which is the failure mode a null must not be allowed to reach.)
      IdentityVerdictMenu.Present(root, null);
      Assert.AreEqual("Set as", root.Header);
      Assert.IsFalse(root.IsEnabled, "no selection can do nothing");

      // A blank name is not a subject either (a row whose name never got written).
      IdentityVerdictMenu.Present(root, string.Empty);
      Assert.IsFalse(root.IsEnabled);

      // Children carry their own IsEnabled, so a greyed parent must grey them too — otherwise the submenu still opens into live verbs.
      Assert.IsTrue(Items(root).All(i => !i.IsEnabled), "a refused cascade refuses its four entries as well");

      IdentityVerdictMenu.Present(root, "Frostmaw");
      Assert.IsTrue(root.IsEnabled, "and it comes back when a single row is selected again");
    });
  }

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
