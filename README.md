# Q-DAY

A mission for GTA V, built with Claude Code. You help Lester build a quantum computer, it breaks the world's encryption, money stops existing and Los Santos turns on itself.

The quantum computer is real: every error rate on screen comes from an Atlas job, and Q-Day itself is a job on IBM quantum hardware whose random numbers decide how the riot plays out.

**Read first:** the overview doc (story, how Atlas is used, what's real and what's dramatised): https://claude.ai/artifact/H8a1vmAToKfQ4RwBYN83W7

## What's in here

| Folder | What it is |
| --- | --- |
| `mod/` | The mission itself. `QDay.cs` is the game code (C#, ScriptHookVDotNet 3), `Atlas.cs` talks to Atlas over HTTPS on background threads, and `Log.cs` is the logger. `bin/QDay.dll` is the built copy that goes in the game (its NuGet dependencies are merged in). Map positions are hardcoded in `Locations.Default` in `QDay.cs`. |
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
- The Atlas key goes only in the game's `scripts\QDay.ini` (`[Atlas] ApiKey`). Never commit it: the repo's `mod/QDay.ini` keeps it empty. Without a key the mission plays from saved real results.
- Story mode only. Never open GTA Online with the mod installed.

## How a delivery is judged
The three heists bring back three parts of one machine: a fridge from Humane Labs, a command system from the FIB Building and a chip from Fort Zancudo. Atlas measures each part's error rate after delivery, and crashing the truck on the way back adds to it, up to 5 points. Each part has to come in under its own target, 11%, 6% and then 2%, or the player goes back for the same part. Gunfire doesn't damage the truck, so only the driving counts. If a live measurement takes more than 3 minutes, the mission uses that part's saved result instead.

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
`dotnet build` in `mod/`, copy `bin/QDay.dll` and `QDay.ini` into `GTAV/scripts/` (set `Debug = true` there for the F5-F9 playtest keys), then press **F11** in game to reload
scripts (not Insert).
