# Reading the game

Mods here are written against the decompiled game. Never commit decompiled source or game files; keep them in a
scratch folder outside the repo (`decompiled/` and `Managed/` are gitignored as a guard).

## Decompiling the assemblies

ILSpy's command-line tool runs in the pinned SDK container; no local .NET install is needed.

```shell
TOOLS=/path/to/scratch/tools OUT=/path/to/scratch/decompiled
MANAGED="$VOLCANOIDS_DIR/Volcanoids_Data/Managed"
docker run --rm -u $(id -u):$(id -g) -e HOME=/tmp -v $TOOLS:/tools mcr.microsoft.com/dotnet/sdk:9.0.300 \
  dotnet tool install --tool-path /tools ilspycmd --version 9.1.0.7988
for assembly in Volcanoids Rock.Base Rock.Unity Unity.RenderPipelines.Universal.Runtime; do
  docker run --rm -u $(id -u):$(id -g) -e HOME=/tmp -e DOTNET_ROLL_FORWARD=Major \
    -v $TOOLS:/tools -v "$MANAGED":/managed:ro -v $OUT:/out mcr.microsoft.com/dotnet/sdk:9.0.300 \
    /tools/ilspycmd -p -o /out/$assembly -r /managed /managed/$assembly.dll
done
```

`Volcanoids.dll` is the game; `Rock.Base` and `Rock.Unity` are the studio's engine layer (`GameTime`,
`DataSceneBase`, helpers).

## Reading scenes and assets

The island is `StreamingAssets/Bundles/IslandScene.bundle`; shared assets, shaders and materials are in
`Common.bundle` (6.7 GB). The `.<Bundle>.txt` files beside each bundle list the asset paths inside it.

UnityPy reads them. It needs Python 3.12 or older for its compiled wheels, so it runs in a container:

```shell
docker run --rm -u $(id -u):$(id -g) -e HOME=/tmp -v $SCRATCH/pydeps:/deps \
  python:3.12.8-slim pip install -q --target /deps UnityPy==1.23.0
```

MonoBehaviour script references point into `Common.bundle`, so in the island bundle alone a behaviour has to be
recognised by its serialized field names. Loading `Common.bundle` takes a few minutes and around 20 GB of memory.

A few objects can be read out of `Common.bundle` without loading it, because it stores its data uncompressed (two
blocks, compression 0). Parse the UnityFS block table, hand UnityPy's `SerializedFile` a seekable stream over the
serialized node (the directory node with flag 4; the `.resS` and `.resource` nodes hold texture and audio payloads),
and set `UnityPy.config.FALLBACK_UNITY_VERSION = "6000.0.75f1"`, since the bundle strips the engine version. Its
metadata (1,241,315 objects) and a few dozen objects then cost a few hundred megabytes. A texture's pixels sit in the
`.resS` node at its `m_StreamData.offset`; a cubemap stores each face's whole mip chain in turn. In the island
scene, a reference with `m_FileID` 1 points into this file.

## The island's lighting, as found

- **`RenderSettings.ambientProbe` does not round-trip.** The getter returns the stored probe multiplied by the
  ambient intensity in linear space (the island's 0.66 reads back as 0.3931, so its stored 0.4719 reads as 0.1855);
  the setter stores exactly what it is given. Read with `ambientIntensity` set to 1 before writing anything back,
  or every write darkens the ambient light by that factor.
- One realtime directional light, `Sun` (intensity 1.8, soft shadows, about 35 degrees up). The eruption timeline
  (`IslandTimeLight`) rewrites its intensity every `LateUpdate`.
- No lightmaps. Ambient light is `Skybox` mode, an SH probe baked into the lighting data that the game leaves
  constant; 76 baked light probes, whose coefficients are not readable from scripts; 132 reflection probes in
  "normal" and "after the blast" pairs.
- The sky is `Skybox/CubemapBlended` (`_Tex`, `_Tex2`, `_Blend`, `_Tint`, `_Exposure`, `_Rotation`), blended by
  `IslandTimeSkybox`. The ocean copies those properties into its own material in `WaterMaterialSkybox.Update`.
- The island's fog is exponential: density about 0.0013 on a clear day in sky blue (0.28, 0.51, 0.68), from a scene
  base of 0.0026. Its linear start and end are 0 and 0.01 m, so switching it to linear would black everything out.
- Fog comes from the game's own URP volume component, `UnityEngine.Rendering.Universal.Fog`; `FogRenderPass` copies
  the blended value into `RenderSettings` per camera, so direct `RenderSettings` writes are overwritten.
- `ZoneSun` on each world camera switches the sun off when the camera has active zones and none on the `MainZone`
  layer (caves, underground drilling).
- Every surface camera's volume mask includes the `MainZone` layer; the island-wide weather volumes live on it.
- URP calls `beginCameraRendering` before it evaluates volumes and culls, so per-camera changes made there take
  effect for that camera.
- Shaders without fog that ship with the game: Unity's built-in `Sprites/Default` and `UI/Default` (in
  `UnityBuiltInShaders.bundle`), and the game's `Sprites/Camera Facing`.

## The eruption timeline, as found

`IslandTimeHandler` evaluates every timeline curve each frame at the current state's index plus its normalized time:
0 `Normal` (680 s), 1 `Beforeblast` (180 s), 2 `Eruption` (22 s), 3 `Afterblast` (460 s), 4 `Recover` (180 s). Every
length but the eruption's is divided by the save's `EruptionFrequency`, so a whole cycle takes about 25 minutes at a
frequency of 1. A save records the state and its normalized time in `DataIsland`.

- **Sun** (`Sun:m_Intensity`): 1.8 until 2.58, easing to 0.5 by 3.0, held at 0.5 to 3.62, back to 1.8 by 3.99.
- **Skybox cubemaps:** `Normal` until 2.59, gone by 2.77; `Afterblast` from 2.68, fading out from 4.0 to 4.48;
  `Recover` rising from 4.02 to 4.36 and fading back out by the cycle's end; `Normal` again from 4.75 to 4.99.
  `Afterblast` is a grey overcast roughly two thirds as bright as `Normal`; `Recover` is brighter than either.
- **Island-wide volumes** (all global, on `MainZone`):

| Volume (priority) | Weight over the timeline | Fog colour, sky cover (alpha), density |
| --- | --- | --- |
| `Default` (0) | 1, except 0 from 2.94 to 4.59 | sky blue, 0, 0.0013 |
| `DefaultGray` (1) | 1 from 2.1 to 4.6 | grey, 0, colour only |
| `LightDust` (2) | 1 from 2.36 to 4.6 | grey 0.6, 1, 0.0001 |
| `HeavyDust` (3) | 1 from 2.76 to 3.32, gone by 3.84 | grey 0.6, 1, 0.01 |
| `SuperDust` (4) | 1 from 2.79 to 3.06, gone by 3.44 | grey 0.38, 1, 0.18 |

The dust volumes grade the image exactly as `Default` does (post exposure 0.5, ACES tonemapping, white balance 4).
So by day an eruption turns the whole sky a flat, light grey for about ten minutes at a frequency of 1, with the sun
under a third for about five of them; the ambient probe never changes.

None of this depends on where the player stands. The only local volumes on the surface are the lava's
(`CrackLavaPostProcess`, `LavaPoolSurfacePostProcess`: orange fog covering the sky, density 0.001, a red vignette,
reaching 30 to 115 m from the lava) and `BlastWaveSurface`, which fades out over 4 seconds once the eruption state
ends.

## The island's scenery and drillships, as found

- **Trees are Unity terrain trees** (`TerrainData.treeInstances`, placed by `TerrainBuilder`): no GameObjects and
  no colliders, so raycasts pass straight through them. They never sway: every tree material is SpeedTree 8
  (`SpeedTree8Extra_PBRLit`) with `_WINDQUALITY_NONE`, enabling a wind quality at runtime changes nothing, and the
  scene's two wind zones (an ambient breeze, and one `ShakeWindZone` drives from camera shake) move only particles
  that have external forces on. Their leaves also take no fog. The models are `PoplarTree`, `MapleTree` and `SmallBush`.
  The terrain draws each at its prefab's root scale (2 for the trees) times the instance's own scale, so a maple
  stands about 40 m tall with a crown reaching 15 to 18 m from its trunk.
- Most surface landing sites are forest clearings with 50 to 140 trees within 90 m. The most open, under open sky,
  are Dangerous Edge (34) and The Edge (40), both on the coast.
- A drillship's pivot is near one end, not its middle. `Train.Length` is its length along the track: 94 m for the
  fully built tier 3 drillship in `StreamingAssets/Drillships/CreativeDrillship.save`, whose deployed modules make
  it about 44 m wide.
