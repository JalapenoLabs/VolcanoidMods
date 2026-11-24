// Copyright © 2026 Jalapeno Labs

using System;
using JalapenoLabs.Weather.Forecast;
using Steamworks;
using UnityEngine;

namespace JalapenoLabs.Weather;

/// <summary>
/// The clock every copy of the mod agrees on: UTC wall time, in seconds since the Unix epoch.
///
/// Volcanoids has no shared, ever-increasing world clock (its only synced time is the island's place in the eruption
/// cycle, which starts over at every eruption). Wall time needs no networking, so two players with the mod share the
/// same storms even when the host doesn't have it. It is the same clock Day/Night Cycle reads; each mod carries its own
/// copy so neither depends on the other being installed.
///
/// A badly set system clock would put one player's weather out of step, so the clock is checked once against Steam's
/// server time. Steam reports whole seconds, so differences under <see cref="SteamCorrectionThresholdSeconds"/> are
/// treated as rounding and ignored, keeping the sub-second precision of the local clock.
/// </summary>
internal sealed class SyncedClock {
    enum Mode {
        /// <summary>Normal play: the shared wall clock.</summary>
        Live,
        /// <summary>Developer weather: runs on from a chosen moment, at real time or a multiple of it.</summary>
        Anchored,
        /// <summary>Developer hold: frozen at one moment.</summary>
        Held,
    }

    const double SteamCorrectionThresholdSeconds = 2;

    static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    readonly double steamCorrectionSeconds;
    readonly double speed;
    Mode mode;
    double anchorSeconds;
    double anchorRealtime;

    public SyncedClock(DevOptions devOptions) {
        steamCorrectionSeconds = MeasureSteamCorrection();
        speed = devOptions.Speed;
        anchorRealtime = Time.realtimeSinceStartupAsDouble;
        anchorSeconds = WallSeconds();
        mode = speed == 1
            ? Mode.Live
            : Mode.Anchored;

        if (devOptions.Start.HasValue) {
            anchorSeconds = FindMoment(anchorSeconds, devOptions.Start.Value);
            mode = devOptions.IsFrozen
                ? Mode.Held
                : Mode.Anchored;
        }
    }

    /// <summary>The synced time, in seconds since the Unix epoch.</summary>
    public double Now {
        get {
            switch (mode) {
                case Mode.Held:
                    return anchorSeconds;
                case Mode.Anchored:
                    return anchorSeconds + (Time.realtimeSinceStartupAsDouble - anchorRealtime) * speed;
                default:
                    return WallSeconds();
            }
        }
    }

    /// <summary>Freezes the clock at one moment. Developer captures use this to photograph chosen weather.</summary>
    public void Hold(double syncedSeconds) {
        anchorSeconds = syncedSeconds;
        mode = Mode.Held;
    }

    /// <summary>The synced time of a moment in the next storm of its kind.</summary>
    public static double FindMoment(double fromSeconds, DevOptions.Moment moment) {
        Storm storm = StormSchedule.NextStormOfKind(fromSeconds, moment.Kind);
        return StormSchedule.MomentIn(storm, moment.Progress);
    }

    double WallSeconds() {
        return (DateTime.UtcNow - UnixEpoch).TotalSeconds + steamCorrectionSeconds;
    }

    static double MeasureSteamCorrection() {
        if (!Steam.IsInitialized) {
            Debug.Log($"{WeatherMod.LogPrefix}Steam is not running; trusting the system clock");
            return 0;
        }
        try {
            double steamSeconds = SteamUtils.GetServerRealTime();
            double localSeconds = (DateTime.UtcNow - UnixEpoch).TotalSeconds;
            double difference = steamSeconds - localSeconds;
            if (Math.Abs(difference) < SteamCorrectionThresholdSeconds) {
                Debug.Log($"{WeatherMod.LogPrefix}System clock agrees with Steam ({difference:+0.0;-0.0}s)");
                return 0;
            }
            Debug.LogWarning(
                $"{WeatherMod.LogPrefix}System clock is {difference:+0.0;-0.0}s off Steam's; using Steam's time"
            );
            return difference;
        } catch (Exception exception) {
            Debug.LogWarning(
                $"{WeatherMod.LogPrefix}Could not read Steam's clock, trusting the system clock: {exception}"
            );
            return 0;
        }
    }
}
