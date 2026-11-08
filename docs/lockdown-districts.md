# The lockdown district file

Alejandro, this is the format I'd like to agree for `mod/lockdown_districts.json` before you replace the placeholder with real road data. Could you look at the three questions at the end in particular?

The file splits the escape area into districts, and each district becomes one qubit in a `graph-v1` job. That job decides which borders between districts are sealed on each run. Everything the game needs to know about the map for this comes from this file, so swapping in real data shouldn't need any code changes.

```json
{
  "placeholder": "Optional note, ignored by the game.",
  "districts": [
    {"id": 0, "name": "Mirror Park", "centre": [1150.0, -650.0, 60.0]},
    {"id": 1, "name": "La Mesa", "centre": [800.0, -1150.0, 25.0]}
  ],
  "links": [
    {"a": 0, "b": 1, "walls": [{"pos": [975.0, -900.0, 42.0], "heading": 145.0, "width": 12.0}]}
  ],
  "start": 0,
  "stops": [1]
}
```

## Fields

| Field | Meaning |
| --- | --- |
| `placeholder` | Optional text saying the file isn't real data yet. The game ignores it, and your file can leave it out. |
| `districts` | Up to 20 entries, because `graph-v1` allows 20 qubits. `id` must run 0, 1, 2 and so on, in order. |
| `districts[].name` | Used in the log, for example when a repair opens a border. |
| `districts[].centre` | World position as x, y, z. Only the last stop's centre is used at the moment, as the temporary end of the escape. |
| `links` | One entry for each pair of districts with at least one road crossing the border between them. |
| `links[].a`, `links[].b` | The two district ids. The order doesn't matter, and a pair can't be listed twice. |
| `links[].walls` | Every site where a force field goes if this border is sealed, so one per road crossing. |
| `walls[].pos` | World position as x, y, z, mostly at junctions. |
| `walls[].heading` | Degrees. Nothing reads this yet, so the convention is yours to set (see below). |
| `walls[].width` | Width of the road to block, in metres. The debug marker uses it, and it defaults to 12 if left out. |
| `start` | The district the escape starts in. |
| `stops` | The districts the player has to reach, with the last one as the final destination. The start and all the stops are held open to each other on every run. |

## Rules

The game refuses the file on load, and logs why, if the ids are out of order, if a district has no `centre`, if there are more than 20 districts, if a link refers to a missing district, joins a district to itself or is listed twice, or if the start can't reach every stop with all the borders open. A refused file means the mission skips the escape and goes straight to the ending.

Two rules can't be checked by the loader, so they rest on the data:

- **Every district must be drivable end to end on its own roads.** The route guarantee only works out which districts connect to each other, so if a district is split in two by a river or a motorway with no junction, a route that looks open may not be.
- **Every road crossing a border needs a wall.** A link with a missing wall leaves a gap in the cordon, and a link with an empty `walls` list is accepted but blocks nothing.

## When the file changes

The recorded jobs in `recorded/lockdown_NN.json` are tied to this file's links, so they need recording again with `tools/record_lockdowns.py` whenever it changes, and `tests/LockdownCheck` fails until they are. The target settings also need checking on the real map, since 9 of the 20 jobs recorded on the placeholder needed a repair and the aim is fewer than 1 in 5. I'd expect fewer stops, and more links around the start and stops, to make repairs less likely, though I haven't measured either.

## Questions for Alejandro

1. **What should `heading` mean?** I'd suggest the direction a car is travelling as it crosses the wall, in GTA's heading convention, but since the force field is yours you should choose whichever suits it. The placeholder's headings are rough, so don't rely on them.
2. **Is one position per wall enough for the force field,** given the width, or do you need two end points?
3. **Do you need anything else per wall or per link** for the minimap markers or particles, such as a road node id?

The placeholder's districts, stops and positions are all rough and unchecked in game. The stops are undecided as well, so please treat them as stand-ins until Declan confirms them.
