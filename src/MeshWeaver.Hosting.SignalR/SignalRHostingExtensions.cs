using MeshWeaver.Mesh;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.SignalR;

/// <summary>
/// Server-side wiring for the SignalR mesh transport. Register the hub services on the mesh
/// (<see cref="AddSignalRHub(MeshBuilder)"/>) and map the endpoint on the app
/// (<see cref="MapMeshWeaverSignalRHubs"/>) — the counterpart to <c>UseSignalRClient</c> on a participant.
/// </summary>
public static class SignalRHostingExtensions
{
    public static MeshBuilder AddSignalRHub(this MeshBuilder builder)
        => builder.ConfigureServices(services => services.AddSignalRHub());

    public static IServiceCollection AddSignalRHub(this IServiceCollection services)
    {
        services.AddSignalR();
        services.AddSingleton<SignalRConnectionRegistry>();
        return services;
    }

    public static IApplicationBuilder MapMeshWeaverSignalRHubs(this IApplicationBuilder app)
    {
        app.UseEndpoints(endpoints => endpoints.MapHub<SignalRConnectionHub>($"/{SignalRConnectionHub.EndPoint}"));
        return app;
    }
}
