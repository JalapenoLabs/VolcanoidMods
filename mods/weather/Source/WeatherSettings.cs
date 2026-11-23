// Copyright © 2026 Jalapeno Labs

using System;
using System.IO;
using UnityEngine;

namespace JalapenoLabs.Weather;

/// <summary>
/// The player's own setting for the mod: how loud the weather is, set with the Weather slider the mod adds to the
/// game's audio options (<see cref="WeatherVolumeSlider"/>).
///
/// It is kept as a small JSON file in the mod's own data folder, the place the game gives each mod for exactly this,
/// rather than in the game's options file, which the game rewrites whole and would not expect a stranger's field in.
/// Everything else about the weather stays fixed.
/// </summary>
internal sealed class WeatherSettings {
    [Serializable]
    sealed class Stored {
        public float Volume = 1;
    }

    const string FileName = "settings.json";

    readonly string path;

    WeatherSettings(string path, float volume) {
        this.path = path;
        Volume = volume;
    }

    /// <summary>How loud every weather sound plays, 0 (silent) to 1 (as made), under the master volume.</summary>
    public float Volume { get; set; }

    /// <summary>Reads the settings from the mod's data folder, or starts from the defaults if there are none.</summary>
    public static WeatherSettings Load(string directory) {
        string path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) {
            Debug.Log($"{WeatherMod.LogPrefix}No saved settings at {path} yet; the weather plays at full volume");
            return new WeatherSettings(path, 1);
        }
        try {
            Stored stored = JsonUtility.FromJson<Stored>(File.ReadAllText(path));
            return new WeatherSettings(path, Mathf.Clamp01(stored.Volume));
        } catch (Exception exception) {
            Debug.LogWarning($"{WeatherMod.LogPrefix}Could not read the settings at {path}; using the defaults: "
                + exception.Message);
            return new WeatherSettings(path, 1);
        }
    }

    public void Save() {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(new Stored { Volume = Volume }));
        } catch (Exception exception) {
            Debug.LogError($"{WeatherMod.LogPrefix}Could not save the settings to {path}: {exception.Message}");
        }
    }
}
