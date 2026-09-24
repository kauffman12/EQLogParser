using EQLogParser.Mirror;

namespace EQLogParser;

// Phase 2 exit gate (docs/batch-parsing-plan.md): cold rules must rebuild zone identity from the
// log alone, measured against the registry files the engine itself accumulated for the same log
// (players.txt / petmapping.txt). Local-only run: EQLP_MIRROR_LOG points at a log,
// EQLP_MIRROR_REGISTRY at the zone's config directory. No logs or registries are committed.
//
// Ground truth is one-sided on purpose: the registry only knows who was verified, so names the
// rules call player that the registry lacks are reported (mostly visitors), while every registry
// name seen in the log must be recovered — that recall number is the metric.
[TestClass]
public class RegistryRebuildTest
{
    [TestMethod]
    public void ColdRules_RegistryRebuild_OnRealLogEnv()
    {
        var path = Environment.GetEnvironmentVariable("EQLP_MIRROR_LOG");
        var registryDir = Environment.GetEnvironmentVariable("EQLP_MIRROR_REGISTRY");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)
          || string.IsNullOrEmpty(registryDir) || !Directory.Exists(registryDir))
        {
            Console.WriteLine("[rebuild] EQLP_MIRROR_LOG/EQLP_MIRROR_REGISTRY not set — skipping the real-log rebuild run.");
            return;
        }

        var run = PipelineHarness.RunFileWithMirror(path);
        var facts = run.Facts;

        var timeline = new EntityTimeline();
        var outcome = ClassificationRules.Apply(facts, timeline);

        var logNames = new HashSet<string>(facts.InternedNames, StringComparer.Ordinal);
        var truthPlayers = ReadRegistryNames(Path.Combine(registryDir, "players.txt"));
        var truthPets = ReadRegistryNames(Path.Combine(registryDir, "petmapping.txt"));
        truthPlayers.IntersectWith(logNames);
        truthPets.ExceptWith(truthPlayers);
        truthPets.IntersectWith(logNames);

        static bool PlayerSide(IdentityKind k) => k is IdentityKind.Player or IdentityKind.Merc;

        var playerHits = truthPlayers.Where(n => PlayerSide(timeline.IdentityWithSource(n, out _))).ToHashSet(StringComparer.Ordinal);
        var petHits = truthPets.Where(n => timeline.IdentityWithSource(n, out _) == IdentityKind.Pet).ToHashSet(StringComparer.Ordinal);

        // Owner-path pet metric: registry keys are pet NAMES that the log often never prints —
        // the pair (Pet=Owner) counts as rebuilt when the owner is recovered AND the log proved
        // an owned pet of his ("X`s pet|warder" attacker strings interned by R5-owner evidence).
        var ownedStrings = logNames.Where(n => n.Contains("`s ", StringComparison.Ordinal)).ToList();
        var ownerPairs = (from line in File.ReadLines(Path.Combine(registryDir, "petmapping.txt"))
                          where line.Contains('=')
                          let i = line.IndexOf('=')
                          select (Pet: line[..i].Trim(), Owner: line[(i + 1)..].Trim())).ToList();
        var ownerPathHits = ownerPairs.Count(p => PlayerSide(timeline.IdentityWithSource(p.Owner, out _))
            && ownedStrings.Any(s => s.StartsWith(p.Owner + "`s", StringComparison.OrdinalIgnoreCase)));

        // R13 output: mercs are exactly what the legacy pipeline auto-verifies into players.txt
        // (plain join lines pass isPossiblePlayerName) — the audit list for registry pollution.
        var mercs = logNames.Where(n => timeline.IdentityWithSource(n, out _) == IdentityKind.Merc).OrderBy(n => n).ToList();

        // R0: the log's own author is player by construction; not registry ground truth.
        var localNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ChatType.You };
        if (!string.IsNullOrEmpty(ConfigUtil.PlayerName)) localNames.Add(ConfigUtil.PlayerName);

        var falsePlayers = logNames
            .Where(n => PlayerSide(timeline.IdentityWithSource(n, out _)) && !truthPlayers.Contains(n) && !localNames.Contains(n))
            .ToList();

        Console.WriteLine($"[rebuild] {Path.GetFileName(path)}: names={logNames.Count} evidence={facts.EvidenceCount} facts={facts.FactCount}");
        Console.WriteLine($"[rebuild] players in log={truthPlayers.Count} recovered={playerHits.Count} recall={(truthPlayers.Count == 0 ? 1 : (double)playerHits.Count / truthPlayers.Count):P1}");
        Console.WriteLine($"[rebuild] pets in log={truthPets.Count} recovered={petHits.Count} recall={(truthPets.Count == 0 ? 1 : (double)petHits.Count / truthPets.Count):P1}; owner-path pairs proven={ownerPathHits}/{ownerPairs.Count}");
        Console.WriteLine($"[rebuild] mercs (R13): {mercs.Count} ({string.Join(", ", mercs.Take(20))})");
        Console.WriteLine($"[rebuild] target-frame conflicts: {outcome.Conflicts.Count} ({string.Join(", ", outcome.Conflicts.Take(10))})");
        Console.WriteLine($"[rebuild] classified player-side but absent from registry: {falsePlayers.Count} (sample: {string.Join(", ", falsePlayers.OrderBy(n => n).Take(25))})");

        var misses = truthPlayers.Except(playerHits)
            .Select(n =>
            {
                var kind = timeline.IdentityWithSource(n, out var s);
                return $"{n} [{kind}:{s ?? "none"}]";
            });
        Console.WriteLine($"[rebuild] player misses: {string.Join(", ", misses.OrderBy(x => x).Take(40))}");

        var sources = logNames
            .Select(n => timeline.IdentityWithSource(n, out var s) == IdentityKind.Unknown ? "(unknown)" : s!)
            .GroupBy(s => s)
            .OrderByDescending(g => g.Count());
        Console.WriteLine("[rebuild] provenance: " + string.Join(", ", sources.Select(g => $"{g.Key}={g.Count()}")));

        // sanity floor: a rebuild that cannot recover most of the zone's known players is broken,
        // not merely conservative. The precision side is deliberately not asserted (see header).
        Assert.IsTrue(truthPlayers.Count == 0 || playerHits.Count / (double)truthPlayers.Count >= 0.6,
            $"cold recall {playerHits.Count}/{truthPlayers.Count} below the 60% floor");
    }

    // "name=timestamp[,Class]" / "pet=owner" - name is everything before the first '='.
    private static HashSet<string> ReadRegistryNames(string file)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(file)) return names;
        foreach (var line in File.ReadLines(file))
        {
            var name = line.Split('=')[0].Trim();
            if (name.Length > 0) names.Add(name);
        }
        return names;
    }
}
