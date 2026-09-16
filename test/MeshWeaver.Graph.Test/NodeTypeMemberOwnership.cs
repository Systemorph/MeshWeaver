using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 THE ONE COPY of "who owns which <see cref="NodeTypeDefinition"/> member". Every guard that
/// asks the ownership question — the sync-seam mask
/// (<c>NodeTypeOperationalContent.MemberNames</c>, pinned by
/// <see cref="NodeTypeOperationalContentTest"/>), the committed-file ban
/// (<see cref="ShippedNodeTypeStateTest"/>) and the compile-state satellite's member set
/// (<c>NodeTypeCompileStateTest</c>, via the mask) — reads it from HERE.
///
/// <para><b>Why one copy (#4480).</b> The naming convention below used to be a private list inside
/// <see cref="ShippedNodeTypeStateTest"/>, and the mask was pinned in ONE direction only
/// (<c>MemberNames ⊆ the record</c>). Neither could see a runtime-state property that was MISSING
/// from the mask — the direction that actually loses data — so four of them were: three that the
/// convention would have named (<c>LatestAssemblyMvid</c>, <c>CompiledModulesHash</c>,
/// <c>CompiledDependencies</c>) and one the convention CANNOT name, because it is spelled outside
/// it (<c>DispatchedBuildInputs</c>). Two lists that both approximate the same set drift pairwise;
/// this file is the single source, and the PARTITION guard
/// (<c>NodeTypeOperationalContentTest.EverySerializedMember_IsClassified_ExactlyOnce</c>) is the
/// one that does not depend on a member being SPELLED like runtime state.</para>
/// </summary>
internal static class NodeTypeMemberOwnership
{
    /// <summary>
    /// The prefixes the compile/release control plane names its state with. Every
    /// <see cref="NodeTypeDefinition"/> member matching one is written by the runtime and must
    /// never be authored into a file. Nothing authored starts with any of them
    /// (<c>Configuration</c>, <c>ContentCollections</c>, <c>CreatableTypes</c> are the near
    /// misses, and none of them match).
    ///
    /// <para>🚨 This is a SUFFICIENT condition, never a necessary one: a runtime member may be
    /// spelled outside the convention (<c>DispatchedBuildInputs</c>, <c>BuildProvenance</c>,
    /// <c>ReleaseNotes</c>, the <c>Adopted*</c> family), which is precisely why
    /// <see cref="Classified"/> exists and why no guard may use this list as its denominator.</para>
    /// </summary>
    public static readonly ImmutableArray<string> RuntimeStatePrefixes =
    [
        "Compilation",       // Status, Error, Diagnostics, ImportRefusals
        "Compiled",          // Sources, FrameworkVersion, ModulesHash, Dependencies
        "LastCompil",        // LastCompileStartedAt/SucceededAt, LastCompiledVersion, LastCompilationActivityPath
        "LastRelease",       // LastReleaseRequestHandledAt
        "LatestAssembly",    // Collection, Path, Mvid
        "LatestRelease",     // LatestReleasePath
        "RequestedRelease",  // Path, At, Force, By
        "CurrentSource",     // CurrentSourceVersions, Fingerprint, Includes
        "Failed",            // FailedBuildInputs (#1793), FailedSourceQueries (#3903)
    ];

    /// <summary>
    /// The record's SERIALISED surface — the denominator every ownership guard counts against.
    /// A member ignored <c>Always</c> (the <c>BuildCreate</c> delegate) never reaches a file or a
    /// wire payload, and the <c>[JsonExtensionData]</c> bag (<c>UnknownMembers</c>) is not a member
    /// at all but the round-trip buffer for members this shape does not declare — the seams handle
    /// it explicitly. Neither can be owned by anyone, so neither is classified.
    ///
    /// <para>🚨 The test is the CONDITION, never the attribute's presence.
    /// <see cref="NodeTypeDefinition.IncludeGlobalTypes"/> carries
    /// <c>[JsonIgnore(Condition = JsonIgnoreCondition.Never)]</c> — which means it is ALWAYS
    /// written — so an attribute-presence filter would silently drop it from the denominator, and a
    /// guard that quietly counts one member fewer is the very defect this file exists to close.</para>
    /// </summary>
    public static readonly ImmutableArray<PropertyInfo> SerializedProperties =
        typeof(NodeTypeDefinition)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition is not JsonIgnoreCondition.Always
                        && p.GetCustomAttribute<JsonExtensionDataAttribute>() is null)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToImmutableArray();

    /// <summary>The serialised properties whose NAME matches <see cref="RuntimeStatePrefixes"/>.</summary>
    public static readonly ImmutableHashSet<string> RuntimeStateNamed =
        SerializedProperties
            .Select(p => p.Name)
            .Where(name => RuntimeStatePrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
            .ToImmutableHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The AUTHORED surface — what the REPO owns. A file is the source of truth for every one of
    /// these, so an import must honour the incoming value and the change-detection token must see
    /// it. If one ever lands in the operational mask, imports would stop honouring the repo for it,
    /// silently.
    /// </summary>
    public static readonly ImmutableHashSet<string> Authored =
    [
        nameof(NodeTypeDefinition.Body),
        nameof(NodeTypeDefinition.CellSurface),
        nameof(NodeTypeDefinition.ChildrenQuery),
        nameof(NodeTypeDefinition.Configuration),
        nameof(NodeTypeDefinition.ContentCollections),
        nameof(NodeTypeDefinition.CreatableTypes),
        nameof(NodeTypeDefinition.DefaultNamespace),
        nameof(NodeTypeDefinition.DefaultValues),
        nameof(NodeTypeDefinition.Dependencies),
        nameof(NodeTypeDefinition.Description),
        nameof(NodeTypeDefinition.Emoji),
        nameof(NodeTypeDefinition.HubConfiguration),
        nameof(NodeTypeDefinition.IncludeGlobalTypes),
        nameof(NodeTypeDefinition.InstanceLocations),
        nameof(NodeTypeDefinition.OwnsPartition),
        nameof(NodeTypeDefinition.PageMaxWidth),
        nameof(NodeTypeDefinition.RestrictedToNamespaces),
        nameof(NodeTypeDefinition.Sources),
        nameof(NodeTypeDefinition.StaticTypeName),
        nameof(NodeTypeDefinition.StorageTable),
        nameof(NodeTypeDefinition.Tests),
    ];

    /// <summary>
    /// 🚨 MESH-WRITTEN, stripped from every FILE shape and deliberately NOT PRESERVED on import —
    /// the third bucket. Read straight off the production set
    /// (<see cref="NodeTypeOperationalContent.StrippedButNotPreserved"/>), which carries the reason
    /// per entry: a bucket the tests declared for themselves would be a fourth copy of exactly the
    /// list this file exists to keep single.
    /// </summary>
    public static IReadOnlySet<string> MeshWrittenButNotPreserved =>
        NodeTypeOperationalContent.StrippedButNotPreserved;

    /// <summary>A property name as it appears in stored content.</summary>
    public static string CamelCase(string propertyName) =>
        JsonNamingPolicy.CamelCase.ConvertName(propertyName);
}
