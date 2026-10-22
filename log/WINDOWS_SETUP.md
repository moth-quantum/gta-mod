# Windows box setup (Paperspace A4000, AMS1, "Windows 10 (Server 2022) - Licensed", 250 GB)

Machine: QDAY, id ps2rfmhzsknm, created 21 Oct 2026. Stop it in the Paperspace console when not in use.

Order matters. Tick as you go, note anything odd in this file.

0. [ ] Click LAUNCH in the Paperspace console (browser stream). Open PowerShell as admin, paste `bootstrap.ps1` from this folder. It fixes the Server 2022 audio and IE-hardening quirks and installs Chrome, Parsec, Steam, Git, Python, Node, Claude Code.

1. [ ] Parsec installed on the box and on the Mac, logged in, 1080p60 stream works.
2. [ ] Steam installed. Buy / install "Grand Theft Auto V Enhanced". In the Steam library, also install the **Legacy** entry it unlocks. Disk: ~100 GB.
3. [ ] Launch Legacy once, get past the Rockstar Launcher login, reach story mode, save, quit. (First launch does shader warm-up.)
4. [ ] Script Hook V 1.0.3889.0 from dev-c.com: `ScriptHookV.dll` + `dinput8.dll` into the GTAV Legacy folder. Launch, confirm no crash.
5. [ ] ScriptHookVDotNet 3 (nightly or 3.6+): `ScriptHookVDotNet.asi`, `ScriptHookVDotNet3.dll`, `ScriptHookVDotNet.ini` into the game folder. Make `scripts/`.
6. [ ] Git, Python 3.11+, Node (for Claude Code), Claude Code. `git clone` this repo to `C:\gta-mod`.
7. [ ] Set `MOTH_API_TOKEN` as a user environment variable (never in a file). Set `QDAY_BRIDGE=C:\gta-mod\bridge`.
8. [ ] `python sidecar\qday_sidecar.py` in its own terminal. It prints `sidecar up`.
9. [ ] Copy `mod\bin\QDay.dll` and `mod\locations.json` -> `GTAV\scripts\QDay.dll`, `GTAV\scripts\QDay.locations.json`.
10. [ ] Launch Legacy, story mode. Expect the notification "Q-DAY loaded. Press F9." Check `scripts\QDay.log`.
11. [ ] Press F9. Follow the blip. Report what you see; Claude Code on the box reads `QDay.log`, `ScriptHookVDotNet.log` and `bridge\sidecar.log`.

Never launch GTA Online with ScriptHookV installed. Remove `dinput8.dll` first if you ever want to.

## Driving the box from the Mac (works, 22 Oct)
Clipboard does not cross the Paperspace stream. Claude Code types into it instead: `log/type_to_box.py` sends keystrokes to the Paperspace app via macOS System Events, 4 chars at a time, then screenshots with `screencapture` to verify before pressing Enter. Needs Terminal in System Settings > Privacy & Security > Accessibility. Shifted chars occasionally lose Shift (`*` arrived as `8`, `+` as `=`), and `#` did something odd in PSReadLine, so keep typed lines free of those and always verify with a screenshot. `log/bootstrap_typed.ps1` is the comment-free version of the bootstrap written for this.

Mouse clicks: System Events `click at` does not reach the stream. `log/click.js` posts real CoreGraphics mouse events: `osascript -l JavaScript log/click.js X Y` in screen points, Paperspace in front. Swift's toolchain is broken on this Mac, so it is JXA.
