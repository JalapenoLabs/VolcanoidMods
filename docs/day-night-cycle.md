# Day/Night Cycle: design

Volcanoids is lit by one sun that never moves. Day/Night Cycle (`mods/day-night-cycle`) gives the surface a
30-minute day and a 20-minute night: dusk, a starry sky with the Milky Way, a moon that runs through its phases, and
nights dark enough that lamps, the flashlight and the glow of cogs and drillships are what players see by.

## Requirements

These come from the user and are fixed:

- **Client-side only.** It works whether the player hosts, joins or plays alone, and changes nothing for players
  without it. Manifest `MultiplayerMode` is `2` (ClientOnly), so it never blocks a join (see
  [modding.md](modding.md#multiplayer-rules)).
- **In sync between players who both have it**, without relying on the other player or the host having it.
- **30 minutes of day, 20 of night, hardcoded.** Not configurable, and unrelated to the per-save eruption timer.
- **A base light at night**, so the surface stays playable, while lamps and the flashlight become important.
- **A moon whose phases follow the real order** and cycle through all of them in a few days.

## The clock

Every copy of the mod reads the time of day from UTC wall time, so two players share a sky with no networking.

The game offers nothing better. Its only synced time is the island's place in the eruption cycle (state index and
normalized time, sent every two seconds), which starts over at each eruption and whose length differs per save.
`GameTime` is the local `Time.time`, player stats are per player and not networked, and no `OnSerialize` in the game
writes a counter that survives the cycle wrap.

The local clock is checked once per game load against `SteamUtils.GetServerRealTime()`. Differences under 2 seconds
(Steam reports whole seconds) are ignored; larger ones correct the clock, so a badly set system clock cannot put one
player's sky out of step.

What wall time costs, by design: the sky keeps turning while the game is paused, a save does not remember its time of
day, and every player of the mod worldwide shares the same schedule.

## The schedule

`Source/Cycle/CycleSchedule.cs` is a pure function of the clock, with no Unity types, and is unit tested.

- A cycle is 50 minutes. Sunrise is at minute 0, solar noon at 15, sunset at 30, midnight at 40.
- Dawn and dusk each last 4 minutes, centred on sunrise and sunset. Daylight eases with a smoothstep.
- Golden hour (warm light and sky) peaks at sunrise and sunset and fades over 4 minutes either side.
- The moon's phase advances continuously, one full round every 8 days, so each night shows the next of the eight
  phases in the real order. The moon culminates later each day by its phase, as the real one does: alongside the sun
  at new moon (no moon at night, the darkest nights), opposite it at full moon (up all night), and for half the night
  at the quarters.
- The star field turns once per cycle about the celestial pole.

## The sky's geometry

`Lighting/CelestialGeometry.cs`. The sun and moon share one great circle that rises opposite the game's sun and sets
under it, culminating 40 degrees up. The low culmination keeps the moon inside a level-looking player's view.

From mid-morning to late afternoon the light is exactly the game's own sun, which the island was lit for. In the
last part of the day it sinks straight down along the game's sun azimuth, stretching the existing shadows into a
sunset; in the first part of the morning it climbs from the opposite horizon.

## Rendering

The night darkens the light falling on the world, never the finished image, so lamps, the flashlight, lava and
emissive cogs keep their full strength. Nothing ships but one assembly: no asset bundle, no custom shader.

Almost everything is applied per camera, just before a surface camera renders, and undone right after
(`Lighting/ICameraEffect.cs`). The game rewrites much of its lighting every frame from the eruption timeline, and
several values live on shared assets that outlive the scene, so this never fights the game's updates and never leaks
into the main menu. URP evaluates volumes and culls after `beginCameraRendering`, so changes there take effect.

A camera is a surface camera when it has the game's `ZoneSun` and ZoneSun's rule gives it the sun (no active zones, or
one on the `MainZone` layer). Inside the drillship the player's camera has no zones, so the night applies there too and
the cabin's own lamps light it. Crossing into a cave fades the night out over 1.5 seconds.

| Effect | What it changes |
| --- | --- |
| `SunMoonLight` | The game's `Sun` light: dimmed and warmed at dusk, then turned into moonlight (blue, from the moon's direction, as bright as its phase and height allow) |
| `AmbientLight` | `RenderSettings.ambientProbe` (read as stored, see [reverse-engineering.md](reverse-engineering.md)) and the default reflection intensity |
| `SkyMaterials` | The skybox's `_Exposure` and `_Tint`, and the ocean material that copies them (`WaterMaterialSkybox`) |
| `ReflectionProbeDimmer` | The intensity of each reflection probe that can see the sky |
| `NightVolume` | A global volume (priority 9, `MainZone` layer) with the game's `Fog` component and colour saturation |
| `CelestialSky` | Milky Way, stars, moon, moon halo and horizon haze, drawn after the skybox |

Reflection probes are dimmed by how much open sky they see (`SkyExposure`: five upward raycasts against the game's
own `Layers.MaskToolRaycast`), because night takes away only sky light: surface probes go dark, cave probes keep their
lava glow. The island's 76 baked light probes expose no coefficients to scripts (neither `LightmapSettings.lightProbes`
nor `LightProbes.GetSharedLightProbesForScene`), so the indirect part of the light on moving objects keeps its daytime
level; the darker sun, ambient and reflections still dim them. The scene report logs whether that ever changes.

The volume's priority sits above the island-wide weather volumes (0 to 5) and below the local lava and blast-wave
volumes (10), so lava keeps its own red haze at night, and the deploy, stun and armour effects (20 and up) stay on top.

### The night sky

All sky parts draw with Unity's built-in `Sprites/Default` shader, which ships in the game's built-in shader bundle.
It is unlit, alpha-blended, depth-tested and takes no fog, so stars at the far plane stay crisp yet hills still hide
them. The parts hang off a root that follows the camera at 95% of its far plane, render in queues 2501 to 2505 (just
after the opaque range: Milky Way, stars, moon, halo, haze), and are enabled only inside a surface camera's render.

Textures and meshes are generated at load from fixed seeds with the mod's own xorshift generator, so every player sees
the identical sky: 5,500 stars clustered along a Milky Way tilted 62 degrees from the pole, blue-white to orange, the
160 brightest twinkling; a cloudy Milky Way band with a warm core and a dust lane; a cratered moon whose face is
repainted for each phase, its bright side turned towards the sun below the horizon; and a band of night-fog colour
round the horizon that fades stars and a setting moon into the haze.

### Eruptions

The game dims its sun under eruption ash. The night reads that brightness against the usual 1.8 as sky clarity and
dims the moonlight, stars and moon by it, so ash hides the night sky too, without the mod knowing about eruptions.

## Tuning

Every brightness and colour lives in `Lighting/NightLook.cs`. Developer launch options (`DevOptions.cs`) hold the clock
at a minute (`-daynight-at 40`), pick a moon (`-daynight-moon 0.5`), speed it up (`-daynight-speed 30`), or photograph
a list of moments and quit (`-daynight-capture DIR -daynight-shots "15,30.5,40@0.5"`), which `scripts/smoke_test.py`
drives. For marketing shots, `-daynight-showcase FILE` adds a fully built drillship design, lit in every light slot,
and frames it instead of the player's view (`docs/building.md#smoke-tests-linux`).
