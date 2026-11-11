# Q-Day and ending titles: in-game test

Alejandro, this is the test for `feature/qday-title`, which replaces the Q-DAY title card and the ending card with animated full-screen titles. The pull request stays a draft until it passes, so could you run through this on the Windows PC and report back on the pull request, with `scripts\QDay.log` attached if anything fails?

The title spells "qday activated" in Atlas Type's grid font, from an export of 120 seeds (6584 to 6703). It flickers through them and lands on the one picked by the first of the Q-Day job's random numbers. The timeline, the font file and the layout maths are tested outside the game with `dotnet test tests/QDayTitle.Tests`, but none of it has been drawn in GTA yet, so everything on screen and the sound are untested.

## How it should play

The screen dims to about half black, and the white letters change about 20 times a second for 2 seconds. Then they slow to about 3 a second, and at 3 seconds they land. The landed letters hold until 4.5 seconds, with the line `every key on earth is public    Bell S = 2.71 > 2` underneath, and then everything fades out by 5 seconds. The player keeps control throughout, and the news ticker runs underneath.

## Setup

Build `mod/`, copy `bin/QDay.dll` to `scripts\`, set `Debug = true` in `QDay.ini` and press F11 in game to reload scripts. The title's log lines in `scripts\QDay.log` start with `qday title`.

## A. It appears

1. **Through the mission:** play through to Q-Day, or use F5 and F6 to get there. The title plays as the riot starts, and the old `Q-DAY` subtitle and notification don't appear. The log shows `qday title: font loaded, "qday activated", 120 seeds 6584 to 6703`, then `qday title: start index N, landing index M, landing seed S from Q-Day number V`.
2. **Through F7:** F7 jumps to Q-Day and plays the title the same way.
3. **Screen effect:** the existing red flash (`DeathFailMPIn`) still plays at the start.
4. **Every time different:** run it two or three times. The flicker starts in a different place each time, as the start index in the log shows. The landing seed is the same each time with the recorded result, since that always has the same random numbers, and changes with a live run.

## B. Pace

5. **Flicker:** for the first 2 seconds the letters change fast and evenly, about 20 times a second.
6. **Slowdown:** from 2 to 3 seconds it slows smoothly, ending at about 3 a second, with no jump in pace.
7. **Landing:** the last change at 3 seconds lands, the log shows `qday title: landed on seed S`, and the letters then hold still until the fade.
8. **Landing seed:** the seed it lands on matches the one in the log. Atlas Type can show seed S for "qday activated", so please compare one run against it.

## C. The letters

9. **Weight:** strokes look even and at full weight, which is 0.4 of a cell. Horizontal, vertical and diagonal strokes should all be the same thickness. The weight is one constant, `StrokeWeight` in `QDayTitleTimeline.cs`, if it needs tuning.
10. **Diagonals:** they run at true 45 degrees, not squashed or stretched, at 1080p. Then change the resolution or aspect ratio in the game's display settings (to 4:3, or a windowed size with a different shape) and check again. The diagonals are drawn as small squares stepped along the stroke, which needs no texture. Please say whether their edges look smooth enough at 1080p.
11. **Joins:** corners and joins close cleanly, with no gaps or notches where strokes meet.
12. **Spacing:** the letters are evenly spaced, with a wider gap between "qday" and "activated", and the whole phrase is centred across about 70% of the width, a little above the middle of the screen.
13. **Baseline:** all the letters sit on one baseline. The "q" hangs below it, and the "d"s and "t"s reach above the others.

## D. Backdrop, line and sound

14. **Backdrop:** the dark backdrop makes the title readable over the game, and fades out with it.
15. **Line underneath:** `every key on earth is public    Bell S = 2.71 > 2` appears at the landing, not before, sits clear of the letters, and fades with them.
16. **Sound:** a short hit plays at the landing (`TextHit` from `WastedSounds`, the sound with the "WASTED" card). Say whether it plays and whether it suits.
17. **HUD and ticker:** the HUD panel in the top right is hidden while the title is up and comes back after. The news ticker along the bottom keeps running throughout.
18. **Control:** the player can move, drive and shoot while it plays.

## E. Clean-up

19. **F10:** press F10 mid-title. It disappears straight away and the log shows `qday title: stopped`.
20. **Death:** die mid-title, which the riot makes easy. It disappears, and `MISSION FAILED` shows as normal.
21. **Script reload:** press Insert (or F11) mid-title. Nothing is left on screen after the reload.
22. **Again:** after each of these, F7 plays the title again from the start.

## F. The ending title

The ending uses the same drawing with a second export, "you survived qday". Once the screen has faded to black, the title flickers and slows exactly as the Q-Day title does, lands at 3 seconds, holds for 4 seconds and fades over half a second, 7.5 seconds in all. It's drawn over a fully black backdrop, and after the game's own fade, so it shows on a black screen. The fastest way there is F7, then wait out the five-minute riot (F6 skips nothing here).

23. **After the fade:** when the riot ends, the screen fades to black and then the title appears, not before. The log shows `ending title: font loaded, "you survived qday", 120 seeds 6584 to 6703` and `ending title: start index N, landing index M, landing seed S from Q-Day number V`.
24. **Visible over black:** the letters are clearly visible on the black screen. If the screen just stays black, the draw order (`SET_SCRIPT_GFX_DRAW_ORDER(7)`, drawn after the fade) isn't working, so please say so.
25. **Pace and hold:** the flicker and slowdown look the same as the Q-Day title's, and the landed letters hold for about 4 seconds before the half-second fade. The log shows `ending title: landed on seed S`, then `ending title: finished`.
26. **Layout:** three words, evenly spaced, with the wider gaps after "you" and "survived", centred across about 70% of the width on one baseline.
27. **Credit line:** from the landing on, the line under the title reads `Random numbers generated on ibm_marrakesh via Moth Atlas. Job 6eb8fa80-d13f-4f28-8b1b-f8fb7057f16b.` with the recorded result, or the live run's machine and job ID after a live Q-Day. It fades with the title. Please check the machine and job match the Q-Day lines in the log.
28. **Nothing else:** there's no `POST-QUANTUM CRYPTOGRAPHY` card and no separate credit notification.
29. **Sound:** the same hit as the Q-Day title plays at the landing.
30. **Back to Idle after the title:** the screen fades back in and the mission returns to Idle only after the title has finished, about 11 seconds after the riot ends, with `phase Ending -> Idle` in the log after `ending title: finished`.
31. **Fallback:** this needs a build with the ending export missing, so it's optional. Delete `ending_title_gridfont.json` from `recorded/` before building, but don't commit that. The log should show `ending title: font refused, using the YOU SURVIVED QDAY card instead`, then a `YOU SURVIVED QDAY` subtitle and the credit as a notification, and the mission returns to Idle after the old 12 seconds.
32. **Clean-up:** press F10 during the ending title, and separately press Insert (or F11). Each time the title disappears, the screen fades back in rather than staying black, and the log shows `ending title: stopped`. A new run after each plays both titles again.

## Reporting back

For each section, please say whether it passed, and for any failure give the check number, what you saw and the matching log lines. Checks 9 to 13, 16, 23, 25 and 27 are about how it looks and sounds, so your view is what's wanted there.
