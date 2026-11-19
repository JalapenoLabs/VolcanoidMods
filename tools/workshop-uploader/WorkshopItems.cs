// Copyright © 2026 Jalapeno Labs

using System;
using System.Collections.Generic;
using System.IO;
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
        int oldScreenshotCount = 0;
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
            oldScreenshotCount = details.AdditionalPreviewCount;
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

        // Replace the screenshots rather than piling new copies on top of the old ones. Indices shift as previews
        // are removed, so they go from the last down.
        for (int index = oldScreenshotCount - 1; index >= 0; index--) {
            Require(SteamUGC.RemoveItemPreview(update, (uint)index), $"remove old screenshot {index}");
        }
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

        Console.Error.WriteLine($"Uploading Workshop item {itemId}...");
        SubmitItemUpdateResult_t result = session.Await<SubmitItemUpdateResult_t>(
            SteamUGC.SubmitItemUpdate(update, request.ChangeNote),
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

        return new Dictionary<string, object> {
            ["itemId"] = itemId.m_PublishedFileId.ToString(),
            ["created"] = isNew,
            ["needsLegalAgreement"] = result.m_bUserNeedsToAcceptWorkshopLegalAgreement,
            ["url"] = PageUrl(itemId.m_PublishedFileId),
        };
    }

    public Dictionary<string, object> Describe(ulong itemId) {
        ItemDetails details = Query(new PublishedFileId_t(itemId));
        return new Dictionary<string, object> {
            ["itemId"] = itemId.ToString(),
            ["title"] = details.Title,
            ["visibility"] = details.Visibility.ToString(),
            ["tags"] = details.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries),
            ["fileSize"] = details.FileSize,
            ["screenshots"] = details.AdditionalPreviewCount,
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
        public readonly int AdditionalPreviewCount;

        public ItemDetails(SteamUGCDetails_t details, int additionalPreviewCount) {
            Title = details.m_rgchTitle;
            Description = details.m_rgchDescription;
            Tags = details.m_rgchTags;
            Visibility = details.m_eVisibility;
            Owner = new CSteamID(details.m_ulSteamIDOwner);
            FileSize = details.m_nFileSize;
            UpdatedUnixSeconds = details.m_rtimeUpdated;
            AdditionalPreviewCount = additionalPreviewCount;
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
            return new ItemDetails(details, (int)SteamUGC.GetQueryUGCNumAdditionalPreviews(query, 0));
        } finally {
            SteamUGC.ReleaseQueryUGCRequest(query);
        }
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
