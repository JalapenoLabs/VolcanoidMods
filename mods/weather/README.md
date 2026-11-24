# Rain & Thunderstorms

[Releases](https://github.com/JalapenoLabs/VolcanoidMods/releases?q=weather) · [Patreon](https://www.jalapenolabs.io/patreon)

A Volcanoids mod that brings **rain and thunderstorms** to the island.

About once an hour, clouds roll in over two or three minutes and darken, and it rains for ten minutes before the sky
slowly clears again. Each storm is one of three kinds, equally likely:

- **Light rain:** grey clouds that still let some sky through, a light shower and small puddles.
- **Heavy rain:** dark clouds, dim light, a heavy downpour with mist on the horizon, and puddles that spread.
- **Thunderstorm:** heavy rain with lightning. Bolts crawl through the clouds and light them up from inside, and
  thunder rolls in after them, sooner and louder the closer they strike.

Lightning lands mostly at random, but it prefers tall things: a strike close to a player, a COG or a drillship jumps
to it. A direct hit hurts. Head for cover when the thunder gets close.

The storm comes with everything that belongs to it:

- Rain sounds loud and clear outside, and muffled and cozy inside a drillship, a mine or a shelter, with rain drumming
  on the hull when you stand next to a drillship.
- Raindrops land on your view and run down it while you're out in the open.
- Puddles gather on open ground, ripple in the rain, and dry up after the storm.
- The ground and drillship hulls turn glossy; rain splashes off the hulls and pours off the roof edges.
- Wind you can see: gusts tear leaves from the trees around you and blow them downwind, drive sheets of spray
  across the ground, ripple the grass, and stream the drillships' chimney smoke off sideways. The rain slants harder
  and the wind roars in each gust.
- The sea rises with the wind: bigger swells, whitecaps and chop.
- Lava pools steam all the time, and boil off great white clouds when rain falls on them. Walking through the steam
  is hard going: you can barely see the far side of the pool.

Underground, all of it stops: no rain, no thunder, no darkened light. It all comes back as you reach the surface.

## Install

- **Steam Workshop (recommended):** subscribe on the Workshop, then turn it on in the game's Mods menu. Steam keeps it
  up to date.
- **By hand:** download the zip from the [latest release](https://github.com/JalapenoLabs/VolcanoidMods/releases?q=weather)
  and unzip it into the game's `LocalMods` folder, so you get `LocalMods/Weather/`
  (Windows: `%USERPROFILE%\AppData\LocalLow\Volcanoid\Volcanoids\LocalMods`,
  Linux: `~/.config/unity3d/Volcanoid/Volcanoids/LocalMods`). Then turn it on in the Mods menu. A save that lists a
  hand-installed mod cannot host multiplayer; the Workshop copy can.

## Multiplayer

**Client-side only.** Install it whether you host, join or play alone. It changes only what you see and hear: nothing
is sent over the network, it never blocks joining a game, and players without it are unaffected.

Players who both run the mod get **the same storms at the same moment, down to every lightning bolt**, without either
one needing the host to have it, because every copy reads the weather from the same clock (UTC, checked against
Steam's servers).

Lightning only does damage in a game hosted by a player with the mod, because the game only applies damage on the
host. There it hurts the host's own character and any COG it hits; players who join see the same bolts but take no
damage from them.

## Day/Night Cycle

Rain & Thunderstorms works with [Day/Night Cycle](../day-night-cycle/README.md) out of the box: storms roll in by day
and by night, and storm clouds hide the moon and stars. Neither mod needs the other.

## Things to know

- **Loading a save made without the mod** brings up the game's mod-mismatch dialog. Choose **Continue** to load it
  with the mod on.
- The weather runs on real time, like a clock: storms keep coming while the game is paused, and a save does not
  remember its weather. Loading a game drops you into whatever the sky is doing right now.
- Armour soaks up a lightning strike the way it soaks up any other hit. Being under a roof (inside a drillship, a mine
  or a shelter) keeps you safe from strikes entirely, and so does a drillship's hull for the COGs inside it.
- The only setting is a **Weather** volume slider, under Warning volume in the game's audio options; the storm
  schedule itself is fixed.

## How it works

The storm schedule is a pure function of the time: every copy of the mod works out the same storms, and the same
lightning strikes, from the same clock. The clouds, rain, splashes, puddles, steam and lightning are particles and
lights drawn with the game's own shaders, and every texture is generated when a game loads. The rain is recorded
(public-domain recordings by Ylmir; see [Recordings/CREDITS.md](Recordings/CREDITS.md)); the wind and thunder are
synthesised. Design notes: [docs/weather.md](../../docs/weather.md).

## Support

Patreon: https://www.jalapenolabs.io/patreon

## Building

From the repository root:

```shell
scripts/mods.py install weather
```

Developer launch options for testing (Steam: Properties, Launch Options): `-weather-at thunder@-1` starts at the next
thunderstorm as its clouds roll in, `-weather-hold` with it freezes that moment (`thunder@0.5` is mid-storm), and
`-weather-speed 5` runs the weather five times faster. With `-weather-keys`, the numpad calls up weather on the
spot: 0 a thunderstorm, 1 light rain, 2 heavy rain (each a few minutes into its rain), 3 a thunderstorm from the
very start, and 5 back to the shared clock. These put your weather out of step with other players, so they are only
for testing. Moments are `light`, `heavy` or `thunder` at a progress: -1 as
the clouds start, 0 as the rain starts, 1 as it stops, 2 once the sky is clear.
