using MeshWeaver.Data;
using MeshWeaver.Graph.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Registers the two NodeTypes a durable stream is made of (Doc/Architecture/DurableStreams):
/// <see cref="DurableStreamNodeTypes.Stream"/> — the stream node, whose hub orders and leases the
/// stream — and <see cref="DurableStreamNodeTypes.Item"/> — one write-once node per item, stored in
/// the owner partition's <c>durable_stream_items</c> table. Both are satellites of the stream's
/// owner: excluded from search/create/context, and readable exactly by those who may read the owner
/// (<see cref="SatelliteAccessRule"/>).
/// </summary>
public static class DurableStreamNodeTypeRegistration
{
    /// <summary>Registers both NodeTypes and their satellite access rules.</summary>
    /// <typeparam name="TBuilder">The mesh builder type.</typeparam>
    /// <param name="builder">The mesh builder.</param>
    /// <returns>The same builder.</returns>
    public static TBuilder AddDurableStreamTypes<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(StreamNodeType(), ItemNodeType());
        // The content discriminators, so a reader on ANY hub types a stream or item node (#2729).
        builder.ConfigureHub(config => config
            .WithType<DurableStreamState>(nameof(DurableStreamState))
            .WithType<DurableStreamItemContent>(nameof(DurableStreamItemContent)));
        builder.AddAutocompleteExcludedTypes(DurableStreamNodeTypes.Stream, DurableStreamNodeTypes.Item);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<INodeTypeAccessRule>(sp =>
                new SatelliteAccessRule(DurableStreamNodeTypes.Stream, sp.GetRequiredService<IMessageHub>()));
            services.AddSingleton<INodeTypeAccessRule>(sp =>
                new SatelliteAccessRule(DurableStreamNodeTypes.Item, sp.GetRequiredService<IMessageHub>()));
            return services;
        });
        return builder;
    }

    /// <summary>The stream node: its hub serialises appends, claims, acknowledgements and releases.</summary>
    /// <returns>The NodeType node.</returns>
    public static MeshNode StreamNodeType() => new(DurableStreamNodeTypes.Stream)
    {
        Name = "Durable Stream",
        IsSatelliteType = true,
        ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
        Content = new NodeTypeDefinition(),
        HubConfiguration = config => config
            .AddMeshDataSource(source => source.WithContentType<DurableStreamState>())
            .ConfigureDurableStreamHub(),
    };

    /// <summary>An item node: write-once, in the owner partition's item table.</summary>
    /// <returns>The NodeType node.</returns>
    public static MeshNode ItemNodeType() => new(DurableStreamNodeTypes.Item)
    {
        Name = "Durable Stream Item",
        IsSatelliteType = true,
        ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
        Content = new NodeTypeDefinition { StorageTable = DurableStreamPaths.ItemTable },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source.WithContentType<DurableStreamItemContent>()),
    };
}
