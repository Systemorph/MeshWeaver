using System.ComponentModel;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.SelfUpdate;

/// <summary>The platform auto-update strategy — the single value on <c>Admin/UpdatePolicy</c>.</summary>
public enum UpdatePolicyKind
{
    /// <summary>Always roll to the newest image on ACR, INCLUDING build-numbered continuous builds
    /// (e.g. <c>3.0.0-ci.51</c>). This is the platform default.</summary>
    Continuous,

    /// <summary>Roll only to the newest CLEAN release (no build number, e.g. <c>3.0.0</c>); ignore
    /// continuous build-numbered images.</summary>
    Stable,

    /// <summary>Never auto-update. Updates are applied manually (operator / admin action).</summary>
    None,
}

/// <summary>
/// Content of the <c>Admin/UpdatePolicy</c> node: the admin-editable update <see cref="Policy"/>
/// plus the self-update poller's bookkeeping (the latest tag it has seen on the registry and when).
/// Edited via the standard node-content editor (the <see cref="Policy"/> enum renders as a dropdown);
/// the two bookkeeping fields are written by the poller as System and hidden from the editor.
/// </summary>
public record UpdatePolicyContent
{
    /// <summary>The update strategy. Defaults to <see cref="UpdatePolicyKind.Continuous"/>.</summary>
    [Description("Update strategy")]
    public UpdatePolicyKind Policy { get; init; } = UpdatePolicyKind.Continuous;

    /// <summary>
    /// When <c>true</c> (default) the install only rolls to builds that PASSED CI ("green").
    /// The continuous-delivery pipeline already publishes an image ONLY when "MeshWeaver Build and
    /// Test" succeeds, so the verified channel contains green builds exclusively; this flag is the
    /// forward-looking guard that keeps that guarantee if an "edge" channel (publish-on-every-build,
    /// tags carrying the <c>edge</c> pre-release label) is ever added — green-only ignores those.
    /// Set <c>false</c> to also accept unverified edge builds (bleeding-edge / pre-merge testing).
    /// </summary>
    [Description("Only update to CI-verified (green) builds")]
    public bool RequireCiGreen { get; init; } = true;

    /// <summary>The newest image tag the poller has found on the registry (for the admin UI /
    /// detect-and-notify). Written by the poller; not user-editable.</summary>
    [Browsable(false)]
    public string? LatestAvailableTag { get; init; }

    /// <summary>When the poller last recorded <see cref="LatestAvailableTag"/>.</summary>
    [Browsable(false)]
    public DateTimeOffset? CheckedAt { get; init; }
}

/// <summary>
/// The <c>Admin/UpdatePolicy</c> singleton node — the platform's auto-update strategy. Mirrors
/// <c>AiSettingsNodeType</c> (typed-content node + storm-safe create-on-absent via a query, never a
/// point-read of a maybe-absent path) and the Admin-partition anchoring of
/// <c>ShippedReleaseSeed.PlatformVersionNode</c>.
/// </summary>
public static class UpdatePolicyNodeType
{
    /// <summary>NodeType discriminator.</summary>
    public const string NodeType = "UpdatePolicy";

    /// <summary>The Admin partition that holds platform-level data (schema <c>admin</c>).</summary>
    public const string AdminPartition = ShippedReleaseSeed.AdminPartition;

    /// <summary>The singleton instance id.</summary>
    public const string NodeId = "UpdatePolicy";

    /// <summary>Full path of the policy node: <c>Admin/UpdatePolicy</c>.</summary>
    public const string NodePath = $"{AdminPartition}/{NodeId}";

    /// <summary>Registers the UpdatePolicy node type + its content type for typed (de)serialization.</summary>
    public static TBuilder AddUpdatePolicyType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.ConfigureHub(config => config.WithType<UpdatePolicyContent>(nameof(UpdatePolicyContent)));
        return builder;
    }

    /// <summary>MeshNode TYPE DEFINITION for <c>nodeType:UpdatePolicy</c>.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Update Policy",
        Icon = "/static/NodeTypeIcons/rocket.svg",
        IsSatelliteType = false,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<UpdatePolicyContent>())
    };

    /// <summary>
    /// Create-on-absent (idempotent, reactive, as System) of <c>Admin/UpdatePolicy</c> with the given
    /// default policy. Existence is read via <c>GetQuery</c> (empty-on-absent) — NEVER a point
    /// <c>GetMeshNodeStream(path)</c> probe of the maybe-absent node (which NotFound-resubscribe-storms
    /// on a fresh DB). Emits the node path when it exists. An existing node is left untouched (its
    /// admin-chosen policy is preserved).
    /// </summary>
    public static IObservable<string> EnsureExists(
        IMessageHub hub, AccessService? accessService, UpdatePolicyKind defaultPolicy, ILogger? logger = null)
    {
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null)
            return Observable.Return(NodePath);
        var workspace = hub.GetWorkspace();

        MeshNode BuildNode() => new(NodeId, AdminPartition)
        {
            NodeType = NodeType,
            Name = "Update Policy",
            State = MeshNodeState.Active,
            Content = new UpdatePolicyContent { Policy = defaultPolicy },
        };

        return Observable.Using(
            () => AccessContextScope.AsSystem(accessService),
            _ => workspace
                .GetQuery($"{NodeType}|{NodePath}", $"path:{NodePath} nodeType:{NodeType}")
                .Take(1)
                .SelectMany(nodes =>
                {
                    var existing = nodes.FirstOrDefault(n =>
                        string.Equals(n.NodeType, NodeType, StringComparison.OrdinalIgnoreCase));
                    if (existing is not null)
                        return Observable.Return(NodePath);
                    logger?.LogInformation(
                        "[SelfUpdate] seeding {Path} = {Policy}.", NodePath, defaultPolicy);
                    return meshService.CreateNode(BuildNode())
                        .Select(_ => NodePath)
                        // Idempotent: a concurrent first-writer (other replica) won the create race.
                        .Catch<string, Exception>(ex => IsAlreadyExists(ex)
                            ? Observable.Return(NodePath)
                            : Observable.Throw<string>(ex));
                }));
    }

    /// <summary>Parses the policy content from a node (handles both typed content and raw
    /// <see cref="JsonElement"/>); returns defaults when absent/unparseable.</summary>
    public static UpdatePolicyContent Parse(MeshNode? node, JsonSerializerOptions options) =>
        ParseContent(node?.Content, options);

    /// <summary>Parses the policy content from a node's <c>Content</c> value.</summary>
    public static UpdatePolicyContent ParseContent(object? content, JsonSerializerOptions options) =>
        content switch
        {
            UpdatePolicyContent c => c,
            JsonElement je => TryDeserialize(je, options) ?? new UpdatePolicyContent(),
            _ => new UpdatePolicyContent(),
        };

    private static UpdatePolicyContent? TryDeserialize(JsonElement je, JsonSerializerOptions options)
    {
        try { return JsonSerializer.Deserialize<UpdatePolicyContent>(je.GetRawText(), options); }
        catch { return null; }
    }

    /// <summary>True if the exception (or any inner) reports an "already exists" outcome — the
    /// idempotent-create success signal.</summary>
    private static bool IsAlreadyExists(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e.Message?.Contains("already exists", StringComparison.OrdinalIgnoreCase) == true)
                return true;
        return false;
    }
}
