using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Blazor.Components;
using MeshWeaver.Blazor.Components.Monaco;
using MeshWeaver.Blazor.FileExplorer;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Catalog;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Layout.Views;
using MeshWeaver.Markdown;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static MeshWeaver.Layout.Client.LayoutClientConfiguration;

[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Blazor")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Blazor.Test")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Monolith.Test")]
namespace MeshWeaver.Blazor;

/// <summary>
/// Static registry that maps <c>IUiControl</c> instances to their Blazor component view types
/// and registers all framework-supplied Blazor views on a <c>MessageHubConfiguration</c>.
/// </summary>
public static class BlazorViewRegistry
{
    // Public so non-Server hosts (e.g. the MAUI in-process portal) can wire the standard Blazor view
    // registry on their hub directly. The Server path reaches it via MeshBuilder.AddBlazor; a Hybrid
    // host that can't reference MeshWeaver.Hosting.Blazor (Microsoft.AspNetCore.App framework ref) calls
    // this on its hub config instead.
    /// <summary>
    /// Wires the standard Blazor view registry, data layer, layout client, and type registrations
    /// onto <paramref name="config"/>. Non-Server hosts (e.g. MAUI hybrid) call this directly when
    /// they cannot reference <c>MeshWeaver.Hosting.Blazor</c>.
    /// </summary>
    /// <param name="config">The hub configuration to extend.</param>
    /// <param name="configuration">Optional callback to further customize the <c>LayoutClientConfiguration</c>.</param>
    /// <returns>The extended <paramref name="config"/>.</returns>
    public static MessageHubConfiguration AddBlazor(
        this MessageHubConfiguration config,
        Func<LayoutClientConfiguration, LayoutClientConfiguration>? configuration = null
    ) => config
        .AddData()
        .AddLayoutClient(c =>
            (configuration ?? (x => x))
            // The DEFAULT control views live in the MeshWeaver.Blazor.Views pack
            // (AddDefaultViews / the ViewsViewPackModule module attribute) — the base pack wires
            // only the machinery and the escaped-HTML FALLBACK slot, which is consulted after
            // every registered map — view packs included — has declined. A host without the Views
            // pack renders every control through that fallback, which is why the portals list the
            // DLL under Modules:Required.
            .Invoke(c.WithFallbackView((i, s, a) => FallbackHtml(i, s, a))))
        .AddMeshTypes()
        .AddMarkdownTypes()
        .AddMarkdownExportTypes()
    ;

    /// <summary>
    /// Registers the markdown-export request/response + dialog control on the Blazor client's
    /// type registry so polymorphic UiControl deserialization can resolve the $type discriminator.
    /// </summary>
    private static MessageHubConfiguration AddMarkdownExportTypes(this MessageHubConfiguration config)
    {
        config.TypeRegistry.AddMarkdownExportTypes();
        return config;
    }

    /// <summary>
    /// Registers Markdown-related types for JSON serialization.
    /// </summary>
    private static MessageHubConfiguration AddMarkdownTypes(this MessageHubConfiguration config)
    {
        config.TypeRegistry.WithType(typeof(MarkdownContent), nameof(MarkdownContent));
        return config;
    }
    private static ViewDescriptor FallbackHtml(
        object instance,
        ISynchronizationStream<JsonElement>? stream,
        string area
    )
    {
        var output = Controls.Html(System.Net.WebUtility.HtmlEncode(instance.ToString() ?? string.Empty));
        return new ViewDescriptor(
            typeof(HtmlView),
            ImmutableDictionary<string, object?>
                .Empty.Add(ViewModel, output)
                .Add(nameof(Stream), stream)
                .Add(nameof(Area), area)
        );
    }
}
