# Q-DAY

A mission for GTA V Legacy or Enhanced. You help Jay build a quantum computer, it breaks the world's encryption, money stops existing and Los Santos turns on itself.

The quantum computer is real: every error rate on screen comes from an Atlas job, and Q-Day itself is a job on IBM quantum hardware whose random numbers decide how the riot plays out.

## Requirements

- ScriptHookV: http://www.dev-c.com/gtav/scripthookv/
- *Legacy*:
    - ScriptHookVDotNet: https://github.com/scripthookvdotnet/scripthookvdotnet
- *Enhanced*:
    - ScriptHookVDotNetEnhanced: https://github.com/Chiheb-Bacha/ScriptHookVDotNetEnhanced
    - DirectStorageFix: https://www.gta5-mods.com/scripts/directstoragefix

## Installation

Copy the `scripts` folder from this zip into the game folder. You should end up with the three Q-DAY files in your `scripts` folder.

```
scripts/
    QDay.dll
    QDay.ini
    QDay.pdb
```

## Starting the mission

Look for the yellow **Q** blip on the map, labelled Q-DAY. It's a payphone, and you'll hear it ringing as you get close. Walk up to it on foot and press **E** when the prompt appears.

**The heists.** Jay needs three parts of one machine: a fridge from Humane Labs, a command system from the FIB Building and a chip from Fort Zancudo. After each delivery, Atlas measures how many errors the part makes. Crashing the truck on the way back makes it worse. Each part has to come in under its target, 11%, then 6%, then 2%, or you go back for another one.

**Q-Day.** When Jay first pitches the plan, a real job starts on an IBM quantum computer and runs in the background while you do the heists. Its random numbers decide how the riot plays out.

## Settings

`scripts\QDay.ini`:

```ini
[General]
Debug = false

[Atlas]
ApiKey =
Offline = false
```

- **`ApiKey`**: paste your Moth Atlas key after the `=`. With a key, the measurements and the Q-Day job run live on Atlas. Without one, the mission uses real results recorded earlier, so it plays the same either way.
- **`Offline`**: set to `true` to use the recorded results even when a key is set, for example when your connection is bad.
- **`Debug`**: set to `true` to turn on debug keys.
