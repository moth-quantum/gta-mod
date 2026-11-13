# GTA mod with Claude Code: working plan

Written 21 Oct 2026. GTA 6 ships 19 Nov 2026, so the attention window is about four weeks.

## 1. What is going on right now

- Pieter Levels (@levelsio) posted on 20 Oct: "I asked Claude Code to make a new GTA: San Andreas mission where CJ has to vibe code a startup and then raise money from investors in SF." A 4m41s video, ~300k views and ~20k likes in a day. Idea credited to @alexchristou_. No code, no writeup, no tooling details anywhere.
- Frames from the video show the ORIGINAL PC San Andreas (classic renderer and HUD), a scripted mission with custom on-screen text and [Y]/[N] key prompts, teleports between San Fierro locations, money changes, and NPC "investors" placed in a lobby. That is exactly the shape of a CLEO script. Almost certainly CLEO Redux (JavaScript) or CLEO 5 via Sanny Builder, which is what an agent would reach for.
- The wider pattern: Ziwen Xu's "GT-Caliber" (a GTA 6 clone built by Claude agent loops in Godot, then Unreal, covered by Gizmodo and GTA BOOM), PC Gamer running a weekly "I added X to GTA 5" column (jetpack 15 Oct, Doom 2 import 9 Oct), NPCTalk (voice-chat with GTA V NPCs, built in Claude Code sessions, ships as a Claude Code plugin with skills). CLEO Redux 1.5.1 shipped 26 Sep 2026, CLEO 5.4.0 in March, SHVDN Enhanced is in beta. The scene is unusually active and press is actively looking for these stories.
- What gets traction is a short video of a mission or mechanic that is funny or culturally pointed, plus a one-line "I asked Claude Code to..." framing. Not a download link. Downloads come second.

## 2. The constraints we start from

- This Mac is an M1 Pro on macOS 14.5. No Windows, Parallels, CrossOver, dotnet or Wine. Every GTA modding toolchain is Windows-only, so the game runs on a cloud Windows box.
- **Decided 21 Oct: Paperspace CORE.** Recommended machine: **A4000 in AMS1** (16 GB VRAM, 8 vCPU, 45 GB RAM, $0.76/hr while on, needs the $8/mo Pro plan), 250 GB disk ($10/mo max), public IP ($3/mo max). RTX4000 ($0.56/hr, free plan) is enough for San Andreas alone. Stream with Parsec. Stop the machine when not playing; only storage and IP bill while it is off.
- **Paperspace catch:** Windows templates are retired for accounts created after 1 July 2024. The Windows 10 / Parsec template only shows up for older accounts. If it is missing in the console, Paperspace cannot host this and the fallback is Shadow PC (consumer Windows gaming box, flat monthly) or an AWS g4dn.xlarge Windows instance with Parsec.
- Rockstar's modding policy: single player only, non-commercial, no redistribution of Rockstar assets. Never load a script hook with GTA Online. A free mod plus a video is squarely inside the policy.
- The Moth side (from the 19 Oct doc in ~/Downloads/gta5.rtf): engines are async jobs, qpu runs queue for minutes to hours, any "quantum" claim needs qpu runs, a key must never ship inside a public mod, and the Cloudflare User-Agent rule applies to every call. The doc's four routes still hold: (1) offline asset pipeline, (2) pre-fetched randomness pool, (3) live calls on a background thread, (4) a Moth proxy service. Routes 1 and 2 ship. Route 3 is for a demo video only.

## 3. Pick the game (decision 1)

| | San Andreas (classic PC) | GTA V (Legacy or Enhanced) |
|---|---|---|
| Script tooling | CLEO Redux: JavaScript/TypeScript, hot reloads on file save, opcode docs in the box | Script Hook V + ScriptHookVDotNet: C#, build a DLL, reload in-game with Insert |
| Agent fit | Excellent. One JS file, no compiler, instant feedback | Good. Needs .NET toolchain on the Windows box, NativeDB for ~6000 natives |
| Hardware | Runs on anything, including a Parallels VM | Needs a real GPU Windows box |
| Visual payoff | Nostalgia, meme-ready, matches the levelsio format | Looks modern, better for texture and shader work |
| Moth asset route | Texture swaps via TXD Workshop/Magic.TXD, radio via SA audio tools | YTD swaps via OpenIV (Legacy) or OpenRPF (Enhanced), CodeWalker for maps |
| Risk | Game needs the 1.0 downgrade patch if bought on Steam/Rockstar | Enhanced tooling is beta. Legacy is the stable modding target |

**Decided 21 Oct: GTA V.** Buy "Grand Theft Auto V Enhanced" on Steam (the only listing; it includes the Legacy build free). **Mod the Legacy build**: Script Hook V 1.0.3889.0 (Jul 2026) supports Legacy 1.0.3889.0, ScriptHookVDotNet 3 is stable there, LemonUI for menus, OpenIV for any texture work. Enhanced's SHVDN build is still a beta and gains us nothing for this mod. Switch to Enhanced only for the final capture if ray tracing is worth it and everything happens to work.

## 4. How we work together

Two machines, one repo.

**Mac (me, with you):** design, mission script writing, all Moth engine work (Python, the existing ~/sphinx-api and quantum-* pipelines already talk to api.mothquantum.com), asset generation, the video edit, the writeup. A git repo at ~/gta-mod with the mod source, a CLAUDE.md that records the toolchain, and a `log/` folder.

**Windows box (Claude Code there, with you):** the game, the mod loader, build and deploy, playtesting. Claude Code on Windows pulls the repo, copies the script into the game folder, reads the loader's log file after each run, and iterates. You play, and feed back either words or a screen recording.

**The loop, per feature:**
1. We agree the beat in plain English (what the player sees, what keys they press, what changes).
2. I write the script on the Mac and commit. The mod writes its own debug log (every state change, every coordinate) because neither of us can see the screen from the terminal.
3. On Windows, Claude Code deploys, you launch the game, trigger the mission, and read back or screen-record. Claude Code reads the log.
4. Fix, repeat. CLEO Redux reloads on save so a round trip is under a minute. SHVDN is about two minutes (build, Insert to reload).

**What you own:** the idea, playtesting, the voice of the on-screen text, the final cut of the video, posting. **What I own:** everything that is typing. Coordinates, opcodes/natives, Moth API plumbing, asset conversion, ffmpeg.

**Tooling I will set up on the Mac side straight away:**
- Repo with CLAUDE.md, a `/deploy` skill for the Windows side, a `log/` convention.
- A coordinate notebook: San Andreas and GTA V map coordinates for every location the mod touches, pulled from the community map databases so we are not guessing in-game.
- Moth asset pipeline scripts reusing the existing blur/tessa/qrc code in your home directory.

## 5. Schedule: a few days, not four weeks

The four weeks to 19 Nov is the attention window, not the build time. A San Andreas mission is one JavaScript file.

| Day | Target |
|---|---|
| 1 | Windows box ready, game modded, hello-world script prints text and teleports CJ. Mod concept locked to one page: three to five beats, one mechanic, one punchline |
| 2 | Full mission plays end to end, placeholder text, logs every beat |
| 3 | Dialogue and timing pass, record, cut on the Mac |
| 4 | Post the video. Download release and writeup can follow the same week |

GTA V with the C# build chain adds roughly two days. Moth engine assets run in parallel on the Mac and add about a day of my time, none of yours.

The remaining weeks before launch are for a second mod, a download release, or a PC Gamer pitch if the first one lands.

## 6. Open decisions (need you)

1. **The mod.** What is it? One paragraph: what the player does, what the joke or the point is, whether Moth engines are part of it or it is a pure vibe-coded mission.
2. ~~The game.~~ Decided 21 Oct: GTA V Legacy (bought as Enhanced).
3. ~~The Windows machine.~~ Paperspace CORE A4000 in AMS1 if the account shows Windows templates, else Shadow PC Power. GTA V rules out Parallels and the RTX4000 is marginal; A4000 or better.
4. ~~Moth's role.~~ Decided 21 Oct: Atlas is required and is the core of the mod.

## Sources
- levelsio post: https://x.com/levelsio/status/2112686345617547538 and https://levels.io/claude-code-gta-startup-mission
- GT-Caliber coverage: https://gizmodo.com/this-guy-is-trying-to-vibe-code-gta-6-before-its-official-release-2000771395
- NPCTalk (Claude Code built GTA V mod, plugin + skills): https://github.com/christos-antono/NPCTalk
- PC Gamer GTA 5 column: https://www.pcgamer.com/games/grand-theft-auto/gta-5-finally-measures-up-to-the-actual-best-gta-game-now-ive-added-a-jetpack/
- CLEO / CLEO Redux: https://cleo.li/
- Script Hook V .NET Enhanced: https://www.gta5-mods.com/tools/script-hook-v-net-enhanced
- Moth integration doc: ~/Downloads/gta5.rtf (19 Oct 2026)

## 7. The mod: Q-DAY (first design, 21 Oct)

> Note, 23 Oct: this section was written for San Andreas and then moved to GTA V. As built, Lester replaces CJ, his house replaces the Doherty garage, and the three parts come from Humane Labs, the FIB building and Fort Zancudo. `mod/QDay.cs` is the source of truth.

Premise: CJ builds a fault-tolerant quantum computer in the Doherty garage. The moment it reaches threshold it runs Shor's algorithm, every key on earth is broken, and San Andreas loses its mind. Money stops existing. Everyone turns on everyone.

The tasks follow the real steps to fault tolerance, so the mission is also a tiny explainer.

### Beats

1. **The pitch.** A phone call or a stranger at the garage: "A machine that factors numbers. That's all it does." CJ: "That's it?" Mission title card: Q-DAY.
2. **Physical qubits.** Steal a cryostat from Area 69 (SA's military base). Wanted level, a truck, get out. The crate shows on the HUD as `physical qubits: 1`.
3. **Noise.** Drive it back to Doherty. Every bump of damage to the truck raises an on-screen `error rate` meter. Arrive above threshold and the delivery counts for less. This is the one real mechanic: driving carefully is error suppression.
4. **Error correction.** Repeat the run three times from three places (Area 69, the Easter Bay chemical plant, a university lab in San Fierro). Each clean delivery turns physical qubits into a logical qubit. HUD: `logical qubits: 0 / 3`, `error rate: 2.1%  threshold: 1.0%`.
5. **Threshold.** With three logical qubits below threshold the garage door drops, the screen flickers, and a countdown runs: `factoring RSA-2048... 00:59`. CJ can't leave. Phone starts ringing. *(As built, 10 Nov: a fixed 10 s, title `THRESHOLD switching on...`, Lester says "It's running.", camera shake and a short flicker, the player keeps control. The hardware job has been running since the pitch.)*
6. **Q-Day.** Countdown hits zero. Three things happen at once:
   - **Money is gone.** Cash counter goes to $0 and stays there. The HUD money readout disappears. Stores and vending give nothing.
   - **Everyone goes mental.** SA's built-in riot mode (from the Los Santos riot missions) switches on citywide. Peds get weapons and hate everyone, including each other. Cops hate everyone too, not just CJ.
   - **The leak ticker.** A news crawl at the bottom of the screen: "SFPD internal mail public." "Caligula's vault PIN: 0000." "Every Grove Street text message, live." Written to be funny and specific to the game's own characters.
7. **Ending.** Survive five minutes, or drive to one place. As built (10 Nov): the screen fades to black, then "you survived qday" plays in Atlas Type's grid font with the Atlas credit under it, and the mission returns to Idle once it has finished. The "Post-quantum cryptography was available the whole time" card is gone.

Per-part targets, as built on 10 Nov: the three deliveries are three parts of one machine, a fridge (cryostat, Humane Labs), a command system (control electronics, FIB Building) and a chip (qubit array, Fort Zancudo). Each part has to come in under its own target, 11%, 6% and 2%, set in `Locations.Targets`, where the total is the part's live Atlas figure plus the damage penalty. A part over its target sends the player back for the same part, without Lester's opening line again, and a pass moves on to the next part or, after the chip, to Threshold. The HUD shows `part n / 3` and the part's target. Only collisions count as damage, as the truck is bulletproof from the moment it spawns (`Entity.IsBulletProof`), while fire, explosions and crashes still damage it and a destroyed truck still fails the mission. With the recorded error rates, the drive back can lose about 500 body health with the fridge, 280 with the command system and 54 with the chip. If a live calibration takes over 180 s, and for F6 in Calibrating, the mod uses that part's recorded graph-v1 result and logs it.

### What each beat needs technically (GTA V Legacy, ScriptHookVDotNet 3, C#)

Natives confirmed against alloc8or's native DB on 21 Oct:
- Title cards, HUD counters, ticker: `BEGIN_TEXT_COMMAND_DISPLAY_TEXT` + `SET_TEXT_SCALE/COLOUR`, `DRAW_RECT` for the backing bar. SHVDN wraps these as `TextElement` and `ContainerElement`. Big moments use scaleform `mp_big_message_freemode` (same thing the game uses for mission passed).
- Heists: the chip truck is placed at the site with `World.CreateVehicle` once the player is within 100 m (a white Mule3 inside the Humane Labs garage, a black Boxville at the FIB building, an olive Mule3 at Fort Zancudo), tuned and armored, with the crate attached as a prop. At Humane Labs the garage shutters stay locked (`SET_STATE_OF_CLOSEST_DOOR_OF_TYPE`) until the player shoots the keypad. At the FIB Building and Fort Zancudo the truck stays locked until the player solves the "Fix the errors" hack at a terminal beside it (`mod/Hack.cs`, rules in `mod/HackPuzzle.cs`): a grid of qubits with hidden flips, read from red checks between neighbours, 4 by 4 with two flips and a 90-second reset at the FIB Building, and 5 by 5 with three flips, a 60-second reset and a new error every 15 seconds at Fort Zancudo (11 Nov). It's a simplified picture of error correction, not a quantum computation, and makes no Atlas call. Pickup is next to the truck with it on screen. Blips with `ADD_BLIP_FOR_COORD` (objective) and on the truck during delivery, wanted level with `SET_PLAYER_WANTED_LEVEL` + `_NOW`. Dying in any mission phase fails the mission.
- Error-rate meter: `GET_VEHICLE_BODY_HEALTH` each tick, body damage since pickup becomes a penalty (up to +5%) on top of the live Atlas figure. The HUD shows the penalty next to the error rate.
- Threshold: a fixed 10 s (`ThresholdSeconds`), the player keeps control; `SHAKE_GAMEPLAY_CAM("SMALL_EXPLOSION_SHAKE")` and a short `ANIMPOSTFX_PLAY("Dont_tazeme_bro")` flicker. The storm (`SET_WEATHER_TYPE_NOW_PERSIST("THUNDER")`) starts in Q-Day once peds are armed, not here, because it empties the streets.
- Q-Day, money gone: `STAT_SET_INT("SP0_TOTAL_CASH" / SP1 / SP2, 0, true)` each tick and `DISPLAY_CASH(false)`. Also `HIDE_HUD_COMPONENT_THIS_FRAME(3)` for the cash component.
- Q-Day, everyone mental: for each nearby ped every few seconds, `GIVE_WEAPON_TO_PED`, `SET_PED_COMBAT_ATTRIBUTES(ped, 46, true)` (always fight), `SET_PED_FLEE_ATTRIBUTES(ped, 0, false)`, `SET_CAN_ATTACK_FRIENDLY`, and `SET_RELATIONSHIP_BETWEEN_GROUPS(5 /*hate*/, CIVMALE, CIVFEMALE / COP / PLAYER)`. The Atlas random bytes pick who gets what.
- Leak ticker: scrolling `TextElement` fed from an array; order shuffled by the Atlas bytes.
- Q-Day title (10 Nov): the Q-DAY card is replaced by "qday activated" in Atlas Type's grid font (`recorded/qday_title_gridfont.json`, 120 seeds, embedded in the DLL), drawn full screen over a 55% black backdrop by `mod/QDayTitle.cs`. It flickers through seeds at 20 a second for 2 s, eases out to about 3 a second by 3 s, lands on the seed picked by the first Q-Day random number (value mod 120), holds to 4.5 s and fades by 5 s, with `every key on earth is public    Bell S = ... > 2` under it from the landing on. Strokes are `DRAW_RECT`, with diagonals as small squares along the stroke, sized for the aspect ratio (`GET_ASPECT_RATIO`). The HUD panel steps aside while it's up, the ticker keeps running, and `TextHit` (`WastedSounds`) plays at the landing. If the font fails to load, the old card shows instead. The ending uses the same code with `recorded/ending_title_gridfont.json` ("you survived qday", 120 seeds): once the screen has faded to black it plays over a fully black backdrop drawn after the fade (`SET_SCRIPT_GFX_DRAW_ORDER(7)`), with the same flicker and slowdown, a 4 s hold and a 0.5 s fade, 7.5 s in all. It lands on the next draw from the riot's `AtlasRandom` and shows `Random numbers generated on <backend> via Moth Atlas. Job <id>.` under it, which replaces the separate credit notification and the post-quantum card. The mission fades back in and returns to Idle only after it finishes. If the ending font fails to load, a `YOU SURVIVED QDAY` card and the credit as a notification show instead, for the old 12 s. Cut short by F10, death or a reload, it fades the screen back in.
- Atlas: `Atlas.cs` calls api.mothquantum.com directly with HttpClient, one background `Thread` per job, never on the script tick; the tick drains finished results. Key from `[Atlas] ApiKey` in `scripts\QDay.ini`; without one it answers from the recorded results embedded in the DLL. (This replaced the original Python sidecar and file bridge.)

Build and deploy: `dotnet build` on the Mac (net48 target, `ScriptHookVDotNet3` 3.6.0 and `LemonUI.SHVDN3` from NuGet), copy `QDay.dll` into `GTAV/scripts/`, press Insert in-game to reload SHVDN. Log at `GTAV/ScriptHookVDotNet.log` plus the mod's own `scripts/QDay.log`.

Force field natives, confirmed against the same DB on 8 Nov (behaviour still to check in game, see docs/forcefield-test.md): DRAW_POLY 0xAC26716048436851, GET_FINAL_RENDERED_CAM_COORD 0xA200EB1EE790F448, SET_ENTITY_VELOCITY 0x1C99BB7B6E96D16F, SET_ENTITY_COORDS_NO_OFFSET 0x239A3351AC1DA385, SET_PED_TO_RAGDOLL 0xAE99FB955581844A, START_PARTICLE_FX_LOOPED_AT_COORD 0xE184F4F0DC5910E7, SET_PARTICLE_FX_LOOPED_COLOUR 0x7F8F65877F88783B, ADD_BLIP_FOR_COORD 0x5A039BB0BCA604B6. Added for the 11 Nov look, confirmed against the same DB: DRAW_TEXTURED_POLY_WITH_THREE_COLOURS 0x736D7AA1B750856B (RGB as floats, alpha as int), REQUEST_STREAMED_TEXTURE_DICT 0xDFA2EF8E04127DD5, HAS_STREAMED_TEXTURE_DICT_LOADED 0x0145F696AAAAD2E4, SET_STREAMED_TEXTURE_DICT_AS_NO_LONGER_NEEDED 0xBE2CACCF5A8AA805. GTA single player has no native to make a texture from a file (CREATE_RUNTIME_TXD is FiveM only), so the QPAM light is carried by the corner colours, not drawn as a texture. Added for the getaway car (12 Nov, confirmed against the same DB, as used by the Quantum Paint mod; see docs/getaway-car-test.md): SET_VEHICLE_CUSTOM_PRIMARY_COLOUR 0x7141766F91D15BEA, SET_VEHICLE_CUSTOM_SECONDARY_COLOUR 0x36CED73BFED89754, SET_VEHICLE_EXTRA_COLOURS 0x2036F561ADD12E33, GET_VEHICLE_EXTRA_COLOURS 0x3BC4245933A166F7, SET_VEHICLE_NEON_ENABLED 0x2AA720E4287BF269, SET_VEHICLE_NEON_COLOUR 0x8E0A582209A62695, SET_VEHICLE_MOD_KIT 0x1F2AA07F00B3217A, SET_VEHICLE_LIVERY 0x60BF608F1B8CD1B6, GET_CLOSEST_VEHICLE_NODE_WITH_HEADING 0xFF071FB798B803B0.

Road switching experiment natives (Debug only, not in the mission, flags undocumented, see docs/forcefield-test.md section G): SET_ROADS_IN_ANGLED_AREA 0x1A5AA1208AF5DB59, SET_ROADS_BACK_TO_ORIGINAL_IN_ANGLED_AREA 0x0027501B9F3B407E, GET_VEHICLE_NODE_IS_SWITCHED_OFF 0x4F5070AA58F69279, GET_NTH_CLOSEST_VEHICLE_NODE_ID 0x22D7275A79FE8215, GET_VEHICLE_NODE_POSITION 0x703123E5E7D429C2 (crashes on a bad ID), GET_GPS_BLIP_ROUTE_LENGTH 0xBBB45C3CF5C8AA85, GET_GPS_BLIP_ROUTE_FOUND 0x869DAACBBE9FA006, SET_BLIP_ROUTE 0x4F7D8A9BFB0B43E9.

### Atlas is the machine (required, measured 21 Oct)

The quantum computer CJ builds is not a prop. Every build step is a real Atlas job, and the numbers on the HUD are the numbers Atlas returns. Measured today on the emulator: graph-v1 and comet-qrng-v1 both return in about 10 seconds, 5 credits each. Hardware queue time: being measured now.

| Beat | Atlas engine | What the game shows |
|---|---|---|
| 2-4. Build the machine | `graph-v1`, mode `fake` (a noisy calibration snapshot of a real IBM chip), a 4-qubit GHZ-style chain | `error rate` = 1 minus the mean ZZ correlation measured from the returned counts. Real number, 2 s per job. |
| 4. Error correction, three deliveries | Same engine. Each delivery swaps in a better chip snapshot. Measured ladder today: brisbane 8.5%, kingston 5.1%, torino 4.6%, kyiv 4.1%, sherbrooke 3.7%, fez 1.7%, marrakesh 1.7% | Delivery 1 installs brisbane (8.5%), delivery 2 torino (4.6%), delivery 3 fez (1.7%). Threshold set at 2%. Driving damage adds a penalty on top of the live figure. |
| 5. Threshold | `comet-qrng-v1`, mode `qpu`, 64 qubits, `bell_witness` on | The job is sent at the pitch (`qday-<n>`, unique per mission start) and runs on a background thread through the heists. Threshold is a fixed 10 s; if the job is back by then Q-Day runs on it, otherwise on a recorded run picked at random from the library (every `recorded/qrng_qpu_64q_*.json`, embedded in the DLL) with a random starting point in its integers. Offline mode always uses a library pick. Measured 21 Oct: 22 s and 68 s on ibm_marrakesh. On screen: `S = 2.71 > 2`, 22 sigma above classical, p = 1e-106. |
| 6. Q-Day | Same job's output | The `commit` hash scrolls as the key being cracked. The derived random integers seed the chaos: who gets which weapon, which leak scrolls first, where the riot starts. |
| 7. Ending | None | Notification: "Random numbers generated on <backend> via Moth Atlas. Job <id>." (Not "certified": the engine's own report says the Bell test is not device-independent certification.) |

Verified output fields: graph-v1 returns `measurements[]{bitstring,count}` (compute ZZ from these), `tomography` (ideal, always 1.0, do not use for the error rate), `edge_agreement_score`, `backend`. comet-qrng returns `bell_witness.S`, `z_above_classical`, `p_value_local_realist`, `equivalent_depolarising_p`, `commitment.commit`, `provenance.backend`, `provenance.provider_job_id`, `random.derived.integers`, `entropy_report.grade`.

**Measured 21 Oct, things that bit:**
- graph-v1 `io` knobs `dynamical_decoupling`, `error_mitigation` and `sim_noise` fail the job outright on fake backends ("job failed and produced no result"). `twirling` is silently ignored. So the "unlock a real error-suppression technique" idea is parked; the chip ladder replaces it. Raise with the platform team.
- `fake_aachen` fails. The seven chips above work.
- comet-qrng at the default 12 qubits returns ZERO conditioned bytes on hardware: grade `hardware-insufficient-entropy`, because the ordering penalty for counts-only readout (about 12 bits per shot at 4096 shots) exceeds 12 qubits x 0.76 bits. The Bell witness and commit hash still come back. **Fixed: 64 qubits, 2048 shots on ibm_marrakesh returned in 22 s with grade `hardware-accounted`, 27k bits budget, 64 certified bytes, S = 2.71, 22 sigma.** Use `num_qubits: 64, shots: 2048` for the Q-Day job. The emulator path (12 qubits, needed to fit under its 20-qubit cap with the witness) gives bytes only sometimes; the mod falls back to local random and flags `certified: false`, which is fine for dev and never used in the video.

### How the game talks to Atlas

CLEO scripts cannot make HTTPS calls and must never hold the key. So:

- **Sidecar.** A small Python process on the Windows box (`qday-sidecar.py`) holds the Atlas key in its environment, watches a `request.json` the mod writes, submits the job, polls, and writes `result.json`. The CLEO script reads that file each second. Claude Code on the Windows box runs and debugs the sidecar.
- **Latency is part of the fiction.** Emulator and fake runs take 10 s to a few minutes; the garage shows "calibrating..." while the sidecar waits. *(Superseded 10 Nov: the Q-Day job is sent at the pitch and the threshold is a fixed 10 s, so the player never waits on the queue. See the table above.)*
- **Offline bundle for release.** The download ships with a `recorded/` folder of real job results (ids and commit hashes included) so it plays without a key. Players with their own Atlas key get live runs. No key ever ships in the mod.
- **Known gotchas.** Cloudflare rejects the default Python User-Agent, so the sidecar sends a browser one. macOS python.org builds need certifi. Stagger submits, the platform 503s on bursts.

### Still needed

- Confirm the Paperspace account can see Windows templates, else Shadow PC Power.
