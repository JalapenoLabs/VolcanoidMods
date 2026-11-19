// Copyright © 2026 Jalapeno Labs

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Steamworks;

namespace JalapenoLabs.WorkshopUploader;

/// <summary>What to upload: the mod's files and its Workshop page.</summary>
internal sealed class PublishRequest {
    /// <summary>The existing item to update, or null to create a new one.</summary>
    public ulong? ItemId;
    public string ContentDirectory;
    public string PreviewFile;
    public string Title;
    public string Description;
    public string ChangeNote;
    public IReadOnlyList<string> Tags;
    /// <summary>Null keeps an existing item's visibility, so a routine update never unhides or hides it.</summary>
    public ERemoteStoragePublishedFileVisibility? Visibility;
    public IReadOnlyList<string> Screenshots;
    /// <summary>Re-upload the screenshots even when the gallery already holds the same images.</summary>
    public bool ForceScreenshots;
}

/// <summary>
/// Creates, updates, inspects and subscribes to Volcanoids Workshop items, making the same Steam calls as the game's
/// own uploader (WorkshopUpload in Volcanoids.dll), plus the ones it leaves out: the description, real change notes
/// and screenshots.
/// </summary>
internal sealed class WorkshopItems {
    readonly SteamSession session;

    public WorkshopItems(SteamSession session) {
        this.session = session;
    }

    public static string PageUrl(ulong itemId) {
        return $"https://steamcommunity.com/sharedfiles/filedetails/?id={itemId}";
    }

    public Dictionary<string, object> Publish(PublishRequest request) {
        bool isNew = !request.ItemId.HasValue;
        PublishedFileId_t itemId;
        IReadOnlyList<string> galleryHashes = Array.Empty<string>();
        if (isNew) {
            itemId = Create();
        } else {
            itemId = new PublishedFileId_t(request.ItemId.Value);
            ItemDetails details = Query(itemId);
            // The game's uploader refuses to update an item another account owns; so does this.
            if (details.Owner != session.AccountId) {
                throw new InvalidOperationException(
                    $"Workshop item {itemId} belongs to another Steam account; sign in to the account that made it"
                );
            }
            galleryHashes = details.GalleryImages.Select(image => image.ContentHash).ToList();
        }

        List<string> screenshotHashes = request.Screenshots.Select(ContentHash).ToList();
        bool isGalleryChanged = request.ForceScreenshots || !galleryHashes.SequenceEqual(screenshotHashes);
        if (isGalleryChanged && galleryHashes.Count > 0) {
            // Steam stores images by content hash. Removing an image and adding identical bytes in the same update
            // leaves the gallery pointing at the deleted file, so the old images go in an update of their own.
            ClearGallery(itemId, galleryHashes.Count);
        }

        UGCUpdateHandle_t update = SteamUGC.StartItemUpdate(SteamSession.VolcanoidsAppId, itemId);
        Require(SteamUGC.SetItemTitle(update, request.Title), "set the title");
        Require(SteamUGC.SetItemDescription(update, request.Description), "set the description");
        Require(SteamUGC.SetItemTags(update, new List<string>(request.Tags)), "set the tags");
        Require(SteamUGC.SetItemContent(update, Path.GetFullPath(request.ContentDirectory)), "set the content");
        Require(SteamUGC.SetItemPreview(update, Path.GetFullPath(request.PreviewFile)), "set the preview image");
        if (request.Visibility.HasValue) {
            Require(SteamUGC.SetItemVisibility(update, request.Visibility.Value), "set the visibility");
        }
        if (isGalleryChanged) {
            foreach (string screenshot in request.Screenshots) {
                Require(
                    SteamUGC.AddItemPreviewFile(
                        update,
                        Path.GetFullPath(screenshot),
                        EItemPreviewType.k_EItemPreviewType_Image
                    ),
                    $"add screenshot {Path.GetFileName(screenshot)}"
                );
            }
        } else {
            Console.Error.WriteLine("The gallery already holds these screenshots; leaving it as it is");
        }

        SubmitItemUpdateResult_t result = Submit(update, itemId, request.ChangeNote);
        return new Dictionary<string, object> {
            ["itemId"] = itemId.m_PublishedFileId.ToString(),
            ["created"] = isNew,
            ["galleryReplaced"] = isGalleryChanged,
            ["needsLegalAgreement"] = result.m_bUserNeedsToAcceptWorkshopLegalAgreement,
            ["url"] = PageUrl(itemId.m_PublishedFileId),
        };
    }

    /// <summary>Removes every gallery image in its own update; indices shift as images go, so last first.</summary>
    void ClearGallery(PublishedFileId_t itemId, int imageCount) {
        Console.Error.WriteLine($"Clearing the {imageCount} old gallery image(s) first...");
        UGCUpdateHandle_t update = SteamUGC.StartItemUpdate(SteamSession.VolcanoidsAppId, itemId);
        for (int index = imageCount - 1; index >= 0; index--) {
            Require(SteamUGC.RemoveItemPreview(update, (uint)index), $"remove old screenshot {index}");
        }
        Submit(update, itemId, "");
    }

    SubmitItemUpdateResult_t Submit(UGCUpdateHandle_t update, PublishedFileId_t itemId, string changeNote) {
        Console.Error.WriteLine($"Uploading Workshop item {itemId}...");
        SubmitItemUpdateResult_t result = session.Await<SubmitItemUpdateResult_t>(
            SteamUGC.SubmitItemUpdate(update, changeNote),
            "Submit the update",
            TimeSpan.FromMinutes(10),
            () => ReportProgress(update)
        );
        if (result.m_eResult != EResult.k_EResultOK) {
            throw new InvalidOperationException($"Steam rejected the update of item {itemId}: {result.m_eResult}");
        }
        if (result.m_bUserNeedsToAcceptWorkshopLegalAgreement) {
            Console.Error.WriteLine(
                "The account must accept the Steam Workshop legal agreement before the item can be seen: "
                + "https://steamcommunity.com/sharedfiles/workshoplegalagreement"
            );
        }
        return result;
    }

    /// <summary>The SHA-1 Steam files an image under: the second-to-last segment of its image URL.</summary>
    static string ContentHash(string path) {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(stream));
    }

    public Dictionary<string, object> Describe(ulong itemId) {
        ItemDetails details = Query(new PublishedFileId_t(itemId));
        return new Dictionary<string, object> {
            ["itemId"] = itemId.ToString(),
            ["title"] = details.Title,
            ["visibility"] = details.Visibility.ToString(),
            ["tags"] = details.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries),
            ["fileSize"] = details.FileSize,
            ["screenshots"] = details.GalleryImages.Count,
            ["brokenImages"] = BrokenImages(details),
            ["updated"] = DateTimeOffset.FromUnixTimeSeconds(details.UpdatedUnixSeconds).ToString("u"),
            ["ownedByThisAccount"] = details.Owner == session.AccountId,
            ["descriptionLength"] = details.Description.Length,
            ["url"] = PageUrl(itemId),
        };
    }

    /// <summary>Subscribes the signed-in account and waits until Steam has downloaded the item.</summary>
    public Dictionary<string, object> Subscribe(ulong itemId) {
        var id = new PublishedFileId_t(itemId);
        var subscribed = session.Await<RemoteStorageSubscribePublishedFileResult_t>(
            SteamUGC.SubscribeItem(id),
            "Subscribe"
        );
        if (subscribed.m_eResult != EResult.k_EResultOK) {
            throw new InvalidOperationException(
                $"Steam refused the subscription to item {itemId}: {subscribed.m_eResult}"
            );
        }
        SteamUGC.DownloadItem(id, true);

        DateTime deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        const EItemState busy = EItemState.k_EItemStateNeedsUpdate
            | EItemState.k_EItemStateDownloading
            | EItemState.k_EItemStateDownloadPending;
        while (true) {
            var state = (EItemState)SteamUGC.GetItemState(id);
            if ((state & EItemState.k_EItemStateInstalled) != 0 && (state & busy) == 0) {
                break;
            }
            if (DateTime.UtcNow > deadline) {
                throw new TimeoutException($"Item {itemId} was still downloading after 5 minutes (state {state})");
            }
            session.Pump(TimeSpan.FromSeconds(1));
        }

        SteamUGC.GetItemInstallInfo(id, out ulong sizeOnDisk, out string folder, 1024, out uint _);
        return new Dictionary<string, object> {
            ["itemId"] = itemId.ToString(),
            ["installFolder"] = folder,
            ["sizeOnDisk"] = sizeOnDisk,
        };
    }

    PublishedFileId_t Create() {
        CreateItemResult_t created = session.Await<CreateItemResult_t>(
            SteamUGC.CreateItem(SteamSession.VolcanoidsAppId, EWorkshopFileType.k_EWorkshopFileTypeCommunity),
            "Create the Workshop item"
        );
        if (created.m_eResult != EResult.k_EResultOK || created.m_nPublishedFileId.m_PublishedFileId == 0) {
            throw new InvalidOperationException($"Steam could not create a Workshop item: {created.m_eResult}");
        }
        Console.Error.WriteLine($"Created Workshop item {created.m_nPublishedFileId}");
        return created.m_nPublishedFileId;
    }

    readonly struct ItemDetails {
        public readonly string Title;
        public readonly string Description;
        public readonly string Tags;
        public readonly ERemoteStoragePublishedFileVisibility Visibility;
        public readonly CSteamID Owner;
        public readonly int FileSize;
        public readonly uint UpdatedUnixSeconds;
        public readonly string PreviewUrl;
        public readonly IReadOnlyList<GalleryImage> GalleryImages;

        public ItemDetails(SteamUGCDetails_t details, string previewUrl, IReadOnlyList<GalleryImage> galleryImages) {
            Title = details.m_rgchTitle;
            Description = details.m_rgchDescription;
            Tags = details.m_rgchTags;
            Visibility = details.m_eVisibility;
            Owner = new CSteamID(details.m_ulSteamIDOwner);
            FileSize = details.m_nFileSize;
            UpdatedUnixSeconds = details.m_rtimeUpdated;
            PreviewUrl = previewUrl;
            GalleryImages = galleryImages;
        }
    }

    ItemDetails Query(PublishedFileId_t itemId) {
        UGCQueryHandle_t query = SteamUGC.CreateQueryUGCDetailsRequest(new[] { itemId }, 1);
        try {
            SteamUGC.SetReturnAdditionalPreviews(query, true);
            SteamUGC.SetReturnLongDescription(query, true);
            SteamUGCQueryCompleted_t completed = session.Await<SteamUGCQueryCompleted_t>(
                SteamUGC.SendQueryUGCRequest(query),
                $"Look up Workshop item {itemId}"
            );
            if (completed.m_eResult != EResult.k_EResultOK || completed.m_unNumResultsReturned == 0
                || !SteamUGC.GetQueryUGCResult(query, 0, out SteamUGCDetails_t details)) {
                throw new InvalidOperationException(
                    $"Workshop item {itemId} could not be found ({completed.m_eResult})"
                );
            }
            if (details.m_eResult != EResult.k_EResultOK) {
                throw new InvalidOperationException($"Workshop item {itemId} is unavailable: {details.m_eResult}");
            }
            SteamUGC.GetQueryUGCPreviewURL(query, 0, out string previewUrl, 1024);
            var galleryImages = new List<GalleryImage>();
            uint imageCount = SteamUGC.GetQueryUGCNumAdditionalPreviews(query, 0);
            for (uint index = 0; index < imageCount; index++) {
                SteamUGC.GetQueryUGCAdditionalPreview(
                    query, 0, index, out string url, 1024, out string _, 260, out EItemPreviewType _
                );
                galleryImages.Add(new GalleryImage(url));
            }
            return new ItemDetails(details, previewUrl, galleryImages);
        } finally {
            SteamUGC.ReleaseQueryUGCRequest(query);
        }
    }

    readonly struct GalleryImage {
        static readonly Regex HashInUrl = new Regex("/([0-9A-Fa-f]{40})/", RegexOptions.Compiled);

        public readonly string Url;
        /// <summary>The image's SHA-1 as Steam files it, upper-case; empty when the URL does not carry one.</summary>
        public readonly string ContentHash;

        public GalleryImage(string url) {
            Url = url;
            Match match = HashInUrl.Match(url ?? "");
            ContentHash = match.Success ? match.Groups[1].Value.ToUpperInvariant() : "";
        }
    }

    /// <summary>
    /// Fetches the cover and every gallery image from Steam's image server and lists the ones that fail. Steam reports
    /// an update as successful even when it leaves the page pointing at deleted images, so only fetching them proves
    /// the page works.
    /// </summary>
    static List<string> BrokenImages(ItemDetails details) {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var broken = new List<string>();
        IEnumerable<string> urls = details.GalleryImages.Select(image => image.Url).Prepend(details.PreviewUrl);
        foreach (string url in urls) {
            using HttpResponseMessage response = client.GetAsync(url).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode) {
                broken.Add($"{(int)response.StatusCode} {url}");
            }
        }
        return broken;
    }

    static void Require(bool succeeded, string action) {
        if (!succeeded) {
            throw new InvalidOperationException($"Steam refused to {action}");
        }
    }

    static void ReportProgress(UGCUpdateHandle_t update) {
        EItemUpdateStatus status = SteamUGC.GetItemUpdateProgress(update, out ulong processed, out ulong total);
        string bytes = total > 0
            ? $" {processed * 100 / total}% of {total / 1024} KB"
            : "";
        Console.Error.WriteLine($"  {status}{bytes}");
    }
}
