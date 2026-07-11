using System.Reactive.Linq;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.GitSync;

/// <summary>
/// The hub-extension surface for the AI-content sync-back: write the agents &amp; skills edited in the
/// running mesh back to the repo's <c>content/ai</c> section. Used by <see cref="AiContentSyncArea"/>
/// and by tests; the same one-liner an admin script would call.
/// </summary>
public static class HubAiContentSyncExtensions
{
    /// <summary>
    /// Writes the live <c>Agent</c> + <c>Skill</c> partitions back to the repo working tree's
    /// <c>content/ai</c> (<see cref="AiContentLocator.RepoSectionRoot"/>). Errors when not running from
    /// a source checkout (a deployed container has no working tree). Cold — the work runs on Subscribe.
    /// </summary>
    public static IObservable<AiContentSyncResult> SyncAiContentToRepo(this IMessageHub hub) =>
        // Enforce the platform-admin gate HERE, not only in the UI area — this is the surface other
        // server code / an admin script calls, so the authorization can't be bypassed by skipping the menu.
        hub.IsGlobalAdmin().Take(1).SelectMany(isAdmin =>
        {
            if (!isAdmin)
                return Observable.Throw<AiContentSyncResult>(new UnauthorizedAccessException(
                    "AI content sync-back is a platform-admin operation."));
            var root = AiContentLocator.RepoSectionRoot();
            if (root is null)
                return Observable.Throw<AiContentSyncResult>(new InvalidOperationException(
                    "AI content sync-back needs a repo checkout (content/ai on disk) — it is a dev-time operation; " +
                    "a deployed portal has no working tree to write to."));
            return hub.ServiceProvider.GetRequiredService<AiContentDiskWriter>().WriteBack(root);
        });

    /// <summary>
    /// Writes to an explicit section root, WITHOUT the admin gate — the low-level seam for the UI area
    /// (which has already gated admin) and for tests (write to a temp dir). Application code should call
    /// the gated <see cref="SyncAiContentToRepo(IMessageHub)"/> instead.
    /// </summary>
    public static IObservable<AiContentSyncResult> SyncAiContentToRepo(this IMessageHub hub, string sectionRoot) =>
        hub.ServiceProvider.GetRequiredService<AiContentDiskWriter>().WriteBack(sectionRoot);
}
