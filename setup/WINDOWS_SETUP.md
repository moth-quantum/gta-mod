# Setting up a Windows PC to play or work on Q-DAY

Works on Windows 10 or 11 with an NVIDIA or AMD graphics card. Tested on a Shadow cloud PC (Windows 11, 23 Oct 2026).

## 1. Install the tools (about 10 minutes)
Open PowerShell as administrator and paste the contents of `bootstrap_windows.ps1` from this folder.
It installs Steam, Git, Python, Claude Code and the two mod loaders, and creates `C:\gta-mod\bridge` (where the install log goes).

## 2. Get the game
1. Sign in to Steam. Install **Grand Theft Auto V Legacy** (not Enhanced). About 105 GB.
2. Launch it once, sign in to the Rockstar Games Launcher, reach story mode, then quit (Alt+F4).

## 3. Turn off BattlEye (both places, or the game crashes with 0xc000009a)
1. Rockstar Games Launcher > Settings > General > untick **BattlEye**.
2. Steam > right-click GTA V Legacy > Properties > Launch options: `-nobattleye`

This only blocks GTA Online, which must never be used with mods anyway.

## 4. Get the repo and install the mod
    git clone git@github.com:moth-quantum/gta-mod.git C:\gta-mod\src
    powershell -ExecutionPolicy Bypass -File C:\gta-mod\src\setup\install_loaders.ps1

It ends with a line saying `DONE`. It copies Script Hook V, ScriptHookVDotNet (3.7.0 nightly; 3.6.0 is too old for this game version) `QDay.dll` and `QDay.ini` into the game folder.

## 5. Connect to Atlas (optional)
Without a key the mission uses saved real results. With one, the tests run live.
Open `scripts\QDay.ini` in the game folder and set:

    [Atlas]
    ApiKey = <your key>

Only edit the game's copy. Never put the key in the repo's `mod\QDay.ini`.

## 6. Play
Press Play in Steam. In story mode, wait for "Q-DAY loaded", then walk to the yellow **Q** blip at Lester's house and press **E** (the context button) when the prompt appears.

| Key | Does |
| --- | --- |
| F10 | Abort |
| F5 | Teleport to the current objective (testing) |
| F6 | Skip an Atlas wait using saved results (testing) |
| F7 | Jump straight to Q-Day (testing) |
| F8 | Teleport to Legion Square (testing) |
| F9 | Start the mission from anywhere (testing) |

The testing keys only work with `Debug = true` in `scripts\QDay.ini`.

On a Mac keyboard streaming to a cloud PC, hold **fn** for these.

## If something goes wrong
Send these two files to whoever is helping:
- `ScriptHookVDotNet.log` in the game folder
- `scripts\QDay.log` in the game folder
