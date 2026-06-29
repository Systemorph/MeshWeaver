using System.Text.Json.Serialization;
using MeshWeaver.Data;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Posted to a NodeType's per-node hub to ask "where's the compiled assembly +
/// HubConfiguration delegate for this NodeType?". The owning hub answers from its
/// per-version compile cache, compiles on miss, or short-circuits when the
/// MeshNode already carries <see cref="MeshNode.HubConfiguration"/> + the
/// persisted <c>NodeTypeDefinition.LatestAssembly{Collection,Path}</c> fields
/// (the static-provider / <c>AddMeshNodes</c> case).
///
/// <para>
/// Use <paramref name="Version"/> to pin to a specific historical
/// <see cref="MeshNode.Version"/>; <c>null</c> means "give me HEAD".
/// </para>
/// </summary>
public record GetCompilationPathRequest(string? Version = null)
    : IRequest<GetCompilationPathResponse>;

/// <summary>
/// Response to <see cref="GetCompilationPathRequest"/>.
/// </summary>
/// <param name="Success">True iff the NodeType could be resolved AND the assembly is available.</param>
/// <param name="AssemblyLocation">
/// Process-local path to the compiled DLL. Valid in the producing silo only.
/// Cross-silo consumers must use <paramref name="Collection"/> + the
/// <c>ContentPath</c> property to fetch the same bytes via <c>IAssemblyStore</c>.
/// Scheduled for removal once every consumer reads from the store.
/// </param>
/// <param name="Collection">Content-collection name where the artifact lives, if any. Null for static-provider types.</param>
/// <param name="Version">The resolved version (echoed so the consumer's cache key matches what was actually compiled).</param>
/// <param name="Error">Error message when <paramref name="Success"/> is false.</param>
/// <param name="HubConfiguration">
/// In-process delegate to configure the per-node hub for instances of this type.
/// <b>Not serializable</b> — marked <see cref="JsonIgnoreAttribute"/> because Func
/// values cannot cross silo boundaries. In-process consumers receive the live
/// delegate; cross-silo consumers receive <c>null</c> and must hydrate from the
/// <paramref name="Collection"/> + <c>ContentPath</c> reference.
/// </param>
/// <param name="Log">
/// Activity log of the compilation attempt — every executed source query, every
/// matched Code path, the final compile result. Lets the caller surface
/// "compile saw no source files" / "compilation succeeded" without re-running
/// the pipeline.
/// </param>
public record GetCompilationPathResponse(
    bool Success,
    string? AssemblyLocation,
    string? Collection,
    string? Version,
    string? Error,
    [property: JsonIgnore] Func<MessageHubConfiguration, MessageHubConfiguration>? HubConfiguration,
    ActivityLog? Log = null)
{
    /// <summary>
    /// Path inside <see cref="Collection"/> where the compiled assembly's bytes
    /// live. Together with <see cref="Collection"/> forms the cross-silo durable
    /// reference. Producer sets it from the assembly-store upload's content-path;
    /// consumers fetch via <c>IAssemblyStore.TryGetAssemblyPath</c> (the store key
    /// is <c>(nodeTypePath, version)</c>, but ContentPath is the canonical
    /// denormalised reference visible on the response).
    /// </summary>
    public string? ContentPath { get; init; }
}
