#!/usr/bin/env python3
"""Run GTA V billboard textures through Moth Atlas's quantum blur (blur-v1) and save them back as DDS
files the game can load in place of the originals.

    python3 tools/process_textures.py --input textures/originals --output textures/pilot/mild \
        --strength mild [--files ah_bboards1.dds ...] [--dry-run]

Every output keeps its original's format (DXT1/DXT5), width, height and mip count: the original's
128-byte DDS header is copied unchanged and only the block data is replaced, then the output's header
is re-read and checked against the original. Any mismatch stops the run.

- Opaque look: the image is sent as RGB without alpha, so the engine blurs all of it (it would
  otherwise use the alpha channel as a mask). The original alpha is put back afterwards, so a DXT5
  keeps its alpha exactly as it was.
- Simulator only: execution_mode "sim" (exact statevector simulation). Nothing here sends a job to
  hardware; the guard in submit_params refuses any preset that tries.
- _lod files whose full-size partner is in the input are not sent to Atlas: they're the processed
  partner shrunk to the LOD's dimensions. A _lod file with no partner in the input is the only copy
  of that billboard, so it's processed like any other file.
- Files with identical texture data are sent once and the result reused.
- The engine's raw PNG for each file is kept in <output>/_raw/, and a later run reuses it rather than
  sending the job again, so re-encoding never costs another job.
- Writes <output>/manifest.csv: texture, dictionary (blank for now), output path, Atlas job id, engine,
  parameters, seconds taken, source.

The Atlas key is read from MOTH_API_TOKEN and never written anywhere. Needs Pillow 13 or later and
numpy; certifi is used if present. Pillow 11.2's BC1/BC3 encoder corrupts everything past the first
196,608 pixels of a level (and can crash), so older versions are refused. Each encoded level is also
decoded again and compared with what went in, as a guard against a bad encoder.
"""

import argparse
import csv
import hashlib
import io
import json
import os
import ssl
import struct
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

import numpy as np
import PIL
from PIL import Image

if int(PIL.__version__.split(".")[0]) < 13:
    sys.exit(f"Pillow {PIL.__version__} is too old: its DXT encoder corrupts large textures. Use Pillow 13 or later.")

BASE_URL = "https://api.mothquantum.com"
ENGINE = "blur-v1"
# Cloudflare 403s non-browser User-Agents (error code 1010).
USER_AGENT = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36"
POLL_SECONDS = 2
TIMEOUT_SECONDS = 1800

# The two looks. size 1024 is the engine's maximum pixel budget per pass (a 1024 x 1024 region); a
# bigger texture is halved before it's sent and scaled back up afterwards (see send_size).
MAX_UPLOAD_SIDE = 1440   # measured 2026-11-10: the asset upload resizes a 2048 x 2048 PNG to 1440 x 1440
PRESETS = {
    "mild": {"execution_mode": "sim", "style": "rx", "strength": 0.3, "reach": 0.2, "size": 1024, "downscale": True},
    "strong": {"execution_mode": "sim", "style": "rx", "strength": 0.6, "reach": 0.8, "size": 1024, "downscale": True},
}
SIMULATOR_MODES = {"sim", "local"}
HARDWARE_KEYS = ("machine", "backend", "provider", "io")

DDS_HEADER_BYTES = 128
MAX_ENCODE_ERROR = 30   # mean absolute RGB error per level, out of 255; a broken encoder gives 70+
FOURCC_BLOCK_BYTES = {b"DXT1": 8, b"DXT5": 16}
MANIFEST_FIELDS = ["texture", "dictionary", "output_path", "atlas_job_id", "engine", "parameters", "seconds", "source"]


# ---------------------------------------------------------------- DDS

class DdsInfo:
    def __init__(self, path):
        data = Path(path).read_bytes()
        if data[:4] != b"DDS ":
            raise ValueError(f"{path}: not a DDS file")
        self.header = data[:DDS_HEADER_BYTES]
        self.height, self.width = struct.unpack_from("<II", data, 12)
        self.mips = max(1, struct.unpack_from("<I", data, 28)[0])
        self.fourcc = data[84:88]
        if self.fourcc not in FOURCC_BLOCK_BYTES:
            raise ValueError(f"{path}: format {self.fourcc!r} isn't DXT1 or DXT5")
        self.payload = data[DDS_HEADER_BYTES:]
        expected = sum(level_bytes(w, h, self.fourcc) for w, h in self.levels())
        if len(self.payload) != expected:
            raise ValueError(f"{path}: {len(self.payload)} bytes of texture data, expected {expected} (extra header or surfaces?)")

    @property
    def format(self):
        return self.fourcc.decode()

    def levels(self):
        return [(max(1, self.width >> i), max(1, self.height >> i)) for i in range(self.mips)]

    def summary(self):
        return (self.format, self.width, self.height, self.mips)


def level_bytes(w, h, fourcc):
    return max(1, (w + 3) // 4) * max(1, (h + 3) // 4) * FOURCC_BLOCK_BYTES[fourcc]


def encode_level(rgba, fourcc):
    """BC1/BC3-encode one mip level with Pillow and return just its block data."""
    w, h = rgba.size
    pw, ph = max(4, (w + 3) // 4 * 4), max(4, (h + 3) // 4 * 4)
    if (pw, ph) != (w, h):   # blocks are 4 x 4; pad by repeating the edge
        a = np.asarray(rgba)
        rgba = Image.fromarray(np.pad(a, ((0, ph - h), (0, pw - w), (0, 0)), mode="edge"), "RGBA")
    buf = io.BytesIO()
    rgba.save(buf, "DDS", pixel_format=fourcc.decode())
    data = buf.getvalue()
    if data[84:88] != fourcc:
        raise RuntimeError(f"Pillow wrote {data[84:88]!r}, not {fourcc!r}")
    blocks = data[DDS_HEADER_BYTES:]
    if len(blocks) != level_bytes(w, h, fourcc):
        raise RuntimeError(f"{w}x{h} level encoded to {len(blocks)} bytes, expected {level_bytes(w, h, fourcc)}")
    if w * h < 64 * 64:   # small mips of busy images err a lot legitimately (four colours per 4 x 4 block)
        return blocks
    back = np.asarray(Image.open(io.BytesIO(data)).convert("RGBA"), dtype=np.int16)
    err = np.abs(back - np.asarray(rgba, dtype=np.int16))[..., :3].mean()
    if err > MAX_ENCODE_ERROR:
        raise RuntimeError(f"{w}x{h} level decodes {err:.1f}/255 away from what was encoded: the encoder is broken")
    return blocks


def write_dds(rgb, alpha, like, path):
    """Write rgb (PIL RGB at like's size) with the given alpha (uint8 array) as a DDS shaped exactly like
    `like`: same header bytes, format, dimensions and mip count. Each mip is resized from the full image.
    For DXT5 the alpha blocks (the first 8 bytes of every 16-byte block) are copied from the original
    byte for byte, so its alpha is exactly the original's at every mip level."""
    if rgb.size != (like.width, like.height):
        raise ValueError(f"image is {rgb.size}, original is {like.width}x{like.height}")
    full = Image.merge("RGBA", (*rgb.split(), Image.fromarray(alpha, "L")))
    data = bytearray()
    for i, (w, h) in enumerate(like.levels()):
        level = full if i == 0 else full.resize((w, h), Image.Resampling.LANCZOS)
        data += encode_level(level, like.fourcc)
    if like.fourcc == b"DXT5":
        a = np.frombuffer(bytes(data), np.uint8).reshape(-1, 16).copy()
        a[:, :8] = np.frombuffer(like.payload, np.uint8).reshape(-1, 16)[:, :8]
        data = a.tobytes()
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(like.header + bytes(data))
    out = DdsInfo(path)   # re-read the header and the data size from disk
    if out.summary() != like.summary():
        raise RuntimeError(f"{path.name}: wrote {out.summary()}, original is {like.summary()}")
    return out


def decode_top(path):
    return Image.open(path).convert("RGBA")


# ---------------------------------------------------------------- Atlas

def ssl_context():
    # python.org builds on macOS ship without system certificates; use certifi if it is there.
    try:
        import certifi  # noqa: optional, not required
        return ssl.create_default_context(cafile=certifi.where())
    except ImportError:
        return ssl.create_default_context()


CTX = ssl_context()


def request(method, url, token=None, body=None, raw=None, headers=None, timeout=120):
    data = json.dumps(body).encode() if body is not None else raw
    req = urllib.request.Request(url, data=data, method=method)
    if token:
        req.add_header("User-Agent", USER_AGENT)
        req.add_header("Authorization", f"Bearer {token}")
    if body is not None:
        req.add_header("Content-Type", "application/json")
    for k, v in (headers or {}).items():
        req.add_header(k, v)
    with urllib.request.urlopen(req, timeout=timeout, context=CTX) as resp:
        return resp.read()


def send(method, path, token, body=None, tries=4):
    """Atlas API call with retries on the 429/5xx and timeouts it gives under load."""
    for attempt in range(tries):
        try:
            return json.loads(request(method, BASE_URL + path, token, body) or b"{}")
        except urllib.error.HTTPError as e:
            text = e.read()[:300].decode(errors="replace")
            if e.code in (429, 500, 502, 503, 504) and attempt < tries - 1:
                time.sleep(2 * (attempt + 1))
                continue
            raise RuntimeError(f"{method} {path}: HTTP {e.code} {text}") from None
        except (urllib.error.URLError, TimeoutError) as e:
            if attempt < tries - 1:
                time.sleep(2 * (attempt + 1))
                continue
            raise RuntimeError(f"{method} {path}: {e}") from None


def upload_png(png, name, token):
    asset = send("POST", "/api/v1/assets", token, {"filename": name, "content_type": "image/png", "size_bytes": len(png)})
    up = asset["upload"]
    request(up.get("method", "PUT"), up["url"], raw=png, headers=up.get("headers") or {"Content-Type": "image/png"}, timeout=300)
    done = send("POST", f"/api/v1/assets/{asset['asset_id']}/complete", token)
    return asset["asset_id"], done.get("metadata") or {}


def send_size(w, h, budget):
    """The size a texture is sent at. The engine blurs at most budget x budget pixels in one pass, and the
    asset upload itself shrinks anything over MAX_UPLOAD_SIDE on a side, so bigger textures are halved
    until they fit and the result is scaled back up. The alternative, tiling, isn't used: tiles are
    blurred independently, so the echoes would stop at the tile edges."""
    while w * h > budget * budget or max(w, h) > MAX_UPLOAD_SIDE:
        w, h = w // 2, h // 2
    return w, h


def submit_params(preset):
    params = dict(PRESETS[preset])
    if params.get("execution_mode") not in SIMULATOR_MODES or any(params.get(k) for k in HARDWARE_KEYS):
        sys.exit(f"preset {preset} would not run on the simulator: {params}")
    return params


def blur(rgb, name, params, token, log):
    """Send one RGB image to blur-v1 and return (result RGB image, job id, upload metadata)."""
    buf = io.BytesIO()
    rgb.save(buf, "PNG")
    asset_id, meta = upload_png(buf.getvalue(), Path(name).stem + ".png", token)
    if meta.get("resized"):
        log(f"  upload metadata says resized: {meta}")
    job = send("POST", f"/api/v1/engines/{ENGINE}/process", token, {"input_files": {"image": asset_id}, "params": params})
    job_id = job["job_id"]
    log(f"  job {job_id}")
    start, last = time.time(), None
    while True:
        status = send("GET", f"/api/v1/jobs/{job_id}/status", token).get("status")
        if status != last:
            log(f"  {time.time() - start:6.1f} s  {status}")
            last = status
        if status == "completed":
            break
        if status in ("failed", "error", "cancelled", "canceled"):
            detail = send("GET", f"/api/v1/jobs/{job_id}", token)
            raise RuntimeError(f"job {job_id} {status}: {json.dumps(detail)[:400]}")
        if time.time() - start > TIMEOUT_SECONDS:
            raise RuntimeError(f"job {job_id} still {status} after {TIMEOUT_SECONDS} s")
        time.sleep(POLL_SECONDS)
    outputs = send("GET", f"/api/v1/jobs/{job_id}/result", token).get("outputs") or []
    out = next((o for o in outputs if o.get("slot") == "result" and o.get("url")), None) or next((o for o in outputs if o.get("url")), None)
    if not out:
        raise RuntimeError(f"job {job_id}: result has no output file")
    img = Image.open(io.BytesIO(request("GET", out["url"], timeout=300))).convert("RGB")
    if img.size != rgb.size:
        raise RuntimeError(f"job {job_id}: sent {rgb.size}, got back {img.size}")
    return img, job_id, meta


# ---------------------------------------------------------------- planning

def lod_partner(name, names):
    stem = name[:-4]
    if stem.endswith("_lod"):
        partner = stem[:-4] + ".dds"
        if partner in names:
            return partner
    return None


def plan(input_dir, files):
    """Work out what each file needs: ('atlas',), ('lod', partner) or ('duplicate', first)."""
    all_names = sorted(p.name for p in input_dir.glob("*.dds"))
    chosen = files or all_names
    missing = [f for f in chosen if f not in all_names]
    if missing:
        sys.exit(f"not in {input_dir}: {', '.join(missing)}")
    infos = {n: DdsInfo(input_dir / n) for n in all_names}
    steps, first_by_content = {}, {}
    for name in chosen:
        partner = lod_partner(name, all_names)
        if partner:
            steps[name] = ("lod", partner)
            continue
        digest = hashlib.sha1(infos[name].header[12:20] + infos[name].payload).hexdigest()   # same dims and data
        if digest in first_by_content:
            steps[name] = ("duplicate", first_by_content[digest])
        else:
            first_by_content[digest] = name
            steps[name] = ("atlas",)
    # A LOD whose partner wasn't asked for still needs the partner processed.
    for name, step in list(steps.items()):
        if step[0] == "lod" and step[1] not in steps:
            steps[step[1]] = ("atlas",)
    order = sorted(steps, key=lambda n: (steps[n][0] != "atlas", n))
    return infos, steps, order


# ---------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--input", required=True, type=Path, help="folder of original .dds files")
    ap.add_argument("--output", required=True, type=Path, help="folder for the processed .dds files")
    ap.add_argument("--strength", required=True, choices=sorted(PRESETS), help="which preset to use")
    ap.add_argument("--files", nargs="*", help="only these file names (default: every .dds in --input)")
    ap.add_argument("--dry-run", action="store_true", help="print the plan and stop, sending nothing")
    args = ap.parse_args()

    infos, steps, order = plan(args.input, args.files)
    params = submit_params(args.strength)
    atlas_jobs = [n for n in order if steps[n][0] == "atlas" and not (args.output / "_raw" / (n[:-4] + ".png")).exists()]
    print(f"{len(order)} files, {sum(steps[n][0] == 'atlas' for n in order)} unique to blur, "
          f"{len(atlas_jobs)} Atlas jobs to send (the rest reuse a saved result), preset {args.strength}: {json.dumps(params)}")
    if args.dry_run:
        for n in order:
            i = infos[n]
            print(f"  {n:70s} {i.format} {i.width}x{i.height} mips {i.mips}  {' '.join(steps[n])}")
        return

    token = os.environ.get("MOTH_API_TOKEN")
    if atlas_jobs and not token:
        sys.exit("MOTH_API_TOKEN is not set")

    args.output.mkdir(parents=True, exist_ok=True)
    raw_dir = args.output / "_raw"
    raw_dir.mkdir(exist_ok=True)
    manifest_path = args.output / "manifest.csv"
    old_rows = {}
    if manifest_path.exists():
        with manifest_path.open() as f:
            old_rows = {r["texture"]: r for r in csv.DictReader(f)}
    rows, processed = {}, {}
    params_text = json.dumps(params, sort_keys=True)

    for name in order:
        info, step = infos[name], steps[name]
        out_path = args.output / name
        alpha = np.asarray(decode_top(args.input / name))[..., 3]
        print(f"{name}  {info.format} {info.width}x{info.height} mips {info.mips}  ({' '.join(step)})", flush=True)
        if step[0] == "atlas":
            raw = raw_dir / (name[:-4] + ".png")
            start = time.time()
            sw, sh = send_size(info.width, info.height, params["size"])
            if raw.exists():
                result = Image.open(raw).convert("RGB")
                old = old_rows.get(name, {})
                job_id, seconds, source = old.get("atlas_job_id", ""), old.get("seconds", ""), old.get("source") or "saved result"
                print(f"  reusing {raw}")
            else:
                rgb = decode_top(args.input / name).convert("RGB")
                if (sw, sh) != rgb.size:
                    rgb = rgb.resize((sw, sh), Image.Resampling.LANCZOS)
                    print(f"  SCALED: sending at {sw}x{sh}, scaled back up to {info.width}x{info.height} afterwards")
                result, job_id, meta = blur(rgb, name, params, token, print)
                result.save(raw)
                seconds = f"{time.time() - start:.1f}"
                source = "atlas"
                if (sw, sh) != (info.width, info.height):
                    source += f"; SCALED: blurred at {sw}x{sh} and scaled back up to {info.width}x{info.height}"
            if result.size != (info.width, info.height):
                result = result.resize((info.width, info.height), Image.Resampling.LANCZOS)
            processed[name] = result
            write_dds(result, alpha, info, out_path)
            rows[name] = [name, "", str(out_path), job_id, ENGINE, params_text, seconds, source]
        elif step[0] == "duplicate":
            src = processed[step[1]]
            write_dds(src, alpha, info, out_path)
            r = rows[step[1]]
            rows[name] = [name, "", str(out_path), r[3], ENGINE, params_text, "0", f"same texture data as {step[1]}"]
        else:   # lod
            src = processed[step[1]].resize((info.width, info.height), Image.Resampling.LANCZOS)
            write_dds(src, alpha, info, out_path)
            rows[name] = [name, "", str(out_path), rows[step[1]][3], ENGINE, params_text, "0", f"{step[1]} shrunk to {info.width}x{info.height}, not sent to Atlas"]
        out = DdsInfo(out_path)
        print(f"  wrote {out_path}  {out.format} {out.width}x{out.height} mips {out.mips}  matches original", flush=True)

    merged = {**old_rows, **{n: dict(zip(MANIFEST_FIELDS, r)) for n, r in rows.items()}}
    with manifest_path.open("w", newline="") as f:
        w = csv.DictWriter(f, MANIFEST_FIELDS)
        w.writeheader()
        for n in sorted(merged):
            w.writerow(merged[n])
    print(f"manifest: {manifest_path}")


if __name__ == "__main__":
    main()
