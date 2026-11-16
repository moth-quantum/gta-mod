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

## 23 Oct, 12:00–13:05: first in-game runs on Shadow
- Mod loads and runs: HUD, F9 start, F5 teleport to Humane Labs, pickup -> DELIVERY all work.
- **Blocker on a fresh save:** the first story mission (Franklin and Lamar, script `armenian1`) auto-starts and fails as soon as Q-DAY moves Franklin, snapping him back to the alley. Killing it from the mod shows "Mission failed" (Restart only). Killing the flow controllers first avoided that at F9, but a teleport still failed it. Real fix: play Franklin and Lamar once (about 10 min) or use a save past it, then Q-DAY runs in free roam.
- Remote control from the Mac through Shadow: keys work while the game has focus, mouse doesn't map; clicks/keys sent from PowerShell (SetCursorPos/mouse_event/keybd_event) work but Alt+Tab leaves the game paused without input focus.

## 23 Oct, 13:45–14:05: full run on a 100% save
- 100% save (libertycity.net file 205368, slot 01 "The Last One") removes the story-mission blocker. Q-DAY runs cleanly in free roam.
- Captured: title, Jay briefing, Humane Labs wanted level, truck at Jay's "calibrating on Atlas", TOO NOISY 8.5%, Q-Day "Bell S = 2.71 > 2", ticker with commit hash + leaks.
- Fixed: ticker was off-screen (SHVDN UI canvas is 1280x720, not the real resolution). Added F8 = teleport to Legion Square.
- Not yet seen: the riot. Legion Square was empty in the Q-Day storm, so there were no peds to arm. Next: spawn a crowd at Q-Day, or skip the THUNDER weather until peds are armed.
- Sidecar was not running on Shadow, so the run used recorded results (F6/F7).

## 3 Nov: ticker fixed, riot finally seen, and the log was lying to us
Five fixes, each on its own branch/PR. Everything below was run on the Shadow box
against GTA V Legacy 1.0.3889 with SHVDN 3.7.0-nightly.194, story mode, audio
muted, using a combined local build of all five.

### VERIFIED in game
- **The leak ticker (#1).** Was janky because it scrolled a fixed step every
  FRAME (`x -= 0.004f`, `Interval = 0`), so its speed tracked framerate and
  lurched on every dip. Rewritten as a time-based belt: measured in game at a
  steady **150 canvas px/s while fps moved between 83 and 102**, offset wrapping
  against the head item's width rather than growing, belt 6-9 items deep. Also
  fixed: it recycled at a hardcoded `x < -0.6f` regardless of message width (long
  messages snapped back mid-sentence, short ones left the bar empty); text
  reaching the right edge WRAPPED onto a second line under the bar (GTA wraps by
  default - no log, only visible on screen); the bar covered the minimap.
- **`scripts\QDay.log` never existed, and it was not a permissions problem (#4).**
  SHVDN shadow-copies script DLLs, so `Assembly.Location` points at
  `%LOCALAPPDATA%\assembly\dl3\...\QDay.dll`. The log was being written there the
  whole time - **nine stale copies** were found in that cache. `Write()` swallowed
  every exception so nothing ever said so.
  **The serious part: `ScriptsDir` also resolves `QDay.locations.json`, which is
  not in the shadow cache - so it was never found and the mod silently ran on the
  hardcoded `Defaults` string. Every edit to locations.json has been discarded.**
  That matters because the chip coordinates in this file are listed above as
  unverified and expected to be corrected here. Recovered proof from the cache:
      locations: NO file found next to the script, using hardcoded Defaults
  After the fix (ask `AppDomain.CurrentDomain.BaseDirectory`, never
  `Assembly.Location`):
      scripts dir: ...\Grand Theft Auto V\scripts (resolved via AppDomain.BaseDirectory (has locations file))
      locations loaded from ...\scripts\QDay.locations.json
- **The riot, at last (#5).** Cause was not `SET_RIOT_MODE_ENABLED`. It was that
  `BeginThreshold` started THUNDER and then waited on the IBM queue for up to
  900s with the player frozen; a storm clears pedestrians, so by the time the
  arming loop ran there was nobody left, and the loop only reached 60m.
  Measured BEFORE: F7 + F8 to Legion Square, normally one of the busiest spots on
  the map - **one** ped at Q-Day start, and 45s later that ped was dead and the
  plaza was empty.
  AFTER (storm held until somebody is armed, radius 60m -> 150m):
      riot pass: 25 peds within 150m, armed 19 this pass, 19 total
      riot: 19 armed, starting the storm
      riot pass: 21 peds within 150m, armed  7 this pass, 33 total
      ... 64 total armed and still climbing
  In-range count churns from 25 down to 6-9 while new peds keep arriving and
  being armed - i.e. attrition. The riot is running.

### FIXED but NOT verified in game - needs a real play session
- **The riot relationship-group order (#2).** `Chaos.EnsureRelationships()` was
  called AFTER peds were assigned to the group it creates, so the first wave got
  a default (hash 0) group and had no reason to fight - and was never revisited,
  because the loop only touches peds that are still Unarmed. Correct by
  inspection and now in the build that produced the numbers above, but "are they
  fighting each other, specifically because of the relationship group" was not
  isolated visually.
- **The Delivery soft-lock (#3).** The truck IS the chip; delivery only completed
  while the player sat in that exact truck, and nothing noticed if it stopped
  existing - no timeout, no respawn. A truck destroyed on the way back left the
  mission waiting forever on an unsatisfiable marker, escapable only with F10, a
  debug key. Now fails cleanly (message, fade, full teardown, back to Idle, F9 to
  restart). **Not verified: reproducing it needs a truck destroyed during a live
  delivery run, which is real driving, not keystrokes.**

### Operational notes for anyone driving this box headlessly
- Script Hook V pops a modal "CHECK FOR SCRIPT HOOK V UPDATES?" that blocks
  GTA5.exe before SHV initialises. Answer No (we are pinned to 1.0.3889).
- **The reload key is F11**, not Insert. `ScriptHookVDotNet.ini` says
  `ReloadKeyBinding=F11`. CLAUDE.md is wrong about this.
- GTA pauses whenever it loses input focus, and anything that steals focus (a
  screenshot tool, a shell command) pauses it. While paused it ignores synthetic
  keys, including Esc - so sequences must be sent from a single process.
- The game waits on the MAIN MENU until STORY MODE is clicked; nothing loads and
  SHVDN runs no scripts until then. The menu appears SEVERAL SECONDS AFTER SHV
  logs "DX init succeeded", so clicking on that signal hits the loading screen.
- **Menu click coordinates differ between fullscreen and windowed** (the window
  has a title bar and the screen reports 1900 rather than 1964 high). Derive them
  from a screenshot; do not hardcode.
- **This box can build the mod.** .NET SDK 8.0.427 in `C:\Users\Shadow\.dotnet`
  (installed with Microsoft's dotnet-install.ps1; winget/choco are blocked, exit
  1602 = elevation declined). `dotnet build mod\QDay.csproj -c Release`, copy the
  DLL in, press F11. QDay.dll is NOT locked while the game runs. The in-box
  csc.exe is C# 5 only and cannot compile QDay.cs. CLAUDE.md's "build on the Mac"
  is stale.

## 9 Nov: to raise with the platform team
`entanglement-shader-v1` with style `frustrated` and interaction 25 fails with `Quantum circuit execution failed: Input matrix is not unitary.`, marked non-retryable. Job `5fef5932-8dcd-49db-9c5c-f19f809c9d76`: reflectance 0.45, absorption 0, layers 2, incoming_rays 4, resolution 256. The force field uses style `peaked` instead, so nothing here is waiting on it.
