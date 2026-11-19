// Copyright © 2026 Jalapeno Labs

namespace JalapenoLabs.Shared;

/// <summary>
/// Who the shared code in /shared is compiled into. Every mod compiles its own copy of that code into its own
/// assembly (so no mod depends on another being installed), and this tells that copy whose log prefix to use.
/// </summary>
internal static class ModIdentity {
    public const string LogPrefix = DayNightCycle.DayNightCycleMod.LogPrefix;
}
