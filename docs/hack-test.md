# Hack: in-game test

Alejandro, this is the test for `feature/hack`, which adds the "Fix the errors" hack that gates the FIB Building and Fort Zancudo heists. The pull request stays a draft until it passes, so could you run through this on the Windows PC and report back on the pull request, with `scripts\QDay.log` attached if anything fails?

The puzzle's rules have been tested outside the game with `dotnet test tests/HackPuzzle.Tests`, covering which checks light, solving it both ways, the marks and 1,000 generated layouts at each level. None of the game side has run in GTA yet, so the drawing, controls, terminal, truck lock, sound and clean-up are all untested. The terminal positions are placeholders 4 m from each truck, so a terminal in a wall or in the road is expected and isn't a failure, but please note where it should go.

## Setup

Build `mod/`, copy `bin/QDay.dll` to `scripts\`, set `Debug = true` in `QDay.ini` and press F11 in game to reload scripts. F9 starts the mission from anywhere, and F5 in a heist teleports to the current chip. The hack's log lines in `scripts\QDay.log` start with `hack`.

## How it should play

The panel (redrawn in the 11 Nov visual pass) dims the world and opens as a dark terminal window with a cyan frame, corner brackets, a faint grid and scanlines. A header strip names the terminal and blinks `BREACH`. The qubits are framed squares labelled q0, q1 and so on, with a short bar between each pair of neighbours: a bar glows and pulses red when its two qubits disagree, and is a thin grey line when they agree. The player moves the yellow corner-bracket cursor and flips a qubit, which flashes white and gets a small glowing cyan square so they can undo it. The status row under the grid counts the red lights and flips and shows the timer as a draining bar. It's solved when every bar is dim: the screen flashes green, the squares turn green in a wave, `SYSTEM OPEN` types itself out, and the panel closes after about 1.5 seconds. The FIB Building is a 4 by 4 grid with two flipped qubits and a 90-second timer. Fort Zancudo is 5 by 5 with three flipped qubits, a 60-second timer, and a new error every 15 seconds: a random qubit flips by itself, its square flashes amber, and the header shows `NEW ERROR`. The header and status text (`FIB BUILDING // FLOOR 49 // SECURE TERMINAL`, `BREACH`, `RED LIGHTS` and so on) are placeholders still to be agreed in `docs/script.md`. Everything is drawn with rectangles and GTA's monospace font; `TextPxPerScale` in `Hack.cs` turns the design's pixel sizes into GTA text scale and is a guess.

## A. Opening it

1. **FIB terminal:** in the FIB Building heist, a terminal with a yellow ground marker and a small green blip appears near the van once the scene spawns. The subtitle reads `Hack the terminal to release the truck.` and the log shows `hack terminal for FIB at ...`. Please note whether the terminal sits sensibly and whether the electrical box model looks right, or whether it fell back to a laptop.
2. **Zancudo terminal:** the same at Fort Zancudo, with `hack terminal for Zancudo`.
3. **Help text:** on foot within about 1.5 m of the terminal, the help box reads `Press E to hack the terminal`, with GTA's own key icon. It doesn't show in a vehicle.
4. **Opening:** pressing E opens the panel. The log shows `hack open FIB: 4x4, 2 flipped, layout ...` (or `Zancudo: 5x5, 3 flipped`). The panel grows open from a line in about a fifth of a second.
5. **F2:** F2 opens the FIB hack anywhere outside the Zancudo heist, and the Zancudo hack during it. The log shows `DEBUG F2 open hack ...`. F2 while the hack is open does nothing.

## B. Controls and drawing

6. **Keyboard:** arrow keys move the cursor, Enter flips and Backspace quits.
7. **Gamepad:** the D-pad moves, A flips and B quits.
8. **Nothing else works:** while the panel is open, the player can't walk, shoot, change weapon, open the phone or open the pause menu, and the player (or their car, with F2 in a vehicle) stays still.
9. **Input guard:** the E press that opens the hack doesn't move or flip anything. Opening with F2 while holding an arrow key also shouldn't register a move in the first quarter of a second.
10. **Prompt bar:** GTA's button prompts in the bottom right read move, flip and quit, with the right icons for keyboard and pad, and they change when you switch between them. The move icon uses an input group number I couldn't confirm, so please say if it shows the wrong buttons.
11. **Readability at 1080p:** the squares, cursor, marks and bars are clear, a lit bar is easy to tell from a dim one without relying on colour, and the instruction line fits inside the panel. The text sizes are the part most likely to be off: check the title, the header strip, the q labels and the status row against each other, and say if `TextPxPerScale` needs changing. On the 5 by 5 Zancudo grid, the panel should clear GTA's button prompts in the bottom right. The HUD panel in the top right is hidden while the hack is open.

## C. Playing it

12. **Checks update:** each flip changes the bars straight away, and the log shows `hack FIB: flip N, M checks lit`.
13. **Wrong flips and undo:** flipping the wrong qubit lights more bars and adds a mark, and flipping it again removes both. There's no other penalty.
14. **Marks:** a mark only shows what you flipped. Flipping a qubit that was already wrong still adds a mark, even though it fixes it.
15. **Solving:** with every bar dim, `SYSTEM OPEN` shows for about 1.5 seconds and then the panel closes. The log shows `hack FIB: solved in N s with M flips`, then `hack FIB solved at the terminal, truck unlocked`.
16. **Success sound:** a short sound plays on solve. The sound name is a guess, `Hack_Success` from `DLC_HEIST_BIOLAB_PREP_HACKING_SOUNDS`, so please say whether you heard anything.
17. **Solving the other way:** flipping every qubit except the faulty ones also leaves every bar dim, and counts as solved.
18. **Timers:** the bar drains from 1:30 at the FIB Building and 1:00 at Fort Zancudo, and turns red in the last 10 seconds. When it runs out, the grid gets a new layout, your marks clear, the timer restarts and the log shows `timer ran out, new layout ...`. The mission doesn't fail.
18a. **New errors at Zancudo:** every 15 seconds a random qubit flips by itself: its square flashes amber, `NEW ERROR` shows in the header for a moment, the bars change, and the log shows `hack Zancudo: new error on qubit N`. It doesn't add a mark. If it happens to flip back the last wrong qubit, the hack counts as solved. Say whether the pace feels fair; it's `ZancudoNewErrorSeconds` in `Hack.cs`.

## D. Cancelling and the truck

19. **Cancel:** Backspace or B closes the panel without solving. The log shows `hack FIB: cancelled after ...`, the objective subtitle shows again, and you can move normally.
20. **Reopening:** E at the terminal opens it again with a new layout and no marks.
21. **Truck locked:** before solving, you can't get into the truck, and walking up to it doesn't start the delivery.
22. **Truck unlocked:** after solving, the marker and blip go, the truck can be entered and the delivery starts as before.
23. **Wanted level:** the delivery starts with 4 stars from the FIB Building and 5 from Fort Zancudo, as before, and not earlier.
24. **Humane Labs:** unchanged, with the keypad to shoot and no terminal.

## E. Danger while hacking

25. **Shot at Fort Zancudo:** the base gives its own wanted level on entry. While the panel is open the player shouldn't lose health, and once it closes they take damage as normal again. Please say whether being untouchable while hacking feels right, or whether it would be better to take damage and fail, which is one constant, `HackInvulnerable` in `Hack.cs`.

## F. Clean-up

Each of these should leave the player free to move and able to take damage again, with no panel left on screen.

26. **F10:** with the hack open, F10 aborts. The log shows `hack closed`, and the terminal blip goes.
27. **Death:** set `HackInvulnerable` to `false` in `Hack.cs` and rebuild, since otherwise the player can't die with the hack open. Then open it with F2 at Fort Zancudo and let the soldiers kill you. The log shows `hack: player died` or `MISSION FAILED`, then `hack closed`.
28. **Script reload:** with the hack open, press Insert (or F11). After the reload the player can move and isn't invincible, and the terminal blip isn't duplicated.
29. **Second run:** after any of these, start the mission again and check the terminal and lock behave as the first time.

## Reporting back

For each section, please say whether it passed, and for any failure give the check number, what you saw and the matching log lines. Checks 1, 2 and 11 depend on the placeholder positions and on how it looks, and 25 is a judgement call, so your view is what's wanted there.
