#!/usr/bin/env python3
"""Record one Q-Day run on IBM hardware into recorded/, for the QDayLibrary fallback.

Usage:  MOTH_API_TOKEN=... python3 tools/record_qday.py

Sends the same comet-qrng-v1 job as QDayParams() in mod/Atlas.cs, to the same
endpoints, waits for it, and saves
    recorded/qrng_qpu_64q_<backend>_<UTC date>_<n>.json
as {"job_id": ..., "result": {"output": ...}}. A run with fewer than 64 derived
integers is refused and not saved. One run per invocation: each one spends Moth's
IBM hardware time. The key is read from MOTH_API_TOKEN and never written anywhere.
Rebuild the mod afterwards (dotnet build in mod/) to embed the new file.

Standard library only.
"""

import datetime
import json
import os
import ssl
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

BASE_URL = "https://api.mothquantum.com"
ENGINE = "comet-qrng-v1"
# Cloudflare 403s non-browser User-Agents (error code 1010).
USER_AGENT = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36"
# Keep in step with QDayParams() in mod/Atlas.cs.
QDAY_PARAMS = {
    "mode": "qpu",
    "num_qubits": 64,
    "shots": 2048,
    "output_bytes": 64,
    "bell_witness": True,
    "derive": {"integers": {"min": 0, "max": 255, "count": 64}},
}
MIN_INTS = 64
POLL_SECONDS = 3
TIMEOUT_SECONDS = 3600
RECORDED = Path(__file__).resolve().parent.parent / "recorded"


def ssl_context():
    # python.org builds on macOS ship without system certificates; use certifi if it is there.
    try:
        import certifi  # noqa: optional, not required
        return ssl.create_default_context(cafile=certifi.where())
    except ImportError:
        return ssl.create_default_context()


CTX = ssl_context()


def send(method, path, token, body=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(BASE_URL + path, data=data, method=method)
    req.add_header("User-Agent", USER_AGENT)
    req.add_header("Authorization", f"Bearer {token}")
    if data is not None:
        req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req, timeout=60, context=CTX) as resp:
            return json.loads(resp.read())
    except urllib.error.HTTPError as e:
        raise RuntimeError(f"{method} {path}: HTTP {e.code} {e.read()[:300].decode(errors='replace')}") from None


def main():
    token = os.environ.get("MOTH_API_TOKEN", "").strip()
    if not token:
        sys.exit("MOTH_API_TOKEN is not set; nothing recorded.")

    started = time.monotonic()
    submit = send("POST", f"/api/v1/engines/{ENGINE}/process", token, {"params": QDAY_PARAMS})
    job_id = submit.get("job_id")
    if not job_id:
        sys.exit(f"submit failed: {json.dumps(submit)[:300]}")
    print(f"submitted {ENGINE} job {job_id}", flush=True)

    failures = 0
    last = None
    while True:
        time.sleep(POLL_SECONDS)
        elapsed = time.monotonic() - started
        if elapsed > TIMEOUT_SECONDS:
            sys.exit(f"job {job_id} timed out after {elapsed:.0f} s")
        try:
            status = send("GET", f"/api/v1/jobs/{job_id}/status", token).get("status")
            failures = 0
        except Exception as e:  # one 502 should not kill a long QPU wait
            failures += 1
            if failures >= 5:
                raise
            print(f"status poll failed ({failures}), retrying: {e}", flush=True)
            continue
        if status != last:
            print(f"  {elapsed:6.0f} s  {status}", flush=True)
            last = status
        if status == "completed":
            break
        if status in ("failed", "error", "cancelled"):
            sys.exit(f"job {job_id} {status}")

    output = (send("GET", f"/api/v1/jobs/{job_id}/result", token).get("result") or {}).get("output")
    total = time.monotonic() - started
    if not output:
        sys.exit(f"job {job_id}: result has no output")

    prov = output.get("provenance") or {}
    backend = prov.get("backend") or "unknown"
    ints = (((output.get("random") or {}).get("derived") or {}).get("integers") or {}).get("values") or []
    grade = (output.get("entropy_report") or {}).get("grade")
    s_value = (output.get("bell_witness") or {}).get("S")

    print(f"backend          {backend}")
    print(f"atlas job        {job_id}")
    print(f"IBM job          {prov.get('provider_job_id')}")
    print(f"end to end       {total:.1f} s")
    print(f"qpu_seconds      {prov.get('qpu_seconds')}")
    print(f"grade            {grade}")
    print(f"Bell S           {s_value}")
    print(f"derived integers {len(ints)}")

    if len(ints) < MIN_INTS:
        sys.exit(f"refusing to save: {len(ints)} derived integers, need {MIN_INTS}")

    date = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d")
    n = 1
    while (path := RECORDED / f"qrng_qpu_64q_{backend}_{date}_{n}.json").exists():
        n += 1
    path.write_text(json.dumps({"job_id": job_id, "result": {"output": output}}, indent=1) + "\n")
    print(f"saved            {path.relative_to(RECORDED.parent)}")


if __name__ == "__main__":
    main()
