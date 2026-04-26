using System.Text.Json.Serialization;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Posted to a NodeType's per-node hub to ask "where's the compiled assembly +
/// HubConfiguration delegate for this NodeType?". The owning hub answers from its
/// per-version compile cache, compiles on miss, or short-circuits when the
/// MeshNode already carries <see cref="MeshNode.AssemblyLocation"/> +
/// <see cref="MeshNode.HubConfiguration"/> (the static-provider /
/// <c>AddMeshNodes</c> case).
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
/// <param name="AssemblyLocation">Absolute path to the compiled DLL for the requested version. Null if Success is false.</param>
/// <param name="Collection">Content-collection name where the artifact lives, if any. Null for static-provider types.</param>
/// <param name="Version">The resolved version (echoed so the consumer's cache key matches what was actually compiled).</param>
/// <param name="Error">Error message when <paramref name="Success"/> is false.</param>
/// <param name="HubConfiguration">
/// In-process delegate to configure the per-node hub for instances of this type.
/// <b>Not serializable</b> — marked <see cref="JsonIgnoreAttribute"/> because Func
/// values cannot cross silo boundaries. In-process consumers receive the live
/// delegate; cross-silo consumers receive <c>null</c> and must reflect on
/// <paramref name="AssemblyLocation"/> to recover it.
/// </param>
public record GetCompilationPathResponse(
    bool Success,
    string? AssemblyLocation,
    string? Collection,
    string? Version,
    string? Error,
    [property: JsonIgnore] Func<MessageHubConfiguration, MessageHubConfiguration>? HubConfiguration);
