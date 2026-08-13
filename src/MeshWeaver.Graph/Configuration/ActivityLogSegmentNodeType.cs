using MeshWeaver.Data;
using MeshWeaver.Graph.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Registers the <c>ActivityLogSegment</c> node type — a sealed slice of an activity's transcript,
/// written once at <c>{activityPath}/_Log/{index:D6}</c> when the messages scroll out of the
/// activity's bounded window (see <see cref="ActivityLogAppender"/>).
///
/// <para>System-generated satellite, exactly like <see cref="ActivityNodeType"/>: excluded from search
/// and create contexts, access delegated to the MainNode via <c>SatelliteAccessRule</c>. It carries no
/// views — a segment is read as part of its activity's log, never as a page of its own.</para>
/// </summary>
public static class ActivityLogSegmentNodeType
{
    /// <summary>The node-type identifier string for activity log segment nodes.</summary>
    public const string NodeType = ActivityLogAppender.SegmentNodeType;

    /// <summary>
    /// Registers the node type on the mesh builder: adds the MeshNode definition, excludes it from
    /// autocomplete, and wires the satellite access rule that delegates access to the parent (MainNode).
    /// </summary>
    /// <typeparam name="TBuilder">The mesh builder type.</typeparam>
    /// <param name="builder">The mesh builder to configure.</param>
    /// <returns>The same builder, to allow fluent chaining.</returns>
    public static TBuilder AddActivityLogSegmentType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<INodeTypeAccessRule>(sp =>
                new SatelliteAccessRule(NodeType, sp.GetRequiredService<IMessageHub>()));
            return services;
        });
        return builder;
    }

    /// <summary>
    /// Builds the MeshNode definition for the activity log segment node type.
    /// </summary>
    /// <returns>The ActivityLogSegment MeshNode definition.</returns>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Activity Log Segment",
        IsSatelliteType = true,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<ActivityLogSegment>())
    };
}
