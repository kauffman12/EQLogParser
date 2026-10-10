using System.Collections.Generic;
using EQLogParser;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser;

/*
 * The rule these tests hold: a board you are reading moves when a name changes WHAT IT IS, and for nothing else.
 *
 * Stated by the operator after two field sessions (2026-10-08): "i really dont want updates happening dynamically because new damage
 * came in either. id rather it be like a snapshot of what was selected at the time except for the pet changes or player turning npc, etc.
 * like classification changes that matter." Both field reports of "the whole table cleared and reloaded for no reason" were rebuilds this
 * rule forbids.
 *
 * The scope test is the one that matters most (`NewNamesBeingPlacedNeverFireTheWatch`), because it is what makes the rule cheap instead of
 * a rename of the flicker: measured over `eqlog_Incogitable_xegony.txt` replayed as 7 growing prefixes, the identity digest moved on 6 of 6
 * passes while ZERO already-placed verdicts flipped — every move was a name seen for the first time (median 58, max 220). A gate on the
 * digest rebuilds once per pass. A gate on displayed names rebuilt none of them.
 */
[TestClass]
public class IdentityWatchTest
{
    [TestMethod]
    [Description("The measured reason for scoping to displayed names: a capture keeps meeting new mobs, and that cannot change what a fixed board shows.")]
    public void NewNamesBeingPlacedNeverFireTheWatch()
    {
        var timeline = new EntityTimeline();
        timeline.SetIdentity("Frostmaw", IdentityKind.Npc, 100, "R6-npcdb");

        var watch = new IdentityWatch();
        watch.CaptureBoard(timeline, ["Frostmaw"]);

        // The raid keeps arriving: nine names placed after the boards were built. None of them is on screen.
        for (var i = 0; i < 9; i++) timeline.SetIdentity($"Raider{i}", IdentityKind.Player, 100, "R3-chat");

        Assert.IsFalse(watch.AnyChanged(timeline),
            "new arrivals must not rebuild a board that lists fixed names - digest-gating did exactly this (6 of 6 passes) with zero real flips");
    }

    [TestMethod]
    [Description("Both directions a real classification change takes: a pet finally learned, and a raider charmed into the enemy list.")]
    public void AWatchedNameChangingKindFires()
    {
        var timeline = new EntityTimeline();
        var watch = new IdentityWatch();

        // Unknown -> Pet: the possessive line or petmapping pair that finally names this summon.
        watch.CaptureBoard(timeline, ["Bubonian`s pet"]);
        timeline.SetIdentity("Bubonian`s pet", IdentityKind.Pet, 100, "R5-owner");
        Assert.IsTrue(watch.AnyChanged(timeline), "the pet change is precisely the case the operator asked to keep");

        // Player -> Npc: a raider charmed, which re-routes her damage and her incoming hits. Strength is what decides the answer's
        // winner (R9-charm registers Strong in production), so the fixture uses a claim that wins the slot - asserting on a claim the
        // rule book correctly ignored would test nothing.
        timeline.SetIdentity("Reisil", IdentityKind.Player, 70, "R4-spell");
        watch.CaptureBoard(timeline, ["Reisil"]);
        timeline.SetIdentity("Reisil", IdentityKind.Npc, 120, "R9-charm");
        Assert.IsTrue(watch.AnyChanged(timeline), "player turning NPC is the other named case");
    }

    [TestMethod]
    [Description("An unset (the identity pane's Reset) answers Unknown again, and that is a change a board must follow.")]
    public void AWatchedNameFallingBackToUnknownFires()
    {
        var timeline = new EntityTimeline();
        timeline.SetIdentity("Sancus", IdentityKind.Player, 100, "R10-manual");

        var watch = new IdentityWatch();
        watch.CaptureBoard(timeline, ["Sancus"]);

        // A fresh timeline is what a pass after the claim was taken back reads: the name answers nothing.
        var cleared = new EntityTimeline();
        Assert.IsTrue(watch.AnyChanged(cleared), "taking a verdict back must refresh the boards it just un-decided");
    }

    [TestMethod]
    [Description("Nothing displayed means nothing can have changed on screen; inventing a rebuild for that puts the flicker straight back.")]
    public void AnEmptyWatchNeverFires()
    {
        var timeline = new EntityTimeline();
        for (var i = 0; i < 50; i++) timeline.SetIdentity($"mob {i}", IdentityKind.Npc, 100, "R6-npcdb");

        Assert.IsFalse(new IdentityWatch().AnyChanged(timeline));

        var watch = new IdentityWatch();
        watch.CaptureBoard(timeline, new List<string>());
        watch.CaptureSelection(timeline, []);
        Assert.IsFalse(watch.AnyChanged(timeline), "empty capture lists are a real state (no selection, empty board) and must not rebuild");
    }

    [TestMethod]
    [Description("Two owners, two slots: the builder refreshes the displayed names on a pool thread while the pane owns the selection. Neither may wipe the other.")]
    public void TheBoardAndSelectionSlotsSurviveEachOther()
    {
        var timeline = new EntityTimeline();
        timeline.SetIdentity("Zevfed", IdentityKind.Player, 100, "R3-chat");
        timeline.SetIdentity("Grimhawq", IdentityKind.Npc, 100, "R6-npcdb");

        var watch = new IdentityWatch();
        watch.CaptureBoard(timeline, ["Zevfed"]);
        watch.CaptureSelection(timeline, ["Grimhawq"]);
        Assert.AreEqual(2, watch.Count);

        // A flip on either side reaches it...
        timeline.SetIdentity("Grimhawq", IdentityKind.Pet, 100, "R24-petslot");
        Assert.IsTrue(watch.AnyChanged(timeline), "the selected row's own name is watched even if no board listed it");

        // ...and re-shooting the board half must not silently forget the selection half (that would blind the pane to its own row).
        watch.CaptureBoard(timeline, ["Zevfed"]);
        Assert.IsTrue(watch.AnyChanged(timeline), "the builder's capture must leave the pane's selection watched");

        watch.CaptureSelection(timeline, ["Grimhawq"]);
        Assert.IsFalse(watch.AnyChanged(timeline), "both halves re-shot against the same timeline read quiet - that is the state a rebuild leaves behind");
    }

    [TestMethod]
    [Description("The log line is what answers 'why did my board just move', so the change names itself with both answers.")]
    public void TheChangeNamesItself()
    {
        var timeline = new EntityTimeline();
        var watch = new IdentityWatch();
        watch.CaptureBoard(timeline, ["A bone walker"]);

        timeline.SetIdentity("A bone walker", IdentityKind.Npc, 60, "R14-shape");
        var moved = watch.FirstChanged(timeline);

        Assert.IsNotNull(moved);
        StringAssert.Contains(moved, "Unknown");
        StringAssert.Contains(moved, "Npc");
        StringAssert.Contains(moved, "A bone walker");
    }

    [TestMethod]
    [Description("Entity names are looked up without case everywhere else in this pipeline; a watch keyed ordinally would miss every flip the parser renamed.")]
    public void WatchingIsCaseInsensitiveLikeEveryOtherEntityKey()
    {
        var timeline = new EntityTimeline();
        var watch = new IdentityWatch();

        // Captured the way a heal/buff line writes it, flipped under the way a combat line interns it.
        watch.CaptureBoard(timeline, ["a bone walker"]);
        timeline.SetIdentity("A bone walker", IdentityKind.Npc, 60, "R14-shape");

        Assert.IsTrue(watch.AnyChanged(timeline), "the two spellings of one entity must be one watched name");
    }

    [TestMethod]
    [Description("A new capture is not a change to the old one's boards: Clear() blinds the watch until the next build re-arms it.")]
    public void ClearingTheWatchBlindsIt()
    {
        var before = new EntityTimeline();
        before.SetIdentity("Vexnia", IdentityKind.Player, 100, "R4-spell");
        var watch = new IdentityWatch();
        watch.CaptureBoard(before, ["Vexnia"]);

        var after = new EntityTimeline();
        after.SetIdentity("Vexnia", IdentityKind.Npc, 90, "R9-charm");
        Assert.IsTrue(watch.AnyChanged(after));

        watch.Clear();
        Assert.AreEqual(0, watch.Count);
        Assert.IsFalse(watch.AnyChanged(after), "a cleared watch never fires - the next build re-arms it with what it shows");
    }

    [TestMethod]
    [Description("This runs on every derive pass of a live raid, so a select-all-sized watch list must be a handful of dictionary reads.")]
    public void AWholeCapturesWorthOfNamesStaysCheap()
    {
        var timeline = new EntityTimeline();
        var names = new List<string>(2400);
        for (var i = 0; i < 2400; i++)
        {
            var name = $"name {i}";
            timeline.SetIdentity(name, i % 3 == 0 ? IdentityKind.Pet : IdentityKind.Player, 60, "R3-chat");
            names.Add(name);
        }

        var watch = new IdentityWatch();
        watch.CaptureBoard(timeline, names);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var pass = 0; pass < 20; pass++) Assert.IsFalse(watch.AnyChanged(timeline));
        sw.Stop();

        timeline.SetIdentity("name 1999", IdentityKind.Npc, 90, "R9-charm");
        Assert.IsTrue(watch.AnyChanged(timeline), "one flip out of 2,400 watched names must still be seen");
        Assert.IsTrue(sw.ElapsedMilliseconds < 200, $"20 passes over 2,400 watched names cost {sw.ElapsedMilliseconds} ms");
    }
}
