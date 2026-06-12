using System.Text.Json;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Capability surface: a query backend that supports vector-similarity search
/// using stored embeddings. Resolved at DI lookup time —
/// <c>sp.GetService&lt;IVectorSearchProvider&gt;()</c> returns null when no
/// vector-capable backend is registered, in which case callers fall back to
/// the text/ILIKE search path on <see cref="IMeshService"/>.
///
/// <para>The provider owns its embedding pipeline: it generates the query
/// embedding using the SAME embedding model that produced the column at
/// write time (a mismatch would silently return irrelevant rows). Callers
/// pass the raw query <em>text</em>, not a vector — only the provider knows
/// which embedding service / model to call.</para>
///
/// <para>Wired into <c>SearchHub</c> (the portal search box) and the
/// <c>Search</c> MCP / agent tool via <c>MeshOperations.Search</c>: when this
/// service is registered AND the query has bare-text content (not a purely
/// structured field-filter), the search routes through here. Structured-only
/// queries continue to use the regular <see cref="IMeshQueryCore.Query"/>
/// path.</para>
/// </summary>
public interface IVectorSearchProvider
{
    /// <summary>
    /// Returns the top-K rows by cosine similarity against the query text's
    /// embedding as a single snapshot emission. Optionally filters by
    /// namespace prefix and/or by user (caller-side access control honoured
    /// by the backend's WHERE clause). Emits an empty collection if the query
    /// is whitespace-only or no embedding can be generated for it.
    ///
    /// <para>Reactive surface — no <c>IAsyncEnumerable</c>, no <c>Task</c>:
    /// the provider bridges its async I/O leaf (embedding round-trip + the
    /// vector SQL) through its <c>IIoPool</c>, so the pump never runs on the
    /// subscriber's scheduler. Cancellation = subscription disposal.</para>
    /// </summary>
    IObservable<IReadOnlyCollection<MeshNode>> Search(
        string queryText,
        JsonSerializerOptions options,
        string? namespacePath = null,
        string? userId = null,
        int topK = 10);
}
