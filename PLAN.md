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

Default recommendation: San Andreas with CLEO Redux if the mod is a mission or a mechanic you want on video within two weeks. GTA V if the mod is about visuals (Moth texture, LUT or shader work) or you already own a modding-ready GTA V install.

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
2. **The game.** San Andreas or GTA V. See section 3.
3. ~~The Windows machine.~~ Decided 21 Oct: Paperspace CORE A4000, AMS1, if the account can see Windows templates.
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

Premise: CJ builds a fault-tolerant quantum computer in the Doherty garage. The moment it reaches threshold it runs Shor's algorithm, every key on earth is broken, and San Andreas loses its mind. Money stops existing. Everyone turns on everyone.

The tasks follow the real steps to fault tolerance, so the mission is also a tiny explainer.

### Beats

1. **The pitch.** A phone call or a stranger at the garage: "A machine that factors numbers. That's all it does." CJ: "That's it?" Mission title card: Q-DAY.
2. **Physical qubits.** Steal a cryostat from Area 69 (SA's military base). Wanted level, a truck, get out. The crate shows on the HUD as `physical qubits: 1`.
3. **Noise.** Drive it back to Doherty. Every bump of damage to the truck raises an on-screen `error rate` meter. Arrive above threshold and the delivery counts for less. This is the one real mechanic: driving carefully is error suppression.
4. **Error correction.** Repeat the run three times from three places (Area 69, the Easter Bay chemical plant, a university lab in San Fierro). Each clean delivery turns physical qubits into a logical qubit. HUD: `logical qubits: 0 / 3`, `error rate: 2.1%  threshold: 1.0%`.
5. **Threshold.** With three logical qubits below threshold the garage door drops, the screen flickers, and a countdown runs: `factoring RSA-2048... 00:59`. CJ can't leave. Phone starts ringing.
6. **Q-Day.** Countdown hits zero. Three things happen at once:
   - **Money is gone.** Cash counter goes to $0 and stays there. The HUD money readout disappears. Stores and vending give nothing.
   - **Everyone goes mental.** SA's built-in riot mode (from the Los Santos riot missions) switches on citywide. Peds get weapons and hate everyone, including each other. Cops hate everyone too, not just CJ.
   - **The leak ticker.** A news crawl at the bottom of the screen: "SFPD internal mail public." "Caligula's vault PIN: 0000." "Every Grove Street text message, live." Written to be funny and specific to the game's own characters.
7. **Ending.** Survive five minutes, or drive to one place. Fade out on a title: "Post-quantum cryptography was available the whole time." Credits: built with Claude Code.

### What each beat needs technically (San Andreas, CLEO Redux)

- Text prompts, title cards, HUD counters: CLEO draws text with the game's own font. Same as levelsio's prompts.
- Area 69 heist: spawn a truck, set a wanted level, a checkpoint blip. Standard CLEO.
- Error-rate meter: read vehicle health each frame, map damage to a percentage. Trivial.
- Riot mode and ped aggression: SA already has a riot flag and a hostility system for the LS riots. A CLEO script can switch both on. Day-one job is to confirm the exact opcode or memory flag.
- Money removal: force player cash to zero every frame and hide the HUD money element.
- Leak ticker: a text draw that scrolls, fed from an array of lines.
- Countdown and lockdown: a timer, a garage door, and a "can't leave" check on player position.

Everything above is in-engine. Nothing needs new models or audio.

### Atlas is the machine (required, measured 21 Oct)

The quantum computer CJ builds is not a prop. Every build step is a real Atlas job, and the numbers on the HUD are the numbers Atlas returns. Measured today on the emulator: graph-v1 and comet-qrng-v1 both return in about 10 seconds, 5 credits each. Hardware queue time: being measured now.

| Beat | Atlas engine | What the game shows |
|---|---|---|
| 2-4. Build the machine | `graph-v1`, mode `fake` (a noisy calibration snapshot of a real IBM chip), a 4-qubit GHZ-style chain | `error rate` = 1 minus the mean ZZ correlation measured from the returned counts. Real number, 2 s per job. |
| 4. Error correction, three deliveries | Same engine. Each delivery swaps in a better chip snapshot. Measured ladder today: brisbane 8.5%, kingston 5.1%, torino 4.6%, kyiv 4.1%, sherbrooke 3.7%, fez 1.7%, marrakesh 1.7% | Delivery 1 installs brisbane (8.5%), delivery 2 torino (4.6%), delivery 3 fez (1.7%). Threshold set at 2%. Driving damage adds a penalty on top of the live figure. |
| 5. Threshold | `comet-qrng-v1`, mode `qpu`, 64 qubits, `bell_witness` on | The countdown is the real hardware queue. Measured today: 22 s and 68 s on ibm_marrakesh. On screen: `S = 2.71 > 2`, 22 sigma above classical, p = 1e-106. |
| 6. Q-Day | Same job's output | The `commit` hash scrolls as the key being cracked. The derived random integers seed the chaos: who gets which weapon, which leak scrolls first, where the riot starts. |
| 7. Ending | None | Title card: "Randomness certified on IBM hardware via Moth Atlas. Job <id>. Commit <hash>." |

Verified output fields: graph-v1 returns `measurements[]{bitstring,count}` (compute ZZ from these), `tomography` (ideal, always 1.0, do not use for the error rate), `edge_agreement_score`, `backend`. comet-qrng returns `bell_witness.S`, `z_above_classical`, `p_value_local_realist`, `equivalent_depolarising_p`, `commitment.commit`, `provenance.backend`, `provenance.provider_job_id`, `random.derived.integers`, `entropy_report.grade`.

**Measured 21 Oct, things that bit:**
- graph-v1 `io` knobs `dynamical_decoupling`, `error_mitigation` and `sim_noise` fail the job outright on fake backends ("job failed and produced no result"). `twirling` is silently ignored. So the "unlock a real error-suppression technique" idea is parked; the chip ladder replaces it. Raise with the platform team.
- `fake_aachen` fails. The seven chips above work.
- comet-qrng at the default 12 qubits returns ZERO conditioned bytes on hardware: grade `hardware-insufficient-entropy`, because the ordering penalty for counts-only readout (about 12 bits per shot at 4096 shots) exceeds 12 qubits x 0.76 bits. The Bell witness and commit hash still come back. **Fixed: 64 qubits, 2048 shots on ibm_marrakesh returned in 22 s with grade `hardware-accounted`, 27k bits budget, 64 certified bytes, S = 2.71, 22 sigma.** Use `num_qubits: 64, shots: 2048` for the Q-Day job. The emulator path (12 qubits, needed to fit under its 20-qubit cap with the witness) gives bytes only sometimes; the sidecar falls back to local random and flags `certified: false`, which is fine for dev and never used in the video.

### How the game talks to Atlas

CLEO scripts cannot make HTTPS calls and must never hold the key. So:

- **Sidecar.** A small Python process on the Windows box (`qday-sidecar.py`) holds the Atlas key in its environment, watches a `request.json` the mod writes, submits the job, polls, and writes `result.json`. The CLEO script reads that file each second. Claude Code on the Windows box runs and debugs the sidecar.
- **Latency is part of the fiction.** Emulator and fake runs take 10 s to a few minutes; the garage shows "calibrating..." while the sidecar waits. The hardware run for Q-Day is submitted the moment the third logical qubit lands, and the countdown is however long the queue takes, which is honest and dramatic. For the video we can pre-submit.
- **Offline bundle for release.** The download ships with a `recorded/` folder of real job results (ids and commit hashes included) so it plays without a key. Players with their own Atlas key get live runs. No key ever ships in the mod.
- **Known gotchas.** Cloudflare rejects the default Python User-Agent, so the sidecar sends a browser one. macOS python.org builds need certifi. Stagger submits, the platform 503s on bursts.

### GTA V variant

Same beats. Humane Labs replaces Area 69, Michael's garage or the Vanilla Unicorn replaces Doherty. GTA V has a native riot mode, ATMs to switch off, and a richer phone for the leak ticker. Costs roughly two more days for the C# toolchain.

### Still needed

- Game: default is San Andreas unless told otherwise.
- The Windows box.
