using MeshWeaver.Mesh;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.Grpc;

/// <summary>
/// Server-side wiring for the gRPC mesh transport. Register the services on the mesh
/// (<see cref="AddGrpcHub(MeshBuilder)"/>) and map the endpoint on the app
/// (<see cref="MapMeshWeaverGrpc"/>) — the counterpart to a foreign-language participant opening the
/// <c>meshweaver.v1.Mesh/Open</c> bidi stream. Mirrors <c>SignalRHostingExtensions</c>.
/// </summary>
public static class GrpcHostingExtensions
{
    /// <summary>Address type for a Python participant (<c>py/&lt;id&gt;</c>).</summary>
    public const string PythonAddressType = "py";

    /// <summary>Address type for a Bun/Node participant (<c>node/&lt;id&gt;</c>).</summary>
    public const string NodeAddressType = "node";

    /// <summary>
    /// Registers the gRPC mesh-transport services on the mesh AND declares the foreign-participant
    /// address types (<see cref="PythonAddressType"/>, <see cref="NodeAddressType"/>) as
    /// stream-routed — so a participant's address routes via its <c>Open</c> stream (like the
    /// <c>portal</c>/<c>client</c> types) instead of being resolved as a mesh node. Without this a
    /// reply addressed to <c>py/…</c> is treated as a node lookup and silently dropped.
    /// </summary>
    /// <param name="builder">The mesh builder to configure.</param>
    /// <returns>The same <paramref name="builder"/> for chaining.</returns>
    public static MeshBuilder AddGrpcHub(this MeshBuilder builder)
        => builder
            .AddStreamRoutedAddressType(PythonAddressType)
            .AddStreamRoutedAddressType(NodeAddressType)
            .ConfigureServices(services => services.AddGrpcHub());

    /// <summary>Registers gRPC and the singleton <see cref="GrpcConnectionRegistry"/> in the service collection.</summary>
    /// <param name="services">The service collection to add the gRPC mesh-transport services to.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddGrpcHub(this IServiceCollection services)
    {
        services.AddGrpc();
        services.AddSingleton<GrpcConnectionRegistry>();
        // Grpc:TrustedPort — the loopback endpoint co-deployed gates authenticate on (GrpcOptions).
        services.AddOptions<GrpcOptions>().BindConfiguration(GrpcOptions.SectionName);
        return services;
    }

    /// <summary>
    /// Maps the <see cref="MeshGrpcService"/> endpoint and enables gRPC-web on it (so browsers / React
    /// Native can use the <c>Connect</c>+<c>Deliver</c> split). Pair with <see cref="UseMeshWeaverGrpcWeb"/>
    /// in the request pipeline.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder to map the gRPC service on.</param>
    /// <returns>The same <paramref name="endpoints"/> for chaining.</returns>
    public static IEndpointRouteBuilder MapMeshWeaverGrpc(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGrpcService<MeshGrpcService>().EnableGrpcWeb();
        return endpoints;
    }

    /// <summary>Adds the gRPC-web middleware so the <c>Connect</c>/<c>Deliver</c> split is reachable from
    /// browsers and React Native (which can't do bidi/HTTP-2 gRPC). Call before mapping endpoints; configure
    /// CORS separately for cross-origin browser callers.</summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The same <paramref name="app"/> for chaining.</returns>
    public static IApplicationBuilder UseMeshWeaverGrpcWeb(this IApplicationBuilder app)
    {
        app.UseGrpcWeb();
        return app;
    }
}
