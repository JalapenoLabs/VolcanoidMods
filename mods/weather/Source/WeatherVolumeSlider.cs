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
/// </summary>
internal sealed class WeatherVolumeSlider : MonoBehaviour {
    const string Label = "Weather";

    static readonly FieldInfo WarningVolumeField = typeof(OptionsUi).GetField(
        "m_warningVolume",
        BindingFlags.Instance | BindingFlags.NonPublic
    );

    WeatherSettings settings;
    bool hasChanged;

    /// <summary>Adds the slider to every options screen in the game that does not have one yet.</summary>
    public static void AddToOptionsScreens(WeatherSettings settings) {
        if (WarningVolumeField == null) {
            Debug.LogWarning($"{WeatherMod.LogPrefix}The game's options screen has no Warning volume slider any more; "
                + "the Weather volume slider is left out");
            return;
        }
        OptionsUi[] screens = Object.FindObjectsByType<OptionsUi>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None
        );
        if (screens.Length == 0) {
            Debug.Log($"{WeatherMod.LogPrefix}No options screen in this scene to add the Weather volume slider to");
            return;
        }
        foreach (OptionsUi screen in screens) {
            if (screen.GetComponentInChildren<WeatherVolumeSlider>(includeInactive: true) == null) {
                AddTo(screen, settings);
            }
        }
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
