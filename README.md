# Q-DAY

A mission for GTA V, built with Claude Code. You help Lester build a quantum computer, it breaks the world's encryption, money stops existing and Los Santos turns on itself.

The quantum computer is real: every error rate on screen comes from an Atlas job, and Q-Day itself is a job on IBM quantum hardware whose random numbers decide how the riot plays out.

**Read first:** the overview doc (story, how Atlas is used, what's real and what's dramatised): https://claude.ai/artifact/H8a1vmAToKfQ4RwBYN83W7

## What's in here

| Folder | What it is |
| --- | --- |
| `mod/` | The mission itself. `QDay.cs` is all the game code (C#, ScriptHookVDotNet 3). `bin/QDay.dll` is the built copy that goes in the game. `locations.json` holds the map positions. |
| `sidecar/` | The helper program that talks to Atlas. The game writes a request file, the helper runs the job and writes the result back. It holds the Atlas key so the game never does. |
| `recorded/` | Real Atlas results saved on 21 Oct 2026. Used when there is no key or Atlas is slow. |
| `setup/` | How to set up a Windows PC: `WINDOWS_SETUP.md` (start here), plus the two install scripts. |
| `log/` | Working notes from testing. `MORNING.md` is the running log of what broke and why. |
| `tools/remote-box/` | Scripts used to drive a cloud PC from a Mac. You won't need these. |
| `PLAN.md` | The original plan and design notes (21 Oct). Some early details have since changed; the code is the source of truth. |
| `CLAUDE.md` | Instructions for Claude Code when it works in this repo. |

## Play it or work on it
You need a Windows PC with GTA V Legacy. Follow `setup/WINDOWS_SETUP.md`. The two things that catch people out:
- **BattlEye must be off** in the Rockstar launcher and Steam needs `-nobattleye`, or the game crashes on start.
- **ScriptHookVDotNet must be 3.7.0 nightly or newer.** 3.6.0 loads but the mission fails to start.

To change the mission, edit `mod/QDay.cs`, rebuild (`dotnet build` in `mod/`), and copy `mod/bin/QDay.dll` into the game's `scripts` folder.

## Rules
- Never put the Atlas key in a file. It lives in the `MOTH_API_TOKEN` environment variable.
- Story mode only. Never open GTA Online with the mod installed.

## Status (3 Nov 2026)
The mission runs end to end in story mode. The leak ticker now scrolls at a
constant speed independent of framerate, and the Q-Day riot has been seen for the
first time: 64 pedestrians armed, where the previous attempt found one. The mod's
own log finally lands in `scripts\QDay.log`, which also means `QDay.locations.json`
is read at last — until now the mod silently ran on hardcoded defaults and every
edit to that file was discarded, so the chip coordinates still need verifying
in-game before they can be trusted.

Still to do: the three heists need their own locations, guards and exits; the
escape (roadblocks, briefcase, plane) is unbuilt; two cutscenes are outstanding.
Open playtest items are tracked as GitHub issues. Deadline: 19 Nov 2026.

Build on any machine with the .NET SDK — the Windows gaming PC is easiest:
`dotnet build` in `mod/`, copy `bin/QDay.dll` and `locations.json` (as
`QDay.locations.json`) into `GTAV/scripts/`, then press **F11** in game to reload
scripts (not Insert).
