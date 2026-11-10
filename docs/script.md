# Q-DAY: the script

Every line the player sees, in the order they see it, taken from `mod/QDay.cs` on 9 November. Edit the lines here, add notes in the margins, write new scenes where they're marked, and the code can be brought into line with this file afterwards.

**Status** says whether a scene is in the game and has been played, is in the code but not yet played, or isn't written yet.

## Mission order

1. Lester: the pitch
2. Drive to Humane Labs, shoot-out, drive back safely
3. Lester: calibration, then the next part
4. Drive to the FIB Building, hacking game, drive back safely
5. Lester: calibration, then the next part
6. Drive to Fort Zancudo, shoot-out, hacking game, drive back safely
7. Lester: calibration passes, then the wait on IBM hardware
8. Q-Day: the title, then the riot starts
9. The escape, with the riot carrying on around the player: drive to the bank, roadblock, through the crowd on foot, cash from the vault, the passport from a house, a car, then the airport or a helipad
10. End

---

## 1. The pitch

**Status:** in the game, played. The lines below are new and not in the code yet.

The player walks to the yellow Q blip at Lester's house and presses E. Lester's lines arrive as texts on the phone, so there's no voice acting.

> **Lester:** I've been building something in my garage...
>
> **Lester:** A quantum computer. One that works could open every bank account in Los Santos. Every police file. Every phone.
>
> **Lester:** Mine doesn't work yet. Too many errors.
>
> **Lester:** If I get the error rate under 2%, it starts fixing its own mistakes. Then it opens everything.
>
> **Lester:** All I need is a fridge colder than outer space, a better command system and a better chip. I know where all three are. You in?

Title card: **Q-DAY**, build a fault-tolerant quantum computer

The first heist starts straight after (see section 2).

*Note, not on screen:* the IBM job for Q-Day starts in the background here (see section 7).

*Notes:*

## 2. Heist one: Humane Labs

**Status:** the truck, the keypad and the 3-star wanted level are in the game and played. The cryostat line is new and not in the code yet. The guards aren't built: there's only a placeholder for them in `QDay.cs` on Alejandro's branch.

> **Lester:** First, the fridge. The chip only works when it's kept just above absolute zero, and mine isn't cold enough. Every bit of heat causes errors. Humane Labs has the one we need. It's called a cryostat.

Objective: *Steal the cryostat from Humane Labs.*

**Shoot-out.** Guards inside the Humane Labs garage. The garage stays shut until the player shoots the keypad, and the truck with the cryostat is inside. Taking the truck sets a 3-star wanted level.

Then the drive back (section 5).

*Notes:*

## 3. Heist two: FIB Building

**Status:** the truck and the 4-star wanted level are in the game and played. The hacking game is built in pull request #11, not yet played. Lester's line and the objective are new and not in the code yet.

> **Lester:** Next, the command system. It tells the qubits what to do. Mine's too old. Too many errors. I've heard the FIB has a brand new, government-grade one. Get it however you can.

Objective: *Steal the command system from the FIB Building.*

**Hacking game: Fix the errors** (built in pull request #11, not yet played). A terminal stands near the van, and the van stays locked until the hack is solved.

Objective: *Hack the terminal to release the truck.*

Help text by the terminal: *Press E to hack the terminal.*

On screen: **FIX THE ERRORS**. *A red light means two neighbours disagree. Find the flipped qubits and flip them back.* A 3 by 3 grid with one flipped qubit and no timer. When solved: **SYSTEM OPEN**.

Taking the truck sets a 4-star wanted level.

Then the drive back (section 5).

*Notes:*

## 4. Heist three: Fort Zancudo

**Status:** the truck and the 5-star wanted level are in the game and played. The shoot-out isn't built. The hacking game is built in pull request #11, not yet played. Lester's line and the objective are new and not in the code yet.

> **Lester:** Last one. The chip. The qubits sit on it, and mine's too flawed to hold them steady. Fort Zancudo has a military-grade one. It's called a qubit array. Do whatever it takes, and DON'T DAMAGE IT.

Objective: *Steal the qubit array from Fort Zancudo.*

**Shoot-out** (not built), then the **hacking game, Fix the errors** (built in pull request #11, not yet played), to release the qubit array. Same text as at the FIB Building (section 3), with a 4 by 4 grid, two flipped qubits and a 60-second timer. When the timer runs out the grid resets with a new layout; the mission doesn't fail.

Taking the truck sets a 5-star wanted level.

Then the drive back (section 5).

*Notes:*

## 5. The drive back

**Status:** in the game, played. Lester's line is new and not in the code yet.

> **Lester:** Got it. Bring it to me. Every bump makes it more error-prone.

If the player leaves the truck: *Get in the truck.*

*Note, not on screen:* collision damage to the truck adds to the error rate on the HUD, and the HUD shows the current part's target. Each part has its own target (section 6), so every drive counts. Decided on 9 November: only collision damage should count, since police gunfire on the truck currently pushes the error rate up however carefully the player drives. Decided on 10 November: the per-part targets. Both are built in pull request #13 (the truck is now bulletproof, so only crashes, fire and explosions damage it), not yet played.

*Notes:*

## 6. Calibration

**Status:** in the game, played. The error rate is a real Atlas job on a simulated IBM chip. The targets, lines and title cards below are built in pull request #13, not yet played. The HUD shows the part number, the error rate and the current part's target.

This runs after each delivery. It is the Lester beat between heists: the result for the part just delivered, then the line for the next heist.

> **Lester:** Installing the new part. Let's see where the errors are now.

Each part has its own target, and the target tightens as the machine gets closer:

| Part | Error rate from Atlas | Target | Damage that still passes |
|---|---|---|---|
| Fridge | 8.5% | under 11% | about 2.5 points |
| Command system | 4.6% | under 6% | about 1.4 points |
| Chip | 1.7% | under 2% | about 0.3 points |

Then one of:

- **Fridge, under target.** Title card: **ERROR RATE 9.1%**, target under 11%.
  > **Lester:** Under 11%. Good enough for now.

  Then the command system line (section 3).
- **Command system, under target.** Title card: **ERROR RATE 5.2%**, target under 6%.
  > **Lester:** Under 6%. Getting closer.

  Then the chip line (section 4).
- **Chip, under target.** Title card: **ERROR RATE 1.8%**, under 2%.
  > **Lester:** Under 2%. It's fixing its own mistakes. We have a working machine.

  Then the threshold (section 7).
- **Any part, over target.** Title card: **ERROR RATE 2.4%**, over the 2% target.
  > **Lester:** Over the target. That's your driving. Get me another one, and don't damage it.

  Then the same heist again, without Lester's opening line for it.

*Note, not on screen:* the figures on the title cards are examples. Each is the part's error rate from Atlas plus the collision damage from that drive, so the code fills them in.

*Notes:*

## 7. Threshold

**Status:** the short version below is built in pull request #12, not yet played. Before it, this was a wait of up to three minutes on the live IBM job.

About ten seconds. The camera shakes and the screen flickers. The player keeps control. The thunderstorm starts once Q-Day begins, not here, since a storm clears the streets before the riot can arm anyone.

Title card: **THRESHOLD**, switching on...

> **Lester:** It's running.

Then Q-Day (section 8).

*Note, not on screen:* the IBM job starts in the background at the pitch, so it has the length of all three heists to come back. If it hasn't come back by the threshold, the game uses a run picked at random from a library of recorded IBM runs. Either way the riot runs on real numbers from IBM hardware and the player never waits. The library holds five recorded runs: ibm_marrakesh (21 October) and four on ibm_fez (10 November). The fallback also starts reading each run's 64 random numbers from a random point, so the riot differs between playthroughs.

*Notes:*

## 8. Q-Day: the riot

**Status:** the riot is in the game and played, but as five minutes with no objective. Decided on 10 November: the escape starts straight after the title, and the riot carries on around the player through the whole escape. Not in the code yet. The animated title below is built in pull request #14, not yet played.

**Title, about five seconds, over the riot as it starts.** The words **qday activated** in Moth's Atlas Type gridfont, full weight, white on a darkened screen. It flickers through a different set of letterforms 20 times a second for two seconds, slows over one second, and lands on one set chosen by the IBM run's random numbers. It holds for a second and a half, then fades out over half a second. A sound sting plays as it lands.

Under the title, while it holds: *every key on earth is public    Bell S = (the run's score) > 2*

The player keeps control throughout, and the news ticker runs underneath.

*Note, not on screen:* the letterforms are 120 seeds of the gridfont exported from Atlas Type (seeds 6584 to 6703, heavy-noise simulator, Random letters), and each playthrough starts the flicker at a different seed.

Money goes to zero and the cash readout disappears. Pedestrians are armed and turn on everyone. A news ticker runs along the bottom of the screen:

- KEY *(the job's hash)* FACTORED

  *Note, not on screen:* the code currently adds "ON (the IBM machine)" to this headline and needs changing to match.
- LSPD INTERNAL MAIL: NOW PUBLIC
- MAZE BANK: ALL ACCOUNTS READ $0
- LIFEINVADER DMs: EVERY ONE. LIVE.
- FLEECA VAULT PIN: 0000
- FIB AGENTS: HOME ADDRESSES POSTED
- WEAZEL NEWS: 'IS MONEY REAL?'
- LOS SANTOS CUSTOMS: YOUR CAR IS NOT YOURS
- MERRYWEATHER PAYROLL: PUBLIC
- EVERY BURNER PHONE IN BLAINE COUNTY: TRANSCRIBED
- CITY HALL: DEEDS DATABASE EMPTY

*More headlines can go here, and the order is shuffled by the quantum random numbers.*

*Notes:*

## 9. The escape

**Status:** partly built, not yet played. The sealed roads and force fields are in the code (pull requests #9 and #10). The structure and Lester's lines below were decided on 10 November; the stops are still to build and the lines aren't in the code yet.

Straight after the title, while the riot builds:

> **Lester:** Every bank balance just hit zero. Cash is king. We broke the door codes on every major bank. Get to *(the bank)* NOW.

1. **The bank.** Money in accounts is gone, but cash in a vault is still real, and the vault codes are public (the ticker's FLEECA VAULT PIN: 0000). Placeholder: a Fleeca branch. Objective: *Get to the bank.*
2. **The roadblock.** A sealed road stops the car short of the bank. The player leaves the car and makes their way through an armed crowd on foot.

   Objective: *Go on foot to the bank.*
3. **The vault.** Open, since the code is public. The player takes the cash.

   Objective: *Take the cash.*

   > **Lester:** Got it? Good. Now get your passport. We need to get out of here. I'll text you where the flight's leaving from as soon as I know. I'll see you on the other side.

   Objective: *Get your passport from (the house).*
4. **The passport.** At one house.

   > **Lester:** Flight's leaving from *(the airport or helipad)*. The pilot only takes cash. Go.

   Objective: *Get to (the airport or helipad).*
5. **A car.** Any car gets the player out. Stretch goal: a fast car with the Entanglement Shader paint from the Quantum Paint mod, waiting in the garage at the passport house.
6. **The way out.** Drive to the airport or a helipad. The pilot only takes cash now, which is what the bank was for.

   If a pilot character can be waiting there: Objective: *Pay the pilot.* The screen fades once the player reaches them. If not, the screen fades as the player arrives.

*For Alejandro:* which bank, and whether its interior can be walked into in story mode (Fleeca is the placeholder); whether the roadblock can be one of the lockdown's sealed roads, placed so it always blocks the route to the bank; whose house has the passport and where; airport or helipad, and where; whether a pilot character can be waiting at the airport or helipad, or the screen just fades on arrival.

*Notes:*

## 10. Ending

**Status:** in the game, played. The credit wording is built in pull request #12, not yet played. The new title is built in pull request #14, not yet played.

The screen fades to black.

**Title, in the same style as qday activated (section 8).** The words **you survived qday** in the Atlas Type gridfont, full weight, white on black. It flickers through letterforms 20 times a second for two seconds, slows over one second, and lands on one set chosen by the IBM run's random numbers, with the same sound sting. It holds for about four seconds, longer than the Q-Day title since nothing is happening behind it, then fades.

Under the title, while it holds:

> Random numbers generated on *(the IBM machine)* via Moth Atlas. Job *(the job's ID)*.

*Notes:*

## If it goes wrong

Dying in any part fails the mission, which goes back to the start.

Title card: **MISSION FAILED**, you died

> **Mission failed.** Go back to the Q blip to start again.

*Notes:*
