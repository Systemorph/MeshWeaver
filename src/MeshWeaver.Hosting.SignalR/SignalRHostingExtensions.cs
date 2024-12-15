using MeshWeaver.Mesh;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.SignalR;

public static class SignalRHostingExtensions
{
    public static MeshBuilder AddSignalRConnections(this MeshBuilder builder)
        => builder.ConfigureServices(services => services.AddSignalRConnections());
    public static IServiceCollection AddSignalRConnections(this IServiceCollection services)
    {
        return services.AddSignalRHub().AddKernelHub();
    }
    public static IServiceCollection AddSignalRHub(this IServiceCollection services)
    {
        services.AddSignalR();
        services.AddSingleton<KernelHub>();
        services.AddSingleton<SignalRConnectionHub>();
        return services;
    }
    public static IServiceCollection AddKernelHub(this IServiceCollection services)
    {
        services.AddSignalR();
        services.AddSingleton<KernelHub>();
        services.AddSingleton<SignalRConnectionHub>();
        return services;
    }

    public static IApplicationBuilder MapMeshWeaverHubs(this IApplicationBuilder app)
    {
        app.UseEndpoints(endpoints =>
            {
                endpoints.MapHub<SignalRConnectionHub>($"/{SignalRConnectionHub.EndPoint}");
                endpoints.MapHub<KernelHub>($"/{KernelHub.EndPoint}");

            }
        );
        return app;
    }
}
