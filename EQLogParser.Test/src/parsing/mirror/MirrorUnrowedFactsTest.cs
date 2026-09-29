using EQLogParser.Mirror;

namespace EQLogParser;

/*
 * The gap between "every fact inside the window" and "the union of the rows".
 *
 * The damage meter is going to be a reader over derived rows: its session picks rows, MirrorStats totals them, and the
 * fight list's select-all picks the same rows and gets the same digits. That equivalence has one precondition worth
 * knowing as a number instead of a hope — some captured facts may belong to NO row, in which case they exist in the
 * capture and in a time-window sum but in no selection, and "the meter and the list agree" would be true only because
 * both leave the same thing out. Whether that residue needs a visible line ("unassigned: 12,000") or is noise floor is
 * a product decision, and this file is where its size gets answered.
 *
 * Two counters already exist inside a snapshot and are NOT this gap:
 *   - UnroutedFactCount — the projection handed the fact to a row, and neither board wanted it (a mob biting another
 *     mob). It reached a row; it is just nobody's damage column.
 *   - DamageFactCount / TankingFactCount — what the boards carry.
 * The gap measured here is one level out: ordinals the projection never handed to ANY row, which is why these tests put
 * a spy in front of the sink and subtract.
 *
 * What CI pins (both cheap, both synthetic):
 *   - a fact no engagement ever opens is counted as gap rather than silently absent — the residue is visible in the
 *     counters, or it is invisible in the numbers, and the second version is the bug;
 *   - an ordinary pull has NO gap: raid-on-mob and mob-on-raid are all claimed, so "select these fights" says
 *     everything that happened during them.
 *
 * What runs only when asked (EQLP_MIRROR_UNROWED=path[;path2], full ingest of each capture): the residue per real
 * evening — how many facts, how much damage, what share of the capture, and the name pairs it sits between, because
 * "0.3 % of facts" is a number you cannot act on but "`Bithika` hit `Useless` 4,000 times" tells you whether a raid
 * was beating its own charmed pet and deserves to be on the board at all.
 */
[TestClass]
public class MirrorUnrowedFactsTest
{
    private const double T0 = 1_000;

    private static DamageFactTable BuildFacts(params (string Atk, string Def, long Dmg, double T, byte Label)[] rows)
    {
        var facts = new DamageFactTable(64);
        var seq = 0;
        foreach (var (atk, def, dmg, t, label) in rows)
        {
            var a = facts.InternName(atk);
            var d = facts.InternName(def);
            facts.AddFact(new DamageFact(seq++, (long)(T0 + t), a, d, total: (uint)dmg, typeId: label,
              flags: 0, modMask: 0, subIdx: ushort.MaxValue));
        }
        return facts;
    }

    // The projection pass with a spy ahead of the index sink: which ordinals did ANY row claim?
    private static (List<DerivedFight> Rows, MirrorDamageIndex Index, HashSet<int> Claimed) Derive(
      DamageFactTable facts, EntityTimeline timeline, bool applyRules = true)
    {
        if (applyRules)
        {
            ClassificationRules.Apply(facts, timeline);
        }

        var index = new MirrorDamageIndex();
        var claimed = new HashSet<int>();
        var rows = FightProjection.Build(facts, timeline, (fact, ordinal, owner, target) =>
        {
            claimed.Add(ordinal);
            index.OnFact(fact, ordinal, owner, target);
        });

        return (rows, index, claimed);
    }

    private static long UnrowedCount(DamageFactTable facts, HashSet<int> claimed)
        => Enumerable.Range(0, facts.FactCount).Count(i => !claimed.Contains(i));

    // ---- what CI holds ----

    [TestMethod]
    public void AFactNoEngagementOpensIsCountedRatherThanVanishing()
    {
        // Two raid members trading friendly fire with no hostile name anywhere in the log. The facts are real — they
        // happened, they are captured — and no row can hold them, so this is exactly the shape of the residue.
        var facts = BuildFacts(
            ("Illuminai", "Bithika", 250, 0, LabelTypes.Melee),
            ("Bithika", "Illuminai", 175, 1, LabelTypes.Melee));

        var timeline = new EntityTimeline();
        timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        timeline.SetIdentity("Bithika", IdentityKind.Player, RuleStrength.Strong, "R3-presence");

        var (rows, index, claimed) = Derive(facts, timeline);

        Assert.AreEqual(0, rows.Count, "two players hitting each other is not an encounter");
        Assert.AreEqual(0, claimed.Count, "no row claimed a fact");
        Assert.AreEqual(facts.FactCount, UnrowedCount(facts, claimed),
          "the residue is COUNTED: invisible in the counters means invisible on every board, which is the bug");
        Assert.AreEqual(0L, index.DamageFactCount + index.TankingFactCount + index.UnroutedFactCount);
    }

    [TestMethod]
    public void AnOrdinaryPullLeavesNoGapAtAll()
    {
        // The shape a session is made of: the raid hitting a mob, and that mob hitting back. Every fact lands on a
        // row, so "select these fights" says everything that happened during them — no unassigned line needed.
        var facts = BuildFacts(
            ("Illuminai", "Grimling", 500, 0, LabelTypes.Melee),
            ("Bithika", "Grimling", 300, 1, LabelTypes.Dd),
            ("Grimling", "Illuminai", 120, 2, LabelTypes.Melee),
            ("Grimling", "Bithika", 90, 3, LabelTypes.Melee));

        var timeline = new EntityTimeline();
        timeline.SetIdentity("Illuminai", IdentityKind.Player, RuleStrength.Strong, "R3-presence");
        timeline.SetIdentity("Bithika", IdentityKind.Player, RuleStrength.Strong, "R3-presence");

        var (rows, index, claimed) = Derive(facts, timeline);

        Assert.AreEqual(1, rows.Count, "one mob, one row");
        Assert.AreEqual(0, UnrowedCount(facts, claimed), "nothing happened that no row can show");
        Assert.AreEqual(facts.FactCount, index.DamageFactCount + index.TankingFactCount + index.UnroutedFactCount,
          "and the three counters account for the whole capture");
    }

    // The test host's working directory is the output folder, so a repo-relative log path only works if it is walked
    // up to. Absolute paths pass straight through.
    private static string Resolve(string path)
    {
        if (Path.IsPathRooted(path) || File.Exists(path)) return path;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, path);
            if (File.Exists(candidate)) return candidate;
        }

        return path;
    }

    // ---- the number itself, on real captures ----

    [TestMethod]
    public void Census_ResiduePerRealCapture()
    {
        var paths = Environment.GetEnvironmentVariable("EQLP_MIRROR_UNROWED");
        if (string.IsNullOrWhiteSpace(paths))
        {
            Assert.Inconclusive("set EQLP_MIRROR_UNROWED=<log>[;<log>...] to run the residue census (full ingest each)");
        }

        foreach (var raw in paths.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var path = Resolve(raw);
            if (!File.Exists(path)) Assert.Fail($"EQLP_MIRROR_UNROWED points at nothing: {raw}");

            var run = PipelineHarness.RunFileWithMirror(path);
            var facts = run.Facts;

            // The harness already ran the rules for its own pass; reuse its timeline rather than claiming twice.
            var (rows, index, claimed) = Derive(facts, run.Timeline, applyRules: false);

            long unrowed = 0;
            long unrowedDamage = 0;
            var byPair = new Dictionary<string, long>();
            var byAttacker = new Dictionary<string, (long Facts, long Damage, bool PetAt)>();
            var all = facts.Facts;
            for (var i = 0; i < facts.FactCount; i++)
            {
                if (claimed.Contains(i)) continue;

                var f = all[i];
                unrowed++;
                unrowedDamage += f.Total;
                var attacker = facts.NameOf(f.AtkIdx);
                var key = $"{attacker} -> {facts.NameOf(f.DefIdx)}";
                byPair.TryGetValue(key, out var hit);
                byPair[key] = hit + 1;
                byAttacker.TryGetValue(attacker, out var sofar);
                // Our own pet at the time of THIS fact: that is the shape to look for in the residue, because an
                // attacker the projection reads as mob-side and whose victim is a boss gets dropped as "two mobs on
                // each other" — which also drops our registered pet's damage off every row. See the notes.
                byAttacker[attacker] = (sofar.Facts + 1, sofar.Damage + f.Total, sofar.PetAt || run.Timeline.IsOurPetAt(attacker, f.TimeS));
            }

            var routed = index.DamageFactCount + index.TankingFactCount;
            Console.WriteLine($"[census] {Path.GetFileName(path)}");
            Console.WriteLine($"[census]   facts {facts.FactCount:N0} | rows {rows.Count:N0} | damage-side {index.DamageFactCount:N0}"
              + $" | tanking-side {index.TankingFactCount:N0} | unrouted(Neither) {index.UnroutedFactCount:N0}");
            Console.WriteLine($"[census]   UNROWED {unrowed:N0} facts ({(facts.FactCount == 0 ? 0 : 100.0 * unrowed / facts.FactCount):0.##} %),"
              + $" {unrowedDamage:N0} damage; claimed+unrowed == total: {claimed.Count + unrowed == facts.FactCount}");
            foreach (var pair in byPair.OrderByDescending(p => p.Value).Take(10))
            {
                Console.WriteLine($"[census]     {pair.Value,8:N0} x {pair.Key}");
            }

            /*
             * Grouped the other way, because that is where the answer lives: which ONE name owns this residue, and what
             * did the rules call it. A raider whose identity reads Npc loses its whole evening — every fact it lands on a
             * boss is mob-on-mob to the projection and files nowhere — and that shows up here as one attacker with
             * billions of damage, not as a 0.8 % blur.
             */
            Console.WriteLine($"[census]   distinct unrowed attackers: {byAttacker.Count:N0} (top by damage, with what the timeline says it is)");
            foreach (var entry in byAttacker.OrderByDescending(p => p.Value.Damage).Take(12))
            {
                var kind = run.Timeline.IdentityWithSource(entry.Key, out var source);
                Console.WriteLine($"[census]     {entry.Value.Damage,15:N0} dmg {entry.Value.Facts,8:N0} facts  {entry.Key}"
                  + $"  [{kind} / {source ?? "-"} | our pet at the time: {entry.Value.PetAt}]");
            }

            Assert.IsTrue(claimed.Count + unrowed == facts.FactCount, "the spy accounts for every ordinal");
            Assert.IsTrue(routed + index.UnroutedFactCount + unrowed == facts.FactCount,
              "routed + unrouted + unrowed is the whole capture — no fact is in two buckets or none");
        }
    }
}
