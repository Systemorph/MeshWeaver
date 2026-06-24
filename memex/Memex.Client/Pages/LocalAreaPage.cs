using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Maui;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Client.Pages;

/// <summary>
/// Renders a REAL local-mesh node area as NATIVE MAUI (no Blazor, no WebView) via the MeshWeaver.Maui view
/// pack — the node's <c>Overview</c> area (registered by <c>AddGraph</c>) served at the node's address.
/// Proof that the native pack renders actual mesh content, not just a hand-registered demo area.
/// </summary>
public class NodeAreaPage : ContentPage
{
    public NodeAreaPage(IMessageHub hub, string nodePath, string area = "Overview", string? title = null)
    {
        Title = title ?? nodePath;
        var workspace = hub.GetWorkspace();
        var renderer = hub.ServiceProvider.GetRequiredService<IMauiControlRenderer>();
        Content = new ScrollView
        {
            Padding = 16,
            // address = the node PATH (implicit string→Address); reference = the AddGraph area name.
            Content = new LayoutAreaView(workspace, nodePath, new LayoutAreaReference(area), renderer),
        };
    }
}

/// <summary>
/// The Home page — renders the LOCAL "home" portal area (intro + a live DataGrid of the mesh's real nodes,
/// see <c>LocalPortal.Home</c>) natively via the MAUI view pack. Served by the local hub, so it's the
/// reliable area-render path; <see cref="NodeAreaPage"/> renders an individual node's remote area on tap.
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
