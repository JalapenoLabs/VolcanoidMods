# Rain & Thunderstorms: design

Volcanoids has no weather beyond its eruptions. Rain & Thunderstorms (`mods/weather`) brings a storm to the surface
about once an hour: clouds that roll in and darken, rain, puddles, wet hulls, rough seas, steaming lava, and, in a
third of the storms, lightning that can hurt what it hits.

## Requirements

These come from the user and are fixed:

- **Client-side only**, like Day/Night Cycle: manifest `MultiplayerMode` `2` (ClientOnly), so it never blocks a join
  and changes nothing for players without it.
- **In sync between players who both have it**, from an independent global clock, not from the host or each other.
- **Fully compatible with Day/Night Cycle.**
- **About once an hour, ten minutes of rain**, almost on a timer, with **slow two-to-three-minute transitions** while
  the clouds roll in and turn dark grey, and again as they clear.
- **Light rain and heavy rain.** Light rain has lighter clouds, lighter rain and very small puddles; heavy rain has dark
  clouds and dark lighting. A storm has a **one-in-three chance to be a thunderstorm**, and thunderstorms are always
  heavy rain.
- **Thunderstorms:** thunder sounds, lightning lighting the clouds from inside, and bolts that strike mostly at random
  and **damage the player or a COG** they hit.
- **Rain falling from the sky, volumetric-looking clouds, puddles** that build up and respond to the rain, **rain
  hitting the drillship's exterior**, and **raindrops on the HUD** while out in the open.
- **Rain sounds loud and clear outside and muffled inside** a drillship, a mineshaft or a house's bunker.
- **Nothing underground:** no sound, HUD effect or lighting change while the player is in caves or tunnels.
- **Optional extras** taken up: seas that grow rougher with the storm, and large steam clouds over the surface's lava
  pools that are hard to see through.
- **Nothing is configurable:** no settings, nothing per save; everything is hardcoded.

## The clock

The weather reads the same clock as Day/Night Cycle (`SyncedClock.cs`): UTC wall time, checked once per load against
`SteamUtils.GetServerRealTime()`, with differences under 2 seconds ignored. Every copy of the mod therefore has the
same weather at the same moment, with no networking. As with the night, the storms keep coming while the game is
paused and a save does not remember its weather.

## The forecast

`Source/Forecast/` is a pure function of the clock, with no Unity types, and is unit tested (`Tests/`).

- Time is cut into hour-long slots. Each slot's storm is drawn from a seeded hash of the slot number
  (`StormRandom`, splitmix64), so it is the same for everyone and needs no state.
- A storm's rain starts 10 to 35 minutes into its slot. Its clouds roll in for 150 seconds before that, the rain lasts
  600 seconds (easing in and out over 45), and the sky clears over the 150 seconds after.
- Each storm is light rain, heavy rain or a thunderstorm with equal chance. Its profile:

  | Kind | Cloud cover | Darkness | Rain | Puddles | Wind (m/s) | Lightning |
  | --- | --- | --- | --- | --- | --- | --- |
  | Light rain | 0.8 | 0.35 | 0.3 | 0.3 | 3 | no |
  | Heavy rain | 1 | 0.75 | 1 | 1 | 7 | no |
  | Thunderstorm | 1 | 0.9 | 1 | 1 | 10 | yes |

- The rain gusts around its level with slow value noise; the wind's heading is drawn per storm.
- Wetness (puddle depth, the sheen on surfaces) fills over 3 minutes of rain and dries over 4, in closed form, so it
  carries over from the previous slot without any state.
- A thunderstorm's lightning (`LightningSchedule.cs`) is drawn from the same seed: discharges about 5 seconds apart on
  average (exponential gaps, clamped to 1.2 to 18 s), kept out of the first and last 30 seconds of the rain. 60% reach
  the ground as bolts; the rest flicker inside the clouds. Most fall anywhere on the island, and 30% within 300 m of a
  storm core that drifts with the wind, so strikes cluster as real cells do. Each has a power between 0.5 and 1.

## Where the player is

`PlayerLocation.cs` sorts the local player, every frame, into one of three:

| Exposure | Where | What the storm does |
| --- | --- | --- |
| Outside | Out under the open sky | Everything: rain on the lens, rain loud and clear |
| Sheltered | In a drillship (`BoardedDrillship.IsInside`), a drillship interior zone, a blast shelter or blast mine (`BlastWaveSafeZone`), or under any solid roof (a ray up 120 m against `Layers.MaskExplosionVisibility`) | The storm is outside: muffled sound, no lens drops, no lightning damage |
| Underground | In zones with no surface layer and the underground layer, or in a drillship travelling underground | Nothing at all, not even the darker light |

The surface share eases over 1.5 seconds and the outdoor share over 0.6, so crossing a threshold never snaps.

## Rendering

The storm changes the light falling on the world and adds particles, lights and fog; it never filters the finished
image, so lamps, lava and glowing COGs keep their strength in the gloom. Nothing ships but one assembly: no asset
bundle and no custom shader. Every effect is drawn with a copy of one of the game's own materials, found by its
shader's enabled keywords (`GameMaterials.cs`): the lit smoke particle material for clouds, rain, splashes, spray and
steam, the unlit glow for bolts, and transparent glass for puddles. Textures (`Art/`) and sounds (`Synth/`) are
generated on a worker thread when a game loads, from fixed seeds.

| Part | What it does |
| --- | --- |
| `StormSun` | Dims the sun, cools it and softens its shadows, as an owned value at `Application.onBeforeRender` |
| `StormAmbience` | Per render: dims and tints the ambient light and reflections, and crossfades the skybox into the game's own painted storm sky (the "Afterblast" cubemap of `IslandTimeSkybox`); lightning flashes boost it |
| `StormVolume` | A global volume (priority 7, `MainZone` layer) with the game's `Fog` and colour saturation: greyer, denser air in heavy rain |
| `CloudDeck` | 96 clouds of 7 lit puffs each in a 3.2 km field that drifts with the wind and wraps around the camera; they arrive from upwind as cover rises and darken with the storm |
| `RainFall` | Near drops around the camera that collide with the world and splash, and a sparser, wider veil of far drops |
| `HullRain` | Splashes cast onto the roofs of every drillship within 160 m (they grow with distance so they still show), mist over the roofs in heavy rain, and run-off pouring from the roof edges |
| `WetSurfaces` | Raises the smoothness of the terrain layers and the drillships' exterior paint with wetness, and restores it exactly |
| `Puddles` | Puddles on flat, open, non-grassy terrain near the camera, kept clear of lava, that grow with wetness and ripple in the rain |
| `LensRain` | Drops on a screen overlay under the HUD, out in the open only, more when looking up; hidden with the HUD |
| `LightningStrikes` | Brings each scheduled strike down: bolt, flash lights in the cloud and on the ground, a directional flash, thunder and damage |
| `StormSea` | Raises the ocean's wave amplitude and length, swings the swell before the wind, and adds foam and chop |
| `LavaSteam` | Steam columns over every surface lava pool, with a local fog volume (priority 11) that makes them hard to see through; always on gently, far stronger in rain |

`DrillshipExteriors` finds every drillship's exterior renderers (its paint system separates exterior from interior)
every 8 seconds, for `HullRain` and `WetSurfaces`.

### Working alongside Day/Night Cycle

Both mods change the same lights, sky and fog, so they nest rather than fight:

- The storm applies its per-render changes at `RenderPipelineManager.beginContextRendering` and undoes them at
  `endContextRendering`. Day/Night applies its own per camera, inside that, and undoes them first, so each change
  unwinds in strict reverse order and each mod restores exactly what it found.
- The storm's sun dimming persists between frames (applied at `Application.onBeforeRender`), so the night, which reads
  the sun's brightness as sky clarity, sees the storm's gloom and dims the moon and stars with it.
- The storm publishes its cloud cover as the global shader value `_JalapenoLabs_CloudCover` (0 clear to 1 overcast).
  Day/Night takes it away from its sky clarity, so storm clouds hide the stars, moon and moonlight. It is the only
  link between the two mods: neither references the other, and either runs alone.
- The storm's fog volume sits at priority 7, under the night's (9), the game's lava and blast-wave volumes (10), and
  the steam fog (11).

## Sound

`Audio/WeatherAudio.cs` plays four synthesised loops (downpour, patter, roof drumming, wind) and thunder. Sheltered,
low-pass filters close down to about 750 Hz, for the muffled, cozy sound of rain heard through a hull or a roof.
Standing next to a drillship adds the drumming of rain on its hull. Thunder is delayed by its distance at the speed of
sound and panned towards the strike; within 650 m it starts with a sharp crack, and within 350 m it shakes the camera.
The sources bypass reverb zones and mixer groups, so caves' reverb never colours the storm, and all of it falls silent
underground.

## Lightning damage

Damage follows the game's rules. Only the host applies damage (the game's `Health` ignores it anywhere else), so the
mod deals damage only when `NetworkServer.active`:

- the host's own player, if out in the open and within 9 m of where the bolt lands;
- every COG within 9 m, unless it is inside a drillship.

A strike takes up to 60% of the target's maximum health, scaled by its power, as `DamageType.Electricity`, so armour
absorbs it as it absorbs any non-heat, non-water damage. A strike landing within 25 m of a player, a COG or the top of
a drillship jumps to it, so standing out in a thunderstorm is a real risk. Players who join a host see the same bolts
but take no damage from them: the host cannot know whether a joining player's game shows the lightning, and a hit from
a bolt nobody could see would be unfair.

## Tuning

Every brightness, colour and density of the storm's light lives in `Lighting/StormLook.cs`; each visual part keeps its
own counts and sizes as named constants. Developer launch options (`DevOptions.cs`) start the weather at a moment of
the next storm of a kind (`-weather-at thunder@-1`), freeze it there (`-weather-hold`), speed it up
(`-weather-speed 5`), or photograph a list of moments and quit (`-weather-capture DIR -weather-shots
"light@0.5,thunder@0.5"`), which `scripts/smoke_test.py weather` drives. `-weather-showcase FILE` adds a fully built
drillship design for the drillship views, as Day/Night's showcase does (`docs/building.md#smoke-tests-linux`), and
`-weather-test-damage` strikes the player and the nearest COG and logs their health.
