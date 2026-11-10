#!/usr/bin/env python3
"""Run GTA V billboard textures through Moth Atlas's quantum blur (blur-v1) and save them back as DDS
files the game can load in place of the originals.

Setup, once, from the repo root (needs Pillow 13, pinned in tools/requirements-textures.txt):

    python3 -m venv .venv && .venv/bin/pip install -r tools/requirements-textures.txt

Run, then check what it wrote:

    .venv/bin/python tools/process_textures.py --input textures/originals --output textures/mild --strength mild
    .venv/bin/python tools/process_textures.py --input textures/originals --output textures/mild --strength mild --verify

Other options: --files a.dds b.dds (only these), --dry-run (print the plan, send nothing),
--derive-lods (see below).

Every output keeps its original's format (DXT1/DXT5), width, height and mip count: the original's
128-byte DDS header is copied unchanged and only the block data is replaced, then the output's header
is re-read and checked against the original. --verify re-reads every output from disk, compares its
header and size with the original, decodes every mip level and checks it against the top level, and
lists any mismatch by name.

- Opaque look: the image is sent as RGB without alpha, so the engine blurs all of it (it would
  otherwise use the alpha channel as a mask). The original alpha is put back afterwards; for DXT5 the
  alpha blocks are copied from the original byte for byte, so the alpha is exactly the original's.
- Simulator only: execution_mode "sim" (exact statevector simulation). Nothing here sends a job to
  hardware; the guard in submit_params refuses any preset that tries.
- Size limits: the engine blurs at most 1024 x 1024 pixels in one pass and the asset upload shrinks
  anything over 1440 on a side, so bigger textures are halved before they're sent and the result is
  scaled back up. The manifest's source column says so for each one.
- _lod files are sent to Atlas like any other texture: a LOD isn't always a smaller copy of its
  partner (sc1_25_rsd_jp_billboard_0001_lod.dds holds the billboard in its top half and a brick
  texture below). --derive-lods brings back the old behaviour, the partner shrunk to the LOD's size.
- Files with identical texture data (same dimensions and bytes) are sent once and the result copied.
- Resumable: the engine's PNG for each file is kept in <output>/_raw/ with its job id beside it, and
  any later run reuses it instead of sending the job again. The manifest is rewritten after every file.
- A failed job is retried once, then skipped and listed as FAILED in the manifest. Anything from the
  API about credits, quota or rate limits stops the run straight away (exit code 3).
- Writes <output>/manifest.csv with a row per file: texture, dictionary (blank for now), output path,
  source, Atlas job id, engine, parameters, seconds taken.

The Atlas key is read from MOTH_API_TOKEN and never written anywhere. Pillow 11.2's BC1/BC3 encoder
corrupts everything past the first 196,608 pixels of a level (and can crash), so older versions are
refused, and each encoded level of 64 x 64 or more is decoded again and compared with what went in.
"""

import argparse
import csv
import hashlib
import io
import json
import os
import re
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
MANIFEST_FIELDS = ["texture", "dictionary", "output_path", "source", "atlas_job_id", "engine", "parameters", "seconds"]
QUOTA_WORDS = re.compile(r"credit|quota|rate.?limit|too many requests|insufficient|balance|billing|payment", re.I)


class QuotaStop(Exception):
    """The API said something about credits, quota or rate limits: stop the run, don't retry."""


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
    """Atlas API call, retried on the 5xx and timeouts it gives under load. A 402, a 429 or any message
    about credits, quota or rate limits raises QuotaStop instead."""
    for attempt in range(tries):
        try:
            return json.loads(request(method, BASE_URL + path, token, body) or b"{}")
        except urllib.error.HTTPError as e:
            text = e.read()[:300].decode(errors="replace")
            if e.code in (402, 429) or QUOTA_WORDS.search(text):
                raise QuotaStop(f"{method} {path}: HTTP {e.code} {text}") from None
            if e.code in (500, 502, 503, 504) and attempt < tries - 1:
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
            if QUOTA_WORDS.search(json.dumps(detail)):
                raise QuotaStop(f"job {job_id} {status}: {json.dumps(detail)[:400]}")
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


def plan(input_dir, files, derive_lods):
    """Work out what each file needs: ('atlas',), ('duplicate', first) or, with derive_lods, ('lod', partner)."""
    all_names = sorted(p.name for p in input_dir.glob("*.dds"))
    chosen = files or all_names
    missing = [f for f in chosen if f not in all_names]
    if missing:
        sys.exit(f"not in {input_dir}: {', '.join(missing)}")
    infos = {n: DdsInfo(input_dir / n) for n in all_names}
    steps, first_by_content = {}, {}
    for name in chosen:
        partner = lod_partner(name, all_names) if derive_lods else None
        if partner:
            steps[name] = ("lod", partner)
            continue
        digest = hashlib.sha1(infos[name].header[12:20] + infos[name].payload).hexdigest()   # same dims and data
        if digest in first_by_content:
            steps[name] = ("duplicate", first_by_content[digest])
        else:
            first_by_content[digest] = name
            steps[name] = ("atlas",)
    # A derived LOD whose partner wasn't asked for still needs the partner processed.
    for name, step in list(steps.items()):
        if step[0] == "lod" and step[1] not in steps:
            steps[step[1]] = ("atlas",)
    order = sorted(steps, key=lambda n: (steps[n][0] != "atlas", n))
    return infos, steps, order


# ---------------------------------------------------------------- run

class Run:
    def __init__(self, args, infos, steps, params):
        self.args, self.infos, self.steps, self.params = args, infos, steps, params
        self.params_text = json.dumps(params, sort_keys=True)
        self.raw_dir = args.output / "_raw"
        self.manifest_path = args.output / "manifest.csv"
        self.rows = {}
        if self.manifest_path.exists():
            with self.manifest_path.open() as f:
                self.rows = {r["texture"]: r for r in csv.DictReader(f)}
        self.jobs_sent = 0
        self.failed = []

    def raw_paths(self, name):
        return self.raw_dir / (name[:-4] + ".png"), self.raw_dir / (name[:-4] + ".json")

    def row(self, name, output_path, source, job_id="", seconds=""):
        self.rows[name] = {"texture": name, "dictionary": self.rows.get(name, {}).get("dictionary", ""),
                           "output_path": str(output_path) if output_path else "", "source": source,
                           "atlas_job_id": job_id, "engine": ENGINE, "parameters": self.params_text, "seconds": seconds}

    def save_manifest(self):
        tmp = self.manifest_path.with_suffix(".tmp")
        with tmp.open("w", newline="") as f:
            w = csv.DictWriter(f, MANIFEST_FIELDS)
            w.writeheader()
            for n in sorted(self.rows):
                w.writerow({k: self.rows[n].get(k, "") for k in MANIFEST_FIELDS})
        tmp.replace(self.manifest_path)

    def result_for(self, name):
        """The engine's result for name at full size, from _raw/, or None if there isn't one."""
        raw, _ = self.raw_paths(name)
        if not raw.exists():
            return None
        info = self.infos[name]
        img = Image.open(raw).convert("RGB")
        if img.size != (info.width, info.height):
            img = img.resize((info.width, info.height), Image.Resampling.LANCZOS)
        return img

    def source_text(self, name, sw, sh):
        info = self.infos[name]
        if (sw, sh) == (info.width, info.height):
            return "processed"
        return f"processed; scaled: blurred at {sw}x{sh} and scaled back up to {info.width}x{info.height}"

    def fetch(self, name, token):
        """Make sure _raw/ has the engine's result for name, sending the job if it hasn't. Returns
        (job id, seconds, source), or None if the job failed twice."""
        info = self.infos[name]
        raw, meta_path = self.raw_paths(name)
        sw, sh = send_size(info.width, info.height, self.params["size"])
        if raw.exists():
            meta = json.loads(meta_path.read_text()) if meta_path.exists() else {}
            print(f"  reusing {raw}")
            return meta.get("job_id", ""), meta.get("seconds", ""), self.source_text(name, sw, sh)
        rgb = decode_top(self.args.input / name).convert("RGB")
        if (sw, sh) != rgb.size:
            rgb = rgb.resize((sw, sh), Image.Resampling.LANCZOS)
            print(f"  SCALED: sending at {sw}x{sh}, scaled back up to {info.width}x{info.height} afterwards")
        for attempt in (1, 2):
            start = time.time()
            try:
                self.jobs_sent += 1
                result, job_id, _ = blur(rgb, name, self.params, token, print)
            except QuotaStop:
                raise
            except Exception as e:   # noqa: anything else is one failed attempt
                print(f"  attempt {attempt} failed: {e}", flush=True)
                if attempt == 2:
                    self.failed.append((name, str(e)))
                    return None
                time.sleep(5)
                continue
            seconds = f"{time.time() - start:.1f}"
            result.save(raw)
            meta_path.write_text(json.dumps({"job_id": job_id, "seconds": seconds, "sent_at": [sw, sh], "params": self.params}) + "\n")
            return job_id, seconds, self.source_text(name, sw, sh)

    def write(self, name, img, out_path):
        """Encode img as name's DDS, unless an output newer than its raw result is already there."""
        raw, _ = self.raw_paths(self.steps[name][1] if self.steps[name][0] != "atlas" else name)
        if out_path.exists() and raw.exists() and out_path.stat().st_mtime >= raw.stat().st_mtime:
            if DdsInfo(out_path).summary() == self.infos[name].summary():
                return
        alpha = np.asarray(decode_top(self.args.input / name))[..., 3]
        write_dds(img, alpha, self.infos[name], out_path)

    def process(self, name, token):
        info, step = self.infos[name], self.steps[name]
        out_path = self.args.output / name
        print(f"{name}  {info.format} {info.width}x{info.height} mips {info.mips}  ({' '.join(step)})", flush=True)
        if step[0] == "atlas":
            got = self.fetch(name, token)
            if got is None:
                self.row(name, "", "FAILED: job failed twice, skipped")
                return
            job_id, seconds, source = got
            self.write(name, self.result_for(name), out_path)
            self.row(name, out_path, source, job_id, seconds)
        elif step[0] == "duplicate":
            first = self.rows.get(step[1], {})
            img = self.result_for(step[1])
            if img is None:
                self.row(name, "", f"FAILED: duplicate of {step[1]}, which failed")
                return
            self.write(name, img, out_path)
            self.row(name, out_path, f"duplicate of {step[1]}", first.get("atlas_job_id", ""), "0")
        else:   # lod, only with --derive-lods
            img = self.result_for(step[1])
            if img is None:
                self.row(name, "", f"FAILED: derived from {step[1]}, which failed")
                return
            self.write(name, img.resize((info.width, info.height), Image.Resampling.LANCZOS), out_path)
            self.row(name, out_path, f"derived: {step[1]} shrunk to {info.width}x{info.height} (--derive-lods)",
                     self.rows.get(step[1], {}).get("atlas_job_id", ""), "0")
        print(f"  wrote {out_path}", flush=True)


# ---------------------------------------------------------------- verify

def decode_level(info, payload, offset, w, h):
    """Decode one mip level by giving Pillow a one-level header with the level's dimensions."""
    hdr = bytearray(info.header)
    struct.pack_into("<II", hdr, 12, h, w)
    struct.pack_into("<I", hdr, 28, 1)
    n = level_bytes(w, h, info.fourcc)
    return Image.open(io.BytesIO(bytes(hdr) + payload[offset:offset + n])).convert("RGBA"), offset + n


def verify(input_dir, output_dir, names):
    """Re-read every output from disk and check it against its original. Returns a list of problems."""
    problems = []
    for name in names:
        out_path = output_dir / name
        if not out_path.exists():
            problems.append(f"{name}: no output")
            continue
        try:
            orig, out = DdsInfo(input_dir / name), DdsInfo(out_path)
        except Exception as e:
            problems.append(f"{name}: unreadable: {e}")
            continue
        if out.summary() != orig.summary():
            problems.append(f"{name}: output {out.summary()}, original {orig.summary()}")
            continue
        if out.header != orig.header:
            problems.append(f"{name}: header bytes differ from the original's")
        if out.fourcc == b"DXT5":
            a = np.frombuffer(out.payload, np.uint8).reshape(-1, 16)[:, :8]
            b = np.frombuffer(orig.payload, np.uint8).reshape(-1, 16)[:, :8]
            if not np.array_equal(a, b):
                problems.append(f"{name}: DXT5 alpha blocks differ from the original's")
        offset, top = 0, None
        for i, (w, h) in enumerate(out.levels()):
            try:
                img, offset = decode_level(out, out.payload, offset, w, h)
            except Exception as e:
                problems.append(f"{name}: mip {i} ({w}x{h}) doesn't decode: {e}")
                break
            if img.size != (w, h):
                problems.append(f"{name}: mip {i} decodes at {img.size}, expected {w}x{h}")
                break
            if i == 0:
                top = img
                continue
            if w * h >= 64 * 64:   # each level should look like the top level shrunk
                ref = np.asarray(top.resize((w, h), Image.Resampling.LANCZOS), dtype=np.int16)
                err = np.abs(np.asarray(img, dtype=np.int16) - ref)[..., :3].mean()
                if err > MAX_ENCODE_ERROR:
                    problems.append(f"{name}: mip {i} ({w}x{h}) is {err:.1f}/255 away from the top level shrunk")
    return problems


# ---------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--input", required=True, type=Path, help="folder of original .dds files")
    ap.add_argument("--output", required=True, type=Path, help="folder for the processed .dds files")
    ap.add_argument("--strength", required=True, choices=sorted(PRESETS), help="which preset to use")
    ap.add_argument("--files", nargs="*", help="only these file names (default: every .dds in --input)")
    ap.add_argument("--dry-run", action="store_true", help="print the plan and stop, sending nothing")
    ap.add_argument("--verify", action="store_true", help="check the outputs against the originals, sending nothing")
    ap.add_argument("--derive-lods", action="store_true",
                    help="make a _lod file by shrinking its processed partner instead of sending it (off by default)")
    args = ap.parse_args()

    infos, steps, order = plan(args.input, args.files, args.derive_lods)
    if args.verify:
        problems = verify(args.input, args.output, sorted(steps))
        print(f"verified {len(steps)} outputs: {len(problems)} problem(s)")
        for p in problems:
            print(f"  {p}")
        sys.exit(1 if problems else 0)

    params = submit_params(args.strength)
    to_send = [n for n in order if steps[n][0] == "atlas" and not (args.output / "_raw" / (n[:-4] + ".png")).exists()]
    print(f"{len(order)} files, {sum(steps[n][0] == 'atlas' for n in order)} unique to blur, "
          f"{len(to_send)} Atlas jobs to send (the rest reuse a saved result), preset {args.strength}: {json.dumps(params)}")
    if args.dry_run:
        for n in order:
            i = infos[n]
            print(f"  {n:70s} {i.format} {i.width}x{i.height} mips {i.mips}  {' '.join(steps[n])}")
        return

    token = os.environ.get("MOTH_API_TOKEN")
    if to_send and not token:
        sys.exit("MOTH_API_TOKEN is not set")
    (args.output / "_raw").mkdir(parents=True, exist_ok=True)
    run = Run(args, infos, steps, params)
    start = time.time()
    try:
        for name in order:
            run.process(name, token)
            run.save_manifest()
    except QuotaStop as e:
        run.save_manifest()
        print(f"STOPPED: the API reported a credit, quota or rate limit problem: {e}")
        print(f"{run.jobs_sent} job(s) sent this run. Re-run the same command to carry on once it's sorted.")
        sys.exit(3)
    print(f"done in {time.time() - start:.0f} s: {run.jobs_sent} job(s) sent this run, {len(run.failed)} failed")
    for name, err in run.failed:
        print(f"  FAILED {name}: {err}")
    print(f"manifest: {run.manifest_path}")


if __name__ == "__main__":
    main()
