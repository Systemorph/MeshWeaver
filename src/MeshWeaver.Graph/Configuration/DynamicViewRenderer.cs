using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Extension methods for adding dynamic view rendering support to layout configuration.
/// </summary>
public static class DynamicViewRendererExtensions
{
    /// <summary>
    /// Adds a dynamic view renderer that delegates to views registered in IDynamicViewRegistry.
    /// This should be called during hub configuration to enable runtime-compiled views.
    /// </summary>
    public static LayoutDefinition AddDynamicViewRenderer(this LayoutDefinition layout)
    {
        // Add a view with a context filter that checks the registry at runtime
        return layout.WithView<UiControl>(
            ctx =>
            {
                // This filter runs at render time, so we can check the registry
                var registry = layout.Hub.ServiceProvider.GetService<IDynamicViewRegistry>();
                return registry?.HasView(ctx.Area) == true;
            },
            (host, ctx) =>
            {
                var registry = host.Hub.ServiceProvider.GetService<IDynamicViewRegistry>();
                var view = registry?.GetView(ctx.Area);
                if (view == null)
                    return Controls.Html("<div>View not found</div>");

                return view(host, ctx);
            });
    }

    /// <summary>
    /// Adds dynamic view support to the message hub configuration.
    /// Registers the necessary services and adds the dynamic view renderer.
    /// </summary>
    public static MessageHubConfiguration AddDynamicViews(this MessageHubConfiguration config)
    {
        return config
            .WithServices(services =>
            {
                services.AddSingleton<IDynamicViewRegistry, DynamicViewRegistry>();
                return services;
            })
            .AddLayout(layout => layout
                .AddDynamicViewRenderer());
    }
}
