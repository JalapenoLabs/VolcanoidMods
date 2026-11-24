// Copyright © 2026 Jalapeno Labs

using System;
using JalapenoLabs.Weather.Forecast;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace JalapenoLabs.Weather.Dev;

/// <summary>
/// Developer hotkeys (-weather-keys): call up weather on the spot instead of waiting up to an hour for it.
///
///   Numpad 0   a thunderstorm, a few minutes into its rain (puddles filling, lightning under way)
///   Numpad 1   light rain, likewise
///   Numpad 2   heavy rain, likewise
///   Numpad 3   a thunderstorm from the very start: watch the clouds roll in, the rain arrive and the lightning begin
///   Numpad 5   back to the shared clock, and whatever the sky is really doing
///
/// Each key moves the mod's clock to a moment of the next storm of that kind and runs on from there, so the storm then
/// plays out in real time. It is behind a launch option because it puts this player's weather out of step with every
/// other player of the mod; a stray keypress in a normal game must never do that.
/// </summary>
internal sealed class DevHotkeys : MonoBehaviour {
    /// <summary>A key, and the moment it jumps to, or null to go back to the shared clock.</summary>
    readonly struct Hotkey {
        public readonly Func<Keyboard, KeyControl> Key;
        public readonly string Name;
        public readonly DevOptions.Moment? Moment;

        public Hotkey(Func<Keyboard, KeyControl> key, string name, DevOptions.Moment? moment) {
            Key = key;
            Name = name;
            Moment = moment;
        }
    }

    /// <summary>A few minutes into the rain: full strength, puddles filling, lightning under way.</summary>
    const double RainingProgress = 0.35;

    /// <summary>Just before the clouds start to gather, so the whole roll-in plays out.</summary>
    const double BeforeCloudsProgress = -1.05;

    /// <summary>How long the on-screen note about a hotkey stays up.</summary>
    const float NoteSeconds = 4;

    static readonly Hotkey[] Hotkeys = {
        new Hotkey(
            keyboard => keyboard.numpad0Key,
            "Thunderstorm",
            new DevOptions.Moment(StormKind.Thunderstorm, RainingProgress)
        ),
        new Hotkey(
            keyboard => keyboard.numpad1Key,
            "Light rain",
            new DevOptions.Moment(StormKind.LightRain, RainingProgress)
        ),
        new Hotkey(
            keyboard => keyboard.numpad2Key,
            "Heavy rain",
            new DevOptions.Moment(StormKind.HeavyRain, RainingProgress)
        ),
        new Hotkey(
            keyboard => keyboard.numpad3Key,
            "Thunderstorm, from the start",
            new DevOptions.Moment(StormKind.Thunderstorm, BeforeCloudsProgress)
        ),
        new Hotkey(keyboard => keyboard.numpad5Key, "Back to the shared clock", null),
    };

    SyncedClock clock;
    string note;
    float noteUntil;
    GUIStyle noteStyle;

    public void Begin(SyncedClock syncedClock) {
        clock = syncedClock;
        Debug.Log($"{WeatherMod.LogPrefix}Weather hotkeys on: numpad 0 thunderstorm, 1 light rain, 2 heavy rain, "
            + "3 thunderstorm from the start, 5 back to the shared clock");
    }

    void Update() {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null) {
            return;
        }
        foreach (Hotkey hotkey in Hotkeys) {
            if (hotkey.Key(keyboard).wasPressedThisFrame) {
                Apply(hotkey);
            }
        }
    }

    void Apply(Hotkey hotkey) {
        if (hotkey.Moment.HasValue) {
            clock.JumpTo(SyncedClock.FindMoment(clock.Now, hotkey.Moment.Value));
        } else {
            clock.ReturnToShared();
        }
        note = $"Weather: {hotkey.Name}";
        noteUntil = Time.unscaledTime + NoteSeconds;
        Debug.Log($"{WeatherMod.LogPrefix}Hotkey: {hotkey.Name}");
    }

    /// <summary>A plain note in the corner, so the player knows the key took.</summary>
    void OnGUI() {
        if (note == null || Time.unscaledTime > noteUntil) {
            return;
        }
        noteStyle ??= new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(14, Screen.height / 50) };
        GUI.Label(new Rect(20, Screen.height * 0.3f, Screen.width * 0.5f, Screen.height * 0.1f), note, noteStyle);
    }
}
