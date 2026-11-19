// Copyright © 2026 Jalapeno Labs

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace JalapenoLabs.DayNightCycle;

/// <summary>
/// Developer controls, read from the game's command line (Steam: Properties, Launch Options).
///
/// The day/night lengths are fixed and these change nothing for normal play. They exist because nobody can tune a
/// sunset by waiting fifty minutes for one. Times are minutes since sunrise: 15 is noon, 30 sunset, 40 midnight.
///
///   -daynight-at 40          hold the clock at minute 40 (midnight)
///   -daynight-moon 0.5       with -daynight-at, pick a night with this moon phase (0 new, 0.5 full)
///   -daynight-speed 30       run the clock 30 times faster (a whole cycle in 100 seconds)
///   -daynight-capture DIR    after loading, screenshot each moment in -daynight-shots into DIR, then quit
///   -daynight-shots LIST     comma-separated minutes, each optionally with a moon phase: "15,30,40@0.5,40@0"
///   -daynight-showcase FILE  with a capture, spawn this drillship design fully built and lit, and frame it instead
///   -daynight-showcase-site NAME   the surface landing site to spawn it at (default: the most open free one)
/// </summary>
internal sealed class DevOptions {
    /// <summary>One moment to photograph: minutes since sunrise, and optionally the moon phase to show.</summary>
    internal readonly struct Shot {
        public readonly double Minute;
        public readonly double? MoonPhase;

        public Shot(double minute, double? moonPhase) {
            Minute = minute;
            MoonPhase = moonPhase;
        }

        public string Label => MoonPhase.HasValue
            ? $"minute{Minute:00.0}-moon{MoonPhase.Value:0.00}"
            : $"minute{Minute:00.0}";
    }

    public readonly double? HoldMinute;
    public readonly double? HoldMoonPhase;
    public readonly double Speed;
    public readonly string CaptureDirectory;
    public readonly IReadOnlyList<Shot> Shots;
    /// <summary>A drillship design file to spawn and frame during a capture, or null.</summary>
    public readonly string ShowcaseDesign;
    /// <summary>The landing site to spawn the showcase drillship at, or null for the most open free one.</summary>
    public readonly string ShowcaseSite;

    DevOptions(
        double? holdMinute,
        double? holdMoonPhase,
        double speed,
        string captureDirectory,
        List<Shot> shots,
        string showcaseDesign,
        string showcaseSite
    ) {
        HoldMinute = holdMinute;
        HoldMoonPhase = holdMoonPhase;
        Speed = speed;
        CaptureDirectory = captureDirectory;
        Shots = shots;
        ShowcaseDesign = showcaseDesign;
        ShowcaseSite = showcaseSite;
    }

    public bool IsCapturing => !string.IsNullOrEmpty(CaptureDirectory) && Shots.Count > 0;

    public bool IsDefault => !HoldMinute.HasValue && Speed == 1 && !IsCapturing;

    public static DevOptions FromCommandLine() {
        string[] arguments = Environment.GetCommandLineArgs();
        string captureDirectory = ReadValue(arguments, "-daynight-capture");
        string shotList = ReadValue(arguments, "-daynight-shots");

        var shots = new List<Shot>();
        if (!string.IsNullOrEmpty(shotList)) {
            foreach (string entry in shotList.Split(',')) {
                string[] parts = entry.Trim().Split('@');
                double? minute = ParseNumber(parts[0], "-daynight-shots minute");
                if (!minute.HasValue) {
                    continue;
                }
                double? moonPhase = parts.Length > 1
                    ? ParseNumber(parts[1], "-daynight-shots moon phase")
                    : null;
                shots.Add(new Shot(minute.Value, moonPhase));
            }
        }
        if (!string.IsNullOrEmpty(captureDirectory) && shots.Count == 0) {
            Debug.LogWarning(
                $"{DayNightCycleMod.LogPrefix}-daynight-capture given without -daynight-shots; ignoring it"
            );
        }

        return new DevOptions(
            holdMinute: ParseNumber(ReadValue(arguments, "-daynight-at"), "-daynight-at"),
            holdMoonPhase: ParseNumber(ReadValue(arguments, "-daynight-moon"), "-daynight-moon"),
            speed: ParseNumber(ReadValue(arguments, "-daynight-speed"), "-daynight-speed") ?? 1,
            captureDirectory: captureDirectory,
            shots: shots,
            showcaseDesign: ReadValue(arguments, "-daynight-showcase"),
            showcaseSite: ReadValue(arguments, "-daynight-showcase-site")
        );
    }

    public string Describe() {
        if (IsDefault) {
            return "Running on the shared clock.";
        }
        string hold = HoldMinute.HasValue
            ? $"held at minute {HoldMinute.Value}" + (HoldMoonPhase.HasValue ? $", moon {HoldMoonPhase.Value}" : "")
            : "not held";
        string capture = IsCapturing
            ? $", capturing {Shots.Count} shots into {CaptureDirectory}"
                + (ShowcaseDesign != null ? $" around a showcase drillship from {ShowcaseDesign}" : "")
            : "";
        return $"Developer clock: {hold}, speed x{Speed}{capture}.";
    }

    static string ReadValue(string[] arguments, string name) {
        int index = Array.IndexOf(arguments, name);
        if (index < 0) {
            return null;
        }
        if (index + 1 >= arguments.Length) {
            Debug.LogWarning($"{DayNightCycleMod.LogPrefix}{name} needs a value; ignoring it");
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
        Debug.LogWarning($"{DayNightCycleMod.LogPrefix}{name} value '{text}' is not a number; ignoring it");
        return null;
    }
}
