#!/usr/bin/env python3
"""Step 0 of the escape lockdown: prove the graph-v1 side outside the game.

Needs MOTH_API_TOKEN and Python only (certifi is used if it is installed, because
python.org builds on macOS have no CA bundle).

  lockdown_probe.py bits [--with-fake]     bit order and sign, 3 and 20 qubits
  lockdown_probe.py timing --mode emu      runtime of a 20-qubit, 35-edge job
  lockdown_probe.py layouts --mode emu     layout variety and repair rate on a dummy graph
  lockdown_probe.py cordons --jobs 10      targets drawn along random cut lines; does graph-v1 still decide?
  lockdown_probe.py rescore DIR            re-run the layout analysis on saved jobs, no API calls

Every job's full output is saved under --out, so analysis can be redone without spending credits.
"""
import argparse
import json
import math
import os
import random
import ssl
import sys
import time
import urllib.error
import urllib.request
from collections import deque
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

BASE = "https://api.mothquantum.com"
UA = "Mozilla/5.0 (Q-DAY)"  # Cloudflare 403s non-browser User-Agents

try:
    import certifi
    CTX = ssl.create_default_context(cafile=certifi.where())
except ImportError:
    CTX = ssl.create_default_context()


def call(method, path, body=None):
    token = os.environ["MOTH_API_TOKEN"]
    req = urllib.request.Request(BASE + path, method=method, headers={
        "Authorization": f"Bearer {token}",
        "User-Agent": UA,
        "Content-Type": "application/json",
    }, data=json.dumps(body).encode() if body is not None else None)
    with urllib.request.urlopen(req, timeout=60, context=CTX) as r:
        return json.load(r)


def run_job(params, timeout=1200):
    """Returns (output, job_id, seconds from submit to result)."""
    t0 = time.time()
    for attempt in range(6):
        try:
            job_id = call("POST", "/api/v1/engines/graph-v1/process", {"params": params})["job_id"]
            break
        except urllib.error.HTTPError as e:
            if e.code < 500 or attempt == 5:  # the platform 503s on bursts
                raise
            time.sleep(5 * (attempt + 1))
    failures = 0
    while True:
        time.sleep(2)
        if time.time() - t0 > timeout:
            raise TimeoutError(f"job {job_id} still running after {timeout}s")
        try:
            status = call("GET", f"/api/v1/jobs/{job_id}/status")
            failures = 0
        except (urllib.error.URLError, TimeoutError) as e:
            failures += 1
            if failures >= 5:
                raise
            print(f"  poll failed ({failures}): {e}", file=sys.stderr)
            continue
        if status["status"] == "completed":
            break
        if status["status"] in ("failed", "error", "cancelled"):
            raise RuntimeError(f"job {job_id} {status['status']}: {json.dumps(status)[:400]}")
    result = call("GET", f"/api/v1/jobs/{job_id}/result")
    return result["result"]["output"], job_id, time.time() - t0


def base_params(n, edges, mode, shots=4096):
    p = {"num_qubits": n, "coupling_map": [list(e) for e in edges], "mode": mode, "shots": shots}
    if mode == "fake":
        p["backend_name"] = "fez"
    return p


def bloch(q, z):
    """bloch targets set a direction only, so always send a unit vector."""
    return {"type": "bloch", "qubit": q, "paulis": {"Z": z, "X": math.sqrt(max(0.0, 1 - z * z))}}


def rel(a, b, s, fraction=1.0):
    op = {"type": "relationship", "qubits": [a, b], "paulis": {"ZZ": s}}
    if fraction != 1.0:
        op["fraction"] = fraction
    return op


# ---- dummy graph: a 4x5 street grid plus four diagonals, 35 links ------------------
def dummy_graph():
    cols, rows = 5, 4
    idx = lambda c, r: r * cols + c
    edges = []
    for r in range(rows):
        for c in range(cols):
            if c + 1 < cols:
                edges.append((idx(c, r), idx(c + 1, r)))
            if r + 1 < rows:
                edges.append((idx(c, r), idx(c, r + 1)))
    edges += [(idx(0, 0), idx(1, 1)), (idx(3, 0), idx(4, 1)), (idx(1, 2), idx(2, 3)), (idx(3, 1), idx(2, 2))]
    return {"n": 20, "edges": edges, "start": 0, "stops": [7, 12, 19]}


def layout_params(g, seed, mode, s_range, t_range, pin, order="bloch_first", shots=4096, fraction=1.0):
    rng = random.Random(seed)
    pinned = {g["start"], *g["stops"]}
    blochs = [bloch(q, pin if q in pinned else rng.uniform(*t_range)) for q in range(g["n"])]
    rels = [rel(a, b, rng.uniform(*s_range), fraction) for a, b in g["edges"]]
    p = base_params(g["n"], g["edges"], mode, shots)
    if order == "bloch_first":
        p["operations"] = blochs + rels
    elif order == "rel_first":
        p["operations"] = rels + blochs
    else:  # repin: lean, correlate, then set the start and stops again so they hold
        p["operations"] = blochs + rels + [bloch(q, pin) for q in sorted(pinned)]
    return p


# ---- the layout rules from the brief (mirrored later in Lockdown.cs) ---------------
def qubit_bits(bitstring):
    """Character 0 = qubit 0. Settled by the `bits` command, see the report."""
    return bitstring


def sealed_links(g, bits):
    return [(a, b) for a, b in g["edges"] if bits[a] != bits[b]]


def components(g, bits):
    parent = list(range(g["n"]))
    def find(x):
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x
    for a, b in g["edges"]:
        if bits[a] == bits[b]:
            parent[find(a)] = find(b)
    return find


def passes(g, bits, min_sealed):
    find = components(g, bits)
    root = find(g["start"])
    connected = all(find(s) == root for s in g["stops"])
    frac = len(sealed_links(g, bits)) / len(g["edges"])
    return connected, frac, connected and frac >= min_sealed


def repair_count(g, bits):
    """0-1 BFS from the start's component to each stop; returns the number of links opened."""
    sealed = {frozenset(e) for e in sealed_links(g, bits)}
    adj = {i: [] for i in range(g["n"])}
    for a, b in g["edges"]:
        adj[a].append(b)
        adj[b].append(a)
    opened = 0
    for stop in g["stops"]:
        dist, prev = {g["start"]: 0}, {}
        dq = deque([g["start"]])
        while dq:
            u = dq.popleft()
            for v in adj[u]:
                w = 1 if frozenset((u, v)) in sealed else 0
                if v not in dist or dist[u] + w < dist[v]:
                    dist[v], prev[v] = dist[u] + w, u
                    dq.appendleft(v) if w == 0 else dq.append(v)
        u = stop
        while u != g["start"]:
            e = frozenset((u, prev[u]))
            if e in sealed:
                sealed.remove(e)
                opened += 1
            u = prev[u]
    return opened


def analyse(g, outputs, min_sealed):
    """Per-job and across-job numbers for a set of graph-v1 outputs."""
    rows, chosen = [], []
    for o in outputs:
        ms = sorted(o["measurements"], key=lambda m: -m["count"])
        total_shown = sum(m["count"] for m in ms)
        pick_rank, pick, any_connected = None, None, False
        for i, m in enumerate(ms):
            bits = qubit_bits(m["bitstring"])
            conn, frac, ok = passes(g, bits, min_sealed)
            any_connected |= conn
            if ok:
                pick_rank, pick = i + 1, m
                break
        top = qubit_bits(ms[0]["bitstring"])
        rows.append({
            "entries": len(ms),
            "top_p": ms[0]["probability"],
            "shown_mass": sum(m["probability"] for m in ms),
            "shown_count": total_shown,
            "top_count": ms[0]["count"],
            "tail_count": ms[-1]["count"],
            "rank": pick_rank,
            "pick_p": pick["probability"] if pick else None,
            "pick_sealed": len(sealed_links(g, qubit_bits(pick["bitstring"]))) if pick else None,
            "repair": None if pick else repair_count(g, top),
            "top_sealed": len(sealed_links(g, top)),
        })
        chosen.append(frozenset(sealed_links(g, qubit_bits((pick or ms[0])["bitstring"]))))
    jac = []
    for i in range(len(chosen)):
        for j in range(i + 1, len(chosen)):
            union = chosen[i] | chosen[j]
            jac.append(len(chosen[i] & chosen[j]) / len(union) if union else 1.0)
    return rows, jac


def report(g, outputs, min_sealed, label=""):
    rows, jac = analyse(g, outputs, min_sealed)
    n = len(rows)
    passed = [r for r in rows if r["rank"]]
    avg = lambda xs: sum(xs) / len(xs) if xs else float("nan")
    print(f"--- {label} jobs={n} min_sealed={min_sealed:.2f} links={len(g['edges'])}")
    print(f"    measurements entries: {sorted({r['entries'] for r in rows})}")
    print(f"    top p {avg([r['top_p'] for r in rows]):.4f}  top-20 mass {avg([r['shown_mass'] for r in rows]):.4f}  "
          f"top count {avg([r['top_count'] for r in rows]):.1f}  20th count {avg([r['tail_count'] for r in rows]):.1f}")
    print(f"    no repair: {len(passed)}/{n}  mean rank {avg([r['rank'] for r in passed]):.1f}  "
          f"picked p {avg([r['pick_p'] for r in passed]):.4f}  sealed {avg([r['pick_sealed'] for r in passed]):.1f}")
    fails = [r for r in rows if not r["rank"]]
    if fails:
        print(f"    repairs: links opened {[r['repair'] for r in fails]}  top layout sealed {[r['top_sealed'] for r in fails]}")
    if jac:
        print(f"    overlap of sealed sets between runs (Jaccard): mean {avg(jac):.2f}  max {max(jac):.2f}  "
              f"identical pairs {sum(1 for j in jac if j == 1.0)}/{len(jac)}")


# ---- cordon test: do targets drawn along cut lines give cordons, and does graph-v1 still decide? ----
def dummy_centres():
    return [(q % 5, q // 5) for q in range(20)]


def side_of_lines(centres, lines):
    """Per district: 0 or 1, the XOR of which side of each line it is on."""
    sides = []
    for x, y in centres:
        v = 0
        for ang, off in lines:
            v ^= int(x * math.cos(ang) + y * math.sin(ang) > off)
        sides.append(str(v))
    return "".join(sides)


def draw_cuts(g, centres, rng, count, min_sealed=0.2, tries=400):
    """First random lines whose cut-only layout keeps the start and stops connected and seals enough."""
    cx = sum(c[0] for c in centres) / len(centres)
    cy = sum(c[1] for c in centres) / len(centres)
    reach = max(math.hypot(c[0] - cx, c[1] - cy) for c in centres)
    for _ in range(tries):
        lines = []
        for _ in range(count):
            ang = rng.uniform(0, math.pi)
            lines.append((ang, cx * math.cos(ang) + cy * math.sin(ang) + rng.uniform(-reach, reach)))
        bits = side_of_lines(centres, lines)
        if passes(g, bits, min_sealed)[2]:
            return lines, bits
    raise RuntimeError("no acceptable cut lines")


def cordon_params(g, cut_bits, rng, neg, posr, t_range, pin, fraction, shots=4096):
    pinned = {g["start"], *g["stops"]}
    blochs = [bloch(q, pin if q in pinned else rng.uniform(*t_range)) for q in range(g["n"])]
    rels = [rel(a, b, rng.uniform(*neg) if cut_bits[a] != cut_bits[b] else rng.uniform(*posr), fraction)
            for a, b in g["edges"]]
    p = base_params(g["n"], g["edges"], "emu", shots)
    p["operations"] = blochs + rels + [bloch(q, pin) for q in sorted(pinned)]
    return p


def cmd_cordons(a):
    g, centres = dummy_graph(), dummy_centres()
    tag = f"neg{a.neg[0]}~{a.neg[1]}_pos{a.pos[0]}~{a.pos[1]}_c{a.cuts}_f{a.fraction}"
    def one(i):
        time.sleep(i * 3)
        rng = random.Random(3000 + i)
        lines, cut_bits = draw_cuts(g, centres, rng, a.cuts)
        p = cordon_params(g, cut_bits, rng, a.neg, a.pos, (-0.6, 0.6), 0.9, a.fraction)
        o, job, secs = run_job(p)
        out = a.out / "cordons"
        save(out, f"cordon_{tag}_{i:02d}", {**p, "_cut_bits": cut_bits}, o, job, secs)
        return o, cut_bits
    with ThreadPoolExecutor(a.workers) as ex:
        results = list(ex.map(one, range(a.jobs)))
    report_cordons(g, results, tag, a.min_sealed)


def report_cordons(g, results, tag, min_sealed):
    avg = lambda xs: sum(xs) / len(xs) if xs else float("nan")
    match, comps, sealed, ranks, repairs, cut_sealed, sets = [], [], [], [], 0, [], []
    for o, cut_bits in results:
        ms = sorted(o["measurements"], key=lambda m: -m["count"])
        pick = next((m for m in ms if passes(g, m["bitstring"], min_sealed)[2]), None)
        rank = ms.index(pick) + 1 if pick else None
        bits = (pick or ms[0])["bitstring"]
        repairs += pick is None
        if rank:
            ranks.append(rank)
        cut = {frozenset(e) for e in sealed_links(g, cut_bits)}
        got = {frozenset(e) for e in sealed_links(g, bits)}
        same = sum((frozenset(e) in cut) == (frozenset(e) in got) for e in g["edges"])
        match.append(same / len(g["edges"]))
        find = components(g, bits)
        comps.append(len({find(i) for i in range(g["n"])}))
        sealed.append(len(got))
        cut_sealed.append(len(cut))
        sets.append(frozenset(got))
    jac = [len(x & y) / len(x | y) for i, x in enumerate(sets) for y in sets[i + 1:] if x | y]
    print(f"--- {tag} jobs={len(results)}")
    print(f"    per-link match with cut-only layout: mean {avg(match):.3f}  min {min(match):.3f}  max {max(match):.3f}  (fail above ~0.80)")
    print(f"    open areas {avg(comps):.1f}  sealed {avg(sealed):.1f}/35 (cut lines alone {avg(cut_sealed):.1f})")
    print(f"    no repair {len(results) - repairs}/{len(results)}  mean rank {avg(ranks):.1f}  overlap between runs {avg(jac):.2f}")


# ---- commands ----------------------------------------------------------------------
def save(out, name, params, output, job_id, secs):
    out.mkdir(parents=True, exist_ok=True)
    (out / f"{name}.json").write_text(json.dumps(
        {"params": params, "job_id": job_id, "seconds": secs, "result": {"output": output}}, indent=1))


def cmd_bits(a):
    chain3 = [(0, 1), (1, 2)]
    # Three qubits as in the brief, then a 20-qubit asymmetric pattern on the full graph,
    # because fake mode routes onto a real chip and that must not scramble the order.
    pattern = [q in (0, 1, 4, 9, 13) for q in range(20)]
    g = dummy_graph()
    cases = []
    for mode in ("emu", "fake") if a.with_fake else ("emu",):  # fake took 500 s here; the mod never uses it
        p = base_params(3, chain3, mode)
        p["operations"] = [bloch(0, -1.0), bloch(1, 1.0), bloch(2, 1.0)]
        cases.append((f"bits3_{mode}", p, "100"))
        p = base_params(20, g["edges"], mode)
        p["operations"] = [bloch(q, -1.0 if pattern[q] else 1.0) for q in range(20)]
        cases.append((f"bits20_{mode}", p, "".join("1" if b else "0" for b in pattern)))
    for name, p, want in cases:
        o, job, secs = run_job(p)
        save(a.out, name, p, o, job, secs)
        got = o["dominant_bitstring"]
        verdict = "char 0 = qubit 0, +Z reads 0" if got == want else (
            "REVERSED: last char = qubit 0" if got == want[::-1] else "NEITHER")
        print(f"{name:12s} want {want} got {got}  {verdict}  {secs:.0f}s  "
              f"top p {o['measurements'][0]['probability']:.3f} backend={o.get('backend')} job={job}", flush=True)


def cmd_timing(a):
    g = dummy_graph()
    for i in range(a.runs):
        p = layout_params(g, 1000 + i, a.mode, (0.3, 0.9), (-0.6, 0.6), 0.9)
        if a.no_update:
            for op in p["operations"]:
                op["update"] = False
        tag = "noupd" if a.no_update else "upd"
        o, job, secs = run_job(p)
        save(a.out, f"timing_{a.mode}_{tag}_{i}", p, o, job, secs)
        print(f"{a.mode} {tag} run {i}: {secs:.0f}s qpu_seconds={o.get('qpu_seconds')} entries={len(o['measurements'])} job={job}", flush=True)


def cmd_layouts(a):
    g = dummy_graph()
    s_range = tuple(a.s)
    t_range = tuple(a.t)
    tag = f"{a.mode}_{a.order}_s{a.s[0]}~{a.s[1]}_t{a.t[0]}~{a.t[1]}_p{a.pin}_f{a.fraction}"
    def one(i):
        time.sleep(i * 3)  # the platform 503s on bursts
        p = layout_params(g, 2000 + i, a.mode, s_range, t_range, a.pin, a.order, a.shots, a.fraction)
        o, job, secs = run_job(p)
        save(a.out, f"layouts_{tag}_{i:02d}", p, o, job, secs)
        return o, secs
    with ThreadPoolExecutor(a.workers) as ex:
        results = list(ex.map(one, range(a.jobs)))
    print(f"seconds: {[round(s) for _, s in results]}")
    report(g, [o for o, _ in results], a.min_sealed, tag)


def cmd_rescore(a):
    g = dummy_graph()
    groups = {}
    for f in sorted(Path(a.dir).glob(a.glob)):
        groups.setdefault(f.stem.rsplit("_", 1)[0], []).append(json.loads(f.read_text())["result"]["output"])
    for tag, outs in groups.items():
        for ms in a.min_sealed:
            report(g, outs, ms, tag)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", type=Path, default=Path("probe_out"))
    sub = ap.add_subparsers(dest="cmd", required=True)
    b = sub.add_parser("bits")
    b.add_argument("--with-fake", action="store_true", help="also check fake/fez, about 10 minutes")
    b.set_defaults(fn=cmd_bits)
    t = sub.add_parser("timing")
    t.add_argument("--mode", default="emu")
    t.add_argument("--runs", type=int, default=3)
    t.add_argument("--no-update", action="store_true", help="send update:false on every operation")
    t.set_defaults(fn=cmd_timing)
    l = sub.add_parser("layouts")
    l.add_argument("--mode", default="emu")
    l.add_argument("--jobs", type=int, default=10)
    l.add_argument("--workers", type=int, default=4)
    l.add_argument("--shots", type=int, default=4096)
    l.add_argument("--s", type=float, nargs=2, default=[-0.7, 0.9])
    l.add_argument("--t", type=float, nargs=2, default=[-0.6, 0.6])
    l.add_argument("--pin", type=float, default=0.9)
    l.add_argument("--order", default="repin", choices=["bloch_first", "rel_first", "repin"])
    l.add_argument("--min-sealed", type=float, default=1 / 3)
    l.add_argument("--fraction", type=float, default=0.5, help="partial rotation on each relationship")
    l.set_defaults(fn=cmd_layouts)
    c = sub.add_parser("cordons")
    c.add_argument("--jobs", type=int, default=10)
    c.add_argument("--workers", type=int, default=4)
    c.add_argument("--cuts", type=int, default=2)
    c.add_argument("--neg", type=float, nargs=2, default=[-0.7, -0.2])
    c.add_argument("--pos", type=float, nargs=2, default=[0.2, 0.9])
    c.add_argument("--fraction", type=float, default=0.5)
    c.add_argument("--min-sealed", type=float, default=1 / 3)
    c.set_defaults(fn=cmd_cordons)
    r = sub.add_parser("rescore")
    r.add_argument("dir")
    r.add_argument("--glob", default="layouts_*.json")
    r.add_argument("--min-sealed", type=float, nargs="+", default=[1 / 3])
    r.set_defaults(fn=cmd_rescore)
    a = ap.parse_args()
    a.fn(a)


if __name__ == "__main__":
    main()
