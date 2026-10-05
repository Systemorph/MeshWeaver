using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeshWeaver.Graph.ControlLane;

/// <summary>Registration of the control→instance lane (Doc/Architecture/ControlLane).</summary>
public static class ControlLaneExtensions
{
    /// <summary>The node type of the target's ledger record.</summary>
    public const string RecordNodeType = "ControlLaneRecord";

    /// <summary>
    /// Registers both halves of the lane on this mesh: the target's receiver, its ledger record
    /// type, the platform's operations (<see cref="ControlLaneOperation.Recycle"/>,
    /// <see cref="ControlLaneOperation.DeleteSpace"/>, <see cref="ControlLaneOperation.Reboot"/>), the HTTP report sink and the HTTP transport.
    /// Registering it arms NOTHING: the receiver refuses every delivery until
    /// <see cref="ControlLaneKeys.TargetKey"/> and <see cref="ControlLaneKeys.DeploymentKey"/> are
    /// set, and the control half signs only with a deployment's own key. A module adds an operation
    /// with <c>services.AddSingleton&lt;IControlLaneOperation, MyOperation&gt;()</c>; a sink or a
    /// transport registered BEFORE this call wins (TryAdd).
    /// </summary>
    public static TBuilder AddControlLane<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.WithMeshType<ControlLaneRecord>();
        builder.ConfigureServices(services =>
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IControlLaneOperation, RecycleOperation>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IControlLaneOperation, DeleteSpaceOperation>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IControlLaneOperation, RebootOperation>());
            services.TryAddSingleton<IControlLaneReportSink, HttpControlLaneReportSink>();
            services.TryAddSingleton<IControlLaneTransport, HttpControlLaneTransport>();
            services.TryAddSingleton<ControlLaneReceiver>();
            return services;
        });
        return builder;
    }

    /// <summary>The NodeType definition of the ledger record.</summary>
    public static MeshNode CreateMeshNode() => new(RecordNodeType)
    {
        Name = "Control Lane Request",
        NodeType = "NodeType",
        Icon = "/static/NodeTypeIcons/satellite.svg",
        HubConfiguration = config => config
            .AddDefaultLayoutAreas()
            .AddMeshDataSource(source => source.WithContentType<ControlLaneRecord>()),
    };
}
