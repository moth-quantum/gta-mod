# Lockdown: in-game test

Alejandro, this is the test for `feature/lockdown`, which adds the escape lockdown to the mission. The pull request stays a draft until it passes, so could you run through this on the Windows PC and report back on the pull request, with `scripts\QDay.log` attached if anything fails?

The code has been built and the logic tested outside the game with `tests/LockdownCheck`, but none of it has run in GTA yet, so the `QDay.cs` hooks, the HUD and the markers are all untested. The districts, stops and wall positions are rough placeholders, so a marker in an odd place is expected at this stage and isn't a failure.

## Setup

Build `mod/`, copy `bin/QDay.dll` to `scripts\`, set `Debug = true` in `QDay.ini` and press F11 in game to reload scripts. Everything mentioned below as a log line is in `scripts\QDay.log`.

## A. F6 advances one phase per press

1. **Calibrating, last chip:** one F6 logs `DEBUG F6 in Calibrating: recorded calibration` and stops at Threshold, without running on into Q-Day. If truck damage pushes the chip over the threshold it goes back to Heist instead, which is also correct.
2. **Threshold:** one F6 logs `DEBUG F6 in Threshold: recorded qday` and stops in the riot.
3. **Riot:** one F6 logs `DEBUG F6 in QDay: skip the riot` and goes to Escape.
4. **Other cases:** F6 in Calibrating on the other chips moves on to the next heist as before, and F6 in any other phase does nothing.

## B. Fast path, using the recorded bank

Press F7 to jump to Q-Day, then F6 to skip the riot.

5. **Loading:** the log shows `lockdown: 20 districts, 36 links, 4 stops`, with no `district file refused`.
6. **Bank use:** after F7 the log says the escape will use the recorded bank, and no `graph-v1` job is submitted.
7. **HUD panel:** it sits under the main panel without overlapping it, and shows `lockdown simulation  aer`, `targets  recorded bank`, `layout k of 20` and the job ID. Check the full job ID fits. There should be no percentages and no mention of a breach, since repairs go to the log only.
8. **Markers:** blue columns appear at sealed borders. Their heights come from unchecked placeholder data, so they may sit underground or float. To compare a few with the log, take the logged bitstring: character 0 is district 0, and a link is sealed when its two districts have different characters.
9. **A route exists:** from Lester's house, drive to Strawberry, Rockford Hills, Banning and LSIA without crossing a marker. Nothing physically blocks the car yet and the placeholder walls aren't at real junctions, so this is only a rough check.
10. **Temporary exit:** press F5 to teleport to the last stop, or drive there, and the mission should end within 40 m. Check the blip and GPS route look right, as the LSIA centre is a rough guess.
11. **Ending credit:** it still names `ibm_marrakesh`, never `aer`.

## C. Riot state during Escape

12. **Money:** stays at zero with the cash readout hidden for the whole escape, and the readout comes back after the mission ends or after F10.
13. **Arming:** drive into an area you haven't been to, where new pedestrians should be armed and hostile. `riot pass` lines in the log should appear about every 4 seconds, against about every 2 in the riot.
14. **Playability:** does the drive stay playable with armed traffic at this rate, and does the frame rate hold? Please also note whether the storm carries on.

## D. Abort and failure

15. **F10 during Escape:** back to Idle, with the markers, HUD panel and blip gone and the cash readout restored.
16. **Dying during Escape:** mission failed, with the same clean-up.
17. **Second run after an abort:** no leftovers, and normally a new layout. A repeat is possible because the bank has 20 files.
18. **F7 twice during the riot:** this only applies with a live key and Q-Day numbers in play, so do it after section E if at all. The log should show `ignoring lockdownN from an earlier run` if an earlier job lands late.

## E. Real flow, live

This needs an Atlas key in the game's `scripts\QDay.ini`, never in the repo, and it spends real IBM time on the Q-Day job, as the mission already does today.

19. **Getting there:** play or use F5 through to Threshold. F6 in Calibrating is safe here, but don't press F6 in Threshold, because that uses the recorded result and no live job would be sent.
20. **Job timing:** at the start of the riot the log shows `atlas submitted graph-v1 job`, then `lockdownN stored` about 10 to 20 seconds later.
21. **Full five-minute riot:** let it run out once and check it moves into Escape on its own. The HUD should read `targets  Q-Day numbers from ibm_...` and show the live job ID.
22. **Hardware job over 180 seconds:** the bank is used and the HUD says so. That's correct behaviour, but it takes another run to see the live path.

## F. Offline

23. **`Offline = true`:** play from the riot onwards. The log shows a `bank file` line and the HUD says `recorded bank`, with the Q-Day numbers still driving the riot.

## Reporting back

For each section, please say whether it passed, and for any failure give the check number, what you saw and the matching log lines. Checks 9 and 10 depend on placeholder data, so problems there need separating from problems in the logic where you can tell. Check 14 is a judgement call, so your view on how it plays is what's wanted.
