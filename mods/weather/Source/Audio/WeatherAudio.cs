// Copyright © 2026 Jalapeno Labs

using System;
using System.Collections.Generic;
using System.IO;
// The game defines its own global Task type, which hides .NET's.
using Tasks = System.Threading.Tasks;
using JalapenoLabs.Weather.Synth;
using UnityEngine;
using UnityEngine.Networking;
using Object = UnityEngine.Object;

namespace JalapenoLabs.Weather.Audio;

/// <summary>
/// Everything the storm sounds like: rain (light or heavy, in the open or heard through a roof), the drum of rain on a
/// drillship's hull beside you, wind, and thunder.
///
/// The rain is recorded (see <c>Recordings/CREDITS.md</c>): four loops shipped in the mod's Sounds folder, light and
/// heavy rain in the open and the same two as a steel hull lets them through. Going under a roof crossfades from the
/// open loops to the muffled ones; nothing is filtered live, because a filter swept as the player moves warbles. The
/// wind and thunder are synthesised on a worker thread when the island loads (see <see cref="WeatherSounds"/>), and
/// the wind's muffled twin is the same loop through a fixed filter. Until the sounds are ready the storm is silent.
///
/// Every source plays without a mixer group, so it follows the player's master volume (AudioListener.volume) and
/// pauses with the game, and on top of that the Weather volume the mod adds to the options (<see
/// cref="WeatherSettings"/>). Underground the weather falls silent.
/// </summary>
internal sealed class WeatherAudio : IDisposable {
    /// <summary>The wind heard through walls: muffled to this cutoff, at this share of its volume.</summary>
    const float ShelteredWindCutoff = 900;
    const float ShelteredWindLevel = 0.35f;

    /// <summary>How loud thunder is heard from under a roof, against out in the open.</summary>
    const float ShelteredThunderLevel = 0.55f;

    /// <summary>Each rain loop's level at full strength; the loops are rendered to a common loudness.</summary>
    const float OpenLightLevel = 0.75f;
    const float OpenHeavyLevel = 0.9f;
    const float IndoorLightLevel = 0.7f;
    const float IndoorHeavyLevel = 0.85f;

    /// <summary>Beside a drillship out in the open, the drumming on its hull mixes in up to this share.</summary>
    const float HullDrumShare = 0.45f;

    /// <summary>The speed of sound, for the gap between a flash and its thunder.</summary>
    const float SoundSpeed = 343;

    /// <summary>Closer than this a strike cracks; farther, only its rumble arrives.</summary>
    const float CrackDistance = 650;

    /// <summary>Closer than this a strike shakes the camera.</summary>
    const float ShakeDistance = 350;

    /// <summary>How near a drillship the rain can be heard drumming on its hull from outside.</summary>
    const float HullDrumReach = 25;

    /// <summary>How often the distance to the nearest drillship hull is measured again.</summary>
    const float HullCheckSeconds = 0.25f;

    const int ThunderVariants = 3;

    /// <summary>The recorded loops, by the file they are shipped as.</summary>
    enum Recording {
        RainLight,
        RainHeavy,
        RainLightIndoor,
        RainHeavyIndoor,
    }

    readonly struct PendingThunder {
        public readonly float PlayAt;
        public readonly AudioClip Clip;
        public readonly float Volume;
        public readonly float Pan;

        public PendingThunder(float playAt, AudioClip clip, float volume, float pan) {
            PlayAt = playAt;
            Clip = clip;
            Volume = volume;
            Pan = pan;
        }
    }

    /// <summary>What the worker thread synthesises: the wind loop, then the thunder variants.</summary>
    sealed class SoundSet {
        public SoundBuffer Wind;
        public SoundBuffer[] NearThunder;
        public SoundBuffer[] FarThunder;
    }

    readonly GameObject host;
    readonly WeatherSettings settings;
    readonly Tasks.Task<SoundSet> synthesis;
    readonly string dumpDirectory;
    readonly Dictionary<Recording, UnityWebRequest> loading = new Dictionary<Recording, UnityWebRequest>();
    readonly Dictionary<Recording, AudioSource> rain = new Dictionary<Recording, AudioSource>();
    readonly List<PendingThunder> pendingThunder = new List<PendingThunder>();
    readonly List<AudioSource> thunderSources = new List<AudioSource>();
    readonly List<AudioClip> clips = new List<AudioClip>();
    readonly ShakeData closeThunderShake;
    readonly Collider[] nearbyColliders = new Collider[128];
    float hullProximity;
    float nextHullCheck;
    AudioSource wind;
    AudioSource windIndoor;
    AudioClip[] nearThunder;
    AudioClip[] farThunder;
    bool isReady;
    bool hasLoggedFailure;

    public WeatherAudio(Transform parent, WeatherSettings settings, string soundsDirectory, string dumpDirectory) {
        host = new GameObject("Weather.Audio");
        host.transform.SetParent(parent, false);
        this.settings = settings;
        this.dumpDirectory = dumpDirectory;
        synthesis = Tasks.Task.Run(Synthesise);
        foreach (Recording recording in (Recording[])Enum.GetValues(typeof(Recording))) {
            string path = Path.Combine(soundsDirectory, $"{recording}.ogg");
            if (!File.Exists(path)) {
                Debug.LogError($"{WeatherMod.LogPrefix}The recorded rain {path} is missing; that rain stays silent");
                continue;
            }
            UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(
                new Uri(path).AbsoluteUri,
                AudioType.OGGVORBIS
            );
            // Kept compressed in memory and decoded as it plays: four loops at once cost little, and all of them
            // decompressed would hold some 30 MB.
            var handler = (DownloadHandlerAudioClip)request.downloadHandler;
            handler.streamAudio = false;
            handler.compressed = true;
            request.SendWebRequest();
            loading[recording] = request;
        }

        closeThunderShake = ScriptableObject.CreateInstance<ShakeData>();
        closeThunderShake.name = "Weather.CloseThunder";
        closeThunderShake.Magnitude = 1.1f;
        closeThunderShake.Roughness = 9;
        closeThunderShake.Length = 0.35f;
        closeThunderShake.FadeOutTime = 1.6f;
        closeThunderShake.MaxDistance = ShakeDistance;
    }

    static SoundSet Synthesise() {
        var set = new SoundSet {
            Wind = WeatherSounds.Wind(104),
            NearThunder = new SoundBuffer[ThunderVariants],
            FarThunder = new SoundBuffer[ThunderVariants],
        };
        for (int variant = 0; variant < ThunderVariants; variant++) {
            set.NearThunder[variant] = WeatherSounds.Thunder(200 + (ulong)variant, isNear: true);
            set.FarThunder[variant] = WeatherSounds.Thunder(300 + (ulong)variant, isNear: false);
        }
        return set;
    }

    /// <summary>Called each frame with the storm's state and where the player is.</summary>
    /// <param name="rainStrength">How hard it rains, 0 to 1.</param>
    /// <param name="windSpeed">The wind's speed this moment, gusts included, in metres per second.</param>
    /// <param name="surface">0 underground, 1 on the surface.</param>
    /// <param name="outdoors">0 under a roof, 1 out in the open.</param>
    public void Update(float rainStrength, float windSpeed, float surface, float outdoors, Vector3 listener) {
        if (!isReady && !TryFinishLoading()) {
            return;
        }
        float volume = settings.Volume * surface;
        // An equal-power crossfade, so stepping through a door neither dips nor swells the rain.
        float open = Mathf.Sqrt(outdoors);
        float sheltered = Mathf.Sqrt(1 - outdoors);

        // Under a roof, or out beside a drillship, the rain drums on the hull.
        if (Time.time >= nextHullCheck) {
            hullProximity = MeasureHullProximity(listener);
            nextHullCheck = Time.time + HullCheckSeconds;
        }
        float indoor = Mathf.Clamp01(sheltered + open * HullDrumShare * hullProximity);

        // Light rain is the light recording; as it strengthens it gives way to the heavy one.
        float heavyShare = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.35f, 0.85f, rainStrength));
        float lightShare = Mathf.Clamp01(rainStrength / 0.2f) * (1 - heavyShare);
        SetVolume(Recording.RainLight, OpenLightLevel * lightShare * open * volume);
        SetVolume(Recording.RainHeavy, OpenHeavyLevel * heavyShare * open * volume);
        SetVolume(Recording.RainLightIndoor, IndoorLightLevel * lightShare * indoor * volume);
        SetVolume(Recording.RainHeavyIndoor, IndoorHeavyLevel * heavyShare * indoor * volume);

        float windLevel = 0.55f * Mathf.Clamp01(windSpeed / 10) * volume;
        wind.volume = windLevel * open;
        windIndoor.volume = windLevel * ShelteredWindLevel * sheltered;

        PlayDueThunder();
    }

    void SetVolume(Recording recording, float volume) {
        if (rain.TryGetValue(recording, out AudioSource source)) {
            source.volume = volume;
        }
    }

    /// <summary>
    /// Schedules a strike's thunder: it arrives after the sound's travel time, from the strike's direction, as a crack
    /// up close and a rumble far off.
    /// </summary>
    public void Thunder(
        Vector3 strike,
        float power,
        ulong seed,
        bool reachesGround,
        Camera camera,
        float surface,
        float outdoors
    ) {
        if (!isReady || camera == null || surface <= 0) {
            return;
        }
        Vector3 toStrike = strike - camera.transform.position;
        float distance = toStrike.magnitude;
        bool isNear = reachesGround && distance < CrackDistance;
        AudioClip[] variants = isNear ? nearThunder : farThunder;
        AudioClip clip = variants[(int)(seed % (ulong)variants.Length)];

        float loudness = power * Mathf.Clamp(1.25f - distance / 2600, 0.12f, 1) * (reachesGround ? 1 : 0.7f);
        float volume = loudness * Mathf.Lerp(ShelteredThunderLevel, 1, outdoors) * surface;
        float pan = Mathf.Clamp(Vector3.Dot(camera.transform.right, toStrike.normalized), -1, 1) * 0.6f;
        pendingThunder.Add(new PendingThunder(Time.time + distance / SoundSpeed, clip, volume, pan));

        if (isNear && distance < ShakeDistance) {
            new ShakeRuntime().Start(closeThunderShake, strike);
        }
    }

    void PlayDueThunder() {
        for (int index = pendingThunder.Count - 1; index >= 0; index--) {
            PendingThunder thunder = pendingThunder[index];
            if (Time.time < thunder.PlayAt) {
                continue;
            }
            pendingThunder.RemoveAt(index);
            AudioSource source = FreeThunderSource();
            source.clip = thunder.Clip;
            // The Weather volume applies when the thunder plays, so moving the slider takes effect on the next roll.
            source.volume = thunder.Volume * settings.Volume;
            source.panStereo = thunder.Pan;
            source.Play();
        }
    }

    AudioSource FreeThunderSource() {
        foreach (AudioSource source in thunderSources) {
            if (!source.isPlaying) {
                return source;
            }
        }
        AudioSource created = CreateSource("Thunder", null, isLooping: false);
        thunderSources.Add(created);
        return created;
    }

    /// <summary>1 against a drillship's hull, fading to 0 at <see cref="HullDrumReach"/> from it.</summary>
    float MeasureHullProximity(Vector3 listener) {
        int count = Physics.OverlapSphereNonAlloc(
            listener,
            HullDrumReach,
            nearbyColliders,
            Layers.MaskToolRaycast,
            QueryTriggerInteraction.Ignore
        );
        float nearest = HullDrumReach;
        for (int index = 0; index < count; index++) {
            Collider collider = nearbyColliders[index];
            if (collider.GetComponentInParent<TrainWagons>() == null) {
                continue;
            }
            nearest = Mathf.Min(nearest, Vector3.Distance(collider.ClosestPointOnBounds(listener), listener));
        }
        return 1 - nearest / HullDrumReach;
    }

    /// <summary>Starts every loop once the synthesis and the recordings have all finished loading.</summary>
    bool TryFinishLoading() {
        if (!synthesis.IsCompleted) {
            return false;
        }
        foreach (UnityWebRequest request in loading.Values) {
            if (!request.isDone) {
                return false;
            }
        }
        if (synthesis.IsFaulted) {
            if (!hasLoggedFailure) {
                Debug.LogError($"{WeatherMod.LogPrefix}The storm's wind and thunder could not be made; the storm "
                    + $"stays silent: {synthesis.Exception}");
                hasLoggedFailure = true;
            }
            return false;
        }

        foreach (KeyValuePair<Recording, UnityWebRequest> entry in loading) {
            using UnityWebRequest request = entry.Value;
            AudioClip clip = request.result == UnityWebRequest.Result.Success
                ? DownloadHandlerAudioClip.GetContent(request)
                : null;
            if (clip == null) {
                Debug.LogError($"{WeatherMod.LogPrefix}Could not load the recorded rain {entry.Key}: {request.error}");
                continue;
            }
            clip.name = $"Weather.{entry.Key}";
            clips.Add(clip);
            rain[entry.Key] = CreateSource(entry.Key.ToString(), clip, isLooping: true);
        }
        loading.Clear();

        SoundSet set = synthesis.Result;
        AudioClip windClip = ToClip("Weather.Wind", set.Wind);
        wind = CreateSource("Wind", windClip, isLooping: true);
        windIndoor = CreateSource("WindIndoor", windClip, isLooping: true);
        // A fixed filter: the indoor wind is a separate source crossfaded in, never a cutoff swept as the player moves.
        windIndoor.gameObject.AddComponent<AudioLowPassFilter>().cutoffFrequency = ShelteredWindCutoff;
        nearThunder = new AudioClip[ThunderVariants];
        farThunder = new AudioClip[ThunderVariants];
        for (int variant = 0; variant < ThunderVariants; variant++) {
            nearThunder[variant] = ToClip($"Weather.ThunderNear{variant}", set.NearThunder[variant]);
            farThunder[variant] = ToClip($"Weather.ThunderFar{variant}", set.FarThunder[variant]);
        }

        var loops = new List<AudioSource>(rain.Values) { wind, windIndoor };
        foreach (AudioSource loop in loops) {
            loop.volume = 0;
            // Start each loop at a different point, so they never line up into an audible pattern.
            loop.timeSamples = (int)(loop.clip.samples * ((loop.GetInstanceID() & 0xFF) / 256f));
            loop.Play();
        }
        // The open and indoor versions of a recording are the same rain; started together, they crossfade in step.
        AlignWith(Recording.RainLightIndoor, Recording.RainLight);
        AlignWith(Recording.RainHeavyIndoor, Recording.RainHeavy);
        windIndoor.timeSamples = wind.timeSamples;

        if (dumpDirectory != null) {
            DumpSounds(set);
        }
        Debug.Log($"{WeatherMod.LogPrefix}Storm sounds ready ({rain.Count} recorded loops, {clips.Count} clips)");
        isReady = true;
        return true;
    }

    void AlignWith(Recording follower, Recording leader) {
        bool hasFollower = rain.TryGetValue(follower, out AudioSource following);
        bool hasLeader = rain.TryGetValue(leader, out AudioSource leading);
        if (!hasFollower || !hasLeader) {
            Debug.Log($"{WeatherMod.LogPrefix}{follower} or {leader} did not load, so they play out of step");
            return;
        }
        following.timeSamples = leading.timeSamples;
    }

    AudioSource CreateSource(string name, AudioClip clip, bool isLooping) {
        var child = new GameObject(name);
        child.transform.SetParent(host.transform, false);
        var source = child.AddComponent<AudioSource>();
        source.clip = clip;
        source.loop = isLooping;
        source.playOnAwake = false;
        source.spatialBlend = 0;
        // The island's reverb zones are tuned for its own sounds at their own positions; weather is all around.
        source.bypassReverbZones = true;
        source.dopplerLevel = 0;
        return source;
    }

    AudioClip ToClip(string name, SoundBuffer sound) {
        AudioClip clip = AudioClip.Create(name, sound.Frames, SoundBuffer.Channels, sound.SampleRate, false);
        clip.SetData(sound.Samples, 0);
        clips.Add(clip);
        return clip;
    }

    /// <summary>Developer aid (-weather-dump-sounds): writes every synthesised sound as a 16-bit WAV file.</summary>
    void DumpSounds(SoundSet set) {
        try {
            Directory.CreateDirectory(dumpDirectory);
            WriteWav("wind", set.Wind);
            for (int variant = 0; variant < ThunderVariants; variant++) {
                WriteWav($"thunder-near-{variant}", set.NearThunder[variant]);
                WriteWav($"thunder-far-{variant}", set.FarThunder[variant]);
            }
            Debug.Log($"{WeatherMod.LogPrefix}Wrote the storm's synthesised sounds to {dumpDirectory}");
        } catch (Exception exception) {
            Debug.LogError($"{WeatherMod.LogPrefix}Could not write the storm's sounds to {dumpDirectory}: {exception}");
        }
    }

    void WriteWav(string name, SoundBuffer sound) {
        using var writer = new BinaryWriter(File.Create(Path.Combine(dumpDirectory, name + ".wav")));
        int dataBytes = sound.Samples.Length * 2;
        writer.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
        writer.Write(36 + dataBytes);
        writer.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)SoundBuffer.Channels);
        writer.Write(sound.SampleRate);
        writer.Write(sound.SampleRate * SoundBuffer.Channels * 2);
        writer.Write((short)(SoundBuffer.Channels * 2));
        writer.Write((short)16);
        writer.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
        writer.Write(dataBytes);
        foreach (float sample in sound.Samples) {
            writer.Write((short)Mathf.Round(Mathf.Clamp(sample, -1, 1) * short.MaxValue));
        }
    }

    public void Dispose() {
        foreach (UnityWebRequest request in loading.Values) {
            request.Dispose();
        }
        Object.Destroy(host);
        Object.Destroy(closeThunderShake);
        foreach (AudioClip clip in clips) {
            Object.Destroy(clip);
        }
    }
}
