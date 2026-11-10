# Q-DAY

A mission for GTA V, built with Claude Code. You help Lester build a quantum computer, it breaks the world's encryption, money stops existing and Los Santos turns on itself.

The quantum computer is real: every error rate on screen comes from an Atlas job, and Q-Day itself is a job on IBM quantum hardware whose random numbers decide how the riot plays out.

**Read first:** the overview doc (story, how Atlas is used, what's real and what's dramatised): https://claude.ai/artifact/H8a1vmAToKfQ4RwBYN83W7

## What's in here

| Folder | What it is |
| --- | --- |
| `mod/` | The mission itself. `QDay.cs` is the game code (C#, ScriptHookVDotNet 3), `Atlas.cs` talks to Atlas over HTTPS on background threads, and `Log.cs` is the logger. `bin/QDay.dll` is the built copy that goes in the game (its NuGet dependencies are merged in). Map positions are hardcoded in `Locations.Default` in `QDay.cs`. |
| `recorded/` | Real Atlas results, embedded in the DLL. Used when there is no key or Atlas is slow. Every `qrng_qpu_64q_*.json` is one recorded Q-Day run on IBM hardware. |
| `setup/` | How to set up a Windows PC: `WINDOWS_SETUP.md` (start here), plus the two install scripts. |
| `log/` | Working notes from testing. `MORNING.md` is the running log of what broke and why. |
| `tools/remote-box/` | Scripts used to drive a cloud PC from a Mac. You won't need these. |
| `tests/` | `HackPuzzle.Tests` checks the hacking minigame's rules without the game: `dotnet test tests/HackPuzzle.Tests`. |
| `PLAN.md` | The original plan and design notes (21 Oct). Some early details have since changed; the code is the source of truth. |
| `CLAUDE.md` | Instructions for Claude Code when it works in this repo. |

## Play it or work on it
You need a Windows PC with GTA V Legacy. Follow `setup/WINDOWS_SETUP.md`. The two things that catch people out:
- **BattlEye must be off** in the Rockstar launcher and Steam needs `-nobattleye`, or the game crashes on start.
- **ScriptHookVDotNet must be 3.7.0 nightly or newer.** 3.6.0 loads but the mission fails to start.

With `Debug = true`, F2 also opens the current heist's hacking minigame from anywhere.

To change the mission, edit `mod/QDay.cs`, rebuild (`dotnet build` in `mod/`), and copy `mod/bin/QDay.dll` into the game's `scripts` folder.

## How the Q-Day job runs
The Q-Day job (comet-qrng-v1, 64 qubits on IBM hardware) is sent when the mission starts, at Lester's pitch, and runs in the background through the heists. The threshold is a fixed 10 seconds. If the job is back by then, the riot runs on its random numbers. If not, or with no key, or in offline mode, the mod picks one of the recorded runs in `recorded/` at random and starts reading its numbers from a random point, so the riot and the leak ticker still come from real IBM random numbers and differ between plays. `mod/QDayPrefetch.cs` and `mod/QDayLibrary.cs` hold this logic.

## Recording more Q-Day runs
`tools/record_qday.py` (Python 3, standard library only) runs one Q-Day job and saves it to `recorded/qrng_qpu_64q_<backend>_<date>_<n>.json`:

```
MOTH_API_TOKEN=... python3 tools/record_qday.py
```

It sends the same job as the mod, prints the backend, Atlas and IBM job IDs, time taken and QPU time, and refuses to save a run with fewer than 64 random integers. The key is read from `MOTH_API_TOKEN` and never written to disk. Each run uses Moth's IBM hardware time, so record one at a time. Rebuild the mod afterwards to embed the new file.

## Rules
- The Atlas key goes only in the game's `scripts\QDay.ini` (`[Atlas] ApiKey`). Never commit it: the repo's `mod/QDay.ini` keeps it empty. Without a key the mission plays from saved real results.
- Story mode only. Never open GTA Online with the mod installed.

## Status (3 Nov 2026)
The mission runs end to end in story mode. The leak ticker now scrolls at a
constant speed independent of framerate, and the Q-Day riot has been seen for the
first time: 64 pedestrians armed, where the previous attempt found one. The mod's
own log finally lands in `scripts\QDay.log`. Map positions are now hardcoded in
`QDay.cs` (there is no locations file any more); the chip coordinates still need
verifying in-game before they can be trusted.

Still to do: the three heists need their own locations, guards and exits; the
escape (roadblocks, briefcase, plane) is unbuilt; two cutscenes are outstanding.
Open playtest items are tracked as GitHub issues. Deadline: 19 Nov 2026.

Build on any machine with the .NET SDK — the Windows gaming PC is easiest:
`dotnet build` in `mod/`, copy `bin/QDay.dll` and `QDay.ini` into `GTAV/scripts/` (set `Debug = true` there for the F4-F9 playtest keys), then press **F11** in game to reload
scripts (not Insert).
