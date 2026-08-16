using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
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
        // Expose the registry's live address claims as a presence check so foreign-language Code
        // runs can fail fast when no worker is connected (a post to a stream-routed address with no
        // subscriber is silently absorbed — no DeliveryFailure — so the run would otherwise hang).
        services.AddSingleton<IParticipantPresence>(sp => sp.GetRequiredService<GrpcConnectionRegistry>());
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
        // AllowAnonymous is today's semantics made EXPLICIT, not a widening: the transport
        // authenticates every connection ITSELF — MeshGrpcService validates the Bearer API token
        // carried in gRPC call metadata (registry.Authenticate), a call on the loopback
        // GrpcOptions.TrustedPort authenticates by reachability, and a definitively invalid token
        // still connects as Anonymous whose writes are cleanly RLS-denied. ASP.NET-level
        // authorization here would break both non-cookie callers (foreign py/node gates present
        // mw_ API tokens no host auth scheme accepts) and the credential-less trusted-port path.
        // The explicit opt-out matters because the module lane (GrpcModuleAttribute) maps this
        // inside MapMeshModuleEndpoints' authenticated-by-default group.
        endpoints.MapGrpcService<MeshGrpcService>().EnableGrpcWeb().AllowAnonymous();
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

    /// <summary>
    /// The module-lane form of <see cref="UseMeshWeaverGrpcWeb"/>: applies the gRPC-web middleware
    /// only when this assembly is INSTALLED as a module (<c>Modules:Assemblies</c> →
    /// <see cref="InstalledModuleAssembly"/>). Middleware cannot ride
    /// <c>MeshEndpointProviderAttribute</c> — it must run in the pipeline between
    /// <c>UseRouting</c> and the endpoint maps — so the host keeps this ONE compiled line and the
    /// module listing stays the single on/off switch: delist the module and both the routes
    /// (via <c>MapMeshModuleEndpoints</c>) and this middleware drop out together.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The same <paramref name="app"/> for chaining.</returns>
    public static IApplicationBuilder UseMeshWeaverGrpcWebWhenInstalled(this IApplicationBuilder app)
    {
        // Match by assembly NAME, not instance: the module list resolves through
        // Assembly.LoadFrom, which normally dedupes onto the compiled reference the host already
        // loaded (double-ship), but a modules/-folder copy must gate identically.
        var moduleName = typeof(GrpcHostingExtensions).Assembly.GetName().Name;
        var installed = app.ApplicationServices.GetServices<InstalledModuleAssembly>()
            .Any(m => m.Assembly.GetName().Name == moduleName);
        // LOUD either way: gRPC silently off is indistinguishable from a broken transport —
        // every "React GUI / py client cannot connect" triage starts by grepping for this line.
        var logger = app.ApplicationServices.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()
            ?.CreateLogger(typeof(GrpcHostingExtensions).FullName!);
        if (installed)
        {
            if (logger is not null)
                Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(logger,
                    "gRPC transport ENABLED: {Module} is installed — gRPC-web middleware active, mesh gRPC endpoints map via the module hook", moduleName);
            app.UseMeshWeaverGrpcWeb();
        }
        else if (logger is not null)
            Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(logger,
                "gRPC transport OFF: {Module} is NOT in this deployment's module set (Modules:Assemblies) — the React GUI and py/node participants cannot connect. List the DLL to enable.", moduleName);
        return app;
    }
}
