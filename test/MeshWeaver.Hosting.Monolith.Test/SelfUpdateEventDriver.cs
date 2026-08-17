using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Drives the self-update check the way production does: by publishing a <c>BuildCompletion</c>
/// record, which is what the GitHub webhook writes when a repository's build goes green.
///
/// <para>🚨 These suites used to be driven by the poller's recurring interval — "wait for the next
/// tick". The check is event-driven now (one startup pass, then a check per build completion of the
/// platform or of ANY module the environment deploys), so a test that waits for a timer waits
/// forever. The assertions are unchanged; only the stimulus moved to the real one, which makes
/// these tests exercise the production wake-up path rather than a timer that no longer exists.</para>
/// </summary>
internal static class SelfUpdateEventDriver
{
    /// <summary>A repository whose green build wakes the check. Any repo works — that is the point
    /// of watching the whole <c>Admin/_Build</c> collection rather than one configured repo.</summary>
    internal const string Owner = "Systemorph";

    /// <summary>Publishes (or re-publishes) a green build for <paramref name="repo"/>, bumping the
    /// record so the watch sees a NEW build rather than a replayed baseline.</summary>
    internal static async Task PublishBuildAsync(IMessageHub mesh, string repo, long runNumber)
    {
        var meshService = mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var path = BuildCompletion.PathFor(Owner, repo);
        var slash = path.LastIndexOf('/');
        var node = new MeshNode(path[(slash + 1)..], path[..slash])
        {
            NodeType = BuildCompletion.NodeType,
            Name = $"{Owner}/{repo} build",
            State = MeshNodeState.Active,
            Content = new BuildCompletion
            {
                RepositoryUrl = $"https://github.com/{Owner}/{repo}",
                Branch = "main",
                HeadSha = $"sha{runNumber}",
                WorkflowName = "MeshWeaver Build and Test",
                RunId = runNumber,
                RunNumber = runNumber,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Conclusion = "success",
            },
        };
        await meshService.CreateOrUpdateNode(node).FirstAsync().ToTask();
    }
}
