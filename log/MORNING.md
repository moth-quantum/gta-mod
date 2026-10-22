# Morning of 22 Oct: where things are

## State of the box (QDAY, Paperspace A4000, AMS1)
- Windows set up, Chocolatey, Chrome, Steam, Git, Python 3.15, Node 24, Claude Code 2.1.307, VC++ runtimes, DirectX. Parsec's package failed; install it by hand from parsec.app in Chrome when you want a better stream than the Paperspace one.
- Atlas key set as a user variable and verified from the box: `/api/v1/me` returns your account.
- Repo cloned at `C:\gta-mod\src` over a read-only deploy key. Update it with `git -C C:\gta-mod\src pull`.
- Sidecar running hidden, bridge at `C:\gta-mod\bridge`. It answered a live calibration from the box (fake_fez, 1.69%, 7 s). Log: `C:\gta-mod\bridge\sidecar.log`. If it is not running after a reboot:
  `Start-Process python -ArgumentList 'C:\gta-mod\src\sidecar\qday_sidecar.py' -WindowStyle Hidden`
- **Installed at 00:48:** Script Hook V 3889 (ScriptHookV.dll, dinput8.dll), ScriptHookVDotNet 3.6.0 (asi + 2.dll + 3.dll), `scripts\QDay.dll` (26624 bytes, the build with F5/F6/F7) and `scripts\QDay.locations.json`, all in the Legacy folder. Log: `C:\gta-mod\bridge\install.log` ends with DONE. Steam shows Play.
- GTA V Enhanced uninstalled (it was filling the disk). Legacy installs to `C:\Program Files (x86)\Steam\steamapps\common\Grand Theft Auto V`.

## What to do
1. At 07:11 Claude launched Legacy in story mode (Steam is now set to always launch story mode, never Online), picked English in the Rockstar Games Launcher, and stopped at its Welcome screen. Press Continue there (it installs launcher components and accepts Rockstar's terms), then sign in to your Rockstar account.
2. Story mode. First load does shader warm-up and takes a few minutes.
3. Expect a notification: "Q-DAY loaded. F9 start, F10 abort, F5 teleport, F6 skip wait, F7 Q-Day now."
4. Press **F9**. Follow the blip to Humane Labs. **F5** teleports you to the current objective so the first run takes minutes, not an hour. **F6** skips an Atlas wait with recorded data if the sidecar is down. **F7** jumps straight to Q-Day to see the riot.
5. Tell Claude what you saw. Logs that matter: `scripts\QDay.log` in the game folder (the mod's own), `ScriptHookVDotNet.log` next to GTA5.exe (loader errors), `C:\gta-mod\bridge\sidecar.log`.

## Known unknowns, first things likely to be wrong
- The three chip coordinates and the lab position are from community lists, unverified. If a blip is in the sea or inside a wall, say so and we move it.
- `SET_RIOT_MODE_ENABLED` is a leftover native. If nothing happens at Q-Day, the per-ped arming loop still runs, so the riot should happen either way, just slower.
- The HUD text uses SHVDN's TextElement; if it draws off-screen at your resolution, say so.
- Never open GTA Online with the loaders installed.
