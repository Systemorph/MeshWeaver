using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Maui;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Client.Pages;

/// <summary>
/// Renders a LOCAL mesh layout area as NATIVE MAUI (no Blazor, no WebView) via the MeshWeaver.Maui view
/// pack — proof of the native portal rendering. The renderer + view registry live in the hub's service
/// provider (registered by <c>AddMaui</c>); the workspace serves the area stream.
/// </summary>
public sealed class LocalAreaPage : ContentPage
{
    public LocalAreaPage(IMessageHub hub)
    {
        Title = "Home";
        var workspace = hub.GetWorkspace();
        var renderer = hub.ServiceProvider.GetRequiredService<IMauiControlRenderer>();
        Content = new ScrollView
        {
            Padding = 16,
            Content = new LayoutAreaView(workspace, new LayoutAreaReference("home"), renderer),
        };
    }
}
