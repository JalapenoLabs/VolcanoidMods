// Copyright © 2026 Jalapeno Labs

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Steamworks;

namespace JalapenoLabs.WorkshopUploader;

/// <summary>
/// A Steamworks session for Volcanoids, run through the Steam client already signed in on this machine.
///
/// Steam treats this process as the game while it runs (it shows the account as playing Volcanoids), which is what
/// lets it create and update the game's Workshop items as that account.
/// </summary>
internal sealed class SteamSession : IDisposable {
    public static readonly AppId_t VolcanoidsAppId = new AppId_t(951440);

    static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);
    static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);

    SteamSession() {
    }

    /// <param name="steamApiLibrary">The game's own libsteam_api.so, matching the Steamworks.NET version.</param>
    public static SteamSession Start(string steamApiLibrary) {
        if (!File.Exists(steamApiLibrary)) {
            throw new FileNotFoundException($"Steam API library not found at {steamApiLibrary}");
        }
        NativeLibrary.SetDllImportResolver(typeof(SteamAPI).Assembly, (name, assembly, searchPath) => {
            return name.Contains("steam_api", StringComparison.OrdinalIgnoreCase)
                ? NativeLibrary.Load(steamApiLibrary)
                : IntPtr.Zero;
        });

        // The native Steam API reads the app ID from the process environment. It has to come from the launcher: on
        // Linux, .NET's Environment.SetEnvironmentVariable changes only a managed copy that native code never sees.
        if (Environment.GetEnvironmentVariable("SteamAppId") != VolcanoidsAppId.m_AppId.ToString()) {
            throw new InvalidOperationException($"Run with SteamAppId={VolcanoidsAppId.m_AppId} in the environment");
        }
        if (!SteamAPI.Init()) {
            throw new InvalidOperationException(
                "SteamAPI.Init failed. Is the Steam client running and signed in to an account that owns Volcanoids?"
            );
        }
        Console.Error.WriteLine($"Steam session started as {SteamFriends.GetPersonaName()}");
        return new SteamSession();
    }

    public CSteamID AccountId => SteamUser.GetSteamID();

    /// <summary>Waits for an asynchronous Steam call, pumping Steam's callbacks meanwhile.</summary>
    /// <param name="onTick">Called every couple of seconds while waiting, to report progress.</param>
    public CallResultType Await<CallResultType>(
        SteamAPICall_t call,
        string description,
        TimeSpan? timeout = null,
        Action onTick = null
    ) where CallResultType : struct {
        if (call == SteamAPICall_t.Invalid) {
            throw new InvalidOperationException($"{description}: Steam refused the call");
        }
        CallResultType result = default;
        bool isDone = false;
        bool isIoFailure = false;
        using CallResult<CallResultType> callResult = CallResult<CallResultType>.Create((value, ioFailure) => {
            result = value;
            isIoFailure = ioFailure;
            isDone = true;
        });
        callResult.Set(call);

        TimeSpan limit = timeout ?? DefaultTimeout;
        DateTime deadline = DateTime.UtcNow + limit;
        DateTime nextTick = DateTime.UtcNow + TickInterval;
        while (!isDone) {
            if (DateTime.UtcNow > deadline) {
                throw new TimeoutException($"{description}: no answer from Steam within {limit}");
            }
            if (onTick != null && DateTime.UtcNow >= nextTick) {
                onTick();
                nextTick = DateTime.UtcNow + TickInterval;
            }
            SteamAPI.RunCallbacks();
            Thread.Sleep(50);
        }
        if (isIoFailure) {
            throw new IOException($"{description}: Steam reported an I/O failure");
        }
        return result;
    }

    /// <summary>Pumps Steam's callbacks for a while, for polling loops (upload and download progress).</summary>
    public void Pump(TimeSpan duration) {
        DateTime until = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < until) {
            SteamAPI.RunCallbacks();
            Thread.Sleep(50);
        }
    }

    public void Dispose() {
        SteamAPI.Shutdown();
    }
}
