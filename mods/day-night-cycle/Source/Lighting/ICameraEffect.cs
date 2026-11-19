// Copyright © 2026 Jalapeno Labs

using UnityEngine;

namespace JalapenoLabs.DayNightCycle.Lighting;

/// <summary>
/// One part of the night, applied to a single camera just before it renders and undone right after.
///
/// The game rewrites much of its lighting every frame (the eruption timeline sets the sun's brightness, the skybox
/// blend and the post-processing weights in LateUpdate), and other parts are shared assets that outlive the scene.
/// Changing values only inside a camera's render, and putting them back afterwards, means the mod never fights the
/// game's own updates, never leaks a dark sky into the main menu, and only touches the cameras it means to.
/// </summary>
internal interface ICameraEffect {
    /// <summary>Darkens what <paramref name="camera"/> is about to render.</summary>
    /// <param name="frame">This frame's sky, shared by every effect.</param>
    /// <param name="surface">
    /// 0 to 1: how much the camera is on the surface rather than underground, eased over a second so walking into a
    /// cave fades the night out instead of snapping it off.
    /// </param>
    void Apply(in NightFrame frame, Camera camera, float surface);

    /// <summary>Puts back everything <see cref="Apply"/> changed.</summary>
    void Restore();
}
