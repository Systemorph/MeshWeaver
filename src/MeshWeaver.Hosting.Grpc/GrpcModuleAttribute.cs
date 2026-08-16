using MeshWeaver.Hosting.AspNetCore;
using MeshWeaver.Mesh;
using Microsoft.AspNetCore.Routing;

[assembly: MeshWeaver.Hosting.Grpc.GrpcMeshModule]
[assembly: MeshWeaver.Hosting.Grpc.GrpcModule]

namespace MeshWeaver.Hosting.Grpc;

/// <summary>
/// The mesh half of the gRPC transport module: listing <c>MeshWeaver.Hosting.Grpc.dll</c> under
/// <c>Modules:Assemblies</c> folds <see cref="GrpcHostingExtensions.AddGrpcHub(MeshBuilder)"/>
/// over the builder — the gRPC mesh-transport services (<see cref="GrpcConnectionRegistry"/>,
/// <c>IParticipantPresence</c>, <see cref="GrpcOptions"/> off <c>Grpc:*</c>) plus the
/// <c>py</c>/<c>node</c> foreign-participant address types declared stream-routed. The same
/// extension a fixture or bespoke host (e.g. <c>Memex.LocalMesh</c>) calls explicitly — the two
/// lanes must never drift.
///
/// <para>🚨 <b>This module is DEFAULT-ON in every deployment.</b> The endpoint it registers is
/// not only the foreign-participant (<c>py/*</c>, <c>node/*</c>) transport — the React GUI's
/// browser data plane rides the SAME <c>meshweaver.v1.Mesh</c> service (the gRPC-web
/// <c>Connect</c>+<c>Deliver</c> split at the origin root; <c>clients/portal-next</c> +
/// <c>clients/portal</c>). Delisting it is only for deployments with NO React GUI and NO foreign
/// participants; everywhere else a delist silently breaks the React frontend's live connection.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class GrpcMeshModuleAttribute : MeshNodeProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<Func<MeshBuilder, MeshBuilder>> BuilderConfigurations =>
        [GrpcHostingExtensions.AddGrpcHub];
}

/// <summary>
/// The endpoint half of the gRPC transport module (the second consumer of the
/// endpoint-contribution hook, after <c>MeshWeaver.Social</c> — design #1655): the
/// <c>meshweaver.v1.Mesh</c> service maps through the host's <c>app.MapMeshModuleEndpoints()</c>,
/// grpc-web enabled and — deliberately — <c>AllowAnonymous</c>: the transport authenticates each
/// connection itself (see the why-comment on
/// <see cref="GrpcHostingExtensions.MapMeshWeaverGrpc"/>). Delisting the module removes the
/// routes wholesale — a 404, not a compiled optional-service 503.
///
/// <para>The gRPC-web MIDDLEWARE is NOT part of this attribute: middleware cannot ride the
/// endpoint hook (it must run in the pipeline between <c>UseRouting</c> and the endpoint maps),
/// so the host keeps one compiled line —
/// <see cref="GrpcHostingExtensions.UseMeshWeaverGrpcWebWhenInstalled"/> — which self-gates on
/// this assembly being listed, keeping the module listing the single on/off switch.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class GrpcModuleAttribute : MeshEndpointProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<Action<IEndpointRouteBuilder>> EndpointConfigurations =>
        [endpoints => endpoints.MapMeshWeaverGrpc()];
}
