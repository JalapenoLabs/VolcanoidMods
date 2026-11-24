// Copyright © 2026 Jalapeno Labs

using System;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace JalapenoLabs.Weather;

/// <summary>
/// A Weather volume slider in the game's audio options, under its Warning volume, so the storm can be turned down
/// without turning down the game.
///
/// The game builds its options screen from a prefab (OptionsUi) and offers mods no way to add a setting, so the slider
/// is a copy of the Warning volume's own row: the same look, layout and behaviour, relabelled and rewired. Its event is
/// replaced outright, so nothing the original row was wired to comes along, and the label's localisation component is
/// removed, so a language change cannot rename it back. The value applies at once and is saved when the options screen
/// closes; the game's Cancel button does not undo it, since the game's options code knows nothing about it.
///
/// The options screen does not exist until it is opened (menus instantiate it from a prefab, and nothing in the game's
/// code names it), so <see cref="OptionsWatcher"/> looks for a newly opened one a few times a second, for the whole
/// session.
/// </summary>
internal sealed class WeatherVolumeSlider : MonoBehaviour {
    const string Label = "Weather";

    static readonly FieldInfo WarningVolumeField = typeof(OptionsUi).GetField(
        "m_warningVolume",
        BindingFlags.Instance | BindingFlags.NonPublic
    );

    WeatherSettings settings;
    bool hasChanged;

    /// <summary>Whether the game's options screen still has the Warning volume slider this copies.</summary>
    public static bool CanAdd => WarningVolumeField != null;

    /// <summary>Adds the slider to every open options screen that does not have one yet.</summary>
    public static void AddToOpenOptionsScreens(WeatherSettings settings) {
        OptionsUi[] screens = Object.FindObjectsByType<OptionsUi>(
            FindObjectsInactive.Exclude,
            FindObjectsSortMode.None
        );
        foreach (OptionsUi screen in screens) {
            if (screen.GetComponentInChildren<WeatherVolumeSlider>(includeInactive: true) == null) {
                AddTo(screen, settings);
            }
        }
    }

    /// <summary>
    /// For developer captures, which cannot open the options screen: builds the slider on a hidden copy of the options
    /// prefab and logs what it made, so a game update that moves the Warning volume row shows up in a smoke test.
    /// </summary>
    public static void TestOnPrefab(WeatherSettings settings) {
        OptionsUi prefab = null;
        foreach (OptionsUi candidate in Resources.FindObjectsOfTypeAll<OptionsUi>()) {
            if (!candidate.gameObject.scene.IsValid()) {
                prefab = candidate;
                break;
            }
        }
        if (prefab == null) {
            Debug.Log($"{WeatherMod.LogPrefix}Slider test: no options prefab loaded in this scene to try");
            return;
        }
        // Under an inactive parent, so the copy's own scripts never wake.
        var holder = new GameObject("Weather.SliderTest");
        holder.SetActive(false);
        OptionsUi copy = Object.Instantiate(prefab, holder.transform);
        AddTo(copy, settings);
        WeatherVolumeSlider added = copy.GetComponentInChildren<WeatherVolumeSlider>(includeInactive: true);
        string label = added != null ? added.GetComponentInChildren<TMP_Text>(includeInactive: true)?.text : null;
        Debug.Log($"{WeatherMod.LogPrefix}Slider test: {(added != null ? $"added, labelled '{label}'" : "not added")}");
        Destroy(holder);
    }

    static void AddTo(OptionsUi screen, WeatherSettings settings) {
        if (!(WarningVolumeField.GetValue(screen) is Slider warning)) {
            Debug.LogWarning($"{WeatherMod.LogPrefix}'{screen.name}' has no Warning volume slider to copy");
            return;
        }
        Transform row = RowOf(warning);
        if (row == null) {
            Debug.LogWarning($"{WeatherMod.LogPrefix}Could not find the row around '{PathOf(warning.transform)}'; "
                + "the Weather volume slider is left out");
            return;
        }
        GameObject copy = Object.Instantiate(row.gameObject, row.parent, worldPositionStays: false);
        copy.name = "WeatherVolume";
        copy.transform.SetSiblingIndex(row.GetSiblingIndex() + 1);
        copy.AddComponent<WeatherVolumeSlider>().Wire(settings);
        Debug.Log($"{WeatherMod.LogPrefix}Added the Weather volume slider beside '{PathOf(row)}'");
    }

    /// <summary>
    /// The row a slider sits in: its largest ancestor that holds no other slider. That takes in the slider's label and
    /// stops short of the list of settings the row belongs to. A row without any text is not a row.
    /// </summary>
    static Transform RowOf(Slider slider) {
        Transform row = slider.transform;
        while (row.parent != null && row.parent.GetComponentsInChildren<Slider>(includeInactive: true).Length == 1) {
            row = row.parent;
        }
        if (row == slider.transform || row.GetComponentInChildren<TMP_Text>(includeInactive: true) == null) {
            return null;
        }
        return row;
    }

    void Wire(WeatherSettings weatherSettings) {
        settings = weatherSettings;
        Slider slider = GetComponentInChildren<Slider>(includeInactive: true);
        slider.onValueChanged = new Slider.SliderEvent();
        // The Warning slider it copies runs past 1, but a sound cannot play louder than its full volume, so this one
        // runs from silent to full.
        slider.minValue = 0;
        slider.maxValue = 1;
        slider.SetValueWithoutNotify(settings.Volume);
        slider.onValueChanged.AddListener(OnValueChanged);

        foreach (TMP_Text text in GetComponentsInChildren<TMP_Text>(includeInactive: true)) {
            // Text inside the slider is its own readout (a percentage), which follows the copied slider by itself.
            if (text.transform.IsChildOf(slider.transform)) {
                continue;
            }
            foreach (MonoBehaviour behaviour in text.GetComponents<MonoBehaviour>()) {
                if (behaviour.GetType().Name.IndexOf("Locali", StringComparison.Ordinal) >= 0) {
                    Destroy(behaviour);
                }
            }
            text.text = Label;
        }
    }

    void OnValueChanged(float value) {
        settings.Volume = value;
        hasChanged = true;
    }

    /// <summary>The options screen closing is when the player is done with it, so the value is kept then.</summary>
    void OnDisable() {
        if (hasChanged) {
            settings.Save();
            hasChanged = false;
        }
    }

    static string PathOf(Transform transform) {
        string path = transform.name;
        for (Transform parent = transform.parent; parent != null; parent = parent.parent) {
            path = $"{parent.name}/{path}";
        }
        return path;
    }
}
