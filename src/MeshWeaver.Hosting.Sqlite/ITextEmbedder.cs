namespace MeshWeaver.Hosting.Sqlite;

/// <summary>
/// Produces a dense embedding vector for a piece of text — the SQLite/MAUI-local counterpart to
/// the Postgres embedding provider. Deliberately self-contained in this assembly so the on-device
/// backend never takes a dependency on the Postgres hosting package (Npgsql, Azure SDK, …).
///
/// <para><see cref="EmbedAsync"/> is an async I/O leaf (an HTTP round-trip to a model server). It
/// is ALWAYS invoked from inside an <c>IIoPool</c> — never on a hub turn or a Blazor circuit — so
/// the actor-model schedulers are never blocked. Implementations must be best-effort: a transient
/// failure should surface to the caller (which stores a NULL embedding and logs), never wedge a
/// write.</para>
/// </summary>
public interface ITextEmbedder
{
    /// <summary>Embeds <paramref name="text"/>, or returns <see langword="null"/> when empty.</summary>
    Task<float[]?> EmbedAsync(string text, CancellationToken ct = default);

    /// <summary>Vector dimensionality this embedder produces.</summary>
    int Dimensions { get; }
}
