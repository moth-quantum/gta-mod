# Force field: in-game test

This is the test for `feature/forcefield`, which replaces the blue columns on sealed roads with a force field. Could you run through it on the Windows PC and report back with `scripts\QDay.log` attached if anything fails?

The maths has been tested outside the game with `tests/LockdownCheck`: turning a wall record into a line, working out which side of it the player is on, catching a crossing between ticks and looking up the colour. In 20,000 simulated drives at up to 120 m/s, none got through. None of it has run in GTA yet, so everything you can see or feel below is untested, including the drawing, the physics, the particles, the blips, the frame rate and the clean-up.

The wall colour comes from `R_lut` only, a table baked once from `entanglement-shader-v1` (job `576f2916-8968-4404-bfe4-97ccdd96141a`, peaked settings) and embedded in the DLL. `T_lut` is baked too, but nothing reads it, so the alpha is a fixed value. The shimmer is the shader's film thickness drifting 50 nm either way over 8 seconds, so every colour on screen is a table lookup and the game makes no engine calls. `docs/swatch_B_peaked.png` shows what the table gives across angle and phase: colour bands that turn close to white at grazing angles.

The look was redone in the 11 Nov visual pass. Each wall is one layer, a grid of 12 by 6 cells (it was two layers of 24 by 8, which made 1,536 draw calls a wall and crashed the game; about 1,400 a frame is the limit). Only the 4 nearest walls in range are drawn, at 288 calls each, and drawing stops at 1,200 calls a frame whatever happens. Walls further away still push, but aren't drawn and have no particles. Colour, haze and light are worked out at every grid corner and blended across each triangle with `DRAW_TEXTURED_POLY_WITH_THREE_COLOURS`, so the wall is a smooth veil with no strips. The haze is strongest at the road and fades towards the top and both ends. The film thickness also varies across the wall in two slow waves, so the colours swirl. Over it is a QPAM light: 8 frames baked once on Qiskit Aer by `tools/bake_qpam_light.py` (a caustic pattern through QPAM, RX 0.3 pi, 2000 shots) and embedded in the DLL, tiled every 4 m, changing 4 times a second and drifting upwards, which brightens and thickens the wall where it falls. The three-colour native needs a texture underneath: it uses the centre of `deadline_trail_01` from the `deadline` dictionary, which Rockstar draws its Deadline trails with. If that dictionary doesn't stream in within 10 seconds, the walls fall back to flat `DRAW_POLY` cells without the light.

The placeholder district file has walls at the midpoints between districts rather than on roads, at rough heights, and they only push when the player is between 2 m below and 6 m above the wall's height, so most tests use the F4 test wall instead. F4 makes a wall in the same way as a real one, so a problem with it is a real problem. Placeholder walls floating or letting you through because of their height aren't failures.

## Setup

Build `mod/`, copy `bin/QDay.dll` to `scripts\`, set `Debug = true` in `QDay.ini` and press F11 in game to reload scripts. F4 drops a 12 m wall 25 m ahead of the player, square to the way they're facing, and works in any phase, including with no mission running. F3 is only for section G. Log lines below are in `scripts\QDay.log`.

## A. The wall

1. **Log:** the first tick after loading logs `forcefield: colour from R_lut only, engine entanglement-shader-v1 job 576f2916-8968-4404-bfe4-97ccdd96141a params {...}`, with no `table refused`.
2. **Across the road, not along it:** drive along a straight road, press F4, and the wall should lie across the road, square to your direction of travel. This is the check on how `heading` is read (the direction the road runs, GTA's convention), and if the wall runs along the road instead, the fix is in `ForceField.Segment` and nowhere else. Try it on a road running each way, as a wrong sign can look right at some headings.
3. **Standing on the road:** it starts at road level and reaches about 6 m, on flat ground and on a hill.
4. **Both sides:** the wall is visible from the front and from behind. A triangle only shows one side, so each is drawn twice.
5. **Rotated walls:** the native DB warns of oddities in some orientations, so check for missing or broken triangles, or flicker, on walls at several headings.

## B. Push-back

6. **Slow:** drive into the wall at walking pace. The car is turned back without getting through. The log shows `forcefield: pushed player vehicle at ...`.
7. **Fast:** do the same at about 100 km/h, then at full speed in a fast car, and try a motorbike and a truck. Nothing should get through, and the bounce should look like hitting something rather than a glitch. The log shows `put back` if a tick ever lands past the wall. The bounce strength is one constant, `Restitution`, so please say if it feels wrong.
8. **At an angle:** a shallow approach should deflect the car along its way, rather than stopping it.
9. **On foot:** walk, then sprint, into the wall. I expect animation to override velocity on a ped, so the player goes limp for about a second and is pushed away. Say whether that's what happens, whether it looks acceptable, and whether you can get through by walking repeatedly at it.
10. **Sitting beside it:** a car parked close to the wall, or driving alongside it, isn't pushed.
11. **Going round:** a 12 m wall can be driven round, so that isn't a failure. You should be able to come back at the wall from the far side and be stopped from that side too.
12. **Height band:** drop a wall under a bridge or on a road below an overpass, then cross on the other level. The other level isn't blocked. A jump higher than 6 m clears a wall, which is accepted, as is flying over one.
13. **Teleport:** F5 or F8 across a wall doesn't bounce you back.

## C. Colour and particles

14. **Colour with angle:** walk or drive slowly round a test wall. The colours change across the wall and with the angle you're at, and go nearly white when viewed almost edge-on, as in the swatch. They're pastel to medium rather than saturated.
15. **Shimmer:** standing still and looking square at the wall, the colours drift and swirl slowly. It's subtler at grazing angles, which is expected.
15a. **Smooth and lit:** the log shows `forcefield: QPAM light loaded, 8 frames of 256 x 256` and `forcefield: deadline streamed in, drawing smooth per-corner colours with the QPAM light`. The wall should be a smooth, bright veil with wisps of light flickering and drifting upwards, no visible triangles, fading out at the top and the ends. If it looks tinted or dark overall, the centre of `deadline_trail_01` isn't white: try another texture in `PlainTxd` / `PlainTexture` / `PlainU` / `PlainV` in `ForceFieldGame.cs`. If the log says it fell back, say so.
15b. **Day and night:** check the brightness at midday and at night. It was tuned on a stand-in scene, so it may be too faint by day or too strong at night; `BaseAlpha` and `ForceField.Lit` / `LitAlpha` set it.
15c. **No crash, and frame rate:** drop 6 or more F4 test walls close together and look at them: the game mustn't crash, only the nearest 4 should show, and the others should appear as you get closer. Compare the frame rate with the walls in view and without them. If 12 by 6 looks too coarse and there's headroom, `Cols` and `Rows` in `ForceField.cs` can go up as long as `CallsPerWall x MaxDrawnWalls` stays under `MaxDrawCallsPerFrame` (LockdownCheck checks this).
16. **Particles:** one burst of `veh_xs_vehicle_mods` / `exp_xs_mine_kinetic`, tinted to the wall's colour, each time the wall pushes the player, on foot or in a car, at most one every 0.4 s. Nothing runs while nobody touches the wall. None of this has been seen in game, so please check that the burst shows where the player hits, takes the tint, and doesn't repeat too often when holding the accelerator into the wall. The log shows `could not start particle` if the game refuses it.
17. **Tint:** the particles should follow the wall's colour, but the native DB says tinting only works on some effects. If it doesn't work here, try `ent_amb_sparking_wires`, `ent_amb_vent_haze_lg` or `ent_amb_foundry_heat_haze`, also in `core`, by changing the `ParticleName` constant in `ForceFieldGame.cs`, and say which looked best.

## D. Blips and range

18. **Blips:** a small blue blip shows on the minimap for each wall within about 300 m, and goes when you drive further than about 320 m away, then comes back on return.
19. **Range:** from more than 150 m away, the wall isn't drawn and its particles stop soon after. Both come back when you return.
20. **Frame rate:** drop about 6 walls in view, then 12 (F4 stops at 12), and note the frame rate with and without them. Each wall is 32 triangles and 3 effects. Try it during the riot too, since that's the real load.

## E. Clean-up

21. **F10:** drop some walls and press F10. Walls, blips and particles all go, in Idle as well as during a mission, and the log shows `forcefield: cleared N walls`.
22. **Dying:** die with walls up, both during the escape and with test walls in Idle. Everything goes.
23. **Mission end:** reach the end of the escape and check that everything goes, then run it again and check there are no leftovers.
24. **Script reload:** press F11 with walls up. No blips or particles are left behind. Blips and looped particles outlive a script, so this is the likeliest place for a leak.

## F. In the mission

25. **Escape:** press F7 then F6 to reach the escape. The log shows `forcefield: N walls up`, where N is the sealed walls in the layout, and sealed sites get walls and blips. With the placeholder data, they'll be in odd places.
26. **Debug view:** the blue columns only appear with `Debug = true`. With `Debug = false`, there are no columns but the force field is still there, and F4 does nothing.
27. **GPS:** the game's route to the objective doesn't know about the walls, so it can lead into one. I'd expect this, but please say how it plays, and also what AI traffic does at a wall: walls within 150 m of the player push every vehicle and ped near them too, so say whether traffic and pedestrians bounce off sensibly, pile up, or get stuck. The log names who was pushed (`player` or `npc`). Section G tests a possible fix for the GPS, and section H a simpler fallback.

## G. Switching off the road past a wall (experiment, Debug only)

This tests whether switching off road nodes makes the GPS route round a sealed road. It only works on an F4 test wall and the mission doesn't use it, so a pass here doesn't yet mean anything for the real walls. It uses `SET_ROADS_IN_ANGLED_AREA` to switch off and `SET_ROADS_BACK_TO_ORIGINAL_IN_ANGLED_AREA` to undo. The native DB confirms both exist but doesn't define the three BOOLs, and I found nothing either way on the GPS, so I don't know what this will do, and it hasn't been run in GTA. Nothing has been built for traffic, so the traffic checks are only there to see what happens.

The area doesn't cover the wall. It starts 5 m past the wall and runs away from the player who dropped it, 12 m wide and 10 m above and below the wall's height, to avoid cutting open roads through a junction at the wall. It shows as a red outline on the road while it's on, and that outline is the first thing to check. There are 16 variants: all eight BOOL combinations, at 20 m and at 50 m. Those with the third BOOL true come last, because the DB ties it to network events and I want the others logged first in case one misbehaves.

Please use a spare save and don't save the game during this section, since I don't know whether a switched-off road gets saved.

**Keys:** drop a wall with F4, then:

- **F3** starts the run, or undoes the current variant and applies the next. Each press takes about 7 seconds to finish, and F3 is ignored until then. The subtitle names the variant when it's ready.
- **Shift+F3** undoes the current variant and stops.
- **Ctrl+F3** only reads and logs the 12 nodes nearest the player, and changes nothing.

**What it logs:** the first F3 logs each node near the wall, with its position and whether it's switched off. With no mission running, it also sets a green blip with a route about 250 m past the wall. During a mission it uses the mission's route, so start the run after the mission has started, and press Shift+F3 before starting one. Each variant then logs:

- **`about to switch off`:** the reading just before the native call, so after a crash the last one of these says which variant caused it.
- **`switched off`:** the node counts inside and outside the area, the IDs of any that changed, and the route as it stands.
- **`route after setting it again`:** the route turned off and back on, then read 2 seconds later.
- **`switched back`** and a second **`route after setting it again`:** the same readings after the undo.

If a `switched back` line says `changed since first reading`, the undo didn't fully work, so please restart the game before trusting any later variant.

28. **The flags:** on a straight road on flat ground, with no junction near, press F3 through all 16. Please report which variants turned nodes off in the area, using the `in area ... turned off` counts. The DB also describes the node native as true on alleys and car parks, so only a change counts, not the value on its own. If nothing ever changes, the node native may not be a reliable measure, so please check whether the route or traffic behaved differently anyway.
29. **Finding the nodes:** road nodes are spaced well apart, so check the first log for how many nodes lie in each area. `in area 0 of 0 off` means the area held no node and says nothing about that variant. Say whether 20 m is enough or only 50 m works.
30. **Neighbouring roads:** drop a wall with a junction just behind you, so the stretch runs away from it. Using a variant that works from check 28, the `outside` counts should show no change and the crossing roads should still route and carry traffic. Then repeat with the junction just ahead, past the wall, to see the harm if the stretch runs into one. Real walls would need to know which side of them faces away from the junction, which the district file doesn't say yet.
31. **GPS:** with a working variant, watch whether the route line moves off the road, and compare the three lengths: `route` before the switch, `route before setting it again` and `route after setting it again`. The first change shows whether the route refreshes by itself, and the second whether it needs setting again. A generated route won't always run through the wall, so if it doesn't, try another spot, or start a mission and use its route.
32. **Destination still reachable:** `found` stays true with a variant on. Whether the last stop stays reachable with every wall in a layout switched off can't be tested until this is on the real walls.
33. **AI traffic:** with a working variant on, note whether new cars stop appearing on that stretch, what happens to cars already on it, and whether they pile up at its edge or block other roads.
34. **Police and hostile drivers:** get chased along the road towards the stretch, and say whether the chasing cars route round it, stop or drive through.
35. **Far away and back:** with a working variant on, drive more than a kilometre away, come back, then press Ctrl+F3 by the stretch. The nodes should still read as off once they've loaded again. Stored node IDs aren't read when you're over 200 m from the wall, as a stale ID could crash the game, so the log says `nodes not read` if you press F3 out there.
36. **Many walls:** this can't be tested yet, because only one stretch is on at a time. The game may limit how many it can hold, so it needs testing with a full layout before the mission uses this.
37. **Undoing it on every way out:** with a working variant on, try each of F10, dying, reaching the mission's end and F11, starting a fresh variant each time. For the mission's end, press F7 then F6 first, drop the wall, start the run and then press F5. The log should show `road switched back by clean-up`, including after F11, where the script is stopping so it's worth checking the line is there at all. That line is read straight after the undo, and says `nodes not read` after the F5 teleport, so the firmer test is to drive back, press Ctrl+F3 and compare with the first log by position, then check the GPS and traffic use the road again. The next run should also start clean.
38. **Crash and fresh load:** a script can't clean up after a crash, so this relies on the game. With a working variant on:
    - End `GTA5.exe` in Task Manager, restart the game and load the save. Drive to the coordinates in the log and press Ctrl+F3. The log adds to the old session rather than replacing it, so compare by position with the first reading, as node IDs may differ between sessions. Also check the GPS and traffic use the road. A road still off here would be serious, since nothing in the mod could put it right.
    - Separately, load a save from the pause menu without closing the game, then do the same checks, and say whether the log shows `cleaning up world` at that point.

## H. Hiding the route line (fallback, Debug only)

If road switching doesn't move the route, this stops the GPS leading the player into a wall by not drawing the line at all. It's off by default and only read with `Debug = true`. Set `HideEscapeRoute = true` under `[General]` in `QDay.ini`, then press F11.

39. **Log:** startup shows `hideEscapeRoute=True`, and with F7 then F6 the escape logs `route line hidden by HideEscapeRoute, stop blip kept`.
40. **On the map:** there's no route line on the minimap or the pause map, and the yellow stop blip and blue `Sealed road` blips are still there. The escape only has one stop blip so far, the last stop, as the others have nothing to mark until their objectives are built. A waypoint the player sets still draws its own line, which this doesn't touch.
41. **How it plays:** can you find your way to the blip without the line, and is that better than a line that leads into walls?
42. **Off:** with `HideEscapeRoute = false` the line is back, and with it true and `Debug = false` the line is also there, as the setting is ignored.

## Reporting back

For each section, please say whether it passed, and for any failure give the check number, what you saw and the matching log lines. For section G, the log lines from check 28 are the main result, so please attach them even if everything looks fine. Checks 7, 9, 14, 20 and 41 are partly judgement calls, so your view on how they look and play is what's wanted.
