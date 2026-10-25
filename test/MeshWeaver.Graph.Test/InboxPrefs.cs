using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Writes a person's <see cref="InboxPreferences"/> node the way their settings would — the node the
/// inbox's checkboxes are bound to — so a test can state what the person chose before (or while) the
/// page is open.
/// </summary>
internal static class InboxPrefs
{
    /// <summary>Every activity state ticked — a person who wants decided approvals listed too.</summary>
    public static InboxPreferences EveryState(InboxPreferences p)
        => p with { ActivityStates = System.Collections.Immutable.ImmutableDictionary<string, bool>.Empty.Add("open", true).Add("done", true) };

    /// <summary>
    /// Creates the preferences node of <paramref name="owner"/> holding <c>change(new InboxPreferences())</c>, or — when
    /// it exists — applies <paramref name="change"/> to what it holds. As System: the test is not the person.
    /// </summary>
    public static async Task SetAsync(IMessageHub mesh, string owner, Func<InboxPreferences, InboxPreferences> change, CancellationToken ct)
    {
        var access = mesh.ServiceProvider.GetRequiredService<AccessService>();
        var service = mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var path = InboxPreferencePaths.PathFor(owner);
        var options = mesh.JsonSerializerOptions;
        var created = await Observable.Defer(() => access.RunAsSystem(() => service.CreateNode(
                new MeshNode(InboxPreferencePaths.NodeId, InboxPreferencePaths.NamespaceFor(owner))
                {
                    NodeType = InboxPreferencePaths.NodeType, Name = "Inbox Preferences", State = MeshNodeState.Active,
                    Content = change(new InboxPreferences()),
                })))
            .Select(node => (MeshNode?)node)
            // Only an ALREADY-EXISTING node is recovered from (and updated below); any other failure is the test's.
            .Catch((Exception ex) => ex.IsNodeAlreadyExists() ? Observable.Return<MeshNode?>(null) : Observable.Throw<MeshNode?>(ex))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        if (created is not null)
            return;
        await access.RunAsSystem(() => mesh.GetMeshNodeStream(path)
                .Update(node => node with { Content = change(node.ContentAs<InboxPreferences>(options) ?? new InboxPreferences()) }))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
    }

    /// <summary>
    /// Reads the preferences node of <paramref name="owner"/> once it holds something <paramref name="until"/> accepts:
    /// an exact-path listing says whether it exists, and the node's own AUTHORITATIVE stream says what it holds — an
    /// index snapshot can trail a write, which would let a stale choice satisfy an assertion.
    /// </summary>
    public static async Task<InboxPreferences> ReadAsync(IMessageHub mesh, string owner, Func<InboxPreferences, bool> until, CancellationToken ct)
    {
        var options = mesh.JsonSerializerOptions;
        var path = InboxPreferencePaths.PathFor(owner);
        var access = mesh.ServiceProvider.GetRequiredService<AccessService>();
        return await mesh
            .GetQuery($"test:prefs:{Guid.NewGuid():N}", $"path:{path} nodeType:{InboxPreferencePaths.NodeType} select:path,id,nodeType")
            .Where(nodes => nodes.Any())
            .Take(1)
            .SelectMany(_ => access.RunAsSystem(() => mesh.GetMeshNodeStream(path)))
            .Select(node => node.ContentAs<InboxPreferences>(options))
            .Where(p => p is not null && until(p))
            .Select(p => p!)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
    }
}
