// Escape lockdown, the logic: which borders are sealed on this run. No ScriptHookVDotNet
// types in this file, so tests/LockdownCheck can run it without the game.
//
// The districts are one qubit each and each link between two districts is a graph-v1
// relationship. The Q-Day numbers set the targets, graph-v1 (on the emu simulator) samples
// bitstrings from them, and each bitstring is one candidate lockdown: a link is sealed when
// its two districts measured differently. Choose() takes the likeliest candidate that keeps
// a route open, so the engine picks the layout and the route is still guaranteed.
//
// The target settings were measured in tools/lockdown_probe.py. They are sensitive:
//   - all-positive ZZ collapses to all zeros, which seals nothing, so the range goes negative;
//   - relationships before the leans looks healthy but the correlations stop following the
//     targets, so the engine no longer shapes anything; keep the order in BuildParams;
//   - bloch targets set a direction only, so each one is sent as a unit vector.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QDay;

public sealed class Wall
{
    public float X, Y, Z, Heading, Width;
}

public sealed class DistrictLink
{
    public int A, B;
    public List<Wall> Walls = [];
}

public sealed class DistrictGraph
{
    public const int MaxDistricts = 20;   // graph-v1's qubit limit

    public List<string> Names = [];
    public List<float[]> Centres = [];    // x, y, z per district
    public List<DistrictLink> Links = [];
    public int Start;
    public List<int> Stops = [];
    public int Count => Names.Count;

    /// <summary>The start and every stop: the districts that must end up connected.</summary>
    public IEnumerable<int> Required => new[] { Start }.Concat(Stops);

    public static DistrictGraph Parse(string json)
    {
        var root = JsonNode.Parse(json) ?? throw new InvalidDataException("district file is empty");
        DistrictGraph g = new();

        var districts = root["districts"] as JsonArray ?? throw new InvalidDataException("no districts");
        for (int i = 0; i < districts.Count; i++)
        {
            int id = districts[i]["id"].GetValue<int>();
            if (id != i) throw new InvalidDataException($"district ids must be 0..n-1 in order, found {id} at position {i}");
            g.Names.Add(districts[i]["name"]?.GetValue<string>() ?? $"district {i}");
            if (districts[i]["centre"] is not JsonArray { Count: 3 } c) throw new InvalidDataException($"district {i} needs a centre of x, y, z");
            g.Centres.Add([c[0].GetValue<float>(), c[1].GetValue<float>(), c[2].GetValue<float>()]);
        }

        var links = root["links"] as JsonArray ?? throw new InvalidDataException("no links");
        foreach (var l in links)
        {
            DistrictLink link = new() { A = l["a"].GetValue<int>(), B = l["b"].GetValue<int>() };
            if (l["walls"] is JsonArray walls)
                foreach (var w in walls)
                {
                    var pos = w["pos"] as JsonArray ?? throw new InvalidDataException($"link {link.A}-{link.B}: wall has no pos");
                    if (pos.Count != 3) throw new InvalidDataException($"link {link.A}-{link.B}: wall pos needs x, y, z");
                    link.Walls.Add(new()
                    {
                        X = pos[0].GetValue<float>(),
                        Y = pos[1].GetValue<float>(),
                        Z = pos[2].GetValue<float>(),
                        Heading = w["heading"]?.GetValue<float>() ?? 0f,
                        Width = w["width"]?.GetValue<float>() ?? 12f,
                    });
                }
            g.Links.Add(link);
        }

        g.Start = root["start"]?.GetValue<int>() ?? throw new InvalidDataException("no start");
        var stops = root["stops"] as JsonArray ?? throw new InvalidDataException("no stops");
        foreach (var s in stops) g.Stops.Add(s.GetValue<int>());

        g.Validate();
        return g;
    }

    public void Validate()
    {
        if (Count < 2 || Count > MaxDistricts) throw new InvalidDataException($"need 2 to {MaxDistricts} districts, found {Count}");
        HashSet<(int, int)> seen = [];
        foreach (var l in Links)
        {
            if (l.A < 0 || l.A >= Count || l.B < 0 || l.B >= Count) throw new InvalidDataException($"link {l.A}-{l.B} refers to a district that does not exist");
            if (l.A == l.B) throw new InvalidDataException($"link {l.A}-{l.B} joins a district to itself");
            if (!seen.Add((Math.Min(l.A, l.B), Math.Max(l.A, l.B)))) throw new InvalidDataException($"link {l.A}-{l.B} is listed twice");
        }
        if (Links.Count == 0) throw new InvalidDataException("no links");
        if (Stops.Count == 0) throw new InvalidDataException("no stops");
        foreach (var q in Required)
            if (q < 0 || q >= Count) throw new InvalidDataException($"start or stop {q} is not a district");

        // With every border open there must be a route, or no layout can ever have one.
        var open = new bool[Links.Count];
        for (int i = 0; i < open.Length; i++) open[i] = true;
        if (!Lockdown.HasRoute(this, open)) throw new InvalidDataException("the start cannot reach every stop even with every border open");
    }
}

/// <summary>One entry of graph-v1's output.measurements.</summary>
public sealed class Measurement
{
    public string Bitstring;
    public int Count;
    public double Probability;
}

/// <summary>The lockdown this run uses, and how it was chosen.</summary>
public sealed class LockdownLayout
{
    public bool[] Sealed;              // per link, same order as DistrictGraph.Links
    public string Bitstring;           // null when the engine gave nothing usable
    public int Rank;                   // 1 = the likeliest bitstring; 0 when there was none
    public int Candidates;             // how many bitstrings came back (20 unless the distribution was narrow)
    public double Probability;
    public bool Breach;                // a repair opened sealed borders
    public List<int> Opened = [];      // links the repair opened
    public int SealedCount => Sealed.Count(s => s);

    // Set by the caller, for the HUD and the log.
    public string JobId, Backend, TargetSource;
}

public static class Lockdown
{
    // The lockdown is a simulation by decision: fake takes 13-20+ minutes at 20 qubits, longer
    // than the riot, and qpu would spend IBM time on every run. The hardware part is the Q-Day
    // numbers that set the targets. Live and bank both run on this mode.
    public const string Mode = "emu";
    public const int Shots = 4096;
    public const double MinSealedFraction = 1.0 / 3;

    public const double LinkMin = -0.7, LinkMax = 0.9;   // ZZ target per link
    public const double LinkFraction = 0.5;              // partial rotation per link
    public const double LeanMin = -0.6, LeanMax = 0.6;   // Z target per ordinary district
    public const double PinZ = 0.9;                      // Z target for the start and stops

    // ---- request -------------------------------------------------------------------
    /// <summary>graph-v1 params for this run: leans, then links, then the start and stops pinned again.</summary>
    public static JsonObject BuildParams(DistrictGraph g, IReadOnlyList<int> ints)
    {
        Random rng = new(Seed(ints));
        var pinned = new HashSet<int>(g.Required);
        JsonArray coupling = [];
        JsonArray ops = [];

        for (int q = 0; q < g.Count; q++)
            ops.Add(Bloch(q, pinned.Contains(q) ? PinZ : Between(rng, LeanMin, LeanMax)));
        foreach (var l in g.Links)
        {
            coupling.Add(new JsonArray(l.A, l.B));
            ops.Add(new JsonObject
            {
                ["type"] = "relationship",
                ["qubits"] = new JsonArray(l.A, l.B),
                ["paulis"] = new JsonObject { ["ZZ"] = Between(rng, LinkMin, LinkMax) },
                ["fraction"] = LinkFraction,
            });
        }
        foreach (var q in pinned.OrderBy(q => q)) ops.Add(Bloch(q, PinZ));

        return new()
        {
            ["mode"] = Mode,
            ["num_qubits"] = g.Count,
            ["coupling_map"] = coupling,
            ["shots"] = Shots,
            ["operations"] = ops,
        };
    }

    /// <summary>FNV-1a over every value, so all 64 Q-Day numbers count. string.GetHashCode would differ per process.</summary>
    public static int Seed(IReadOnlyList<int> ints)
    {
        if (ints is null || ints.Count == 0) throw new ArgumentException("no Q-Day numbers to seed from", nameof(ints));
        unchecked
        {
            uint h = 2166136261;
            foreach (var v in ints) h = (h ^ (uint)v) * 16777619;
            return (int)h;
        }
    }

    static double Between(Random rng, double min, double max) => min + (max - min) * rng.NextDouble();

    // A bloch target sets a direction only, so the length has to be 1 for Z to mean anything.
    static JsonObject Bloch(int q, double z) => new()
    {
        ["type"] = "bloch",
        ["qubit"] = q,
        ["paulis"] = new JsonObject { ["Z"] = z, ["X"] = Math.Sqrt(Math.Max(0, 1 - z * z)) },
    };

    // ---- reading a layout ----------------------------------------------------------
    /// <summary>Sealed state per link. Measured on emu and fake: character 0 is qubit 0, and Z = +1 reads as 0.</summary>
    public static bool[] Decode(DistrictGraph g, string bitstring)
    {
        if (bitstring is null || bitstring.Length != g.Count)
            throw new ArgumentException($"bitstring has length {bitstring?.Length ?? 0}, expected {g.Count}");
        var sealedLinks = new bool[g.Links.Count];
        for (int i = 0; i < sealedLinks.Length; i++)
            sealedLinks[i] = bitstring[g.Links[i].A] != bitstring[g.Links[i].B];
        return sealedLinks;
    }

    /// <summary>True when the start reaches every stop without crossing a link where open[i] is false.</summary>
    public static bool HasRoute(DistrictGraph g, bool[] open)
    {
        var parent = new int[g.Count];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        for (int i = 0; i < g.Links.Count; i++)
            if (open[i]) parent[Find(g.Links[i].A)] = Find(g.Links[i].B);
        int root = Find(g.Start);
        return g.Stops.All(s => Find(s) == root);
    }

    // ---- choosing a layout ---------------------------------------------------------
    /// <summary>
    /// The likeliest bitstring that keeps a route and seals enough; if none does, the likeliest
    /// bitstring with a near-minimal set of borders opened. Always returns a layout with a route.
    /// </summary>
    public static LockdownLayout Choose(DistrictGraph g, IEnumerable<Measurement> measurements)
    {
        // Ignore anything the wrong length, such as a bank file recorded on an older district file.
        var ranked = (measurements ?? [])
            .Where(m => m?.Bitstring != null && m.Bitstring.Length == g.Count && m.Bitstring.All(c => c is '0' or '1'))
            .OrderByDescending(m => m.Count)
            .ThenBy(m => m.Bitstring, StringComparer.Ordinal)   // ties must not depend on input order
            .ToList();

        for (int i = 0; i < ranked.Count; i++)
        {
            var sealedLinks = Decode(g, ranked[i].Bitstring);
            if (HasRoute(g, Invert(sealedLinks)) && sealedLinks.Count(s => s) >= MinSealedFraction * g.Links.Count)
                return new() { Sealed = sealedLinks, Bitstring = ranked[i].Bitstring, Rank = i + 1, Candidates = ranked.Count, Probability = ranked[i].Probability };
        }

        if (ranked.Count == 0)   // nothing usable came back: everything open, still a route
            return new() { Sealed = new bool[g.Links.Count], Rank = 0, Candidates = 0 };

        LockdownLayout layout = new()
        {
            Sealed = Decode(g, ranked[0].Bitstring),
            Bitstring = ranked[0].Bitstring,
            Rank = 1,
            Candidates = ranked.Count,
            Probability = ranked[0].Probability,
        };
        Repair(g, layout);
        return layout;
    }

    static bool[] Invert(bool[] a)
    {
        var o = new bool[a.Length];
        for (int i = 0; i < a.Length; i++) o[i] = !a[i];
        return o;
    }

    /// <summary>
    /// For each stop in turn, 0-1 BFS from the start (open = 0, sealed = 1), opening the sealed links on the path.
    /// Near-minimal: each path is the cheapest for its stop, but together they can open a link more than strictly needed.
    /// </summary>
    static void Repair(DistrictGraph g, LockdownLayout layout)
    {
        var adjacent = new List<(int to, int link)>[g.Count];
        for (int i = 0; i < g.Count; i++) adjacent[i] = [];
        for (int i = 0; i < g.Links.Count; i++)
        {
            adjacent[g.Links[i].A].Add((g.Links[i].B, i));
            adjacent[g.Links[i].B].Add((g.Links[i].A, i));
        }

        foreach (var stop in g.Stops)
        {
            var dist = new int[g.Count];
            var viaLink = new int[g.Count];
            var viaNode = new int[g.Count];
            for (int i = 0; i < g.Count; i++) { dist[i] = int.MaxValue; viaLink[i] = -1; viaNode[i] = -1; }
            dist[g.Start] = 0;
            LinkedList<int> queue = new();
            queue.AddFirst(g.Start);
            while (queue.Count > 0)
            {
                int u = queue.First.Value;
                queue.RemoveFirst();
                foreach (var (to, link) in adjacent[u])
                {
                    int cost = layout.Sealed[link] ? 1 : 0;
                    if (dist[u] + cost >= dist[to]) continue;
                    dist[to] = dist[u] + cost;
                    viaLink[to] = link;
                    viaNode[to] = u;
                    if (cost == 0) queue.AddFirst(to); else queue.AddLast(to);
                }
            }
            for (int at = stop; at != g.Start; at = viaNode[at])
                if (layout.Sealed[viaLink[at]])
                {
                    layout.Sealed[viaLink[at]] = false;
                    layout.Opened.Add(viaLink[at]);
                    layout.Breach = true;
                }
        }
    }

    // ---- what the player and the log are told ------------------------------------------
    // The layout is a simulation, so nothing here may call it hardware. Probabilities and repairs stay in the log.
    /// <summary>Where the targets came from. Only certified Q-Day numbers count as hardware; the bank's were seeded on a laptop.</summary>
    public static string TargetSource(bool fromBank, bool certified, string qdayBackend) =>
        fromBank ? "recorded bank"
        : certified ? $"Q-Day numbers from {qdayBackend ?? "IBM hardware"}"
        : "local random, no certified bytes";

    public static List<string> HudLines(LockdownLayout l)
    {
        return
        [
            $"lockdown simulation  {l.Backend ?? "unknown"}",
            $"targets  {l.TargetSource}",
            l.Rank > 0 ? $"layout {l.Rank} of {l.Candidates}" : "no layout returned",
            $"job  {l.JobId ?? "none"}",
        ];
    }

    public static List<string> LogLines(DistrictGraph g, LockdownLayout l)
    {
        List<string> lines =
        [
            $"lockdown simulation: backend={l.Backend} job={l.JobId} targets={l.TargetSource}",
            $"lockdown layout {l.Rank} of {l.Candidates}, p={l.Probability:P2}, bitstring {l.Bitstring ?? "none"}, sealed {l.SealedCount} of {g.Links.Count}, breach={l.Breach}",
        ];
        if (l.Rank == 0) lines.Add("lockdown: the engine returned no usable bitstring, every border stays open");
        else if (l.SealedCount < MinSealedFraction * g.Links.Count) lines.Add($"lockdown: only {l.SealedCount} borders sealed, below the {MinSealedFraction:P0} floor");
        foreach (var i in l.Opened)
            lines.Add($"lockdown breach: opened {g.Names[g.Links[i].A]} - {g.Names[g.Links[i].B]}");
        return lines;
    }
}
