// Moth Atlas client. Each request runs on its own background thread; the game
// picks finished results up with Take() once per tick, so no native is ever
// called off the script thread.
//
// Live:    [Atlas] ApiKey in scripts\QDay.ini.
// Offline: no key, or [Atlas] Offline = true. Answers from the real job results
//          captured 2026-10-21 (../recorded, embedded in the DLL), so the mod plays
//          without an Atlas account.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace QDay;

public class AtlasResult
{
    public const string RecordedQDayJob = "6eb8fa80-d13f-4f28-8b1b-f8fb7057f16b";

    public string Name, Status, Kind, Message, Commit, Backend, JobId, Grade;
    public double ErrorRate = double.NaN, S = double.NaN;
    public bool Certified;
    public List<int> Ints;

    public override string ToString() => Status == "error"
        ? $"{Kind} error: {Message}"
        : $"{Kind} {Status} backend={Backend} error_rate={ErrorRate} S={S} certified={Certified} job={JobId}";

    public static AtlasResult Recorded() => new()
    {
        Name = "qday",
        Status = "done",
        Kind = "qday",
        S = 2.711,
        Commit = "40bb90660f941c13f6ccc0d1998adfce33daa061d50459b0719ad999c93352f2",
        Backend = "ibm_marrakesh",
        JobId = RecordedQDayJob,
        Certified = true,
        Grade = "hardware-accounted",
    };
}

public sealed class Atlas : IDisposable
{
    const string BaseUrl = "https://api.mothquantum.com";
    const string UserAgent = "Mozilla/5.0 (Q-DAY)";   // Cloudflare 403s non-browser User-Agents

    // 4-qubit GHZ-style chain on a fake (noise-model) backend; fragile to noise, so the chips separate cleanly.
    static JsonObject CalibrationParams(string chip) => new()
    {
        ["mode"] = "fake",
        ["backend_name"] = chip,
        ["num_qubits"] = 4,
        ["coupling_map"] = new JsonArray(new JsonArray(0, 1), new JsonArray(1, 2), new JsonArray(2, 3)),
        ["seed"] = 7,
        ["shots"] = 2048,
        ["operations"] = new JsonArray(
            new JsonObject { ["type"] = "bloch", ["qubit"] = 0, ["paulis"] = new JsonObject { ["X"] = 1.0 } },
            Relationship(0, 1), Relationship(1, 2), Relationship(2, 3)),
    };

    static JsonObject Relationship(int a, int b) => new()
    {
        ["type"] = "relationship",
        ["qubits"] = new JsonArray(a, b),
        ["paulis"] = new JsonObject { ["ZZ"] = 1.0 },
    };

    // 12 qubits yields zero certified bytes on hardware; 64 is the verified production job.
    static JsonObject QDayParams() => new()
    {
        ["mode"] = "qpu",
        ["num_qubits"] = 64,
        ["shots"] = 2048,
        ["output_bytes"] = 64,
        ["bell_witness"] = true,
        ["derive"] = new JsonObject { ["integers"] = new JsonObject { ["min"] = 0, ["max"] = 255, ["count"] = 64 } },
    };

    readonly HttpClient http;                    // null when offline
    readonly ConcurrentQueue<AtlasResult> inbox = new();
    readonly CancellationTokenSource cts = new();

    public bool Offline => http is null;

    public Atlas(string apiKey, bool offline)
    {
        if (!offline && !string.IsNullOrWhiteSpace(apiKey))
        {
            // GTA5.exe has no .config, so .NET may not offer TLS 1.2 by default.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            http = new() { BaseAddress = new(BaseUrl), Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            http.DefaultRequestHeaders.Authorization = new("Bearer", apiKey.Trim());
        }
        Log.Write(Offline ? "atlas: offline, answering from recorded results" : $"atlas: live, {BaseUrl}");
    }

    public void Calibrate(string name, string chip) => Start(name, "calibrate", () => DoCalibrate(chip));
    public void QDay(string name) => Start(name, "qday", DoQDay);

    /// <summary>Next finished result, or null. Call from the script thread.</summary>
    public AtlasResult Take() => inbox.TryDequeue(out var r) ? r : null;

    public void Dispose() { cts.Cancel(); http?.Dispose(); }

    void Start(string name, string kind, Func<AtlasResult> work)
    {
        Log.Write($"atlas request {name} ({kind}, {(Offline ? "offline" : "live")})");
        new Thread(() =>
        {
            AtlasResult r;
            try { r = work(); r.Status = "done"; }
            catch (Exception) when (cts.IsCancellationRequested) { return; }   // script unloading
            catch (Exception e) { r = new() { Status = "error", Message = e.Message }; }
            r.Name = name; r.Kind = kind;
            inbox.Enqueue(r);
        })
        { IsBackground = true, Name = $"QDay Atlas {name}" }.Start();
    }

    // ---- the two jobs -----------------------------------------------------------
    AtlasResult DoCalibrate(string chip)
    {
        var jobId = "recorded";
        var o = Offline ? RecordedOutput($"graph_fake_{chip}.json") : RunJob("graph-v1", CalibrationParams(chip), 600, out jobId);
        return new() { Backend = Str(o["backend"]), ErrorRate = Math.Round(ErrorRate(o), 4), JobId = jobId };
    }

    /// <summary>A part's recorded graph-v1 error rate, read straight away on the calling thread: the fallback when a live calibration times out, and F6.</summary>
    public static double RecordedErrorRate(string chip) => Math.Round(ErrorRate(RecordedOutput($"graph_fake_{chip}.json")), 4);

    AtlasResult DoQDay()
    {
        var jobId = AtlasResult.RecordedQDayJob;
        var o = Offline ? RecordedOutput("qrng_qpu_64q_marrakesh.json") : RunJob("comet-qrng-v1", QDayParams(), 3600, out jobId);
        var bw = o["bell_witness"];
        AtlasResult r = new()
        {
            S = Math.Round(Num(bw?["S"]), 3),
            ErrorRate = Math.Round(Num(bw?["equivalent_depolarising_p"]), 4),
            Commit = Str(o["commitment"]?["commit"]),
            Backend = Str(o["provenance"]?["backend"]),
            Grade = Str(o["entropy_report"]?["grade"]),
            JobId = jobId,
            Ints = [],
        };
        if (o["random"]?["derived"]?["integers"]?["values"] is JsonArray values)
            foreach (var v in values) r.Ints.Add(v.GetValue<int>());
        r.Certified = r.Ints.Count > 0;
        if (!r.Certified)
        {
            // The certificate refused to hand out bytes; keep the game playable, but say so.
            Random rng = new();
            for (int i = 0; i < 64; i++) r.Ints.Add(rng.Next(256));
        }
        return r;
    }

    /// <summary>1 - mean ZZ over the chain's three edges, from the measured counts (not tomography, which is the ideal state).</summary>
    static double ErrorRate(JsonNode output)
    {
        if (output["measurements"] is not JsonArray meas) throw new InvalidDataException("no measurements");
        double total = 0, zz = 0;
        foreach (var m in meas) total += Num(m["count"]);
        for (int a = 0; a < 3; a++)
            foreach (var m in meas)
            {
                var bits = Str(m["bitstring"]);                     // qubit 0 is the LAST character
                int q0 = bits.Length - 1 - a;                        // (no ^index: net48 has no System.Index)
                zz += Num(m["count"]) * (bits[q0] == bits[q0 - 1] ? 1 : -1) / total;
            }
        return 1 - zz / 3;
    }

    // ---- HTTP -------------------------------------------------------------------
    JsonNode RunJob(string engine, JsonObject parameters, int timeoutSeconds, out string jobId)
    {
        var submit = Send(HttpMethod.Post, $"/api/v1/engines/{engine}/process", new JsonObject { ["params"] = parameters });
        jobId = Str(submit["job_id"]) ?? throw new Exception($"submit failed: {submit.ToJsonString()}");
        Log.Write($"atlas submitted {engine} job {jobId}");

        var clock = Stopwatch.StartNew();
        int failures = 0;
        while (true)
        {
            if (cts.Token.WaitHandle.WaitOne(3000)) throw new OperationCanceledException(cts.Token);
            if (clock.Elapsed.TotalSeconds > timeoutSeconds) throw new TimeoutException($"job {jobId} timed out");
            string status;
            try { status = Str(Send(HttpMethod.Get, $"/api/v1/jobs/{jobId}/status")["status"]); failures = 0; }
            catch (Exception e) when (!cts.IsCancellationRequested && ++failures < 5)
            {
                Log.Write($"atlas status poll failed ({failures}), retrying: {e.Message}");   // one 502 should not kill a 15-minute QPU wait
                continue;
            }
            if (status == "completed") break;
            if (status is "failed" or "error" or "cancelled") throw new Exception($"job {jobId} {status}");
        }
        return Send(HttpMethod.Get, $"/api/v1/jobs/{jobId}/result")["result"]?["output"]
            ?? throw new InvalidDataException($"job {jobId}: result has no output");
    }

    JsonObject Send(HttpMethod method, string path, JsonObject body = null)
    {
        using HttpRequestMessage req = new(method, path);
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = http.SendAsync(req, cts.Token).GetAwaiter().GetResult();
        var text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"{method} {path}: HTTP {(int)resp.StatusCode} {text.Substring(0, Math.Min(300, text.Length))}");
        return JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException($"{method} {path}: not a JSON object");
    }

    static JsonNode RecordedOutput(string file)
    {
        using var s = typeof(Atlas).Assembly.GetManifestResourceStream($"recorded.{file}")
                      ?? throw new FileNotFoundException($"no recorded result {file}");
        return JsonNode.Parse(s)?["result"]?["output"] ?? throw new InvalidDataException($"{file}: no result.output");
    }

    static string Str(JsonNode n) => n?.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : null;
    static double Num(JsonNode n) => n?.GetValueKind() == JsonValueKind.Number ? n.GetValue<double>() : double.NaN;
}
