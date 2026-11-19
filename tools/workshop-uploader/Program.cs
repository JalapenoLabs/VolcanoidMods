// Copyright © 2026 Jalapeno Labs
//
// Volcanoids Workshop uploader. scripts/workshop.py builds and runs it; it is not meant to be run by hand.
//
//   WorkshopUploader info      --steam-api <lib> --item <id>
//   WorkshopUploader subscribe --steam-api <lib> --item <id>
//   WorkshopUploader publish   --steam-api <lib> [--item <id>] --content <dir> --preview <file> --title <text>
//                              --description-file <file> --change-note-file <file> --tags <a,b,c>
//                              [--visibility <public|friends|private|unlisted>] [--screenshot <file>]...
//                              [--force-screenshots true]
//
// The Steam client must be running, and the process needs SteamAppId=951440 in its environment.
//
// Every command prints one JSON object on stdout; progress and errors go to stderr. Exit code 1 on failure.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using JalapenoLabs.WorkshopUploader;
using Steamworks;

var visibilityByName = new Dictionary<string, ERemoteStoragePublishedFileVisibility> {
    ["public"] = ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPublic,
    ["friends"] = ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityFriendsOnly,
    ["private"] = ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPrivate,
    ["unlisted"] = ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityUnlisted,
};

try {
    if (args.Length == 0) {
        throw new ArgumentException("Usage: WorkshopUploader <info|subscribe|publish> --steam-api <lib> ...");
    }
    string command = args[0];
    Arguments options = Arguments.Parse(args[1..]);

    using SteamSession session = SteamSession.Start(options.Single("--steam-api"));
    var items = new WorkshopItems(session);
    Dictionary<string, object> result = command switch {
        "info" => items.Describe(ulong.Parse(options.Single("--item"))),
        "subscribe" => items.Subscribe(ulong.Parse(options.Single("--item"))),
        "publish" => items.Publish(new PublishRequest {
            ItemId = options.Optional("--item") is string itemId ? ulong.Parse(itemId) : null,
            ContentDirectory = options.Single("--content"),
            PreviewFile = options.Single("--preview"),
            Title = options.Single("--title"),
            Description = File.ReadAllText(options.Single("--description-file")),
            ChangeNote = File.ReadAllText(options.Single("--change-note-file")),
            Tags = options.Single("--tags").Split(',', StringSplitOptions.RemoveEmptyEntries),
            Visibility = options.Optional("--visibility") is not string visibilityName
                ? null
                : visibilityByName.TryGetValue(visibilityName, out var visibility)
                    ? visibility
                    : throw new ArgumentException(
                        $"--visibility must be one of: {string.Join(", ", visibilityByName.Keys)}"
                    ),
            Screenshots = options.All("--screenshot"),
            ForceScreenshots = options.Optional("--force-screenshots") == "true",
        }),
        _ => throw new ArgumentException($"Unknown command '{command}'"),
    };
    Console.WriteLine(JsonSerializer.Serialize(result));
    return 0;
} catch (Exception exception) {
    Console.Error.WriteLine($"Error: {exception.Message}");
    return 1;
}

/// <summary>"--name value" pairs; a name may repeat (screenshots).</summary>
internal sealed class Arguments {
    readonly Dictionary<string, List<string>> values = new Dictionary<string, List<string>>();

    public static Arguments Parse(string[] arguments) {
        var parsed = new Arguments();
        for (int index = 0; index < arguments.Length; index += 2) {
            string name = arguments[index];
            if (!name.StartsWith("--") || index + 1 >= arguments.Length) {
                throw new ArgumentException($"Expected '--name value' pairs, got '{name}'");
            }
            if (!parsed.values.TryGetValue(name, out List<string> list)) {
                list = new List<string>();
                parsed.values[name] = list;
            }
            list.Add(arguments[index + 1]);
        }
        return parsed;
    }

    public string Single(string name) {
        return Optional(name) ?? throw new ArgumentException($"{name} is required");
    }

    public string Optional(string name) {
        if (!values.TryGetValue(name, out List<string> list)) {
            return null;
        }
        if (list.Count > 1) {
            throw new ArgumentException($"{name} was given {list.Count} times");
        }
        return list[0];
    }

    public IReadOnlyList<string> All(string name) {
        return values.TryGetValue(name, out List<string> list)
            ? list
            : Array.Empty<string>();
    }
}
