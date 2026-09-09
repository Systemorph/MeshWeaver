using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The <see cref="PinnedPlatformReferenceSource"/> of a control instance: every platform build a
/// <c>Hosting/Deployment</c> record PINS (<c>pinnedImageTag</c>) and every platform build a
/// registered instance REPORTS running or adopted (<c>Hosting/ModuleInventory</c> —
/// <see cref="DeploymentReport.PlatformVersion"/> and <see cref="DeploymentReport.FrameworkIdentity"/>).
///
/// <para>On the registry (memex-cloud) a remote instance pulls its OWN identity's seal over the
/// HTTP prebuilt surface, so an identity such an instance pins or runs is referenced however old
/// it is. Age alone cannot reveal whether a remote instance still uses that build.
/// A Deployment record's <c>pinnedImageTag</c> is a mesh NodeType defined outside this assembly,
/// so it is read as JSON (case-insensitively) rather than through a CLR type.</para>
///
/// <para>Read as System, mesh-wide, on every pass. Errors propagate: a pin set that could not be
/// read aborts the pass, as does a report with an incomplete adoption inventory.</para>
/// </summary>
public static class DeploymentPinnedReferences
{
    /// <summary>The Deployment record's node type (a mesh NodeType shipped by the Hosting plugin).</summary>
    public const string DeploymentNodeType = "Hosting/Deployment";

    /// <summary>The Deployment record's pinned image tag field.</summary>
    public const string PinnedImageTagField = "pinnedImageTag";

    private static readonly TimeSpan EnumerationBudget = TimeSpan.FromSeconds(60);

    /// <summary>The source, bound to the mesh hub the host resolves at call time.</summary>
    public static PinnedPlatformReferenceSource SourceFor(IServiceProvider services) =>
        () =>
        {
            var hub = services.GetService<IMessageHub>();
            var meshService = hub?.ServiceProvider.GetService<IMeshService>();
            if (hub is null || meshService is null)
                return Observable.Throw<ImmutableList<PinnedPlatformReference>>(new InvalidOperationException(
                    "no mesh hub / IMeshService on this host — the pinned platform builds cannot be read"));
            var logger = hub.ServiceProvider.GetService<ILogger<PrebuiltBundleRetentionHostedService>>();
            return Read(hub, meshService, logger);
        };

    /// <summary>The pins and the reported builds, in one list.</summary>
    public static IObservable<ImmutableList<PinnedPlatformReference>> Read(
        IMessageHub hub, IMeshService meshService, ILogger? logger = null)
    {
        var access = hub.ServiceProvider.GetService<AccessService>();
        return access.RunAsSystem(() =>
            Records(meshService, DeploymentNodeType)
                .Zip(Records(meshService, DeploymentReportService.InventoryNodeType), (deployments, inventories) =>
                    deployments.Select(n => DeploymentPinOf(n, hub.JsonSerializerOptions))
                        .Concat(inventories.SelectMany(n => ReportedBuildsOf(n, hub.JsonSerializerOptions)))
                        .Where(r => r is not null)
                        .Select(r => r!)
                        .ToImmutableList())
                .Do(refs => logger?.LogDebug(
                    "PrebuiltBundleRetention: {Count} pinned platform reference(s) from Deployment records and instance reports",
                    refs.Count)));
    }

    private static IObservable<IReadOnlyList<MeshNode>> Records(IMeshService meshService, string nodeType) =>
        meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery(MeshWideQuery.OfType(nodeType)))
            .Take(1)
            .Timeout(EnumerationBudget)
            .Select(change => change.Items.Where(n => n.State == MeshNodeState.Active).ToList());

    /// <summary>A Deployment record's pin, or null when the record pins nothing.</summary>
    public static PinnedPlatformReference? DeploymentPinOf(MeshNode node, JsonSerializerOptions options)
    {
        var tag = Field(node, PinnedImageTagField, options);
        return string.IsNullOrWhiteSpace(tag)
            ? null
            : new PinnedPlatformReference($"Deployment {node.Path}", tag, null);
    }

    /// <summary>
    /// The running platform and every adopted build in a complete consumer inventory.
    /// Legacy, incomplete or malformed inventories abort retention instead of implying no consumers.
    /// </summary>
    public static ImmutableList<PinnedPlatformReference> ReportedBuildsOf(MeshNode node, JsonSerializerOptions options)
    {
        var content = node.Content is JsonElement element ? element : JsonSerializer.SerializeToElement(node.Content, options);
        JsonElement Find(string name) => content.ValueKind == JsonValueKind.Object
            ? content.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Value
            : default;
        var complete = Find(nameof(DeploymentReport.AdoptedFrameworkInventoryComplete));
        var identities = Find(nameof(DeploymentReport.AdoptedFrameworkIdentities));
        if (complete.ValueKind != JsonValueKind.True || identities.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"instance report {node.Path}: adopted artifact inventory is incomplete or legacy; retention must not delete");
        var references = ImmutableList.CreateBuilder<PinnedPlatformReference>();
        var running = ReportedBuildOf(node, options)
            ?? throw new InvalidOperationException($"instance report {node.Path}: running build is missing; retention must not delete");
        references.Add(running);
        foreach (var identity in identities.EnumerateArray())
        {
            if (identity.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(identity.GetString()))
                throw new InvalidOperationException($"instance report {node.Path}: an adopted artifact identity is malformed; retention must not delete");
            references.Add(new PinnedPlatformReference($"adopted build reported by {node.Path}", null, identity.GetString()));
        }
        return references.ToImmutable();
    }

    /// <summary>An inventory record's reported build, or null when it reports neither a version nor an identity.</summary>
    public static PinnedPlatformReference? ReportedBuildOf(MeshNode node, JsonSerializerOptions options)
    {
        var version = Field(node, nameof(DeploymentReport.PlatformVersion), options);
        var identity = Field(node, nameof(DeploymentReport.FrameworkIdentity), options);
        return string.IsNullOrWhiteSpace(version) && string.IsNullOrWhiteSpace(identity)
            ? null
            : new PinnedPlatformReference($"instance report {node.Path}",
                string.IsNullOrWhiteSpace(version) ? null : version,
                string.IsNullOrWhiteSpace(identity) ? null : identity);
    }

    /// <summary>
    /// One string field of a node's content, whatever CLR shape the content arrived in — the
    /// record's type is a mesh NodeType this assembly does not carry, so the content is read as a
    /// JSON document and the field matched case-insensitively.
    /// </summary>
    internal static string? Field(MeshNode node, string name, JsonSerializerOptions options)
    {
        if (node.Content is null)
            return null;
        var element = node.Content is JsonElement e ? e : JsonSerializer.SerializeToElement(node.Content, options);
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
        return null;
    }
}
