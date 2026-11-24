// Copyright © 2026 Jalapeno Labs

using UnityEngine;

namespace JalapenoLabs.Weather;

/// <summary>
/// Watches for the game's options screen opening, to add the Weather volume slider to it (see
/// <see cref="WeatherVolumeSlider"/>). It lives for the whole session, across the main menu and every game.
/// </summary>
internal sealed class OptionsWatcher : MonoBehaviour {
    /// <summary>
    /// Finding objects by type is not free, so the screen is looked for only this often; the slider appears a moment
    /// after the screen opens, before anyone reaches for it.
    /// </summary>
    const float CheckSeconds = 0.25f;

    WeatherSettings settings;
    float nextCheck;

    public static void Begin(WeatherSettings settings) {
        var host = new GameObject("Weather.OptionsWatcher");
        DontDestroyOnLoad(host);
        host.AddComponent<OptionsWatcher>().settings = settings;
    }

    void Update() {
        if (Time.unscaledTime < nextCheck) {
            return;
        }
        nextCheck = Time.unscaledTime + CheckSeconds;
        WeatherVolumeSlider.AddToOpenOptionsScreens(settings);
    }
}
