# Getaway car: in-game test

Alejandro, this is the test for `feature/getaway-paint`, the stretch goal in `docs/script.md` section 9, step 5: a fast getaway car with the Entanglement Shader paint from the Quantum Paint mod (https://github.com/moth-quantum/gta-quantum-paint). Could you run through it on the Windows PC and report on the pull request, with `scripts\QDay.log` attached if anything fails?

The paint maths has been checked outside the game with `dotnet run --project tests/LockdownCheck` (the table, the viewing angle, the pearl colours and the easing). None of it has run in GTA yet. The paint is the same code the Quantum Paint mod runs, which has been played, but here it's inside Q-DAY for the first time.

## Setup

Build `mod/`, copy `bin/QDay.dll` to `scripts\`, set `Debug = true` in `QDay.ini` and reload scripts. Get to the escape's passport step (F9 to start, F7 for Q-Day, then the bank and the cash; F5 teleports to the current stop). The car's log lines start with `getaway car`.

## How it should play

When the player opens the briefcase that holds the passport, a Jester (racecar, `Jester2`) appears on the road nearest that briefcase, with a blue sports car blip named `Getaway car`, its engine running and its neon on. Its paint changes with the angle between the camera and the car: purple and green head-on, through red and orange, to silver and white side on, easing rather than stepping. The secondary coat and the pearl layer follow the same table a little further on, and the neon matches the body. The blip goes once the player gets in. Taking it is optional: any car still gets the player to the airport. The car isn't deleted at the end, so it doesn't vanish as the ending fades.

The car wears livery 11 (`GetawayCar.Livery`), whose texture `zentorno_livery_12` (`GetawayCar.LiveryTexture`) TextureSwap replaces with the embedded `recorded/getaway_livery/qblur_livery.dds` (the Quantum Paint repo's 1024 x 1024 PNG as DXT5). Texture replacement is on from the start of Q-Day to the end of the mission; with `Debug = true`, T toggles it. The log at script load says `TextureSwap: 'zentorno_livery_12' is swapped for 'qblur_livery'`, and the first frame that draws the livery says `TextureSwap: replacement 'qblur_livery' created at ...`.

## Checks

1. **Spawn:** opening the passport briefcase logs `getaway car: Jester2 at ..., heading ...`. Note whether it sits on the road sensibly at Forum Drive and at Grove Street (try both: if the first briefcase is empty, the second has the passport). If the nearest road is more than 60 m away it parks beside the player instead and logs why.
2. **Paint:** walk around the car and orbit the camera while driving. The colour sweeps with the angle and doesn't flicker. Note how it looks by day and by night.
2a. **Engine:** with the engine off the paint, secondary coat and neon are their grey, and the pearl is pearl white. Starting the engine flickers the colour in over about half a second, wobbling smoothly brighter and dimmer around the fade (strongest halfway, no hard flashes), and stopping it fades it out smoothly (`GetawayCar.HueFactor`, `QuantumPaint.HueFadeSeconds`).
3. **Neon:** all four neon strips are on and follow the body colour.
4. **Blip:** the blip is a blue sports car and goes when the player gets in.
5. **The riot:** note whether rioters steal or wreck it. If it's wrecked, the log shows `getaway car: gone or wrecked` and painting stops, with no errors.
6. **The end:** paying the pilot, or F10 to abort, stops the painting and leaves the car where it is, with no errors in the log.
7. **Livery:** the quantum blur livery shows on this car, with the paint glowing through. With `Debug = true`, T switches it back to the stock livery and again to the swap.
