# Q-Day title: in-game test

Alejandro, this is the test for `feature/qday-title`, which replaces the Q-DAY title card with an animated full-screen title. The pull request stays a draft until it passes, so could you run through this on the Windows PC and report back on the pull request, with `scripts\QDay.log` attached if anything fails?

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

## F. Optional: sprite diagonals

23. **Sprites:** to compare the sprite version, set `DiagonalsAsSprites` to `true` in `QDayTitle.cs` and rebuild. Diagonals are then one rotated sprite each, using `commonmenu` / `gradient_nav`, which I couldn't confirm is plain white. The log shows `qday title: sprite commonmenu/gradient_nav is W x H` or `not found, using stamps`. Please say whether the sprite diagonals look solid and match the straight strokes, and whether they're better than the default.

## Reporting back

For each section, please say whether it passed, and for any failure give the check number, what you saw and the matching log lines. Checks 9 to 13, 16 and 23 are about how it looks and sounds, so your view is what's wanted there.
