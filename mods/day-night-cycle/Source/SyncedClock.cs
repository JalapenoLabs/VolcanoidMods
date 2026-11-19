// Copyright © 2026 Jalapeno Labs

using System;
using JalapenoLabs.DayNightCycle.Cycle;
using Steamworks;
using UnityEngine;

namespace JalapenoLabs.DayNightCycle;

/// <summary>
/// The clock every copy of the mod agrees on: UTC wall time, in seconds since the Unix epoch.
///
/// Volcanoids has no shared, ever-increasing world clock. The only time it syncs is the island's place in the eruption
/// cycle, which starts over at every eruption and whose length differs per save. Wall time needs no networking, so two
/// players with the mod share a sky even when the host doesn't have it.
///
/// A badly set system clock would put one player's sky out of step, so the clock is checked once against Steam's
/// server time. Steam reports whole seconds, so differences under <see cref="SteamCorrectionThresholdSeconds"/> are
/// treated as rounding and ignored, keeping the sub-second precision of the local clock.
///
/// The cost of wall time: the sky keeps turning while the game is paused, and a save does not remember its time of day.
/// </summary>
internal sealed class SyncedClock {
    enum Mode {
        /// <summary>Normal play: the shared wall clock.</summary>
        Live,
        /// <summary>Developer speed-up: runs from an anchor moment at a multiple of real time.</summary>
        Accelerated,
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
            : Mode.Accelerated;

        if (devOptions.HoldMinute.HasValue) {
            anchorSeconds = CycleSchedule.FindMoment(
                anchorSeconds,
                devOptions.HoldMinute.Value,
                devOptions.HoldMoonPhase
            );
            // On its own, -daynight-at freezes the sky. With -daynight-speed it is the starting point instead.
            mode = speed == 1
                ? Mode.Held
                : Mode.Accelerated;
        }
    }

    /// <summary>The synced time, in seconds since the Unix epoch.</summary>
    public double Now {
        get {
            switch (mode) {
                case Mode.Held:
                    return anchorSeconds;
                case Mode.Accelerated:
                    return anchorSeconds + (Time.realtimeSinceStartupAsDouble - anchorRealtime) * speed;
                default:
                    return WallSeconds();
            }
        }
    }

    /// <summary>Freezes the clock at one moment. Developer captures use this to photograph chosen times.</summary>
    public void Hold(double syncedSeconds) {
        anchorSeconds = syncedSeconds;
        mode = Mode.Held;
    }

    double WallSeconds() {
        return (DateTime.UtcNow - UnixEpoch).TotalSeconds + steamCorrectionSeconds;
    }

    static double MeasureSteamCorrection() {
        if (!Steam.IsInitialized) {
            Debug.Log($"{DayNightCycleMod.LogPrefix}Steam is not running; trusting the system clock");
            return 0;
        }
        try {
            double steamSeconds = SteamUtils.GetServerRealTime();
            double localSeconds = (DateTime.UtcNow - UnixEpoch).TotalSeconds;
            double difference = steamSeconds - localSeconds;
            if (Math.Abs(difference) < SteamCorrectionThresholdSeconds) {
                Debug.Log($"{DayNightCycleMod.LogPrefix}System clock agrees with Steam ({difference:+0.0;-0.0}s)");
                return 0;
            }
            Debug.LogWarning(
                $"{DayNightCycleMod.LogPrefix}System clock is {difference:+0.0;-0.0}s off Steam's; using Steam's time"
            );
            return difference;
        } catch (Exception exception) {
            Debug.LogWarning(
                $"{DayNightCycleMod.LogPrefix}Could not read Steam's clock, trusting the system clock: {exception}"
            );
            return 0;
        }
    }
}
