using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeshWeaver.Mesh;

/// <summary>
/// The MESH-OWNED (operational) members of a NodeType node's content — the compile bookkeeping the
/// framework itself persists onto the <c>NodeTypeDefinition</c> as types compile (status,
/// timestamps, assembly pointers, source-version maps, release-request triggers). These members are
/// NOT authored content: the repo owns the definition (configuration, sources, description, …); the
/// mesh owns the compile state. Every seam that moves a NodeType node between a git repo and a live
/// mesh must honour that split:
///
/// <list type="bullet">
/// <item><b>Export STRIPS them</b> (<see cref="StripOperational"/>) — a repo file must not carry a
/// stale compile verdict into git.</item>
/// <item><b>Every UPSERT preserves the live node's values</b> (<see cref="PreserveLiveOperational"/>,
/// applied by the owner inside <c>CreateOrUpdateNodeRequest</c>'s merge) — no writer may regress
/// live compile state to whatever stale copy it happens to hold, whether that is a repo file's
/// embedded verdict (the "stale green" that claims a weeks-old assembly is current, which parks the
/// type when the cache is cold) or a syncing client's eventually-consistent snapshot (which lags the
/// compile pipeline and reverts a healthy type to its previous state — memex 2026-08-02).
/// 🚨 Callers must NOT pre-apply this against their own snapshot: the rule is enforced where the
/// answer is authoritative, and a client-side copy can only ever be as fresh as the index it read.</item>
/// <item><b>Change detection IGNORES them</b> (<see cref="PartitionSourceFingerprint"/>) — a node
/// differing only in bookkeeping has not changed.</item>
/// </list>
///
/// <para>This is the transitional ownership rule until the compile state moves off the
/// NodeTypeDefinition entirely (into the compile activity / a <c>_Compile</c> satellite the sync
/// never touches); once that lands this class keeps legacy repo files and stored nodes honest.</para>
/// </summary>
public static class NodeTypeOperationalContent
{
    /// <summary>Whether <paramref name="node"/> is a type-definition node — the one node type whose
    /// content carries the operational members.</summary>
    public static bool IsNodeTypeNode(MeshNode? node) =>
        string.Equals(node?.NodeType, MeshNode.NodeTypePath, StringComparison.Ordinal);

    /// <summary>
    /// The operational members by their serialized (camelCase) names, matched CASE-INSENSITIVELY:
    /// stored content is camelCased under the hub's naming policy, but writers fall back to the
    /// PascalCase property name when a hub carries no policy (see <c>StampReleaseRequest</c>), so
    /// both casings denote the same member. Pinned against the <c>NodeTypeDefinition</c> record by
    /// <c>NodeTypeOperationalContentTest</c> in the Graph test suite, so the list cannot silently
    /// drift from the record.
    /// </summary>
    public static readonly IReadOnlySet<string> MemberNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "compilationStatus",
        "compilationError",
        "compilationDiagnostics",
        "lastCompileStartedAt",
        "lastCompileSucceededAt",
        "lastCompiledVersion",
        "lastCompilationActivityPath",
        "latestReleasePath",
        "requestedReleasePath",
        "requestedReleaseAt",
        "requestedReleaseForce",
        "requestedReleaseBy",
        "lastReleaseRequestHandledAt",
        "releaseNotes",
        "latestAssemblyCollection",
        "latestAssemblyPath",
        "compiledSources",
        "currentSourceVersions",
        "compiledFrameworkVersion",
    };

    /// <summary>
    /// The node with the operational members REMOVED from its content — the shape a repo file (and
    /// the change-detection token) uses. Non-NodeType nodes, non-object content, and content that
    /// carries none of the members pass through as the SAME instance (no reshaping).
    /// </summary>
    public static MeshNode StripOperational(MeshNode node, JsonSerializerOptions options)
    {
        if (!IsNodeTypeNode(node) || ContentObject(node, options) is not { } content)
            return node;
        var removed = false;
        foreach (var key in content.Select(member => member.Key).Where(MemberNames.Contains).ToArray())
            removed |= content.Remove(key);
        return removed ? node with { Content = content } : node;
    }

    /// <summary>
    /// The INCOMING (repo) node with the LIVE node's operational members carried over — the import
    /// ownership rule. Authored members come from the repo; each operational member ends up exactly
    /// as the mesh last wrote it: the live value when the live node has one, ABSENT when it does not
    /// (a stale value embedded in the file never survives, in either direction). Returns the same
    /// instance when nothing would change, so an authored-identical import stays a no-op upsert.
    /// </summary>
    public static MeshNode PreserveLiveOperational(
        MeshNode incoming, MeshNode? live, JsonSerializerOptions options)
    {
        if (!IsNodeTypeNode(incoming) || live is null
            || ContentObject(incoming, options) is not { } original)
            return incoming;
        var liveContent = ContentObject(live, options);
        var merged = (JsonObject)original.DeepClone();
        foreach (var key in merged.Select(member => member.Key).Where(MemberNames.Contains).ToArray())
            merged.Remove(key);
        if (liveContent is not null)
            foreach (var (key, value) in liveContent.Where(member => MemberNames.Contains(member.Key)))
                merged[key] = value?.DeepClone();
        return JsonNode.DeepEquals(merged, original) ? incoming : incoming with { Content = merged };
    }

    /// <summary>
    /// The node's content as a fresh, mutable <see cref="JsonObject"/>, or null when the content is
    /// not object-shaped. Typed content serializes with its CONCRETE runtime type — never the
    /// <c>object</c> overload, whose polymorphic path adopts foreign types into the registry as a
    /// side effect of a read.
    /// </summary>
    private static JsonObject? ContentObject(MeshNode node, JsonSerializerOptions options) =>
        node.Content switch
        {
            null => null,
            JsonObject jo => (JsonObject)jo.DeepClone(),
            JsonNode => null,
            JsonElement je => je.ValueKind == JsonValueKind.Object ? JsonObject.Create(je) : null,
            var typed => JsonSerializer.SerializeToNode(typed, typed.GetType(), options) as JsonObject,
        };
}
