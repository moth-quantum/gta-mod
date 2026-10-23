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

## Paused 22 Oct, 07:50: GTA5.exe crash 0xc000009a with mods
- **Unmodded game runs.** With no loader it reaches Display Calibration.
- **Any mod setup crashes at start** with "unable to start correctly (0xc000009a)". No ScriptHookV.log is written, so Script Hook V never initialises.
- Ruled out: disk (100 GB free), RAM (45 GB), game/SHV version mismatch (both 1.0.3889.0), old SHVDN (swapped to nightly 3.7.0-194, still crashes), SHV's own loader (swapped to Ultimate ASI Loader 9.7.4, still crashes), missing system dinput8.dll (present).
- **Test in flight when paused:** UAL loader present, ScriptHookVDotNet.asi renamed to `.asi.off`, game launched. If it starts, the crash is in the SHVDN to ScriptHookV.dll chain; if not, any dinput8 proxy crashes on this Server 2022 box.
- **Game folder state now:** `dinput8.dll` = Ultimate ASI Loader 9.7.4; `dinput8.shv.off` = Script Hook V's original loader; `ScriptHookVDotNet.asi.off`; ScriptHookVDotNet 2/3 dlls + ini = nightly 194; ScriptHookV.dll 3889; scripts\QDay.dll unchanged.
- Steam now always launches Legacy in story mode.
- Next ideas: read the in-flight result; try ScriptHookV + NativeTrainer.asi only; check Windows Event Viewer for the faulting module; if Server 2022 itself is the cause, move to a Windows 10/11 box (Shadow PC).

## 22 Oct, 12:20–12:40: diagnosis by Claude Code on the box (transcribed from its screen; its own edits stayed on the box)
- **Test 1, Ultimate ASI Loader + ScriptHookV.dll, SHVDN off:** crashes, 0xc000009a. No ScriptHookV.log, ScriptHookVDotNet.log or QDay.log written.
- **Test 2, Script Hook V's own 2015 dinput8.dll only, no .asi, no .NET:** crashes the same way.
- **Event log / WER:** nothing for GTA5.exe, ever. 0xc000009a (STATUS_INSUFFICIENT_RESOURCES) is raised by the Windows loader during DLL init, before any handler or crash reporting, so there is no faulting module to find.
- **Ruled out:** CI/code-integrity policy, Defender, AppLocker (no logs), IFEO, Get-ProcessMitigation (all NOTSET). Plus the earlier list: disk, RAM, versions, SHVDN build, loader choice.
- **Test 3 (proxy as version.dll instead of dinput8):** inconclusive. The Rockstar Games Launcher started failing its self-update (code 241) on every launch, even with a vanilla folder, so GTA5.exe never started.
- **Conclusion:** two independent dinput8 proxies both crash GTA5.exe at load on this Windows Server 2022 box. Treat it as a Server 2022 incompatibility. Move to a Windows 10/11 machine (Shadow PC). Separately, expect to re-check the launcher 241 issue on the new box.
- Box left with a vanilla game folder; loaders kept as .off files.

## 23 Oct, 01:45: Shadow PC (Windows 11): game starts with mods
- **The 0xc000009a crash was BattlEye**, not Server 2022 and not the cloud. Fix: Rockstar Games Launcher > Settings > General > untick BattlEye, AND Steam > GTA V Legacy > Properties > Launch options `-nobattleye`. Both machines had it on.
- With that, Script Hook V and ScriptHookVDotNet load and find QDay.dll.
- **Next failure:** SHVDN 3.6.0 throws in `SHVDN.NativeMemory..cctor` (NullReferenceException) when the mod constructor calls `Notification.Show`. 3.6.0 is too old for game 1.0.3889. Use 3.7.0-nightly.194; bootstrap now downloads it.
