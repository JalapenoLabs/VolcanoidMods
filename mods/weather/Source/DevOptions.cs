// Copyright © 2026 Jalapeno Labs

using System;
using System.Collections.Generic;
using System.Globalization;
using JalapenoLabs.Weather.Forecast;
using UnityEngine;

namespace JalapenoLabs.Weather;

/// <summary>
/// Developer controls, read from the game's command line (Steam: Properties, Launch Options).
///
/// The weather is fixed and these change nothing for normal play. They exist because nobody can tune a thunderstorm by
/// waiting up to an hour for one. A moment is a storm kind (light, heavy or thunder) and a progress through it: -1 as
/// its clouds start rolling in, 0 as the rain starts, 1 as it stops, 2 once the sky is clear.
///
///   -weather-at thunder@-1      start the weather at a moment of the next storm of that kind, and run on from there
///   -weather-hold               with -weather-at, freeze the weather at that moment instead
///   -weather-speed 10           run the weather 10 times faster (from the -weather-at moment, if given)
///   -weather-capture DIR        after loading, screenshot each moment in -weather-shots into DIR, then quit
///   -weather-shots LIST         comma-separated moments: "light@0.5,heavy@0.5,thunder@-0.3,thunder@0.6"
///   -weather-views LIST         views to capture (island, sky, drillship, deck, hull, lens, player); default all
///                               (lens: the island view with the raindrops on the lens shown, though the HUD is off)
///   -weather-dump-sounds DIR    write every synthesised sound as a WAV file into DIR
///   -weather-survey             log the game's particle materials, water and lava, for building the storm's effects
///   -weather-showcase FILE      with a capture, spawn this drillship design fully built and lit, and centre the views
///                               on it (the drillship view frames it with clear sightlines)
///   -weather-showcase-site NAME the surface landing site to spawn it at (default: the most open free one)
///   -weather-test-damage        in the first thunderstorm moment, strike the player (out in the open) and the nearest
///                               cog, and log their health before and after
/// </summary>
internal sealed class DevOptions {
    /// <summary>A moment within a storm of one kind.</summary>
    internal readonly struct Moment {
        public readonly StormKind Kind;
        public readonly double Progress;

        public Moment(StormKind kind, double progress) {
            Kind = kind;
            Progress = progress;
        }

        public string Label => $"{NamesByKind[Kind]}{Progress.ToString("+0.00;-0.00", CultureInfo.InvariantCulture)}";
    }

    static readonly Dictionary<string, StormKind> KindsByName = new Dictionary<string, StormKind> {
        ["light"] = StormKind.LightRain,
        ["heavy"] = StormKind.HeavyRain,
        ["thunder"] = StormKind.Thunderstorm,
    };

    static readonly Dictionary<StormKind, string> NamesByKind = new Dictionary<StormKind, string> {
        [StormKind.LightRain] = "light",
        [StormKind.HeavyRain] = "heavy",
        [StormKind.Thunderstorm] = "thunder",
    };

    static readonly string[] AllViews = { "island", "sky", "drillship", "deck", "hull", "lens", "player" };

    /// <summary>The moment the weather starts from instead of the shared clock, or null.</summary>
    public readonly Moment? Start;
    /// <summary>Whether the weather stays frozen at <see cref="Start"/>.</summary>
    public readonly bool IsFrozen;
    public readonly double Speed;
    public readonly string CaptureDirectory;
    public readonly IReadOnlyList<Moment> Shots;
    public readonly IReadOnlyList<string> Views;
    public readonly string SoundDumpDirectory;
    public readonly bool IsSurveying;
    /// <summary>A drillship design file to spawn and photograph during a capture, or null.</summary>
    public readonly string ShowcaseDesign;
    /// <summary>The landing site to spawn the showcase drillship at, or null for the most open free one.</summary>
    public readonly string ShowcaseSite;
    public readonly bool IsTestingDamage;

    DevOptions(
        Moment? start,
        bool isFrozen,
        double speed,
        string captureDirectory,
        List<Moment> shots,
        List<string> views,
        string soundDumpDirectory,
        bool isSurveying,
        string showcaseDesign,
        string showcaseSite,
        bool isTestingDamage
    ) {
        Start = start;
        IsFrozen = isFrozen;
        Speed = speed;
        CaptureDirectory = captureDirectory;
        Shots = shots;
        Views = views;
        SoundDumpDirectory = soundDumpDirectory;
        IsSurveying = isSurveying;
        ShowcaseDesign = showcaseDesign;
        ShowcaseSite = showcaseSite;
        IsTestingDamage = isTestingDamage;
    }

    public bool IsCapturing => !string.IsNullOrEmpty(CaptureDirectory) && Shots.Count > 0;

    public bool IsDefault => !Start.HasValue && Speed == 1 && !IsCapturing && SoundDumpDirectory == null
        && !IsSurveying;

    public static DevOptions FromCommandLine() {
        string[] arguments = Environment.GetCommandLineArgs();
        string captureDirectory = ReadValue(arguments, "-weather-capture");

        var shots = new List<Moment>();
        string shotList = ReadValue(arguments, "-weather-shots");
        if (!string.IsNullOrEmpty(shotList)) {
            foreach (string entry in shotList.Split(',')) {
                Moment? moment = ParseMoment(entry, "-weather-shots");
                if (moment.HasValue) {
                    shots.Add(moment.Value);
                }
            }
        }
        if (!string.IsNullOrEmpty(captureDirectory) && shots.Count == 0) {
            Debug.LogWarning($"{WeatherMod.LogPrefix}-weather-capture given without -weather-shots; ignoring it");
        }

        var views = new List<string>();
        string viewList = ReadValue(arguments, "-weather-views");
        foreach (string view in viewList?.Split(',') ?? AllViews) {
            string name = view.Trim().ToLowerInvariant();
            if (Array.IndexOf(AllViews, name) < 0) {
                Debug.LogWarning($"{WeatherMod.LogPrefix}Unknown capture view '{view}'; expected one of "
                    + string.Join(", ", AllViews));
                continue;
            }
            views.Add(name);
        }

        string startText = ReadValue(arguments, "-weather-at");
        Moment? start = startText != null ? ParseMoment(startText, "-weather-at") : null;
        bool isFrozen = Array.IndexOf(arguments, "-weather-hold") >= 0;
        if (isFrozen && !start.HasValue) {
            Debug.LogWarning($"{WeatherMod.LogPrefix}-weather-hold needs a moment from -weather-at; ignoring it");
            isFrozen = false;
        }
        return new DevOptions(
            start: start,
            isFrozen: isFrozen,
            speed: ParseNumber(ReadValue(arguments, "-weather-speed"), "-weather-speed") ?? 1,
            captureDirectory: captureDirectory,
            shots: shots,
            views: views,
            soundDumpDirectory: ReadValue(arguments, "-weather-dump-sounds"),
            isSurveying: Array.IndexOf(arguments, "-weather-survey") >= 0,
            showcaseDesign: ReadValue(arguments, "-weather-showcase"),
            showcaseSite: ReadValue(arguments, "-weather-showcase-site"),
            isTestingDamage: Array.IndexOf(arguments, "-weather-test-damage") >= 0
        );
    }

    public string Describe() {
        if (IsDefault) {
            return "Running on the shared clock.";
        }
        string start = "on the shared clock";
        if (Start.HasValue) {
            start = IsFrozen
                ? $"held at {Start.Value.Label}"
                : $"starting at {Start.Value.Label}";
        }
        string capture = IsCapturing
            ? $", capturing {Shots.Count} moments ({string.Join(", ", Views)}) into {CaptureDirectory}"
                + (ShowcaseDesign != null ? $" around a showcase drillship from {ShowcaseDesign}" : "")
            : "";
        string sounds = SoundDumpDirectory != null
            ? $", writing its sounds to {SoundDumpDirectory}"
            : "";
        string survey = IsSurveying
            ? ", surveying the scene"
            : "";
        return $"Developer weather: {start}, speed x{Speed}{capture}{sounds}{survey}.";
    }

    static Moment? ParseMoment(string text, string option) {
        string[] parts = text.Trim().Split('@');
        if (parts.Length != 2 || !KindsByName.TryGetValue(parts[0].Trim().ToLowerInvariant(), out StormKind kind)) {
            Debug.LogWarning(
                $"{WeatherMod.LogPrefix}{option}: '{text}' is not a moment like thunder@0.5 (light, heavy or thunder)"
            );
            return null;
        }
        double? progress = ParseNumber(parts[1], option);
        if (!progress.HasValue) {
            return null;
        }
        return new Moment(kind, progress.Value);
    }

    static string ReadValue(string[] arguments, string name) {
        int index = Array.IndexOf(arguments, name);
        if (index < 0) {
            return null;
        }
        if (index + 1 >= arguments.Length) {
            Debug.LogWarning($"{WeatherMod.LogPrefix}{name} needs a value; ignoring it");
            return null;
        }
        return arguments[index + 1];
    }

    static double? ParseNumber(string text, string name) {
        if (string.IsNullOrEmpty(text)) {
            return null;
        }
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) {
            return value;
        }
        Debug.LogWarning($"{WeatherMod.LogPrefix}{name}: '{text}' is not a number; ignoring it");
        return null;
    }
}
