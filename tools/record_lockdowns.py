#!/usr/bin/env python3
"""Step 6 of the escape lockdown: record the bank that offline play and late live jobs fall back on.

Runs real graph-v1 jobs on the district file, one per seed, and saves each as recorded/lockdown_NN.json,
embedded through QDay.csproj. The params come from the real BuildParams in mod/Lockdown.cs, by way of
tests/LockdownCheck, so the bank cannot drift from what the game sends. Their targets are seeded here, not
by Q-Day numbers, so the game labels them "recorded bank" and never as hardware.

Rerun whenever mod/lockdown_districts.json changes; tests/LockdownCheck fails until you do.

  MOTH_API_TOKEN=... tools/record_lockdowns.py [--jobs 20]
"""
import argparse
import json
import os
import shutil
import subprocess
import time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

from lockdown_probe import run_job

REPO = Path(__file__).resolve().parent.parent


def dotnet():
    return shutil.which("dotnet") or str(Path.home() / ".dotnet" / "dotnet")


def build_params(exe, seed):
    return json.loads(subprocess.run([*exe, "params", str(seed)], check=True, capture_output=True, text=True).stdout)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--jobs", type=int, default=20)
    ap.add_argument("--workers", type=int, default=4)
    a = ap.parse_args()

    project = REPO / "tests" / "LockdownCheck" / "LockdownCheck.csproj"
    # Built once up front; the jobs below then run the same binary.
    subprocess.run([dotnet(), "build", str(project), "-c", "Release", "-v", "q", "--nologo"], check=True)
    exe = [dotnet(), str(project.parent / "bin" / "Release" / "net8.0" / "LockdownCheck.dll")]

    def one(i):
        time.sleep(i * 3)  # the platform 503s on bursts
        seed = 100 + i
        params = build_params(exe, seed)
        if params["mode"] != "emu":
            raise SystemExit(f"the bank is emu only, BuildParams says {params['mode']}")
        output, job_id, secs = run_job(params)
        path = REPO / "recorded" / f"lockdown_{i:02d}.json"
        path.write_text(json.dumps({"job_id": job_id, "targets_seed": seed, "params": params, "result": {"output": output}}, indent=1) + "\n")
        print(f"{path.name} job={job_id} {secs:.0f}s", flush=True)

    with ThreadPoolExecutor(a.workers) as ex:
        list(ex.map(one, range(a.jobs)))


if __name__ == "__main__":
    if "MOTH_API_TOKEN" not in os.environ:
        raise SystemExit("MOTH_API_TOKEN is not set")
    main()
