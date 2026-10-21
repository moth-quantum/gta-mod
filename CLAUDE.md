# Q-DAY: a GTA mod built with Claude Code on Moth Atlas

Read PLAN.md first. Section 7 is the mission design; the Atlas table in it has the measured numbers.

## Layout
- `PLAN.md`             the plan and design, keep it current
- `sidecar/`            Python bridge between the game and Atlas (game writes request_*.json, sidecar writes result_*.json). Game-agnostic.
- `recorded/`           real Atlas job results captured 2026-10-21, used offline and as the release fallback
- `mod/`                the in-game script: `QDay.cs` + `QDay.csproj` (ScriptHookVDotNet 3, C#, net48), `locations.json` (verify coords in-game). `dotnet build` here on the Mac, copy `bin/QDay.dll` and `locations.json` (as `QDay.locations.json`) into `GTAV/scripts/`.
- `log/`                playtest notes and logs from the Windows box

## Rules
- The Atlas key lives in the environment (`MOTH_API_TOKEN`), never in a file in this repo, never in the mod folder.
- Always send a browser-like User-Agent to api.mothquantum.com (Cloudflare 403s the Python default).
- Single player only. Never run with GTA Online.
- Error rate = 1 - mean ZZ computed from `measurements` counts, not from `tomography` (that is the ideal state).
- Q-Day QRNG job: `num_qubits: 64, shots: 2048, mode: qpu`. 12 qubits yields zero certified bytes on hardware. On `emu` the circuit is capped at 20 qubits including the 8 witness qubits, so the sidecar uses 12 there and the byte budget is flaky (1393 bits one run, 13 the next). Dev runs may fall back to Python random for the chaos seed; the result then says `certified: false`. Production is the qpu path, verified.
- graph-v1 `io` knobs other than seeds fail or are ignored on fake backends. Use the chip ladder instead.

## Game: GTA V Legacy
Bought as "GTA V Enhanced" on Steam (includes Legacy). Mod the Legacy build: Script Hook V 1.0.3889.0, ScriptHookVDotNet 3.6+, LemonUI optional. F9 starts the mission, F10 aborts, Insert reloads scripts. Natives used are listed in PLAN.md section 7 and were checked against alloc8or's native DB (SET_RIOT_MODE_ENABLED = 0x2587A48BC88DFADF).

## Sidecar
    MOTH_API_TOKEN=... python3 sidecar/qday_sidecar.py           # live
    python3 sidecar/qday_sidecar.py --offline                    # recorded results
    QDAY_BRIDGE=<path> ...                                       # bridge folder the game also sees
Tested 2026-10-21 on macOS against live Atlas and offline.
