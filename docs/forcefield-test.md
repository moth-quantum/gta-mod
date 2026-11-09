# Force field: in-game test

This is the test for `feature/forcefield`, which replaces the blue columns on sealed roads with a force field. Could you run through it on the Windows PC and report back with `scripts\QDay.log` attached if anything fails?

The maths has been tested outside the game with `tests/LockdownCheck`: turning a wall record into a line, working out which side of it the player is on, catching a crossing between ticks and looking up the colour. In 20,000 simulated drives at up to 120 m/s, none got through. None of it has run in GTA yet, so everything you can see or feel below is untested, including the drawing, the physics, the particles, the blips, the frame rate and the clean-up.

The wall colour comes from `R_lut` only, a table baked once from `entanglement-shader-v1` (job `576f2916-8968-4404-bfe4-97ccdd96141a`, peaked settings) and embedded in the DLL. `T_lut` is baked too, but nothing reads it, so the alpha is a fixed value. The shimmer is the shader's film thickness drifting 50 nm either way over 8 seconds, so every colour on screen is a table lookup and the game makes no engine calls. `docs/swatch_B_peaked.png` shows what the table gives across angle and phase: colour bands that turn close to white at grazing angles.

The placeholder district file has walls at the midpoints between districts rather than on roads, at rough heights, and they only push when the player is between 2 m below and 6 m above the wall's height, so most tests use the F4 test wall instead. F4 makes a wall in the same way as a real one, so a problem with it is a real problem. Placeholder walls floating or letting you through because of their height aren't failures.

## Setup

Build `mod/`, copy `bin/QDay.dll` to `scripts\`, set `Debug = true` in `QDay.ini` and press F11 in game to reload scripts. F4 drops a 12 m wall 25 m ahead of the player, square to the way they're facing, and works in any phase, including with no mission running. Log lines below are in `scripts\QDay.log`.

## A. The wall

1. **Log:** the first tick after loading logs `forcefield: colour from R_lut only, engine entanglement-shader-v1 job 576f2916-8968-4404-bfe4-97ccdd96141a params {...}`, with no `table refused`.
2. **Across the road, not along it:** drive along a straight road, press F4, and the wall should lie across the road, square to your direction of travel. This is the check on how `heading` is read (the direction the road runs, GTA's convention), and if the wall runs along the road instead, the fix is in `ForceField.Segment` and nowhere else. Try it on a road running each way, as a wrong sign can look right at some headings.
3. **Standing on the road:** it starts at road level and reaches about 6 m, on flat ground and on a hill.
4. **Both sides:** the wall is visible from the front and from behind. `DRAW_POLY` only shows one side, so each strip is drawn twice.
5. **Rotated walls:** the native DB warns of oddities in some orientations, so check for missing or broken strips, or flicker, on walls at several headings.

## B. Push-back

6. **Slow:** drive into the wall at walking pace. The car is turned back without getting through. The log shows `forcefield: pushed vehicle at ...`.
7. **Fast:** do the same at about 100 km/h, then at full speed in a fast car, and try a motorbike and a truck. Nothing should get through, and the bounce should look like hitting something rather than a glitch. The log shows `put back` if a tick ever lands past the wall. The bounce strength is one constant, `Restitution`, so please say if it feels wrong.
8. **At an angle:** a shallow approach should deflect the car along its way, rather than stopping it.
9. **On foot:** walk, then sprint, into the wall. I expect animation to override velocity on a ped, so the player goes limp for about a second and is pushed away. Say whether that's what happens, whether it looks acceptable, and whether you can get through by walking repeatedly at it.
10. **Sitting beside it:** a car parked close to the wall, or driving alongside it, isn't pushed.
11. **Going round:** a 12 m wall can be driven round, so that isn't a failure. You should be able to come back at the wall from the far side and be stopped from that side too.
12. **Height band:** drop a wall under a bridge or on a road below an overpass, then cross on the other level. The other level isn't blocked. A jump higher than 6 m clears a wall, which is accepted, as is flying over one.
13. **Teleport:** F5 or F8 across a wall doesn't bounce you back.

## C. Colour and particles

14. **Colour with angle:** walk or drive slowly round a test wall. The colours change across the wall and with the angle you're at, and go nearly white when viewed almost edge-on, as in the swatch. They're pastel to medium rather than saturated.
15. **Shimmer:** standing still and looking square at the wall, the colours drift slowly over about 8 seconds. It's subtler at grazing angles, which is expected.
16. **Particles:** three looped effects along each wall, using `core` / `ent_amb_elec_crackle`. None of this has been seen in game, so please check that they show, keep running rather than firing once, and sit along the wall. The log shows `could not start particle` if the game refuses them.
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
27. **GPS:** the game's route to the objective doesn't know about the walls, so it can lead into one. I'd expect this, but please say how it plays, and also what AI traffic does at a wall, since the walls only affect the player. Section G is a possible fix for both.

## G. Switching off the road under a wall (not built yet)

These checks are for later and can't be run on this branch. The idea is to switch off the road nodes under each sealed wall with `SET_ROADS_IN_ANGLED_AREA`, so the GPS routes round it and AI traffic avoids it, and to undo that with `SET_ROADS_BACK_TO_ORIGINAL_IN_ANGLED_AREA`. The native DB confirms both exist, but it leaves the flags that control them undocumented. A FiveM forum thread reports the plainer `SET_ROADS_IN_AREA` clearing traffic from a bridge, and I found nothing either way on the GPS, so these checks are what would show whether it works.

28. **The flags:** the log should show `GET_VEHICLE_NODE_IS_SWITCHED_OFF` going from false to true for the node under the wall, and back to false afterwards. This is what settles which of the three flags does what. The DB also describes that native as true on alleys and car parks, so only a change on the same node counts.
29. **Finding the nodes:** road nodes are spaced well apart, so a thin area across the road can miss them all. Note how long the area has to be along the road before a node switches off.
30. **Neighbouring roads:** the wall sites are mostly at junctions, so check that the roads crossing at the junction still work, and that switching off this road doesn't take them out too.
31. **GPS:** with a route that runs through the wall, the route should move round it, comparing `GET_GPS_BLIP_ROUTE_LENGTH` before and after. Test nodes switched off before the route is set, and also after it's set, as the route may not refresh by itself. `BeginEscape` currently sets the route first, so the order may need to change.
32. **Destination still reachable:** with all the walls for a layout up, `GET_GPS_BLIP_ROUTE_FOUND` stays true and the route still reaches the last stop. Switched-off nodes could split the road network differently from the districts that the route guarantee works from.
33. **AI traffic:** new cars stop appearing on the road and avoid it from further away. Note what happens to cars already there as the wall goes up, and whether they pile up or block the player's open route.
34. **Police and hostile drivers:** chasing cars should route round it or stop, rather than getting stuck at the wall, as the force field only pushes the player.
35. **Far-away walls:** try a wall with its road nodes not loaded yet, more than a few hundred metres away, then drive there. The change has to hold when the nodes load.
36. **Many walls:** switch off every wall in the worst layout, as the game may limit how many areas it can hold, and check that all of them take effect.
37. **Undoing it:** after F10, dying, mission end and F11, the same nodes read as on again, and traffic and the GPS use the road. The native DB doesn't say whether this resets by itself when a script ends, so I'm assuming it doesn't and that every way out has to undo it. Also check that a mission started after each of those starts clean.
38. **Crash or game closed mid-mission:** after restarting the game and loading a save, the road should be back to normal. A script can't clean up after a crash, so this is the one case that relies on the game.

## Reporting back

For each section, please say whether it passed, and for any failure give the check number, what you saw and the matching log lines. Checks 7, 9, 14 and 20 are partly judgement calls, so your view on how they look and play is what's wanted.
