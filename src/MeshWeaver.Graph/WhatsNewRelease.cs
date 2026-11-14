using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>
/// What the header's ✨ <b>What's New</b> entry shows (policy <c>whats-new-header</c>): the NEWEST
/// release-notes page and whether this viewer has opened it yet.
/// </summary>
/// <param name="NewestPath">The newest release-notes page's path (<c>Doc/ReleaseNotes/3_0_0</c>), or
/// null while none is listed.</param>
/// <param name="Unseen">True while the viewer has not opened <paramref name="NewestPath"/> — the dot.</param>
public sealed record WhatsNewReleaseState(string? NewestPath, bool Unseen)
{
    /// <summary>The state before anything is known: no page, no dot.</summary>
    public static WhatsNewReleaseState None { get; } = new(null, false);

    /// <summary>The application href of the newest release-notes page, or null while none is listed.</summary>
    public string? Href => NewestPath is null ? null : "/" + NewestPath;
}

/// <summary>
/// The data half of the header's ✨ <b>What's New</b> entry (policy <c>whats-new-header</c>): which
/// release-notes page is the newest, whether the viewer has seen it, and the write that records
/// that they have. The header button itself lives with the portal shell (MeshWeaver.Plugins); this
/// is the part every client shares.
///
/// <para><b>Newest by version, from a listing.</b> Release notes are written per RELEASE (policy
/// <c>whatsnew-cadence</c>) as one page per version under <see cref="ReleaseNotesNamespace"/>, named
/// by the version with underscores (<c>3_0_0</c>, <c>3_1_0</c>). Listing a namespace's children is a
/// valid query use; the newest is decided by the parsed version, never by the order rows arrive in
/// or by string order (<c>3_10_0</c> sorts after <c>3_9_0</c>).</para>
///
/// <para><b>Seen is per user and durable.</b> The viewer's last opened release is
/// <see cref="User.LastSeenReleaseNotes"/> on their own node, written through
/// <c>GetMeshNodeStream(userPath).Update</c> — never browser storage — so the dot clears on every
/// device once it is opened on one. See <c>Doc/GUI/WhatsNewHeader</c>.</para>
/// </summary>
public static class WhatsNewRelease
{
    /// <summary>The documentation namespace holding one release-notes page per released version.</summary>
    public const string ReleaseNotesNamespace = "Doc/ReleaseNotes";

    /// <summary>The listing of every release-notes page (a children listing — a valid query use).</summary>
    public const string ListingQuery = "path:" + ReleaseNotesNamespace + " scope:children";

    /// <summary>The synced-query id of <see cref="ListingQuery"/>; stable, so every caller shares one cache.</summary>
    public const string ListingQueryId = "whatsnew:release-notes";

    /// <summary>
    /// The version a release-notes page is named for: its last path segment with underscores read as
    /// dots (<c>Doc/ReleaseNotes/3_1_0</c> → 3.1.0). Null when the segment is not a version. Pure.
    /// </summary>
    /// <param name="path">The page's path.</param>
    /// <returns>The version, or null.</returns>
    public static Version? VersionOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var segment = path.TrimEnd('/');
        segment = segment[(segment.LastIndexOf('/') + 1)..];
        return Version.TryParse(segment.Replace('_', '.'), out var version) ? version : null;
    }

    /// <summary>
    /// The newest release-notes page among <paramref name="nodes"/>: the highest version; a page
    /// whose name is not a version never wins over one that is. Null when there is none. Pure.
    /// </summary>
    /// <param name="nodes">The listed release-notes pages.</param>
    /// <returns>The newest page's path, or null.</returns>
    public static string? NewestPath(IEnumerable<MeshNode>? nodes)
        => (nodes ?? [])
            .Where(n => n?.Path is { Length: > 0 })
            .Select(n => (n.Path, Version: VersionOf(n.Path)))
            .Where(n => n.Version is not null)
            .OrderByDescending(n => n.Version)
            .Select(n => n.Path)
            .FirstOrDefault();

    /// <summary>
    /// Whether the viewer has NOT opened <paramref name="newestPath"/> yet: true while there is a
    /// newest page and it differs from <paramref name="lastSeen"/>. Pure.
    /// </summary>
    /// <param name="newestPath">The newest release-notes page.</param>
    /// <param name="lastSeen">The viewer's <see cref="User.LastSeenReleaseNotes"/>.</param>
    /// <returns>Whether to show the dot.</returns>
    public static bool IsUnseen(string? newestPath, string? lastSeen)
        => !string.IsNullOrEmpty(newestPath)
           && !string.Equals(newestPath.Trim('/'), lastSeen?.Trim('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The live state for the viewer at <paramref name="viewerId"/>: the newest release-notes page
    /// and whether they have opened it, recombined whenever a release page is added or their
    /// profile changes. Starts with <see cref="WhatsNewReleaseState.None"/> so a header paints at once.
    /// </summary>
    /// <param name="hub">The hub to read through (the caller's identity applies).</param>
    /// <param name="viewerId">The signed-in viewer's user id (their user node's path).</param>
    /// <returns>The live state.</returns>
    public static IObservable<WhatsNewReleaseState> ObserveWhatsNewRelease(this IMessageHub hub, string viewerId)
    {
        var options = hub.JsonSerializerOptions;
        var newest = hub.GetQuery(ListingQueryId, ListingQuery)
            .Select(NewestPath)
            .DistinctUntilChanged();
        var lastSeen = hub.GetMeshNodeStream(viewerId)
            .Select(node => node?.ContentAs<User>(options)?.LastSeenReleaseNotes)
            .DistinctUntilChanged();
        return newest
            .CombineLatest(lastSeen, (path, seen) => new WhatsNewReleaseState(path, IsUnseen(path, seen)))
            .DistinctUntilChanged()
            .StartWith(WhatsNewReleaseState.None);
    }

    /// <summary>
    /// Records that the viewer opened the release-notes page at <paramref name="releasePath"/>: sets
    /// <see cref="User.LastSeenReleaseNotes"/> on their own node through the one mutation API, with
    /// the typed overload so an unreadable profile faults the write rather than being overwritten.
    /// Cold — runs on Subscribe. Idempotent.
    /// </summary>
    /// <param name="hub">The hub issuing the write (carries the viewer's identity).</param>
    /// <param name="viewerId">The viewer's user id (their user node's path).</param>
    /// <param name="releasePath">The release-notes page they opened.</param>
    /// <returns>The node after the write.</returns>
    public static IObservable<MeshNode> MarkReleaseNotesSeen(this IMessageHub hub, string viewerId, string releasePath)
    {
        var path = releasePath.Trim('/');
        return hub.GetMeshNodeStream(viewerId).Update<User>((node, content) =>
        {
            var user = content ?? new User();
            return string.Equals(user.LastSeenReleaseNotes, path, StringComparison.OrdinalIgnoreCase)
                ? node
                : node with { Content = user with { LastSeenReleaseNotes = path } };
        });
    }
}
