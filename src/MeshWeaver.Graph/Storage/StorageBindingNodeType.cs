using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Storage;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeshWeaver.Graph.Storage;

/// <summary>
/// The <c>StorageBinding</c> NodeType: one partition's override of WHERE one purpose is stored,
/// at <c>{partition}/_Storage/{id}</c> in the partition's own <c>storage</c> table — the instance's
/// global bindings are the <c>Admin</c> partition's. Registers the type, its access rule (Update on
/// the partition root for everything), the watcher that validates and creates on the binding's own
/// hub, and the mesh's <see cref="IStorageBindingResolver"/>. See <c>Doc/Architecture/StorageBindings</c>.
/// </summary>
public static class StorageBindingNodeType
{
    /// <summary>The NodeType value.</summary>
    public const string NodeType = StorageBindingPaths.NodeType;

    /// <summary>Registers the type, its access rule and the resolver.</summary>
    /// <typeparam name="TBuilder">The mesh builder type.</typeparam>
    /// <param name="builder">The mesh builder.</param>
    public static TBuilder AddStorageBindingType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<INodeTypeAccessRule>(sp => new StorageBindingAccessRule(sp.GetRequiredService<IMessageHub>()));
            services.TryAddSingleton<IStorageBindingResolver>(sp => new StorageBindingResolver(sp.GetRequiredService<IMessageHub>()));
            return services;
        });
        return builder;
    }

    /// <summary>The NodeType definition node.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Storage binding",
        Icon = "/static/NodeTypeIcons/settings.svg",
        // Created from the Storage settings section, never from the generic "create" menu; never a
        // search hit or a content listing entry.
        ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
        HubConfiguration = config => config
            .ApplyNodeHubContributions(NodeType)
            .AddMeshDataSource(source => source.WithContentType<StorageBinding>())
            .AddDefaultLayoutAreas()
            .AddStorageBindingViews()
            .WithInitialization(StorageBindingValidation.Install)
    };
}
