#!/usr/bin/env python3
"""Bakes the force field's colour table from entanglement-shader-v1. Offline only: the game never calls this engine.

Runs the engine (or fetches a finished job with --job-id, which costs nothing), keeps the raw ZIP in
recorded/, and writes recorded/entanglement_lut.bin, which QDay.csproj embeds in the DLL.

The tables hold one number per cell, so colour comes from sampling R_lut three times with the shader's
own formula (see EntanglementTable in mod/ForceField.cs). R is capped at 1 and then divided by one fixed
scale for the whole table. T_lut is baked as well but nothing reads it: the colour comes from R_lut only.

File layout, little-endian: "QLUT", uint32 header length, UTF-8 JSON header, then width x height uint16
for R and the same for T, row 0 first (head-on), each value being the table value x 65535.

  MOTH_API_TOKEN=... tools/bake_entanglement.py [--job-id ID]
"""
import argparse
import io
import json
import os
import re
import ssl
import struct
import time
import urllib.request
import zipfile
from pathlib import Path

import certifi
import numpy as np

REPO = Path(__file__).resolve().parent.parent
BASE = "https://api.mothquantum.com"
ENGINE = "entanglement-shader-v1"
PARAMS = {"reflectance": 0.45, "absorption": 0.3, "layers": 2, "incoming_rays": 4, "interaction": 5, "style": "peaked", "resolution": 256}
R_CAP = 1.0
SCALE_PERCENTILE = 99.5
WAVELENGTHS = (650.0, 530.0, 470.0)  # nm, from the engine's HLSL, in R, G, B order
THICKNESS = 500.0                    # nm, the shader's default; a shader uniform, not engine output
CTX = ssl.create_default_context(cafile=certifi.where())


def headers():
    return {"Authorization": "Bearer " + os.environ["MOTH_API_TOKEN"], "User-Agent": "Mozilla/5.0 (Q-DAY)", "Content-Type": "application/json"}


def call(method, path, body=None):
    req = urllib.request.Request(BASE + path, method=method, headers=headers(), data=json.dumps(body).encode() if body is not None else None)
    with urllib.request.urlopen(req, timeout=60, context=CTX) as f:
        return json.load(f)


def run_engine(params):
    start = time.time()
    job = call("POST", f"/api/v1/engines/{ENGINE}/process", {"params": params})["job_id"]
    print("job", job, flush=True)
    while True:
        time.sleep(5)
        status = call("GET", f"/api/v1/jobs/{job}/status")["status"]
        if status == "completed":
            return job
        if status in ("failed", "error", "cancelled"):
            raise SystemExit(f"job {job} {status}")
        if time.time() - start > 560:
            raise SystemExit(f"job {job} still {status} after 560 s")


def download(job):
    result = call("GET", f"/api/v1/jobs/{job}/result")
    url = next(o["url"] for o in result["outputs"] if o.get("url"))
    with urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0 (Q-DAY)"}), timeout=120, context=CTX) as f:
        return f.read()


def read_hdr(data):
    """One channel of a Radiance .hdr. The engine's files are flat RGBE even though the header says rle, so both are read."""
    start = data.index(b"\n\n") + 2
    end = data.index(b"\n", start)
    match = re.match(r"-Y (\d+) \+X (\d+)", data[start:end].decode())
    h, w = int(match[1]), int(match[2])
    body = data[end + 1:]
    if len(body) == w * h * 4:
        rgbe = np.frombuffer(body, np.uint8).reshape(h, w, 4)
    else:
        rgbe = np.zeros((h, w, 4), np.uint8)
        o = 0
        for y in range(h):
            if body[o] != 2 or body[o + 1] != 2:
                raise SystemExit("unrecognised .hdr scanline")
            o += 4
            for c in range(4):
                x = 0
                while x < w:
                    n = body[o]
                    o += 1
                    if n > 128:
                        n -= 128
                        rgbe[y, x:x + n, c] = body[o]
                        o += 1
                    else:
                        rgbe[y, x:x + n, c] = np.frombuffer(body, np.uint8, n, o)
                        o += n
                    x += n
    e = rgbe[..., 3].astype(float)
    return np.where(e > 0, rgbe[..., 0] * 2.0 ** (e - 136), 0.0)


def halve(table):
    """2 x 2 averages. Each new cell centre lands exactly between the four old ones, so the angle and phase axes keep their meaning."""
    h, w = table.shape
    return table.reshape(h // 2, 2, w // 2, 2).mean(axis=(1, 3))


def sample(table, s, t):
    """Bilinear: phase s wraps, angle t clamps. The C# EntanglementTable must agree with this."""
    h, w = table.shape
    x = (s % 1.0) * w - 0.5
    y = min(max(t * h - 0.5, 0.0), h - 1.0)
    x0, y0 = int(np.floor(x)), int(np.floor(y))
    fx, fy = x - x0, y - y0
    y1 = min(y0 + 1, h - 1)
    x1 = (x0 + 1) % w
    x0 %= w
    return (table[y0, x0] * (1 - fx) + table[y0, x1] * fx) * (1 - fy) + (table[y1, x0] * (1 - fx) + table[y1, x1] * fx) * fy


def colour(table, scale, cos_theta, thickness):
    theta = np.arccos(cos_theta)
    d = -4 * np.pi * thickness * cos_theta
    return [min(1.0, sample(table, ((d / wl) % (2 * np.pi)) / (2 * np.pi), theta / (np.pi / 2)) / scale) for wl in WAVELENGTHS]


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--job-id", help="use this finished job instead of running the engine, assuming the settings in PARAMS")
    a = ap.parse_args()

    job = a.job_id or run_engine(PARAMS)
    raw = download(job)
    zip_path = REPO / "recorded" / "entanglement_peaked.zip"
    zip_path.write_bytes(raw)

    with zipfile.ZipFile(io.BytesIO(raw)) as z:   # read in memory: nothing from the download is unpacked or run
        r_full = read_hdr(z.read("R_lut.hdr"))
        t_full = read_hdr(z.read("T_lut.hdr"))

    r_capped = np.minimum(r_full, R_CAP)
    scale = float(np.percentile(r_capped, SCALE_PERCENTILE))
    r, t = halve(r_capped), halve(t_full)
    h, w = r.shape

    # What halving the table costs, in colour, across the angles and thicknesses the game uses.
    grid = [(c, th) for c in np.linspace(0.0, 1.0, 181) for th in (450.0, 475.0, 500.0, 525.0, 550.0)]
    errors = [abs(np.array(colour(r_capped, scale, c, th)) - np.array(colour(r, scale, c, th))).max() for c, th in grid]
    print(f"compact vs full table, colour difference 0-1: mean {np.mean(errors):.4f}, max {np.max(errors):.4f}")

    samples = [{"cos": c, "thickness": th, "rgb": [float(v) for v in colour(r, scale, c, th)]}
               for c in (1.0, 0.9, 0.7, 0.5, 0.25, 0.05, 0.0) for th in (450.0, 500.0, 550.0)]
    header = {
        "engine": ENGINE,
        "job_id": job,
        "params": PARAMS,
        "width": w,
        "height": h,
        "r_cap": R_CAP,
        "scale": scale,
        "scale_rule": f"{SCALE_PERCENTILE}th percentile of capped R",
        "thickness_nm": THICKNESS,
        "wavelengths_nm": list(WAVELENGTHS),
        "colour_source": "R_lut only; T_lut is baked but unused",
        "samples": samples,  # worked here in Python, checked against the C# lookup by tests/LockdownCheck
    }
    head = json.dumps(header, separators=(",", ":")).encode()
    to_u16 = lambda table: np.round(np.clip(table, 0.0, 1.0) * 65535).astype("<u2").tobytes()
    out = REPO / "recorded" / "entanglement_lut.bin"
    out.write_bytes(b"QLUT" + struct.pack("<I", len(head)) + head + to_u16(r) + to_u16(t))

    print(f"job {job} params {json.dumps(PARAMS)}")
    print(f"R raw {r_full.min():.3f} to {r_full.max():.3f}, capped at {R_CAP}, scale {scale:.4f}; T {t_full.min():.3f} to {t_full.max():.3f}")
    print(f"{zip_path.name} {zip_path.stat().st_size} bytes; {out.name} {out.stat().st_size} bytes, {w} x {h}")


if __name__ == "__main__":
    main()
