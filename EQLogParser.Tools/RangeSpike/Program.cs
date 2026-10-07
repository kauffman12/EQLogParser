using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

using EQLogParser;
using EQLogParser;

/*
 * RangeSpike — a measurement spike, NOT a feature. It is deliberately outside EQLogParser.sln so it can
 * neither ship by accident nor be mistaken for the design.
 *
 * The question: the stage ladder says one pass over eqlog_Incogitable parses in ~10.5 s, and running that
 * same pass four times concurrently over quarters of the file gives 3.9 s (2.7x) — but every worker there
 * re-reads and re-date-parses the WHOLE file, which a real shard would not. This tool answers the two
 * questions that decide whether sharding is worth designing at all:
 *
 *   1. How fast is a byte-range shard when it reads ONLY its own slice? (plan + parse)
 *   2. Does merging shards reproduce the sequential capture — same facts, same order, same totals? (merge + diff)
 *
 * (2) is the one that can kill the idea, and it is the reason this exists as code rather than as a slide:
 * the line parsers carry process state across lines (`_previousAction`, `_lastCrit`, `_delayCritRecord`,
 * `_slainTime`, MiscLineParser._randomPlayer/_lastLine) and every one of those is empty at a shard's first
 * line. Guessing which of them matters is what this measures, fact by fact.
 *
 * Commands:
 *   plan  --file F --parts N                     newline-aligned byte cut points (one per line, then file length)
 *   parse --file F --out S [--from B --to E]     parse that byte range into a fact spool, print ms/RSS
 *   merge --dir D --out S                        merge every shard spool in D into one, renumbering the sequence
 *   diff  --a S --b S                            elementwise compare of two spools (ids resolved to strings)
 */
internal static class Program
{
    private const string Magic = "EQLPSPL1";

    // Set by --ignore-seq: compare fact CONTENT and skip the sequence number. A shard re-verifying a name
    // spends sequence numbers the single pass did not, so every later row shifts by a constant — real work
    // for a merge to fix, but not a difference in what the capture saw.
    private static bool IgnoreSeq;

    private static int Main(string[] args)
    {
        var opt = Options.Parse(args.Skip(1).ToArray());
        return args.Length > 0 ? args[0] switch
        {
            "plan" => Plan(opt),
            "parse" => ParseShard(opt),
            "merge" => Merge(opt),
            "diff" => Diff(opt),
            "boards" => Boards(opt),
            _ => Help(),
        } : Help();
    }

    private static int Help()
    {
        Console.Error.WriteLine("range-spike plan|parse|merge|diff [--file F] [--out S] [--from B] [--to E] [--parts N] [--dir D] [--cwd DIR] [--player NAME]");
        return 2;
    }

    // ---- plan ------------------------------------------------------------------

    /*
     * Cut points must land on a line start, or every shard would have to guess about a partial first line.
     * `File.ReadLines` based sharding could instead discard a partial line per shard, but then ownership of
     * that line is decided by read-buffer luck; aligned offsets make "line i belongs to exactly one shard"
     * a property of the plan rather than of the reader.
     */
    private static int Plan(Options o)
    {
        var len = new FileInfo(o.File!).Length;
        var parts = Math.Max(1, o.Parts);
        var cuts = new List<long> { 0 };
        var targetIndex = 1;

        const int Chunk = 1 << 23;
        var buffer = new byte[Chunk];
        using (var fs = new FileStream(o.File!, FileMode.Open, FileAccess.Read, FileShare.Read, Chunk, FileOptions.SequentialScan))
        {
            long pos = 0;
            int read;
            while (targetIndex < parts && (read = fs.Read(buffer, 0, Chunk)) > 0)
            {
                while (targetIndex < parts && (targetIndex * len) / parts < pos + read)
                {
                    var target = (targetIndex * len) / parts - pos;
                    var found = -1;
                    for (var i = (int)Math.Max(0, target); i < read; i++)
                    {
                        if (buffer[i] == (byte)'\n') { found = i + 1; break; }
                    }
                    if (found < 0) break;   // a line longer than one chunk: keep the target for the next chunk
                    cuts.Add(pos + found);
                    targetIndex++;
                }
                pos += read;
            }
        }

        cuts.Add(len);
        foreach (var c in cuts) Console.WriteLine(c);
        return 0;
    }

    // ---- parse -----------------------------------------------------------------

    private static int ParseShard(Options o)
    {
        EnsureHost(o);

        DamageLineParser.ResetProcessState();
        PlayerRegistry.Instance.Clear();
        if (!string.IsNullOrEmpty(o.Seed)) SeedRegistry(o);
        // No legacy FightManager wiring: that engine is deleted. Deaths are recorded inline by
        // DamageLineParser.UpdateSlain, so a headless run has no per-fight object to hand the parser.

        var facts = new DamageFactTable(1 << 16);
        var heals = new HealFactTable(facts, 1 << 14);
        var mirror = new CombatCapture(facts, heals);
        mirror.Start();

        var from = o.From;
        var to = o.To > 0 ? o.To : new FileInfo(o.File!).Length;

        var sw = Stopwatch.StartNew();
        long lines = 0;
        double lastTs = double.NaN;

        using (var items = new BlockingCollection<LogReaderItem>(new ConcurrentQueue<LogReaderItem>(), 100_000))
        using (var processor = new LogProcessor(o.File!, new ChatSink(mirror), new ChatSink(mirror)))
        {
            processor.LinkTo(items);

            var batch = new List<LogReaderItem>(5000);
            foreach (var line in ReadRange(o.File!, from, to))
            {
                if (line.Length <= 28) continue;
                var dt = DateUtil.ParseStandardDate(line);
                if (dt == DateTime.MinValue) continue;
                var ts = DateUtil.ToDotNetSeconds(dt);
                lastTs = ts;
                lines++;
                batch.Add(new LogReaderItem(line, ts, false));
                if (batch.Count >= 5000)
                {
                    foreach (var item in batch) items.Add(item);
                    batch.Clear();
                }
            }
            foreach (var item in batch) items.Add(item);
            items.CompleteAdding();

            processor.Completion?.Wait();
        }

        mirror.Stop();
        sw.Stop();

        WriteSpool(o.Out!, o.File!, from, to, facts, heals);

        var p = Process.GetCurrentProcess();
        Console.WriteLine($"[spike] parse from={from} to={to} ms={sw.ElapsedMilliseconds:N0} lines={lines:N0} " +
                          $"damage={facts.FactCount:N0} heal={heals.HealCount:N0} death={facts.DeathCount:N0} " +
                          $"taunt={facts.TauntCount:N0} identity={facts.IdentityEventCount:N0} evidence={facts.EvidenceCount:N0} " +
                          $"names={facts.InternedNames.Count:N0} lastTs={lastTs:F0} " +
                          $"peakRSS={p.PeakWorkingSet64 / (1024 * 1024):N0}MB out={o.Out}");
        return 0;
    }

    /*
     * Byte-range line reader with exact ownership and no StreamReader buffering games: chunked decode, a
     * line that straddles a chunk carries its unfinished bytes into the next iteration (so no multi-byte
     * character is ever split), and boundaries come from `plan` so no line straddles a shard edge.
     */
    private static IEnumerable<string> ReadRange(string path, long from, long to)
    {
        const int Chunk = 1 << 22;
        var buffer = new byte[Chunk];
        List<byte>? leftover = null;
        var first = true;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, Chunk, FileOptions.SequentialScan);
        if (from > 0) fs.Seek(from, SeekOrigin.Begin);

        long consumed = from;
        int read;
        while (consumed < to && (read = fs.Read(buffer, 0, (int)Math.Min(Chunk, to - consumed))) > 0)
        {
            var batch = new List<string>(8192);
            var start = 0;
            if (first && from == 0 && read >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
            {
                start = 3;   // StreamReader would strip a leading BOM; do the same so both paths agree
            }

            while (true)
            {
                var nl = -1;
                for (var i = start; i < read; i++)
                {
                    if (buffer[i] == (byte)'\n') { nl = i; break; }
                }
                if (nl < 0) break;

                var end = nl;
                if (end > start && buffer[end - 1] == (byte)'\r') end--;

                string line;
                if (leftover is { Count: > 0 })
                {
                    var joined = new byte[leftover.Count + (end - start)];
                    leftover.CopyTo(joined);
                    Array.Copy(buffer, start, joined, leftover.Count, end - start);
                    leftover.Clear();
                    line = Encoding.UTF8.GetString(joined);
                }
                else
                {
                    line = Encoding.UTF8.GetString(buffer, start, end - start);
                }

                batch.Add(line);
                start = nl + 1;
            }

            if (start < read)
            {
                // Unterminated tail: hand it to the next chunk so a split multi-byte character survives.
                leftover ??= new List<byte>(1024);
                for (var i = start; i < read; i++) leftover.Add(buffer[i]);
            }

            consumed += read;
            first = false;
            foreach (var done in batch) yield return done;
        }

        if (leftover is { Count: > 0 }) yield return Encoding.UTF8.GetString(leftover.ToArray());
    }

    // ---- merge -----------------------------------------------------------------

    /*
     * Merging is not concatenation. Two things have to be rebuilt deliberately:
     *
     *  - the name pool. Each shard interned names into its own indices, so a raider is index 7 here and 402
     *    there. Keys are compared ignore-case and stored CapitalizeFirst exactly as DamageFactTable.InternName
     *    does — a merge with ordinal keys would be the "one entity, two ids" bug rebuilt at a new seam.
     *  - the sequence. Damage, heals, deaths, taunts, identity events and evidence share ONE counter in the
     *    sequential capture (that is how heal facts get fight attribution later without either table knowing
     *    about fights), so the merged stream interleaves all six arrays per shard by their local sequence and
     *    renumbers them with a single counter. Line order is shard order then local order, which for contiguous
     *    byte ranges is exactly the original file order.
     */
    private static int Merge(Options o)
    {
        var files = Directory.GetFiles(o.Dir!, "*.spool").OrderBy(f => ReadHeaderFrom(f)).ToList();
        if (files.Count == 0) { Console.Error.WriteLine($"no .spool files under {o.Dir}"); return 2; }

        var names = new Pool(StringComparer.OrdinalIgnoreCase);
        var subtypes = new Pool(StringComparer.Ordinal);
        var auxs = new Pool(StringComparer.OrdinalIgnoreCase);
        var spells = new Pool(StringComparer.Ordinal);

        var damage = new List<DamageFact>();
        var death = new List<DeathFact>();
        var taunt = new List<TauntFact>();
        var identity = new List<IdentityEvent>();
        var evidence = new List<EvidenceFact>();
        var heal = new List<HealFact>();

        long seq = 0;
        foreach (var file in files)
        {
            var s = Spool.Load(file);
            Console.WriteLine($"[merge] {Path.GetFileName(file)} from={s.From} names={s.Names.Count} subtypes={s.Subtypes.Count} auxs={s.Auxs.Count} spells={s.Spells.Count} damage={s.Damage.Length} heal={s.Heal.Length} evidence={s.Evidence.Length}");
            var nameMap = MapPool(s.Names, names);
            var subMap = MapPool(s.Subtypes, subtypes);
            var auxMap = MapPool(s.Auxs, auxs);
            var spellMap = MapPool(s.Spells, spells);

            // K-way merge of the six arrays by their local (shared) sequence.
            int[] heads = new int[6];
            while (true)
            {
                var bestKind = -1;
                var bestSeq = int.MaxValue;
                if (heads[0] < s.Damage.Length && s.Damage[heads[0]].Seq < bestSeq) { bestKind = 0; bestSeq = s.Damage[heads[0]].Seq; }
                if (heads[1] < s.Heal.Length && s.Heal[heads[1]].Seq < bestSeq) { bestKind = 1; bestSeq = s.Heal[heads[1]].Seq; }
                if (heads[2] < s.Death.Length && s.Death[heads[2]].Seq < bestSeq) { bestKind = 2; bestSeq = s.Death[heads[2]].Seq; }
                if (heads[3] < s.Taunt.Length && s.Taunt[heads[3]].Seq < bestSeq) { bestKind = 3; bestSeq = s.Taunt[heads[3]].Seq; }
                if (heads[4] < s.Identity.Length && s.Identity[heads[4]].Seq < bestSeq) { bestKind = 4; bestSeq = s.Identity[heads[4]].Seq; }
                if (heads[5] < s.Evidence.Length && s.Evidence[heads[5]].Seq < bestSeq) { bestKind = 5; bestSeq = s.Evidence[heads[5]].Seq; }
                if (bestKind < 0) break;

                var n = (int)++seq;
                try
                {
                switch (bestKind)
                {
                    case 0:
                    {
                        var f = s.Damage[heads[0]++];
                        damage.Add(new DamageFact(n, f.TimeS, Map(nameMap, f.AtkIdx), Map(nameMap, f.DefIdx), f.Total, f.TypeId, f.Flags, f.ModMask,
                            f.SubIdx == ushort.MaxValue ? ushort.MaxValue : (ushort)subMap[f.SubIdx]));
                        break;
                    }
                    case 1:
                    {
                        var f = s.Heal[heads[1]++];
                        heal.Add(new HealFact(n, f.TimeS, Map(nameMap, f.HealerIdx), Map(nameMap, f.HealedIdx), f.Total, f.OverTotal, f.TypeId, f.Flags, f.ModMask,
                            f.SubIdx == ushort.MaxValue ? ushort.MaxValue : (ushort)spellMap[f.SubIdx]));
                        break;
                    }
                    case 2:
                    {
                        var f = s.Death[heads[2]++];
                        death.Add(new DeathFact(n, f.TimeS, Map(nameMap, f.KilledIdx), Map(nameMap, f.KillerIdx)));
                        break;
                    }
                    case 3:
                    {
                        var f = s.Taunt[heads[3]++];
                        taunt.Add(new TauntFact(n, f.TimeS, Map(nameMap, f.NpcIdx)));
                        break;
                    }
                    case 4:
                    {
                        var f = s.Identity[heads[4]++];
                        identity.Add(new IdentityEvent(n, f.TimeS, Map(nameMap, f.NameIdx), f.Kind));
                        break;
                    }
                    default:
                    {
                        var f = s.Evidence[heads[5]++];
                        evidence.Add(new EvidenceFact(n, f.TimeS, Map(nameMap, f.NameIdx), f.Kind, Map(auxMap, f.AuxIdx)));
                        break;
                    }
                }
                }
                catch (Exception ex)
                {
                    var kindName = new[] { "damage", "heal", "death", "taunt", "identity", "evidence" }[bestKind];
                    Console.WriteLine($"[merge] INDEX FAIL file={Path.GetFileName(file)} kind={kindName} seq={bestSeq} global={n} " +
                                      $"namesPool={names.Count} subtypesPool={subtypes.Count} auxsPool={auxs.Count} spellsPool={spells.Count}");
                    throw new InvalidOperationException($"merge index failure ({kindName} at local seq {bestSeq})", ex);
                }
            }
        }

        WriteMerged(o.Out!, files.Count, names.Items, subtypes.Items, auxs.Items, spells.Items,
                    damage, death, taunt, identity, evidence, heal);

        Console.WriteLine($"[spike] merge shards={files.Count} seq={seq:N0} damage={damage.Count:N0} heal={heal.Count:N0} " +
                          $"death={death.Count:N0} taunt={taunt.Count:N0} identity={identity.Count:N0} evidence={evidence.Count:N0} " +
                          $"names={names.Count:N0} out={o.Out}");
        return 0;
    }

    /*
     * A name index of -1 is a real value, not a bug: DeathFact stores (short)-1 when the log line names no
     * killer at all. Passing it through the map would be an off-by-one into someone else's raider.
     */
    private static short Map(int[] map, short idx) => idx < 0 ? (short)-1 : (short)map[idx];

    private static int[] MapPool(List<string> source, Pool global)
    {
        var map = new int[source.Count];
        for (var i = 0; i < source.Count; i++) map[i] = global.Intern(source[i]);
        return map;
    }

    // ---- diff ------------------------------------------------------------------

    private static int Diff(Options o)
    {
        IgnoreSeq = o.IgnoreSeq;
        var a = Spool.Load(o.A!);
        var b = Spool.Load(o.B!);

        Console.WriteLine($"[diff] A={Path.GetFileName(o.A)} damage={a.Damage.Length:N0} heal={a.Heal.Length:N0} names={a.Names.Count:N0}");
        Console.WriteLine($"[diff] B={Path.GetFileName(o.B)} damage={b.Damage.Length:N0} heal={b.Heal.Length:N0} names={b.Names.Count:N0}");

        if (o.IgnoreSeq) Console.WriteLine("[diff] SEQ IGNORED — content only (names, amounts, flags, subtypes); drift-proof mode");

        var failed = false;
        failed |= Compare("damage", a.Damage.Length, b.Damage.Length,
            i => Describe(a, a.Damage[i]), i => Describe(b, b.Damage[i]), 8);
        failed |= Compare("heal", a.Heal.Length, b.Heal.Length,
            i => DescribeHeal(a, a.Heal[i]), i => DescribeHeal(b, b.Heal[i]), 8);
        failed |= Compare("death", a.Death.Length, b.Death.Length,
            i => $"{Seq(a.Death[i].Seq)}|{Name(a, a.Death[i].KilledIdx)}|{Name(a, a.Death[i].KillerIdx)}",
            i => $"{Seq(b.Death[i].Seq)}|{Name(b, b.Death[i].KilledIdx)}|{Name(b, b.Death[i].KillerIdx)}", 5);
        failed |= Compare("taunt", a.Taunt.Length, b.Taunt.Length,
            i => $"{Seq(a.Taunt[i].Seq)}|{Name(a, a.Taunt[i].NpcIdx)}", i => $"{Seq(b.Taunt[i].Seq)}|{Name(b, b.Taunt[i].NpcIdx)}", 5);
        failed |= CompareSets(a, b);

        FlagReport(a, b);

        var sumA = a.Damage.Sum(static f => (long)f.Total);
        var sumB = b.Damage.Sum(static f => (long)f.Total);
        var healA = a.Heal.Sum(static f => (long)f.Total);
        var healB = b.Heal.Sum(static f => (long)f.Total);
        Console.WriteLine($"[diff] damageTotal A={sumA:N0} B={sumB:N0} delta={sumB - sumA:N0} ({(sumA > 0 ? (sumB - sumA) * 100.0 / sumA : 0):F4}%)");
        Console.WriteLine($"[diff] healTotal   A={healA:N0} B={healB:N0} delta={healB - healA:N0} ({(healA > 0 ? (healB - healA) * 100.0 / healA : 0):F4}%)");
        Console.WriteLine(failed ? "[diff] RESULT: shards differ from sequential" : "[diff] RESULT: identical");
        return failed ? 1 : 0;
    }

    private static bool Compare(string what, int countA, int countB, Func<int, string> pickA, Func<int, string> pickB, int show)
    {
        var n = Math.Min(countA, countB);
        long mismatch = 0;
        for (var i = 0; i < n; i++)
        {
            var x = pickA(i);
            var y = pickB(i);
            if (x != y)
            {
                if (mismatch < show) Console.WriteLine($"[diff]   {what}[{i}] A={x} B={y}");
                mismatch++;
            }
        }

        var missing = Math.Abs(countA - countB);
        Console.WriteLine($"[diff] {what,-9} A={countA:N0} B={countB:N0} aligned={n:N0} mismatch={mismatch:N0} countDelta={missing:N0}");
        return mismatch > 0 || missing > 0;
    }

    private static string Describe(Spool s, DamageFact f) =>
        $"{Seq(f.Seq)}|{f.TimeS}|{Name(s, f.AtkIdx)}->{Name(s, f.DefIdx)}|{f.Total}|{f.TypeId}|{f.Flags}|{f.ModMask}|{Sub(s, f.SubIdx)}";

    private static string DescribeHeal(Spool s, HealFact f) =>
        $"{Seq(f.Seq)}|{f.TimeS}|{Name(s, f.HealerIdx)}->{Name(s, f.HealedIdx)}|{f.Total}|{f.OverTotal}|{f.TypeId}|{f.Flags}|{f.ModMask}|{Spell(s, f.SubIdx)}";

    /*
     * Which bits move at a shard boundary, and whether anything moves with them.
     *
     * This used to be the census that PROVED the boundary problem was a knowledge problem: the two side bits
     * (FlagAttkPlayerSide/FlagDefPlayerSide) were the registry's answer at capture time, flipped on 32 of 999
     * attacker names within ONE sequential pass, and nothing about a shard's text could reproduce them. Those bits are
     * now deleted from the engine (docs/DesignNotes.md -> "A fact carries what its line says"), which is prerequisite
     * (b) of the sharding design satisfied at source — so today a non-zero count on bits 2/3 means something was
     * stamped into a fact that the line cannot write, and THAT is the bug to chase. Bits 0/1 (AttkIsSpell, OwnerInLine)
     * are off the text and must be identical across workers; anything in 4-7 is an accident or a new flag that
     * reused a retired value.
     */
    private static void FlagReport(Spool a, Spool b)
    {
        var n = Math.Min(a.Damage.Length, b.Damage.Length);
        var flagsOnly = 0;
        long[] bitCount = new long[9];
        for (var i = 0; i < n; i++)
        {
            var x = a.Damage[i];
            var y = b.Damage[i];
            if (x.Flags == y.Flags) continue;
            var xor = (byte)(x.Flags ^ y.Flags);
            for (var bit = 0; bit < 8; bit++)
            {
                if ((xor & (1 << bit)) != 0) bitCount[bit]++;
            }

            var sameApartFromFlags = x.Total == y.Total && x.TypeId == y.TypeId && x.ModMask == y.ModMask && x.SubIdx == y.SubIdx
                && x.TimeS == y.TimeS && x.AtkIdx == y.AtkIdx && x.DefIdx == y.DefIdx;
            if (sameApartFromFlags) flagsOnly++;
        }

        Console.WriteLine($"[diff] damage flag bits moved: AttkIsSpell={bitCount[0]:N0} OwnerInLine={bitCount[1]:N0} " +
                          $"retired-side-bits={bitCount[2] + bitCount[3]:N0} (must be 0: deleted from the engine) " +
                          $"other={bitCount[4] + bitCount[5] + bitCount[6] + bitCount[7]:N0}");
        Console.WriteLine($"[diff] damage rows differing on flags: those differing ONLY on flags = {flagsOnly:N0}");
    }

    private static string Seq(int seq) => IgnoreSeq ? "-" : seq.ToString();

    // Identity events are idempotent knowledge ("this name is a verified player"), and a shard that starts
    // empty re-learns a raider its predecessor already knew. So the question is never "at which sequence" —
    // it is whether the merged knowledge came out the same.
    private static bool CompareSets(Spool a, Spool b)
    {
        var ka = a.Identity.Select(e => $"{Name(a, e.NameIdx)}|{e.Kind}").GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var kb = b.Identity.Select(e => $"{Name(b, e.NameIdx)}|{e.Kind}").GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var onlyA = ka.Keys.Except(kb.Keys).OrderBy(x => x).ToList();
        var onlyB = kb.Keys.Except(ka.Keys).OrderBy(x => x).ToList();
        var reVerified = kb.Keys.Where(k => ka.ContainsKey(k) && kb[k] > ka[k]).ToList();
        Console.WriteLine($"[diff] identity  A={a.Identity.Length:N0} B={b.Identity.Length:N0} onlyInA={onlyA.Count} onlyInB={onlyB.Count} reVerifiedNames={reVerified.Count}");
        if (onlyA.Count > 0) Console.WriteLine($"[diff]   only in sequential: {string.Join(", ", onlyA.Take(6))}");
        if (reVerified.Count > 0) Console.WriteLine($"[diff]   re-verified by a shard: {string.Join(", ", reVerified.Take(6))}");
        if (onlyB.Count > 0) Console.WriteLine($"[diff]   only in shards: {string.Join(", ", onlyB.Take(6))}");
        return onlyA.Count > 0;
    }

    private static string Name(Spool s, short idx) => idx < 0 ? "<none>" : s.Names[idx];

    private static string Sub(Spool s, ushort idx) => idx == ushort.MaxValue || idx >= s.Subtypes.Count ? "-" : s.Subtypes[idx];
    private static string Spell(Spool s, ushort idx) => idx == ushort.MaxValue || idx >= s.Spells.Count ? "-" : s.Spells[idx];

    /*
     * FlagCheck is GONE, and so is the mode that ran it. It existed to answer "is a side flag a fact about the line,
     * or about when the line was read?" — it measured 32 of 999 attacker names carrying both values of one side bit
     * inside a single sequential pass (Betebeatz: side=0 on 9,512 facts, side=1 on 5,342) and 28 healer names flipping
     * across 52,302 heal facts. The answer was "the latter", and the engine has since deleted those bits rather than
     * keep a reproducible-answer problem in the storage format; identity is derived per name by the rule book from
     * captured evidence. A new census is wanted only if bits 2/3 (damage) or 2/4 (heal) ever come back non-zero —
     * that would mean an opinion is being stamped into a fact again.
     */

    /*
     * The acceptance test a player would run: does each name's board number survive the sharding?
     *
     * Row-for-row comparison is the wrong question at this point — it reports every ordering difference as a
     * mismatch, and the legacy pipeline legitimately reorders (a delayed crit is emitted after the line that
     * caused it, so a shard boundary changes where its fact lands). What matters is the sum per name, which is
     * what every grid, bar and export prints. So aggregate and compare that.
     */
    private static int Boards(Options o)
    {
        var a = Spool.Load(o.A!);
        var b = Spool.Load(o.B!);
        var ka = Aggregate(a);
        var kb = Aggregate(b);

        var names = ka.Keys.Union(kb.Keys).ToList();
        var dmgDiff = new List<(string name, long delta)>();
        var healDiff = new List<(string name, long delta)>();
        long sumA = 0;
        long sumB = 0;
        foreach (var n in names)
        {
            ka.TryGetValue(n, out var x);
            kb.TryGetValue(n, out var y);
            sumA += x.Damage;
            sumB += y.Damage;
            if (x.Damage != y.Damage) dmgDiff.Add((n, y.Damage - x.Damage));
            if (x.Heal != y.Heal) healDiff.Add((n, y.Heal - x.Heal));
        }

        Report2("damage per name", names.Count, ka.Keys.Except(kb.Keys), kb.Keys.Except(ka.Keys), dmgDiff, sumA, sumB);
        Report2("heal per name", names.Count, [], [], healDiff, ka.Sum(k => k.Value.Heal), kb.Sum(k => k.Value.Heal));
        return 0;
    }

    private static void Report2(string what, int nameCount, IEnumerable<string> onlyA, IEnumerable<string> onlyB,
        List<(string name, long delta)> diffs, long sumA, long sumB)
    {
        var worst = diffs.OrderByDescending(d => Math.Abs(d.delta)).Take(5).ToList();
        Console.WriteLine($"[boards] {what}: names={nameCount:N0} differing={diffs.Count:N0} " +
                          $"onlyInA={onlyA.Count()} onlyInB={onlyB.Count()} total A={sumA:N0} B={sumB:N0} " +
                          $"delta={sumB - sumA:N0} ({(sumA > 0 ? Math.Abs(sumB - sumA) * 100.0 / sumA : 0):F6}%)");
        foreach (var d in worst.Where(d => d.delta != 0)) Console.WriteLine($"[boards]   {d.name}: {(d.delta > 0 ? "+" : "")}{d.delta:N0}");
    }

    private readonly record struct Board(long Damage, long Heal, long Hits);

    private static Dictionary<string, Board> Aggregate(Spool s)
    {
        var map = new Dictionary<string, Board>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in s.Damage)
        {
            if (f.AtkIdx < 0) continue;
            var n = Name(s, f.AtkIdx);
            map.TryGetValue(n, out var cur);
            map[n] = new Board(cur.Damage + f.Total, cur.Heal, cur.Hits + 1);
        }

        foreach (var f in s.Heal)
        {
            if (f.HealerIdx < 0) continue;
            var n = Name(s, f.HealerIdx);
            map.TryGetValue(n, out var cur);
            map[n] = new Board(cur.Damage, cur.Heal + f.Total, cur.Hits);
        }

        return map;
    }

    // ---- spool format ----------------------------------------------------------

    private static void WriteSpool(string path, string sourceFile, long from, long to,
        DamageFactTable facts, HealFactTable heals)
    {
        var subtypes = PrivateList<DamageFactTable>("_subtypes", facts);
        var auxs = PrivateList<DamageFactTable>("_auxs", facts);
        var spells = PrivateList<HealFactTable>("_spells", heals);

        using var w = NewWriter(path);
        w.Write(Magic);
        w.Write(sourceFile);
        w.Write(from);
        w.Write(to);
        WriteStrings(w, (IReadOnlyList<string>)facts.InternedNames);
        WriteStrings(w, subtypes);
        WriteStrings(w, auxs);
        WriteStrings(w, spells);
        WriteStructs(w, facts.Facts);
        WriteStructs(w, heals.Heals);
        WriteStructs(w, facts.Deaths);
        WriteStructs(w, facts.Taunts);
        WriteStructs(w, facts.IdentityEvents);
        WriteStructs(w, facts.Evidence);
    }

    private static void WriteMerged(string path, int shards, List<string> names, List<string> subtypes, List<string> auxs,
        List<string> spells, List<DamageFact> damage, List<DeathFact> death, List<TauntFact> taunt,
        List<IdentityEvent> identity, List<EvidenceFact> evidence, List<HealFact> heal)
    {
        using var w = NewWriter(path);
        w.Write(Magic);
        w.Write($"merged:{shards}");
        w.Write(0L);
        w.Write(0L);
        WriteStrings(w, names);
        WriteStrings(w, subtypes);
        WriteStrings(w, auxs);
        WriteStrings(w, spells);
        WriteStructs(w, damage.ToArray());
        WriteStructs(w, heal.ToArray());
        WriteStructs(w, death.ToArray());
        WriteStructs(w, taunt.ToArray());
        WriteStructs(w, identity.ToArray());
        WriteStructs(w, evidence.ToArray());
    }

    private static BinaryWriter NewWriter(string path) =>
        new(new BufferedStream(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20), 1 << 20));

    private static void WriteStrings(BinaryWriter w, IReadOnlyList<string> values)
    {
        w.Write(values.Count);
        foreach (var v in values) w.Write(v ?? string.Empty);
    }

    private static void WriteStructs<T>(BinaryWriter w, ReadOnlySpan<T> values) where T : unmanaged
    {
        w.Write(values.Length);
        w.Write(MemoryMarshal.AsBytes(values));
    }

    // The tables expose lookups but not their backing lists; reflection keeps the spike out of production.
    private static List<string> PrivateList<T>(string field, T target)
    {
        var f = typeof(T).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance);
        return (List<string>)f!.GetValue(target)!;
    }

    private static long ReadHeaderFrom(string path)
    {
        using var r = new BinaryReader(new BufferedStream(new FileStream(path, FileMode.Open, FileAccess.Read), 1 << 16));
        if (r.ReadString() != Magic) throw new InvalidDataException($"{path}: not a fact spool");
        r.ReadString();
        return r.ReadInt64();
    }

    private sealed class Spool
    {
        public string Source = "";
        public long From;
        public long To;
        public List<string> Names = [];
        public List<string> Subtypes = [];
        public List<string> Auxs = [];
        public List<string> Spells = [];
        public DamageFact[] Damage = [];
        public HealFact[] Heal = [];
        public DeathFact[] Death = [];
        public TauntFact[] Taunt = [];
        public IdentityEvent[] Identity = [];
        public EvidenceFact[] Evidence = [];

        public static Spool Load(string path)
        {
            var s = new Spool();
            using var r = new BinaryReader(new BufferedStream(new FileStream(path, FileMode.Open, FileAccess.Read), 1 << 20), Encoding.UTF8, leaveOpen: false);
            if (r.ReadString() != Magic) throw new InvalidDataException($"{path}: not a fact spool");
            s.Source = r.ReadString();
            s.From = r.ReadInt64();
            s.To = r.ReadInt64();
            s.Names = ReadStrings(r);
            s.Subtypes = ReadStrings(r);
            s.Auxs = ReadStrings(r);
            s.Spells = ReadStrings(r);
            s.Damage = ReadStructs<DamageFact>(r);
            s.Heal = ReadStructs<HealFact>(r);
            s.Death = ReadStructs<DeathFact>(r);
            s.Taunt = ReadStructs<TauntFact>(r);
            s.Identity = ReadStructs<IdentityEvent>(r);
            s.Evidence = ReadStructs<EvidenceFact>(r);
            return s;
        }

        private static List<string> ReadStrings(BinaryReader r)
        {
            var n = r.ReadInt32();
            var list = new List<string>(n);
            for (var i = 0; i < n; i++) list.Add(r.ReadString());
            return list;
        }

        private static T[] ReadStructs<T>(BinaryReader r) where T : unmanaged
        {
            var n = r.ReadInt32();
            var bytes = r.ReadBytes(n * Marshal.SizeOf<T>());
            var array = new T[n];
            bytes.AsSpan(0, Math.Min(bytes.Length, MemoryMarshal.AsBytes(array.AsSpan()).Length)).CopyTo(MemoryMarshal.AsBytes(array.AsSpan()));
            return array;
        }
    }

    private sealed class Pool(StringComparer comparer)
    {
        private readonly Dictionary<string, int> _map = new(comparer);
        public List<string> Items { get; } = [];
        public int Count => Items.Count;

        public int Intern(string value)
        {
            if (_map.TryGetValue(value, out var idx)) return idx;
            Items.Add(value);
            idx = Items.Count - 1;
            _map[value] = idx;
            return idx;
        }
    }

    /*
     * What a production worker would start with. MainWindow calls PlayerRegistry.Init(), which loads the
     * operator's own knowledge for this server — the verified roster (players.txt) and the pet map
     * (petmapping.txt) — before the first line is parsed. That seed is replayed here by hand because
     * ConfigUtil.ReadPlayers() builds its path with `\` joins, which cannot resolve on this Linux host;
     * the registry calls are the same ones Init makes, so the worker starts with the same knowledge.
     *
     * This exists to answer one question: how much of the shard-boundary damage is "the worker did not know
     * who the raiders were", as opposed to parser state that genuinely cannot be reconstructed.
     */
    private static void SeedRegistry(Options o)
    {
        var registry = PlayerRegistry.Instance;
        if (!string.IsNullOrEmpty(o.Player)) registry.AddVerifiedPlayer(o.Player, DateUtil.ToDotNetSeconds(DateTime.Now), true);

        var players = System.IO.Path.Combine(o.Seed!, "players.txt");
        if (File.Exists(players))
        {
            foreach (var raw in File.ReadAllLines(players))
            {
                var line = raw.Trim();
                if (line.Length <= 2) continue;
                var name = line;
                string? className = null;
                var parsed = 0d;
                var split = line.Split('=');
                if (split.Length == 2)
                {
                    name = split[0];
                    var parts = split[1].Split(',');
                    double.TryParse(parts[0], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out parsed);
                    if (parts.Length >= 2) className = parts[1];
                }

                if (name.Length <= 2 || "You".Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                registry.AddVerifiedPlayer(name, parsed, true);
                registry.SetDefaultPlayerClass(name, className, true);
            }
        }

        var mapping = System.IO.Path.Combine(o.Seed!, "petmapping.txt");
        if (File.Exists(mapping))
        {
            foreach (var raw in File.ReadAllLines(mapping))
            {
                var split = raw.Trim().Split('=');
                if (split.Length != 2 || split[0].Length <= 2) continue;
                var pet = split[0];
                var owner = "You".Equals(split[1], StringComparison.OrdinalIgnoreCase) ? o.Player : split[1];
                if (string.IsNullOrEmpty(owner)) continue;
                registry.AddVerifiedPlayer(owner, 0d, true);
                registry.AddVerifiedPet(pet, true);
                registry.AddPetToPlayer(pet, owner, true);
            }
        }

        Console.WriteLine($"[spike] seeded from {o.Seed}: players={registry.GetVerifiedPlayers().Count:N0} pets={registry.GetVerifiedPets().Count:N0}");
    }

    private static string? Abs(string? path) => string.IsNullOrEmpty(path) ? path : System.IO.Path.GetFullPath(path);

    private sealed class ChatSink(CombatCapture mirror) : IChatSink, ITriggerHook
    {
        public void Init() { }
        public void Add(ChatType chat) => mirror.HandleChat(chat);
        public void CheckQuickShare(ChatType chat, string action, double beginTime) { }
    }

    // Same host injection the test harness does: EQDataStore reads data/ off the working directory, and
    // the class-label lookup is normally supplied by the WPF app's resources.
    private static void EnsureHost(Options o)
    {
        var cwd = o.Cwd ?? throw new InvalidOperationException("--cwd DIR (the folder holding data/) is required");
        Environment.CurrentDirectory = cwd;
        CombatRecordLookup.ClassLabelByEnumName = ClassLabel;
        EQDataStore.Instance = new EQDataStore();

        if (!string.IsNullOrEmpty(o.Player))
        {
            ConfigUtil.PlayerName = o.Player;
        }
        else
        {
            var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileNameWithoutExtension(o.File!), @"^eqlog_(.+?)_.+$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) ConfigUtil.PlayerName = m.Groups[1].Value;
        }
    }

#pragma warning disable CS8603 // Possible null reference return.
    private static string ClassLabel(string resourceName) => resourceName switch
    {
        "WAR" => "Warrior", "CLR" => "Cleric", "PAL" => "Paladin", "RNG" => "Ranger",
        "SHD" => "Shadow Knight", "DRU" => "Druid", "MNK" => "Monk", "BRD" => "Bard",
        "ROG" => "Rogue", "SHM" => "Shaman", "NEC" => "Necromancer", "WIZ" => "Wizard",
        "MAG" => "Magician", "ENC" => "Enchanter", "BST" => "Beastlord", "BER" => "Berserker",
        _ => null,
    };
#pragma warning restore CS8603 // Possible null reference return.

    private sealed class Options
    {
        public string? File;
        public string? Out;
        public string? Dir;
        public string? A;
        public string? B;
        public string? Cwd;
        public string? Player;
        public string? Seed;
        public long From;
        public long To;
        public int Parts = 1;
        public bool IgnoreSeq;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (var i = 0; i < args.Length - 1; i += 2)
            {
                var key = args[i];
                var value = args[i + 1];
                switch (key)
                {
                    case "--file": o.File = value; break;
                    case "--out": o.Out = value; break;
                    case "--dir": o.Dir = value; break;
                    case "--a": o.A = value; break;
                    case "--b": o.B = value; break;
                    case "--cwd": o.Cwd = value; break;
                    case "--player": o.Player = value; break;
                    case "--seed": o.Seed = value; break;
                    case "--from": o.From = long.Parse(value); break;
                    case "--to": o.To = long.Parse(value); break;
                    case "--parts": o.Parts = int.Parse(value); break;
                    case "--ignore-seq": o.IgnoreSeq = true; i -= 1; break;
                    default: throw new ArgumentException($"unknown option {key}");
                }
            }

            // EnsureHost below moves the process working directory (EQDataStore reads data/ from it), so every
            // path handed in has to be absolute before that happens.
            o.File = Abs(o.File);
            o.Out = Abs(o.Out);
            o.Dir = Abs(o.Dir);
            o.A = Abs(o.A);
            o.B = Abs(o.B);
            o.Cwd = Abs(o.Cwd);
            o.Seed = Abs(o.Seed);
            return o;
        }
    }
}
