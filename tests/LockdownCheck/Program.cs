using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using QDay;

// Log.cs needs the game; Atlas.cs only needs this.
namespace QDay
{
    public static class Log { public static void Write(string s) => Console.WriteLine("    log: " + s); }
}

static class Program
{
    static int failures, thin;

    static void Check(bool ok, string what)
    {
        if (!ok) { failures++; Console.WriteLine($"FAIL {what}"); }
    }

    static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "params")
        {
            Console.WriteLine(Lockdown.BuildParams(LoadPlaceholder(), SyntheticInts(int.Parse(args[1]))).ToJsonString());
            return 0;
        }

        var g = LoadPlaceholder();
        Console.WriteLine($"district file: {g.Count} districts, {g.Links.Count} links, {g.Stops.Count} stops");
        Validation();
        Params(g);
        Decoding();
        Repairs();
        Synthetic(g);
        Bank(g);
        Wording(g);
        Runs(g);
        OfflineClient(g);
        ForceFieldChecks.Run(g, Resource("recorded.lockdown_districts.json"), Check);
        if (args.Contains("live")) LiveClient(g);

        Console.WriteLine(failures == 0 ? "all checks passed" : $"{failures} CHECKS FAILED");
        return failures == 0 ? 0 : 1;
    }

    static string Resource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new FileNotFoundException(name);
        using StreamReader r = new(s);
        return r.ReadToEnd();
    }

    static DistrictGraph LoadPlaceholder() => DistrictGraph.Parse(Resource("recorded.lockdown_districts.json"));

    static List<int> SyntheticInts(int seed)
    {
        Random r = new(seed);
        return Enumerable.Range(0, 64).Select(_ => r.Next(256)).ToList();
    }

    // ---- district file ---------------------------------------------------------------
    static void Validation()
    {
        const string districts = "\"districts\":[{\"id\":0,\"name\":\"a\",\"centre\":[0,0,0]},{\"id\":1,\"name\":\"b\",\"centre\":[1,0,0]},{\"id\":2,\"name\":\"c\",\"centre\":[2,0,0]}]";
        const string links = "\"links\":[{\"a\":0,\"b\":1,\"walls\":[]},{\"a\":1,\"b\":2,\"walls\":[]}]";
        Check(Accepts($"{{{districts},{links},\"start\":0,\"stops\":[2]}}"), "a good small file is accepted");
        var bad = new Dictionary<string, string>
        {
            ["ids out of order"] = $"{{\"districts\":[{{\"id\":1,\"name\":\"a\",\"centre\":[0,0,0]}},{{\"id\":0,\"name\":\"b\",\"centre\":[1,0,0]}}],\"links\":[{{\"a\":0,\"b\":1}}],\"start\":0,\"stops\":[1]}}",
            ["link to a missing district"] = $"{{{districts},\"links\":[{{\"a\":0,\"b\":3}}],\"start\":0,\"stops\":[2]}}",
            ["link to itself"] = $"{{{districts},\"links\":[{{\"a\":1,\"b\":1}},{{\"a\":0,\"b\":1}},{{\"a\":1,\"b\":2}}],\"start\":0,\"stops\":[2]}}",
            ["duplicate link, reversed"] = $"{{{districts},\"links\":[{{\"a\":0,\"b\":1}},{{\"a\":1,\"b\":0}},{{\"a\":1,\"b\":2}}],\"start\":0,\"stops\":[2]}}",
            ["a district with no centre"] = $"{{\"districts\":[{{\"id\":0}},{{\"id\":1,\"centre\":[1,0,0]}}],\"links\":[{{\"a\":0,\"b\":1}}],\"start\":0,\"stops\":[1]}}",
            ["start missing"] = $"{{{districts},{links},\"stops\":[2]}}",
            ["stops missing"] = $"{{{districts},{links},\"start\":0}}",
            ["no stops"] = $"{{{districts},{links},\"start\":0,\"stops\":[]}}",
            ["stop not a district"] = $"{{{districts},{links},\"start\":0,\"stops\":[9]}}",
            ["stop unreachable"] = $"{{{districts},\"links\":[{{\"a\":0,\"b\":1}}],\"start\":0,\"stops\":[2]}}",
            ["more than 20 districts"] = "{\"districts\":[" + string.Join(",", Enumerable.Range(0, 21).Select(i => $"{{\"id\":{i},\"centre\":[0,0,0]}}")) + "],\"links\":[{\"a\":0,\"b\":1}],\"start\":0,\"stops\":[1]}",
        };
        foreach (var kv in bad) Check(!Accepts(kv.Value), $"district file with {kv.Key} is rejected");
    }

    static bool Accepts(string json)
    {
        try { DistrictGraph.Parse(json); return true; }
        catch (Exception e) when (e is InvalidDataException or NullReferenceException or InvalidOperationException or FormatException) { return false; }
    }

    // ---- request ---------------------------------------------------------------------
    static void Params(DistrictGraph g)
    {
        var a = SyntheticInts(1);
        var pa = Lockdown.BuildParams(g, a);
        Check(pa.ToJsonString() == Lockdown.BuildParams(g, a).ToJsonString(), "the same numbers give the same params");
        Check(pa.ToJsonString() != Lockdown.BuildParams(g, SyntheticInts(2)).ToJsonString(), "different numbers give different params");
        for (int i = 0; i < a.Count; i++)
        {
            List<int> b = [.. a];
            b[i] ^= 1;
            Check(pa.ToJsonString() != Lockdown.BuildParams(g, b).ToJsonString(), $"changing Q-Day number {i} changes the params");
        }
        // The seed must not depend on the runtime: net48 in the game, net8 here. Pinned to a value worked by hand from FNV-1a.
        Check(Lockdown.Seed([0]) == unchecked((int)(2166136261u * 16777619u)), "seed is FNV-1a");

        Check((string)pa["mode"] == "emu", "mode is emu");
        Check(pa["backend_name"] is null, "no backend_name");
        Check((int)pa["num_qubits"] == g.Count, "num_qubits is the district count");
        Check(((JsonArray)pa["coupling_map"]).Count == g.Links.Count, "coupling_map has every link");

        var ops = ((JsonArray)pa["operations"]).Select(o => o.AsObject()).ToList();
        var pinned = g.Required.Distinct().OrderBy(q => q).ToList();
        Check(ops.Count == g.Count + g.Links.Count + pinned.Count, "operation count");
        Check(ops.Take(g.Count).All(o => (string)o["type"] == "bloch"), "leans come first");
        Check(ops.Skip(g.Count).Take(g.Links.Count).All(o => (string)o["type"] == "relationship"), "then links");
        Check(ops.Skip(g.Count + g.Links.Count).All(o => (string)o["type"] == "bloch"), "then the re-pin");
        Check(ops.Skip(g.Count + g.Links.Count).Select(o => (int)o["qubit"]).SequenceEqual(pinned), "the re-pin covers the start and stops");
        foreach (var o in ops.Where(o => (string)o["type"] == "bloch"))
        {
            double z = (double)o["paulis"]["Z"], x = (double)o["paulis"]["X"];
            Check(Math.Abs(x * x + z * z - 1) < 1e-9, "bloch targets are unit vectors");
            bool isPin = pinned.Contains((int)o["qubit"]);
            Check(isPin ? z == Lockdown.PinZ : z >= Lockdown.LeanMin && z <= Lockdown.LeanMax, "lean in range, pin at Z = 0.9");
        }
        foreach (var o in ops.Where(o => (string)o["type"] == "relationship"))
        {
            double s = (double)o["paulis"]["ZZ"];
            Check(s >= Lockdown.LinkMin && s <= Lockdown.LinkMax, "ZZ in range");
            Check((double)o["fraction"] == 0.5, "fraction 0.5");
        }
        Check(Throws(() => Lockdown.BuildParams(g, [])), "no numbers is refused, not silently seeded");
    }

    static bool Throws(Action a) { try { a(); return false; } catch (ArgumentException) { return true; } }

    static void Decoding()
    {
        var g = Graph(3, [(0, 1), (1, 2)], 0, [2]);
        // Measured with lockdown_probe.py: qubit 0 at Z = -1 and the rest at +1 gave "100".
        Check(Lockdown.Decode(g, "100").SequenceEqual([true, false]), "character 0 is qubit 0");
        Check(Lockdown.Decode(g, "001").SequenceEqual([false, true]), "last character is the last qubit");
        Check(Throws(() => Lockdown.Decode(g, "10")), "a wrong-length bitstring is refused");
    }

    // ---- repairs ---------------------------------------------------------------------
    static DistrictGraph Graph(int n, (int a, int b)[] links, int start, int[] stops)
    {
        DistrictGraph g = new() { Start = start, Stops = [.. stops] };
        for (int i = 0; i < n; i++) g.Names.Add($"d{i}");
        foreach (var (a, b) in links) g.Links.Add(new() { A = a, B = b });
        return g;
    }

    /// <summary>The true minimum number of sealed links to open, by trying every subset.</summary>
    static int BruteMinimum(DistrictGraph g, string bits)
    {
        var sealedLinks = Lockdown.Decode(g, bits);
        var sealedIdx = Enumerable.Range(0, sealedLinks.Length).Where(i => sealedLinks[i]).ToList();
        int best = int.MaxValue;
        for (int mask = 0; mask < 1 << sealedIdx.Count; mask++)
        {
            int size = System.Numerics.BitOperations.PopCount((uint)mask);
            if (size >= best) continue;
            var open = sealedLinks.Select(s => !s).ToArray();
            for (int k = 0; k < sealedIdx.Count; k++) if ((mask >> k & 1) != 0) open[sealedIdx[k]] = true;
            if (Lockdown.HasRoute(g, open)) best = size;
        }
        return best;
    }

    static void Repairs()
    {
        var cases = new (string name, DistrictGraph g, string bits, int want)[]
        {
            ("a chain, every border sealed", Graph(3, [(0, 1), (1, 2)], 0, [2]), "010", 2),
            ("a short way and a long way round", Graph(6, [(0, 1), (1, 5), (0, 2), (2, 3), (3, 4), (4, 5)], 0, [5]), "001011", 1),
            ("two stops sharing a border", Graph(4, [(0, 1), (1, 2), (1, 3)], 0, [2, 3]), "0111", 1),
            ("two stops on separate branches", Graph(5, [(0, 1), (0, 2), (1, 3), (2, 4)], 0, [3, 4]), "01111", 2),
            ("the start's area reaches most of the way", Graph(5, [(0, 1), (1, 2), (2, 3), (3, 4)], 0, [4]), "00001", 1),
        };
        foreach (var (name, g, bits, want) in cases)
        {
            var layout = Lockdown.Choose(g, [new Measurement { Bitstring = bits, Count = 10, Probability = 1 }]);
            Check(layout.Breach, $"repair case '{name}' needed a repair");
            Check(layout.Opened.Count == want, $"repair case '{name}': opened {layout.Opened.Count}, minimum is {want}");
            Check(want == BruteMinimum(g, bits), $"repair case '{name}': hand-worked minimum disagrees with brute force");
            Check(Lockdown.HasRoute(g, layout.Sealed.Select(s => !s).ToArray()), $"repair case '{name}' has a route");
        }

        // Per-stop shortest paths are not a guaranteed global minimum, so measure how often it costs extra.
        Random rng = new(11);
        int tried = 0, extra = 0, extraLinks = 0;
        while (tried < 3000)
        {
            int n = rng.Next(4, 9);
            HashSet<(int, int)> set = [];
            for (int i = 1; i < n; i++) set.Add((rng.Next(i), i));   // a spanning tree, so a route exists
            for (int i = rng.Next(n); i > 0; i--)
            {
                int a = rng.Next(n), b = rng.Next(n);
                if (a != b) set.Add((Math.Min(a, b), Math.Max(a, b)));
            }
            var g = Graph(n, set.ToArray(), 0, Enumerable.Range(1, n - 1).OrderBy(_ => rng.Next()).Take(rng.Next(1, 4)).ToArray());
            var bits = new string(Enumerable.Range(0, n).Select(_ => rng.Next(2) == 0 ? '0' : '1').ToArray());
            var layout = Lockdown.Choose(g, [new Measurement { Bitstring = bits, Count = 1 }]);
            if (!layout.Breach) continue;
            tried++;
            Check(Lockdown.HasRoute(g, layout.Sealed.Select(s => !s).ToArray()), "random repair has a route");
            int min = BruteMinimum(g, bits);
            Check(layout.Opened.Count >= min, "random repair opened fewer links than the brute-force minimum");
            if (layout.Opened.Count > min) { extra++; extraLinks += layout.Opened.Count - min; }
        }
        Console.WriteLine($"repairs on {tried} random small graphs: {extra} opened more than the minimum, {extraLinks} extra links in all");
    }

    // ---- the route guarantee -----------------------------------------------------------
    static void AssertLayout(DistrictGraph g, IReadOnlyCollection<Measurement> ms, string what)
    {
        var l = Lockdown.Choose(g, ms);
        Check(l.Sealed?.Length == g.Links.Count, $"{what}: sealed has one entry per link");
        Check(Lockdown.HasRoute(g, l.Sealed.Select(s => !s).ToArray()), $"{what}: has a route");
        if (l.Rank == 0)
        {
            Check(l.SealedCount == 0, $"{what}: nothing usable means nothing sealed");
            return;
        }
        var decoded = Lockdown.Decode(g, l.Bitstring);
        if (l.Breach)
        {
            Check(l.Opened.Count > 0 && l.Rank == 1, $"{what}: a repair starts from the likeliest and opens something");
            for (int i = 0; i < decoded.Length; i++) Check(l.Sealed[i] == (decoded[i] && !l.Opened.Contains(i)), $"{what}: a repair only opens listed links");
        }
        else
        {
            Check(l.Opened.Count == 0, $"{what}: no repair opens nothing");
            Check(l.Sealed.SequenceEqual(decoded), $"{what}: an accepted layout is the engine's bitstring unchanged");
            // Below the floor is only allowed when no candidate passed and the likeliest already had a route.
            if (l.SealedCount < Lockdown.MinSealedFraction * g.Links.Count)
            {
                thin++;
                Check(l.Rank == 1, $"{what}: a thin layout must be the likeliest");
                foreach (var m in ms.Where(m => m.Bitstring?.Length == g.Count))
                {
                    var d = Lockdown.Decode(g, m.Bitstring);
                    Check(!(Lockdown.HasRoute(g, d.Select(x => !x).ToArray()) && d.Count(x => x) >= Lockdown.MinSealedFraction * g.Links.Count), $"{what}: a thin layout was taken over one that passed");
                }
            }
        }
    }

    static void Synthetic(DistrictGraph g)
    {
        Random rng = new(21);
        int repaired = 0, empty = 0;
        thin = 0;
        for (int run = 0; run < 10000; run++)
        {
            List<Measurement> ms = [];
            for (int i = rng.Next(0, 21); i > 0; i--)
            {
                int len = rng.Next(50) == 0 ? rng.Next(0, 30) : g.Count;   // now and then the wrong length
                double lean = rng.NextDouble();
                ms.Add(new()
                {
                    Bitstring = new string(Enumerable.Range(0, len).Select(_ => rng.NextDouble() < lean ? '1' : '0').ToArray()),
                    Count = rng.Next(1, 500),
                    Probability = rng.NextDouble(),
                });
            }
            AssertLayout(g, ms, $"synthetic set {run}");
            var l = Lockdown.Choose(g, ms);
            if (l.Breach) repaired++;
            if (l.Rank == 0) empty++;
        }
        Check(Lockdown.Choose(g, null).Rank == 0, "null measurements still give a layout");
        Check(Lockdown.Choose(g, [new Measurement { Bitstring = new string('x', g.Count), Count = 9 }]).Rank == 0, "non-binary bitstrings are ignored");
        var order = new[] { new Measurement { Bitstring = new string('0', g.Count), Count = 5 }, new Measurement { Bitstring = new string('1', g.Count), Count = 5 } };
        Check(Lockdown.Choose(g, order).Bitstring == Lockdown.Choose(g, order.Reverse().ToList()).Bitstring, "tied counts do not depend on input order");
        Console.WriteLine($"10000 synthetic sets: {repaired} repaired, {empty} with nothing usable, {thin} below the seal floor");
    }

    static void Bank(DistrictGraph g)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var bank = assembly.GetManifestResourceNames().Where(n => n.StartsWith("recorded.lockdown_") && char.IsDigit(n["recorded.lockdown_".Length])).OrderBy(n => n).ToList();
        Check(bank.Count >= 20, $"bank has about 20 files, found {bank.Count}");
        HashSet<string> layouts = [];
        int repaired = 0;
        thin = 0;
        foreach (var file in bank)
        {
            var root = JsonNode.Parse(Resource(file));
            var o = root["result"]["output"];
            Check((string)o["mode"] == "emu", $"{file}: recorded on emu");
            Check((string)root["job_id"] != null, $"{file}: has a job id");
            var coupling = ((JsonArray)o["coupling_map"]).Select(e => ((int)e[0], (int)e[1])).ToList();
            Check(coupling.SequenceEqual(g.Links.Select(l => (l.A, l.B))), $"{file}: matches the district file. If the file changed, rerun tools/record_lockdowns.py");
            var ms = ((JsonArray)o["measurements"]).Select(m => new Measurement { Bitstring = (string)m["bitstring"], Count = (int)m["count"], Probability = (double)m["probability"] }).ToList();
            AssertLayout(g, ms, file);
            var l = Lockdown.Choose(g, ms);
            layouts.Add(string.Concat(l.Sealed.Select(s => s ? '1' : '0')));
            if (l.Breach) repaired++;
        }
        Console.WriteLine($"bank: {bank.Count} files, {layouts.Count} distinct layouts, {repaired} needed a repair, {thin} below the seal floor");
    }

    // ---- wording: a simulation, with the hardware named only where it is true --------------
    static void Wording(DistrictGraph g)
    {
        var ms = new List<Measurement> { new() { Bitstring = "1" + new string('0', g.Count - 1), Count = 7, Probability = 0.0123 } };
        var l = Lockdown.Choose(g, ms);
        l.Backend = "aer"; l.JobId = "job"; l.TargetSource = Lockdown.TargetSource(false, true, "ibm_marrakesh");
        var hud = string.Join("\n", Lockdown.HudLines(l));
        Check(hud.Contains("simulation"), "HUD calls it a simulation");
        Check(hud.Contains("layout 1 of 1"), "HUD shows layout k of n");
        Check(!hud.Contains('%') && !hud.Contains("p="), "HUD shows no probability");
        var repaired = Lockdown.Choose(g, [new Measurement { Bitstring = string.Concat(Enumerable.Range(0, g.Count).Select(i => g.Required.Contains(i) ? (i == g.Start ? '0' : '1') : '0')), Count = 3 }]);
        Check(repaired.Breach, "wording case needs a repair");
        Check(!string.Join("\n", Lockdown.HudLines(repaired)).Contains("breach", StringComparison.OrdinalIgnoreCase), "HUD does not mention a repair");
        Check(string.Join("\n", Lockdown.LogLines(g, repaired)).Contains("lockdown breach: opened"), "log lists every link a repair opened");
        Check(string.Join("\n", Lockdown.LogLines(g, l)).Contains("simulation"), "log calls it a simulation");
        Check(string.Join("\n", Lockdown.LogLines(g, l)).Contains("p=1.23%"), "log has the probability");
        Check(Lockdown.TargetSource(false, true, "ibm_marrakesh") == "Q-Day numbers from ibm_marrakesh", "certified numbers name the hardware");
        Check(!Lockdown.TargetSource(false, false, "ibm_marrakesh").Contains("marrakesh"), "uncertified numbers do not claim hardware");
        Check(!Lockdown.TargetSource(true, true, "ibm_marrakesh").Contains("marrakesh"), "the bank does not claim hardware");
    }

    // ---- the real Atlas client ----------------------------------------------------------
    // ---- one playthrough: the five paths step 7 relies on -------------------------------
    static void Runs(DistrictGraph g)
    {
        using Atlas atlas = new("", true);   // offline, so no credits; hand-built results stand in for live ones
        var ints = SyntheticInts(3);
        AtlasResult Certified() => new() { Ints = ints, Certified = true, Backend = "ibm_marrakesh" };
        var bits = new string('0', g.Count);
        AtlasResult Live(string name, string job = "live-job") => new()
        {
            Name = name, Kind = "lockdown", Status = "done", Backend = "aer", JobId = job,
            Measurements = [new Measurement { Bitstring = bits, Count = 9, Probability = 1 }],
        };

        // A result arriving before the escape is kept and used, and names the hardware.
        LockdownRun run = new(g);
        run.Submit(atlas, Certified());
        run.Store(Live(run.JobName));
        Check(run.Begin() && run.Layout.JobId == "live-job", "a stored result is used");
        Check(run.Layout.TargetSource == "Q-Day numbers from ibm_marrakesh", "certified numbers name the hardware on the HUD");
        Check(run.Layout.Backend == "aer", "the layout reports the simulator as its backend");

        // After the layout is settled a late result changes nothing.
        run.Store(Live(run.JobName, "late-job"));
        Check(run.Layout.JobId == "live-job", "a late result is ignored");

        // A job from an aborted run can't land in the next one, and names differ per run.
        run.Submit(atlas, Certified());
        var first = run.JobName;
        run.Reset();
        run.Submit(atlas, Certified());
        Check(first != run.JobName && first != null, "each run gets its own job name");
        run.Store(Live(first, "stale-job"));
        run.Begin();
        Check(run.Layout.JobId != "stale-job", "a result from an earlier run is ignored");
        Check(run.Layout.TargetSource == "recorded bank", "with no result in hand the escape uses the bank");

        // Recorded() has no numbers: F6, F7 and the 180 s hardware timeout all come through here.
        run.Submit(atlas, AtlasResult.Recorded());
        Check(run.JobName == null, "the recorded Q-Day result sends no job");
        run.Begin();
        Check(run.Layout.TargetSource == "recorded bank" && run.Layout.JobId != null && run.Layout.Rank > 0, "the recorded Q-Day result goes to the bank");

        // A failed job goes to the bank too.
        run.Submit(atlas, Certified());
        run.Store(new() { Name = run.JobName, Kind = "lockdown", Status = "error", Message = "boom" });
        run.Begin();
        Check(run.Layout.TargetSource == "recorded bank", "a failed job goes to the bank");

        // Uncertified numbers never claim hardware.
        run.Submit(atlas, new() { Ints = ints, Certified = false, Backend = "ibm_marrakesh" });
        run.Store(Live(run.JobName));
        run.Begin();
        Check(run.Layout.TargetSource.StartsWith("local random"), "uncertified numbers are labelled local random");

        // Reset clears the layout, as F10 and a failed mission do through BeginIdle.
        run.Reset();
        Check(run.Layout == null && run.JobName == null, "reset clears the run");

        // A refused district file skips the escape rather than stopping it.
        LockdownRun none = new(null);
        none.Submit(atlas, Certified());
        Check(!none.Begin(), "no district file means no escape");

        while (atlas.Take() != null) { }
    }

    static AtlasResult Await(Atlas atlas, int seconds)
    {
        for (int i = 0; i < seconds * 10; i++)
        {
            if (atlas.Take() is { } r) return r;
            Thread.Sleep(100);
        }
        return null;
    }

    static void OfflineClient(DistrictGraph g)
    {
        using Atlas atlas = new("", true);
        atlas.Lockdown("lockdown", Lockdown.BuildParams(g, SyntheticInts(1)));
        var r = Await(atlas, 10);
        Check(r is { Status: "done", Kind: "lockdown", Name: "lockdown", FromBank: true }, $"offline answers from the bank: {r}");
        if (r?.Status != "done") return;
        Check(r.Measurements.Count > 0 && r.JobId != null && r.Backend == "aer", "bank result has measurements, job and backend");
        AssertLayout(g, r.Measurements, "offline client");
        Console.WriteLine($"offline: {r}");
    }

    static void LiveClient(DistrictGraph g)
    {
        var token = Environment.GetEnvironmentVariable("MOTH_API_TOKEN");
        using Atlas atlas = new(token, false);
        var ints = SyntheticInts(Environment.TickCount);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        atlas.Lockdown("lockdown", Lockdown.BuildParams(g, ints));
        var r = Await(atlas, 130);
        Check(r is { Status: "done", FromBank: false }, $"live job returned: {r}");
        if (r?.Status != "done") return;
        var l = Lockdown.Choose(g, r.Measurements);
        l.JobId = r.JobId; l.Backend = r.Backend; l.TargetSource = Lockdown.TargetSource(false, false, null);
        AssertLayout(g, r.Measurements, "live client");
        Console.WriteLine($"live: {r} in {clock.Elapsed.TotalSeconds:0}s");
        foreach (var line in Lockdown.LogLines(g, l)) Console.WriteLine("  " + line);
        foreach (var line in Lockdown.HudLines(l)) Console.WriteLine("  HUD: " + line);
    }
}
