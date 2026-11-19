// Copyright © 2026 Jalapeno Labs

using System;
using System.Collections.Generic;
using System.IO;
// The game defines its own global Task type, which hides .NET's.
using Tasks = System.Threading.Tasks;
using JalapenoLabs.Weather.Synth;
using UnityEngine;
using Object = UnityEngine.Object;

namespace JalapenoLabs.Weather.Audio;

/// <summary>
/// Everything the storm sounds like: rain (a light patter or a roaring downpour), wind, the drum of rain on a roof
/// overhead, and thunder.
///
/// The sounds are synthesised on a worker thread when the island loads (see <see cref="WeatherSounds"/>); until they
/// are ready the storm is silent. Every source plays without a mixer group, so it follows the player's master volume
/// (AudioListener.volume) and pauses with the game, but no zone's mixer snapshot can duck or swell it. Muffling under a
/// roof is the mod's own low-pass filter. Underground the weather falls silent.
/// </summary>
internal sealed class WeatherAudio : IDisposable {
    /// <summary>How the rain sounds through walls: muffled to this cutoff, at this share of its volume.</summary>
    const float ShelteredCutoff = 750;
    const float ShelteredLevel = 0.4f;
    const float OpenCutoff = 22000;

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

    /// <summary>What the worker thread synthesises: the loops, then the thunder variants.</summary>
    sealed class SoundSet {
        public SoundBuffer Downpour;
        public SoundBuffer Patter;
        public SoundBuffer Roof;
        public SoundBuffer Wind;
        public SoundBuffer[] NearThunder;
        public SoundBuffer[] FarThunder;
    }

    readonly GameObject host;
    readonly Tasks.Task<SoundSet> synthesis;
    readonly string dumpDirectory;
    readonly List<PendingThunder> pendingThunder = new List<PendingThunder>();
    readonly List<AudioSource> thunderSources = new List<AudioSource>();
    readonly List<AudioClip> clips = new List<AudioClip>();
    readonly ShakeData closeThunderShake;
    readonly Collider[] nearbyColliders = new Collider[128];
    float hullProximity;
    float nextHullCheck;
    AudioSource downpour;
    AudioSource patter;
    AudioSource roof;
    AudioSource wind;
    AudioLowPassFilter downpourFilter;
    AudioLowPassFilter patterFilter;
    AudioLowPassFilter windFilter;
    AudioClip[] nearThunder;
    AudioClip[] farThunder;
    bool isReady;
    bool hasLoggedFailure;

    public WeatherAudio(Transform parent, string dumpDirectory) {
        host = new GameObject("Weather.Audio");
        host.transform.SetParent(parent, false);
        this.dumpDirectory = dumpDirectory;
        synthesis = Tasks.Task.Run(Synthesise);

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
            Downpour = WeatherSounds.Downpour(101),
            Patter = WeatherSounds.Patter(102),
            Roof = WeatherSounds.Roof(103),
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
    public void Update(float rain, float windSpeed, float surface, float outdoors, Vector3 listener) {
        if (!isReady && !TryFinishSynthesis()) {
            return;
        }

        float cutoff = Mathf.Lerp(ShelteredCutoff, OpenCutoff, outdoors * outdoors);
        float level = Mathf.Lerp(ShelteredLevel, 1, outdoors) * surface;
        downpourFilter.cutoffFrequency = cutoff;
        patterFilter.cutoffFrequency = cutoff;
        windFilter.cutoffFrequency = Mathf.Lerp(ShelteredCutoff * 2, OpenCutoff, outdoors);

        // Light rain is mostly patter; as it strengthens the patter drowns in the downpour's roar.
        float drowned = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.35f, 0.9f, rain));
        float patterShare = Mathf.Clamp01(rain / 0.3f) * (1 - drowned);
        float downpourShare = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.15f, 1, rain));
        patter.volume = 0.75f * patterShare * level;
        downpour.volume = 0.85f * downpourShare * level;

        // Under a roof, or next to a drillship, the rain drums on the panels overhead.
        if (Time.time >= nextHullCheck) {
            hullProximity = MeasureHullProximity(listener);
            nextHullCheck = Time.time + HullCheckSeconds;
        }
        float hullDrum = (1 - outdoors) + outdoors * 0.5f * hullProximity;
        roof.volume = 0.8f * Mathf.Clamp01(rain * 1.3f) * hullDrum * surface;

        wind.volume = 0.55f * Mathf.Clamp01(windSpeed / 10) * Mathf.Lerp(0.35f, 1, outdoors) * surface;

        PlayDueThunder();
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
        float volume = loudness * Mathf.Lerp(ShelteredLevel + 0.15f, 1, outdoors) * surface;
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
            source.volume = thunder.Volume;
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

    bool TryFinishSynthesis() {
        if (!synthesis.IsCompleted) {
            return false;
        }
        if (synthesis.IsFaulted) {
            if (!hasLoggedFailure) {
                Debug.LogError($"{WeatherMod.LogPrefix}The storm's sounds could not be made; it stays silent: "
                    + synthesis.Exception);
                hasLoggedFailure = true;
            }
            return false;
        }

        SoundSet set = synthesis.Result;
        downpour = CreateSource("Downpour", ToClip("Weather.Downpour", set.Downpour), isLooping: true);
        patter = CreateSource("Patter", ToClip("Weather.Patter", set.Patter), isLooping: true);
        roof = CreateSource("Roof", ToClip("Weather.Roof", set.Roof), isLooping: true);
        wind = CreateSource("Wind", ToClip("Weather.Wind", set.Wind), isLooping: true);
        downpourFilter = downpour.gameObject.AddComponent<AudioLowPassFilter>();
        patterFilter = patter.gameObject.AddComponent<AudioLowPassFilter>();
        windFilter = wind.gameObject.AddComponent<AudioLowPassFilter>();
        nearThunder = new AudioClip[ThunderVariants];
        farThunder = new AudioClip[ThunderVariants];
        for (int variant = 0; variant < ThunderVariants; variant++) {
            nearThunder[variant] = ToClip($"Weather.ThunderNear{variant}", set.NearThunder[variant]);
            farThunder[variant] = ToClip($"Weather.ThunderFar{variant}", set.FarThunder[variant]);
        }
        foreach (AudioSource loop in new[] { downpour, patter, roof, wind }) {
            loop.volume = 0;
            // Start each loop at a different point, so the loops never line up into an audible pattern.
            loop.timeSamples = (int)(loop.clip.samples * ((loop.GetInstanceID() & 0xFF) / 256f));
            loop.Play();
        }

        if (dumpDirectory != null) {
            DumpSounds(set);
        }
        Debug.Log($"{WeatherMod.LogPrefix}Storm sounds ready ({clips.Count} clips)");
        isReady = true;
        return true;
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

    /// <summary>Developer aid (-weather-dump-sounds): writes every sound as a 16-bit WAV file to listen to.</summary>
    void DumpSounds(SoundSet set) {
        try {
            Directory.CreateDirectory(dumpDirectory);
            WriteWav("downpour", set.Downpour);
            WriteWav("patter", set.Patter);
            WriteWav("roof", set.Roof);
            WriteWav("wind", set.Wind);
            for (int variant = 0; variant < ThunderVariants; variant++) {
                WriteWav($"thunder-near-{variant}", set.NearThunder[variant]);
                WriteWav($"thunder-far-{variant}", set.FarThunder[variant]);
            }
            Debug.Log($"{WeatherMod.LogPrefix}Wrote the storm's sounds to {dumpDirectory}");
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
        Object.Destroy(host);
        Object.Destroy(closeThunderShake);
        foreach (AudioClip clip in clips) {
            Object.Destroy(clip);
        }
    }
}
