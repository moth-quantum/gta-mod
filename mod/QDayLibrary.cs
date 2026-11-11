// The recorded Q-Day runs: every recorded/qrng_qpu_64q_*.json, embedded in the DLL.
// Used whenever the live job is not there in time, and for the whole of offline mode.
// Each pick is a random run plus a random starting point in its integers, so two
// fallback riots in a row do not play out the same way. Record more runs with
// tools/record_qday.py.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace QDay;

public static class QDayLibrary
{
    const string Prefix = "recorded.qrng_qpu_64q_";
    static readonly Random pickRng = new();

    /// <summary>Embedded resource names of every recorded 64-qubit Q-Day run.</summary>
    public static IReadOnlyList<string> Runs { get; } = typeof(QDayLibrary).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
        .OrderBy(n => n, StringComparer.Ordinal)
        .ToList();

    /// <summary>A random recorded run, parsed in full, with a random starting offset into its integers.</summary>
    public static AtlasResult Pick()
    {
        if (Runs.Count == 0) throw new InvalidOperationException("no recorded Q-Day runs embedded");
        string name;
        int start;
        lock (pickRng) name = Runs[pickRng.Next(Runs.Count)];   // Pick runs on Atlas threads as well as the script thread
        var r = Load(name);
        lock (pickRng) start = pickRng.Next(r.Ints.Count);
        r.Start = start;
        r.Name = "qday-library";
        r.Kind = "qday";
        r.Status = "done";
        Log.Write($"qday library: picked {name.Substring("recorded.".Length)} job={r.JobId} offset={start}/{r.Ints.Count} backend={r.Backend} certified={r.Certified}");
        return r;
    }

    static AtlasResult Load(string resource)
    {
        using var s = typeof(QDayLibrary).Assembly.GetManifestResourceStream(resource)
                      ?? throw new FileNotFoundException($"no embedded {resource}");
        var doc = JsonNode.Parse(s) ?? throw new InvalidDataException($"{resource}: empty");
        var output = doc["result"]?["output"] ?? throw new InvalidDataException($"{resource}: no result.output");
        // Runs saved by tools/record_qday.py carry their Atlas job ID at the top level.
        // The original marrakesh run (21 Oct) predates that and has none.
        var jobId = Atlas.Str(doc["job_id"]) ?? AtlasResult.RecordedQDayJob;
        return Atlas.ParseQDay(output, jobId);
    }
}
