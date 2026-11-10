# Q-DAY: a GTA mod built with Claude Code on Moth Atlas

Read README.md first, then log/MORNING.md for what broke and why. PLAN.md section 7 has the original design and the measured Atlas numbers; where it disagrees with mod/QDay.cs (it still mentions CJ and San Andreas), the code wins.

## Layout
- `PLAN.md`             the plan and design, keep it current
- `recorded/`           real Atlas job results captured 2026-10-21, embedded in QDay.dll for offline mode and as the release fallback
- `mod/`                the in-game script: `QDay.cs` + `Atlas.cs` (Atlas HTTP client, background threads) + `Log.cs` + `QDay.csproj` (ScriptHookVDotNet 3, C#, net48). Map coords are hardcoded in `Locations.Default` (verify in-game). `dotnet build` here on the Mac, copy `bin/QDay.dll` and `QDay.ini` into `GTAV/scripts/`. NuGet runtime dependencies (System.Text.Json and friends) are merged and internalized into QDay.dll by `ILRepack.targets` after every build; SHVDN and LemonUI are compile-only (`ExcludeAssets="runtime"`), the game provides them.
- `setup/`              Windows PC setup: WINDOWS_SETUP.md, bootstrap_windows.ps1, install_loaders.ps1
- `log/`                playtest notes (MORNING.md is the running log)
- `tools/remote-box/`   Mac-side helpers for driving a cloud PC by keystrokes
- `mod/Lockdown.cs`, `mod/LockdownRun.cs` escape lockdown logic and one run's state, free of game types; `mod/LockdownGame.cs` draws it; `mod/lockdown_districts.json` is the district file the game embeds, generated from the game's roads (format in `docs/lockdown-districts.md`)
- `mod/ForceField.cs` force field logic and colour lookup, free of game types; `mod/ForceFieldGame.cs` draws and pushes; `docs/forcefield-test.md` is the in-game test. The colour comes from `R_lut` only (`T_lut` is baked but unused)
- `mod/RoadTest.cs` logic and `mod/RoadTestGame.cs` natives for the Debug-only road switching experiment on F4 walls (`docs/forcefield-test.md` section G). The mission doesn't use it
- `tools/bake_entanglement.py` bakes `recorded/entanglement_lut.bin` from `entanglement-shader-v1`, keeping the raw ZIP beside it. Offline only: the game never calls that engine
- `tests/LockdownCheck/` net8 checks for the lockdown and force field logic and Atlas client: `dotnet run --project tests/LockdownCheck`
- `tools/lockdown_probe.py` measures graph-v1 outside the game; `tools/record_lockdowns.py` records `recorded/lockdown_NN.json` and must be rerun when the district file changes (`--districts FILE --out DIR` records another file's bank elsewhere). `recorded/lockdown_NN.json` is the bank for the generated file

## Rules
- The Atlas key lives only in the game's `scripts\QDay.ini` (`[Atlas] ApiKey`), never in a file in this repo. The repo's `mod/QDay.ini` keeps `ApiKey` empty. No key, or `Offline = true`, plays from the recorded results.
- Always send a browser-like User-Agent to api.mothquantum.com (Cloudflare 403s non-browser defaults).
- Single player only. Never run with GTA Online.
- Error rate = 1 - mean ZZ computed from `measurements` counts, not from `tomography` (that is the ideal state).
- Q-Day QRNG job: `num_qubits: 64, shots: 2048, mode: qpu`. 12 qubits yields zero certified bytes on hardware. On `emu` the circuit is capped at 20 qubits including the 8 witness qubits, and the byte budget is flaky (1393 bits one run, 13 the next); the mod has no emu path. If a job returns no bytes the mod falls back to System.Random for the chaos seed and the result says `certified: false`. Production is the qpu path, verified.
- The lockdown layout is a simulation (`emu`, live and bank); the hardware part is the Q-Day numbers that set the targets. Say so on the HUD and in logs. `fake` takes 13-20+ minutes at 20 qubits. No `qpu` in live runs.
- graph-v1 `io` knobs other than seeds fail or are ignored on fake backends. Use the chip ladder instead.

## Game: GTA V Legacy
Bought as "GTA V Enhanced" on Steam (includes Legacy). Mod the Legacy build: Script Hook V 1.0.3889.0, ScriptHookVDotNet 3.7.0-nightly.194+ (3.6.0 fails in NativeMemory on this build), LemonUI optional. BattlEye must be off (launcher setting + Steam `-nobattleye`) or GTA5.exe dies with 0xc000009a. The mission starts on foot at the yellow Q blip (`Locations.Default.Start`, Lester's house) with INPUT_CONTEXT (E); F10 aborts; F3-F9 playtest keys (F3 = road switching test, Shift+F3 stop, Ctrl+F3 read nodes; F4 = force field test wall; F9 = start from anywhere) only with `Debug = true` in QDay.ini; `HideEscapeRoute = true` hides the escape's GPS line, also only with Debug; Insert reloads scripts. Natives used are listed in PLAN.md section 7 and were checked against alloc8or's native DB (SET_RIOT_MODE_ENABLED = 0x2587A48BC88DFADF).
