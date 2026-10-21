#!/usr/bin/env python3
"""Q-DAY sidecar: the bridge between the game script and Moth Atlas.

The game (CLEO Redux or SHVDN) cannot make HTTPS calls and must never hold the
Atlas key. So the game writes small JSON request files into a shared folder and
this process turns them into Atlas jobs, then writes the result back as a
flat JSON the game can read line by line.

    bridge/request_<name>.json   -> written by the game
    bridge/result_<name>.json    -> written by us (status: queued|running|done|error)
    bridge/sidecar.log           -> everything we did, for Claude Code to read

Requests:
    {"kind": "calibrate", "chip": "brisbane"}      -> graph-v1, mode fake, returns error_rate
    {"kind": "qday"}                                -> comet-qrng-v1, mode qpu, 64 qubits, returns S, commit, ints
Add "mode": "emu" to any request to force the emulator (fast, free of hardware).

Offline mode (no key, or --offline): answers from ../recorded/ with the real
job results captured on 2026-10-21, so the mod plays without an Atlas account.
"""
import json, os, sys, time, pathlib, subprocess, random

BASE = "https://api.mothquantum.com"
UA = "Mozilla/5.0 (Q-DAY sidecar)"  # Cloudflare 403s the default Python UA
HERE = pathlib.Path(__file__).resolve().parent
RECORDED = HERE.parent / "recorded"
BRIDGE = pathlib.Path(os.environ.get("QDAY_BRIDGE", HERE / "bridge"))
KEY = os.environ.get("MOTH_API_TOKEN") or os.environ.get("MOTH_API_KEY")
OFFLINE = "--offline" in sys.argv or not KEY

CHAIN = {  # 4-qubit GHZ-style chain; fragile to noise, so chips separate cleanly
    "num_qubits": 4, "coupling_map": [[0, 1], [1, 2], [2, 3]], "seed": 7, "shots": 2048,
    "operations": [{"type": "bloch", "qubit": 0, "paulis": {"X": 1.0}}]
    + [{"type": "relationship", "qubits": [i, i + 1], "paulis": {"ZZ": 1.0}} for i in range(3)],
}
CHIPS = ["brisbane", "kingston", "torino", "kyiv", "sherbrooke", "fez", "marrakesh"]  # noisiest first


def log(msg):
    line = f"{time.strftime('%H:%M:%S')} {msg}"
    print(line, flush=True)
    with open(BRIDGE / "sidecar.log", "a") as f:
        f.write(line + "\n")


def api(method, path, body=None):
    cmd = ["curl", "-s", "-X", method, "-A", UA, "-H", f"Authorization: Bearer {KEY}",
           "-H", "Content-Type: application/json", BASE + path]
    if body is not None:
        cmd += ["-d", json.dumps(body)]
    out = subprocess.run(cmd, capture_output=True, text=True).stdout
    try:
        return json.loads(out)
    except json.JSONDecodeError:
        return {"error": out[:300]}


def run_job(engine, params, timeout=3600):
    j = api("POST", f"/api/v1/engines/{engine}/process", {"params": params})
    jid = j.get("job_id")
    if not jid:
        raise RuntimeError(f"submit failed: {j}")
    log(f"submitted {engine} job {jid}")
    t0 = time.time()
    while True:
        st = api("GET", f"/api/v1/jobs/{jid}/status").get("status")
        if st == "completed":
            break
        if st in ("failed", "error", "cancelled"):
            raise RuntimeError(f"job {jid} {st}")
        if time.time() - t0 > timeout:
            raise RuntimeError(f"job {jid} timed out")
        time.sleep(3)
    r = api("GET", f"/api/v1/jobs/{jid}/result")
    return jid, r["result"]["output"], time.time() - t0


def error_rate(output):
    meas = output["measurements"]
    tot = sum(m["count"] for m in meas)
    zz = [sum(m["count"] * (1 if m["bitstring"][::-1][a] == m["bitstring"][::-1][a + 1] else -1)
              for m in meas) / tot for a in range(3)]
    return 1 - sum(zz) / 3


def handle_calibrate(req):
    chip = req.get("chip", "brisbane")
    mode = req.get("mode", "fake")
    if OFFLINE:
        o = json.load(open(RECORDED / f"graph_fake_{chip}.json"))["result"]["output"]
        jid, secs = "recorded", 0
    else:
        params = dict(CHAIN, mode=mode)
        if mode == "fake":
            params["backend_name"] = chip
        jid, o, secs = run_job("graph-v1", params, timeout=600)
    return {"chip": chip, "backend": o.get("backend"), "error_rate": round(error_rate(o), 4),
            "dominant": o.get("dominant_bitstring"), "job_id": jid, "seconds": round(secs)}


def handle_qday(req):
    mode = req.get("mode", "qpu")
    if OFFLINE:
        o = json.load(open(RECORDED / "qrng_qpu_64q_marrakesh.json"))["result"]["output"]
        jid, secs = "recorded", 0
    else:
        params = {"mode": mode, "num_qubits": 64 if mode == "qpu" else 20, "shots": 2048,
                  "output_bytes": 64, "bell_witness": True,
                  "derive": {"integers": {"min": 0, "max": 255, "count": 64}}}
        jid, o, secs = run_job("comet-qrng-v1", params)
    bw = o["bell_witness"]
    ints = o["random"].get("derived", {}).get("integers", {}).get("values", [])
    if not ints:  # certificate refused to hand out bytes; keep the game playable, but say so
        ints = [random.randrange(256) for _ in range(64)]
        certified = False
    else:
        certified = True
    return {"S": round(bw["S"], 3), "sigma": round(bw["z_above_classical"], 1),
            "p_value": bw["p_value_local_realist"], "error_rate": round(bw["equivalent_depolarising_p"], 4),
            "commit": o["commitment"]["commit"], "backend": o["provenance"]["backend"],
            "provider_job_id": o["provenance"]["provider_job_id"], "grade": o["entropy_report"]["grade"],
            "certified": certified, "ints": ints, "job_id": jid, "seconds": round(secs)}


HANDLERS = {"calibrate": handle_calibrate, "qday": handle_qday}


def write_result(name, payload):
    tmp = BRIDGE / f"result_{name}.json.tmp"
    tmp.write_text(json.dumps(payload))
    tmp.replace(BRIDGE / f"result_{name}.json")  # atomic so the game never reads a half file


def main():
    BRIDGE.mkdir(parents=True, exist_ok=True)
    log(f"sidecar up, bridge={BRIDGE}, offline={OFFLINE}")
    while True:
        for req_path in sorted(BRIDGE.glob("request_*.json")):
            name = req_path.stem[len("request_"):]
            try:
                req = json.loads(req_path.read_text())
            except json.JSONDecodeError:
                continue  # the game is still writing it
            req_path.unlink()
            write_result(name, {"status": "running", "kind": req.get("kind")})
            log(f"request {name}: {req}")
            try:
                out = HANDLERS[req["kind"]](req)
                write_result(name, dict(status="done", **out))
                log(f"done {name}: {out}")
            except Exception as e:  # noqa: BLE001
                write_result(name, {"status": "error", "message": str(e)})
                log(f"error {name}: {e}")
        if "--once" in sys.argv and not list(BRIDGE.glob("request_*.json")):
            return
        time.sleep(0.5)


if __name__ == "__main__":
    main()
