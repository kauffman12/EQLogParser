using System.Diagnostics;

using EQLogParser;

namespace EQLogParser;

/*
 * The measurement that killed a rule: "a known or suspected NPC keeps hitting this name, so the name is one of ours".
 * It looked like the cheapest unused signal in the capture — every fight writes dozens of "Virul bites Breshanna" lines
 * — and it was going to be the fix for the Unknown defenders that `EntityTimeline.IsRaidVictimAt` lets into the tank
 * board on the strength of exactly that reasoning.
 *
 * It has nothing to claim. Asked over three captures against a classified timeline, the population a rule like that
 * could fire on is not "mobs biting unnamed players" but a residue too small to deserve a rule:
 *
 *   capture                       hit facts     defenders no rule placed      names in "a mob is hitting it"
 *   eqlog_Incogitable_xegony      1,727,942     4,323 (0.25 %) / 1.69 B       52
 *   eqlog_Kizant_xegony-8-20-23   3,429,601       356 (0.01 %) / 231 M          1
 *   eqlog_Kizant_xegony-09-03-26  4,585,905         0 (0.00 %) / 0              0
 *
 * and of the names in that bucket, **none** is on the operator's own player roster, while the top of it is
 * `Herald of the Outer Brood`, `War Trainer Prime` and `A scalewrought soldier` — mobs. Two reasons, both structural:
 *
 *   1. When a mob hits something here, that something is usually another mob or somebody's swarm, and R6 (npcs.txt),
 *      R14 (article shape) and R1-target have already placed those names. What stays unplaced is mostly *mobs the NPC
 *      database does not know*, not people.
 *   2. Our people are named before they take a hit that matters — R3 presence/chat, R17 consume lines, R4 spells, R15
 *      healing, the registry seed. A raider unknown to every one of those is rare enough that the hint's yield
 *      measures zero.
 *
 * So no new rule. `IsRaidVictimAt` stays exclusion for the reason written on it (a live derive cannot wait for evidence
 * about a person being face-checked by three mobs), and its measured cost — 86 facts / 15.6 M on Incogitable — sits on
 * that method rather than here.
 *
 * What this file keeps is the instrument, because the *interesting* failure it found is not about the rule at all:
 * `PipelineHarness` hands back a timeline carrying the registry seeds and NOT the rule table. Run the census against
 * that state and "a mob is hitting 372 unplaced names" reads as a real signal, the idea looks urgent, and none of it is
 * true — the first version of this file reported exactly that, and the numbers on `IsRaidVictimAt` and in AGENTS.md
 * inherited it. Classify (RegistrySeed + ClassificationRules, the way DeriveEngine derives) before asking an identity
 * question.
 *
 * The assertion is a coverage guard rather than a rule: the unplaced-defender residue has to stay under 2 % of hit
 * facts, so a change that quietly stops R6/R14/R15 placing mobs gets caught here instead of in a tank board somebody
 * squints at.
 *
 * Runs only when EQLP_DERIVE_LOGS names one or more logs (`:` or `;` separated):
 *   EQLP_DERIVE_LOGS=local/eqlog_Incogitable_xegony.txt dotnet test --filter Census_HitsFromNpcs --logger "console;verbosity=detailed"
 * Opt-in because it ingests whole captures. Ground truth scores against EQLogParser/data/npcs.txt (38,423 NPC names —
 * firing on one of those is a false claim) and local/EQLPData/config/xegony/players.txt (past-session rosters — firing
 * on one of those is right). Both are low recall, high precision; the `unread` bucket is printed to be read.
 */
[TestClass]
[DoNotParallelize]
public class HitByNpcCensusTest
{
    private const string DefaultNpcFile = "EQLogParser/data/npcs.txt";
    private const string DefaultPlayerFile = "local/EQLPData/config/xegony/players.txt";

    // What one partner did to (or with) one unplaced name, plus what that name did back.
    private sealed class Candidate
    {
        public long Hits;
        public long Damage;
        public readonly HashSet<string> Partners = new(StringComparer.OrdinalIgnoreCase);
        public long EdgesAtPlayer;   // its own swings at names already called people: raider behaviour
        public long EdgesAtNpc;      // its own swings at NPCs/pets: mob-or-pet behaviour
    }

    /*
     * The two lists live in the repository and a test runs from its output directory, so resolve them from the
     * solution root rather than asking whoever set the env var to remember the current directory.
     */
    private static string Resolve(string path)
    {
        if (Path.IsPathRooted(path)) return path;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, path);
            if (File.Exists(candidate)) return candidate;
        }

        return path;
    }

    private static HashSet<string> LoadNames(string path)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        path = Resolve(path);
        if (!File.Exists(path)) return names;

        // players.txt lines are "Name=numericId,Class"; npcs.txt is one bare name per line.
        foreach (var line in File.ReadLines(path))
        {
            var name = line.Split('=')[0].Trim();
            if (name.Length > 0) names.Add(name);
        }

        return names;
    }

    [TestMethod]
    public void Census_HitsFromNpcsAsIdentityEvidence()
    {
        var raw = Environment.GetEnvironmentVariable("EQLP_DERIVE_LOGS");
        if (string.IsNullOrEmpty(raw)) Assert.Inconclusive("set EQLP_DERIVE_LOGS=<log>[:<log>] to run the census");

        var npcFile = Environment.GetEnvironmentVariable("EQLP_NPC_FILE") ?? DefaultNpcFile;
        var playerFile = Environment.GetEnvironmentVariable("EQLP_PLAYER_FILE") ?? DefaultPlayerFile;
        var npcs = LoadNames(npcFile);
        var players = LoadNames(playerFile);

        Assert.IsTrue(npcs.Count > 0 || players.Count > 0, $"neither {Resolve(npcFile)} nor {Resolve(playerFile)} could be read");
        Console.WriteLine($"[hitcensus] ground truth: {npcs.Count:N0} npc names ({Resolve(npcFile)}), "
                          + $"{players.Count:N0} roster names ({Resolve(playerFile)})");

        foreach (var path in raw.Split([':', ';'], StringSplitOptions.RemoveEmptyEntries))
            RunOne(path.Trim(), npcs, players);
    }

    private static void RunOne(string path, HashSet<string> npcs, HashSet<string> players)
    {
        path = Resolve(path);
        Assert.IsTrue(File.Exists(path), $"EQLP_DERIVE_LOGS names a file that does not exist: {path}");
        PlayerRegistry.Instance.Clear();

        var sw = Stopwatch.StartNew();
        var run = PipelineHarness.RunFileDerived(path);

        /*
         * The app's classify step, replayed over the finished capture (RegistrySeed then ClassificationRules on a
         * fresh timeline — DeriveEngine does exactly this inside every derive). Skipping it is the trap this census
         * walked into first: the timeline the harness hands back carries only registry seeds, so almost every MOB is
         * Unknown, "a mob is hitting it" matches 372 names that are overwhelmingly mobs (`A scalewrought soldier`,
         * `Herald of the Outer Brood`), and the whole rule reads as nonsense. Every question below is asked against
         * the classification the product actually runs.
         */
        var timeline = new EntityTimeline();
        RegistrySeed.Apply(timeline, run.Facts, run.Facts.Facts.Length > 0 ? run.Facts.Facts[0].TimeS : 0,
                           run.Facts.Facts.Length > 0 ? run.Facts.Facts[^1].TimeS : 0);
        ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);

        Console.WriteLine($"[hitcensus] === {Path.GetFileName(path)} ingested+classified in {sw.ElapsedMilliseconds:N0} ms, "
                          + $"{run.Facts.Facts.Length:N0} damage facts");

        // Identity is stamped from -infinity (SetIdentity's default effectiveFrom), so for any name a rule ever
        // touched "unknown at that second" == "unknown at the end". That is what makes one pass enough here.
        var buckets = new Dictionary<string, Dictionary<string, Candidate>>(StringComparer.Ordinal)
        {
            ["mob is hitting it"] = new(StringComparer.OrdinalIgnoreCase),
            ["our people are hitting it"] = new(StringComparer.OrdinalIgnoreCase),
            ["neither name is placed"] = new(StringComparer.OrdinalIgnoreCase),
        };

        long facts = 0, damage = 0;
        foreach (var fact in run.Facts.Facts)
        {
            if (!LabelTypes.IsHit(fact.TypeId)) continue;

            var attacker = run.Facts.NameOf(fact.AtkIdx);
            var defender = run.Facts.NameOf(fact.DefIdx);
            if (attacker is null || defender is null || attacker == defender) continue;
            if (timeline.IdentityAt(defender, fact.TimeS) is not IdentityKind.Unknown) continue;

            facts++;
            damage += fact.Total;

            var bucket = timeline.Identity(attacker) switch
            {
                IdentityKind.Npc or IdentityKind.Pet => buckets["mob is hitting it"],
                IdentityKind.Player or IdentityKind.Merc => buckets["our people are hitting it"],
                _ => buckets["neither name is placed"],
            };

            if (!bucket.TryGetValue(defender, out var cand))
                cand = bucket[defender] = new Candidate();

            cand.Hits++;
            cand.Damage += fact.Total;
            cand.Partners.Add(attacker);
        }

        long hitFacts = 0;
        foreach (var fact in run.Facts.Facts)
        {
            if (LabelTypes.IsHit(fact.TypeId)) hitFacts++;
        }

        Console.WriteLine($"[hitcensus] unplaced defenders: {facts:N0} of {hitFacts:N0} hit facts "
                          + $"({100.0 * facts / Math.Max(1, hitFacts):F2} %) worth {damage:N0}");
        Assert.IsTrue(facts <= hitFacts * 0.02,
                      $"{Path.GetFileName(path)}: {facts:N0} of {hitFacts:N0} hit facts landed on a defender no rule placed "
                      + "(> 2 %). Something stopped R6/R14/R15 placing mobs — the buckets below say who.");

        foreach (var (label, bucket) in buckets)
        {
            Behaviour(timeline, run.Facts, bucket);
            Report(timeline, bucket, npcs, players, label);
        }
    }

    // What each candidate does back — the same discriminator shape R15 uses ("any swing back above the share vetoes
    // it"): a raider swings at NPCs, a mob or a pet swings at people.
    private static void Behaviour(EntityTimeline timeline, DamageFactTable facts, Dictionary<string, Candidate> bucket)
    {
        if (bucket.Count == 0) return;

        foreach (var fact in facts.Facts)
        {
            if (!LabelTypes.IsHit(fact.TypeId)) continue;

            var attacker = facts.NameOf(fact.AtkIdx);
            if (attacker is null || !bucket.TryGetValue(attacker, out var cand)) continue;

            var target = facts.NameOf(fact.DefIdx);
            if (target is null) continue;

            switch (timeline.Identity(target))
            {
                case IdentityKind.Player or IdentityKind.Merc:
                    cand.EdgesAtPlayer++;
                    break;
                case IdentityKind.Npc or IdentityKind.Pet:
                    cand.EdgesAtNpc++;
                    break;
            }
        }
    }

    private static void Report(EntityTimeline timeline, Dictionary<string, Candidate> candidates,
                               HashSet<string> npcs, HashSet<string> players, string label)
    {
        Console.WriteLine($"[hitcensus] --- {label}: {candidates.Count:N0} distinct names");

        foreach (var minPartners in new[] { 1, 2, 3 })
        {
            foreach (var minHits in new[] { 1, 5, 25, 200 })
            {
                long right = 0, wrong = 0, unread = 0;

                foreach (var (name, cand) in candidates)
                {
                    if (cand.Hits < minHits || cand.Partners.Count < minPartners) continue;
                    if (npcs.Contains(name)) wrong++;
                    else if (players.Contains(name)) right++;
                    else unread++;
                }

                Console.WriteLine($"[hitcensus]   partners>={minPartners} hits>={minHits,-3}: {right,4} roster-confirmed(RIGHT) "
                                  + $"{wrong,4} in npcs.txt (WRONG) {unread,5} unread");
            }
        }

        // The top of the pile: what firing at a modest dial would actually claim, name by name.
        foreach (var (name, cand) in candidates.OrderByDescending(kv => kv.Value.Hits).Take(20))
        {
            var verdict = npcs.Contains(name) ? "npcs.txt" : players.Contains(name) ? "ROSTER" : "-";
            Console.WriteLine($"[hitcensus]     {name,-28} hits={cand.Hits,-7} partners={cand.Partners.Count,-4} "
                              + $"dmg={cand.Damage,-12:N0} atPeople={cand.EdgesAtPlayer,-6} atNpc={cand.EdgesAtNpc,-7} {verdict}");
        }
    }
}
