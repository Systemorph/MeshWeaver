using System.Reactive.Linq;
using MeshWeaver.AI;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.GitSync;

/// <summary>
/// DI-registered <see cref="INodeMenuProvider"/> that contributes the "Sync to repo" entry to the NODE
/// menu on any node in the <c>Agent</c> or <c>Skill</c> partition — the one-click way to write the
/// agents &amp; skills edited in the mesh back to the on-disk <c>content/ai</c> section
/// (<see cref="AiContentSyncArea"/> → <see cref="AiContentDiskWriter"/>).
///
/// <para>Shown only when BOTH hold: the portal runs from a source checkout
/// (<see cref="AiContentLocator.RepoSectionRoot"/> is non-null — a deployed container has no working
/// tree to commit to) and the viewer is a platform admin. It gates on admin, NOT on partition Update
/// permission, because the operation READS the mesh and WRITES to disk — the built-in partitions'
/// read-only policy does not apply to a disk write.</para>
/// </summary>
public sealed class AiContentSyncMenuProvider : INodeMenuProvider
{
    /// <inheritdoc />
    public string Context => NodeMenuItemsExtensions.NodeMenuContext;

    private static readonly string[] AiPartitions = ["Agent", SkillNodeType.RootNamespace];

    /// <inheritdoc />
    public IObservable<IReadOnlyCollection<NodeMenuItemDefinition>> GetItems(
        LayoutAreaHost host, RenderingContext ctx)
    {
        IReadOnlyCollection<NodeMenuItemDefinition> none = [];

        // Dev-only: a deployed container has no repo working tree to write back to.
        if (AiContentLocator.RepoSectionRoot() is null)
            return Observable.Return(none);

        var hubPath = host.Hub.Address.ToString();
        var top = hubPath.Split('/', 2)[0];
        if (!AiPartitions.Contains(top, StringComparer.Ordinal))
            return Observable.Return(none);

        var item = new NodeMenuItemDefinition(
            Label: "Sync to repo",
            Area: AiContentSyncArea.AreaName,
            Icon: "⬇️",
            Order: 38,   // content/history section (Files=30, Versions=32, Synchronizations=36)
            Href: AiContentSyncArea.Href(hubPath),
            Tooltip: "Write the agents & skills edited here back to content/ai in the repo");

        // Platform-admin gated; StartWith so a host CombineLatest fires, Catch so a permission race
        // never breaks the whole menu.
        return host.Hub.IsGlobalAdmin()
            .Select(isAdmin => isAdmin ? (IReadOnlyCollection<NodeMenuItemDefinition>)[item] : none)
            .DistinctUntilChanged()
            .Catch<IReadOnlyCollection<NodeMenuItemDefinition>, Exception>(_ => Observable.Return(none))
            .StartWith(none);
    }
}
