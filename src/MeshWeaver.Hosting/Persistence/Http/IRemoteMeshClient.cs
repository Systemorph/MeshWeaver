using MeshWeaver.Mesh;

namespace MeshWeaver.Hosting.Persistence.Http;

/// <summary>
/// Factory for <see cref="IRemoteMeshClient"/>. Production registration
/// (<see cref="McpRemoteMeshClientFactory"/>) creates an MCP-over-HTTP client
/// against the given remote portal. Tests register a stub implementation
/// that returns a recording fake — verifies the mirror handler talks to the
/// right tools without standing up a real MCP server.
/// </summary>
public interface IRemoteMeshClientFactory
{
    /// <summary>Open an <see cref="IRemoteMeshClient"/> against the named remote portal + token.</summary>
    IRemoteMeshClient Create(string remoteBaseUrl, string remoteToken);
}

/// <summary>
/// Thin abstraction over a remote MeshWeaver portal's MCP tool surface — exposes
/// just the operations <see cref="HttpMeshStorageAdapter"/> needs (Get / Create /
/// Update / Delete / Search). Production implementation
/// (<see cref="McpRemoteMeshClient"/>) wraps <c>ModelContextProtocol.Client.McpClient</c>
/// and forwards each call as a <c>CallToolAsync(...)</c> request. Tests inject a stub.
///
/// <para><b>Surface is <see cref="IObservable{T}"/>, not <see cref="System.Threading.Tasks.Task{TResult}"/></b>
/// — per the codebase convention (Doc/Architecture/AsynchronousCalls.md). Each
/// call emits exactly one value (or completes with no value for the void-shaped
/// ops) and then completes. Subscribers compose with <c>.Select</c> /
/// <c>.SelectMany</c>; the only Task bridge is at the storage-adapter boundary
/// (where <see cref="MeshWeaver.Mesh.Services.IStorageAdapter"/> still has Task-based methods).</para>
///
/// <para>Why an interface instead of using <c>McpClient</c> directly: tests need to
/// verify request shape (tool name, arguments, headers) and exercise error paths
/// (404 on missing nodes, 401 on bad token) without standing up an MCP HTTP server.
/// A mockable boundary at this layer keeps unit tests deterministic and fast.</para>
/// </summary>
public interface IRemoteMeshClient
{
    /// <summary>Read a single node by path. Emits null if not found.</summary>
    IObservable<MeshNode?> Get(string path);

    /// <summary>Create a new node. Errors if it already exists.</summary>
    IObservable<System.Reactive.Unit> Create(MeshNode node);

    /// <summary>
    /// Full-replace an existing node. Errors if it doesn't exist; the adapter
    /// orchestrates create-or-update upsert semantics by combining
    /// <see cref="Get"/> + <see cref="Create"/> / this.
    /// </summary>
    IObservable<System.Reactive.Unit> Update(MeshNode node);

    /// <summary>Delete a node. No-op (or error) if it doesn't exist; impl-defined.</summary>
    IObservable<System.Reactive.Unit> Delete(string path);

    /// <summary>
    /// Run a MeshWeaver search query (GitHub-style syntax) and emit the raw
    /// list of matched node paths. Caller assembles the typed responses via
    /// <see cref="Get"/> as needed. The portal caps results at 50; pagination
    /// is left to the adapter when it matters.
    /// </summary>
    IObservable<IReadOnlyList<string>> SearchPaths(string query);
}
