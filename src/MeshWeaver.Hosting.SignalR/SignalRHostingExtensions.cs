using MeshWeaver.Mesh;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.SignalR;

public static class SignalRHostingExtensions
{
    public static MeshBuilder AddSignalRHubs(this MeshBuilder builder)
        => builder.ConfigureServices(services => services.AddSignalRHubs());
    public static IServiceCollection AddSignalRHubs(this IServiceCollection services)
    {
        return services.AddSignalRHub().AddKernelHub();
    }
    public static IServiceCollection AddSignalRHub(this IServiceCollection services)
    {
        services.AddSignalR();
        services.AddSingleton<SignalRConnectionHub>();
        services.AddMemoryCache();
        return services;
    }
    public static IServiceCollection AddKernelHub(this IServiceCollection services)
    {
        services.AddSignalR();
        services.AddSingleton<KernelHub>();
        services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        services.AddSingleton<IKernelService, KernelService>();
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
