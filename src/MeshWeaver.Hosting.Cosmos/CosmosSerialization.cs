using System.Text.Json;
using Microsoft.Azure.Cosmos;

namespace MeshWeaver.Hosting.Cosmos;

/// <summary>
/// The ONE definition of how MeshWeaver documents are serialized into Cosmos. Every
/// <see cref="CosmosClient"/> that talks to a MeshWeaver container — production factory, Aspire
/// wiring, and the test fixture alike — must be built with <see cref="CreateClientOptions"/>, so
/// the stored shape cannot drift from the shape the query layer reads.
///
/// <para>
/// 🚨 camelCase is NOT cosmetic — it is the storage contract, and two hard requirements depend
/// on it:
/// </para>
/// <list type="number">
///   <item><b>Cosmos requires a lowercase <c>id</c> on every document.</b> <c>MeshNode.Id</c> is
///     PascalCase in C#, so without a camelCase policy the SDK emits <c>"Id"</c> and Cosmos
///     rejects the write outright with <c>400 — "Document does not contain an id field"</c>.
///     That is not an edge case: it means NO node can be written at all.</item>
///   <item><b><see cref="CosmosSqlGenerator"/> emits camelCase field references</b>
///     (<c>c.nodeType</c>, <c>c.namespace</c>, <c>c.lastModified</c>), and the containers are
///     partitioned on <c>/namespace</c>. A PascalCase document would not match any generated
///     predicate even if it could be stored.</item>
/// </list>
///
/// <para>
/// Both invariants are covered by <c>EmulatorSmokeTests</c> against the emulator — the write /
/// read / query round-trip fails loudly if a client is ever constructed without these options.
/// </para>
/// </summary>
public static class CosmosSerialization
{
    /// <summary>
    /// The serializer options defining the stored document shape. Kept separate from
    /// <see cref="CreateClientOptions"/> so callers that need to serialize a document themselves
    /// (diagnostics, tooling) produce byte-identical JSON.
    /// </summary>
    public static JsonSerializerOptions CreateSerializerOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Builds <see cref="CosmosClientOptions"/> carrying the MeshWeaver storage contract.
    /// </summary>
    /// <param name="configure">Optional hook for caller-specific settings (connection mode,
    /// timeouts, endpoint limiting) applied on top of the contract.</param>
    public static CosmosClientOptions CreateClientOptions(Action<CosmosClientOptions>? configure = null)
    {
        var options = new CosmosClientOptions
        {
            // A PROPERTY in SDK 3.x (not a method) — assigning it replaces the SDK's default
            // serializer wholesale, which is exactly the intent.
            UseSystemTextJsonSerializerWithOptions = CreateSerializerOptions(),
        };
        configure?.Invoke(options);
        return options;
    }

    /// <summary>
    /// Creates a <see cref="CosmosClient"/> against <paramref name="connectionString"/> with the
    /// storage contract applied. Prefer this over <c>new CosmosClient(connectionString)</c>.
    /// </summary>
    public static CosmosClient CreateClient(
        string connectionString, Action<CosmosClientOptions>? configure = null)
        => new(connectionString, CreateClientOptions(configure));
}
