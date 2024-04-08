using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace OpenSmc.Application.SignalR;

public static class SignalRExtensions
{
    public const string DefaultSignalREndpoint = "/signalR/application";

    public static IServiceCollection ConfigureApplicationSignalR(this IServiceCollection services)
    {
        services.AddSignalR(o =>
            {
                o.EnableDetailedErrors = true; // TODO: False for Prod environment (2021/05/14, Alexander Yolokhov)
                o.MaximumReceiveMessageSize = 400000; // TODO: see what's recommended size (2021/12/07, Alexander Kravets)
            });

        return services;
    }

    public static IApplicationBuilder UseApplicationSignalR(this IApplicationBuilder app)
    {
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapHub<ApplicationHub>(DefaultSignalREndpoint);
        });

        return app;
    }
}
