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
- Fog comes from the game's own URP volume component, `UnityEngine.Rendering.Universal.Fog`; `FogRenderPass` copies
  the blended value into `RenderSettings` per camera, so direct `RenderSettings` writes are overwritten.
- `ZoneSun` on each world camera switches the sun off when the camera has active zones and none on the `MainZone`
  layer (caves, underground drilling).
- Every surface camera's volume mask includes the `MainZone` layer; the island-wide weather volumes live on it.
- URP calls `beginCameraRendering` before it evaluates volumes and culls, so per-camera changes made there take
  effect for that camera.
- Shaders without fog that ship with the game: Unity's built-in `Sprites/Default` and `UI/Default` (in
  `UnityBuiltInShaders.bundle`), and the game's `Sprites/Camera Facing`.
