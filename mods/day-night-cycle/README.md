# Day/Night Cycle

A Volcanoids mod that gives the island a **30-minute day and a 20-minute night**.

The sun sinks into a golden-hour sunset, the sky darkens, and the stars come out: 5,500 of them, with the Milky Way
arching overhead and the brightest stars twinkling as the sky slowly turns. A moon rises and crosses the night, lit by
the sun below the horizon, and runs through all eight phases in their real order every eight days. Full-moon nights
are bright enough to see by; new-moon nights are pitch black apart from starlight.

At night the world itself goes dark, but nothing that makes its own light does. Lamps, your flashlight, lava, and the
glow of COGs and drillships stay as bright as ever, so they become what you navigate by. Caves keep their own lighting,
and the night fades away as you go underground.

## Multiplayer

**Client-side only.** Install it whether you host, join or play alone. It changes only what you see: nothing is sent
over the network, it never blocks joining a game, and players without it are unaffected.

Players who both run the mod see the **same sky at the same moment**, without either one needing the host to have
it, because every copy reads the time of day from the same clock (UTC, checked against Steam's servers).

## Things to know

- **Loading a save made without the mod** brings up the game's mod-mismatch dialog. Choose **Continue** to load it
  with the mod on; "switch mods" restarts the game with the save's own list, which turns the night off.
- Saves remember which mods were on. A save played with the mod lists it, and loading it without the mod brings up
  the same dialog, where Continue is again the way to keep playing.
- Day and night lengths are fixed, and have nothing to do with the eruption timer.
- The clock is real time: the sky keeps turning while the game is paused, and a save does not remember its time of
  day. Loading a game drops you wherever the shared clock is.
- Whenever the game dims its sun (as eruption ash does), the moon and stars dim with it.

## How it works

The night darkens the light reaching the world (the sun, ambient light, sky, reflections and fog), never the finished
image, which is why light sources keep their strength. The stars, Milky Way and moon are generated when a game loads,
from fixed seeds, so every player's sky is identical. Design notes: [docs/day-night-cycle.md](../../docs/day-night-cycle.md).

## Building

From the repository root:

```shell
scripts/mods.py install day-night-cycle
```

Developer launch options for testing (Steam: Properties, Launch Options): `-daynight-at 40` holds midnight,
`-daynight-moon 0.5` picks a full-moon night, and `-daynight-speed 30` runs a whole cycle in 100 seconds.
