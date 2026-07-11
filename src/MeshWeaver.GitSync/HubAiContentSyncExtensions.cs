using System.Reactive.Linq;
using MeshWeaver.AI;
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
    public static IObservable<AiContentSyncResult> SyncAiContentToRepo(this IMessageHub hub)
    {
        var root = AiContentLocator.RepoSectionRoot();
        if (root is null)
            return Observable.Throw<AiContentSyncResult>(new InvalidOperationException(
                "AI content sync-back needs a repo checkout (content/ai on disk) — it is a dev-time operation; " +
                "a deployed portal has no working tree to write to."));
        return hub.ServiceProvider.GetRequiredService<AiContentDiskWriter>().WriteBack(root);
    }

    /// <summary>Writes to an explicit section root — the testable overload (write to a temp dir).</summary>
    public static IObservable<AiContentSyncResult> SyncAiContentToRepo(this IMessageHub hub, string sectionRoot) =>
        hub.ServiceProvider.GetRequiredService<AiContentDiskWriter>().WriteBack(sectionRoot);
}
