using MeshWeaver.Blazor;
using MeshWeaver.Layout.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Hosting.Blazor;

public  static class BlazorHostingExtensions
{
    public static TBuilder AddBlazor<TBuilder>(this TBuilder builder, Func<LayoutClientConfiguration, LayoutClientConfiguration> clientConfig = null)
    where TBuilder:MeshWeaverApplicationBuilder<TBuilder>
    {
        builder.Host.Services.AddRazorPages();
        builder.Host.Services.AddServerSideBlazor();
        builder.Host.Services.AddFluentUIComponents();

        return builder.ConfigureHub(hub => hub.AddBlazor(clientConfig));

    }
}
