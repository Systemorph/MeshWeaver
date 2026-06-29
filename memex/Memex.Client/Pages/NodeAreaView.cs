using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Maui;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Client.Pages;

/// <summary>
/// Renders a mesh node's layout area (default <c>Overview</c>) natively via the MeshWeaver.Maui view pack —
/// the node's area is served at the node's address, so this uses the LayoutAreaView remote ctor
/// (<c>GetRemoteStream&lt;JsonElement, LayoutAreaReference&gt;(path, ref)</c>). Pushed into the shell's
/// content frame when a dashboard card is tapped.
/// </summary>
public sealed class NodeAreaView : ContentView
{
    public NodeAreaView(IMessageHub hub, string nodePath, string area = "Overview")
    {
        var workspace = hub.GetWorkspace();
        var renderer = hub.ServiceProvider.GetRequiredService<IMauiControlRenderer>();
        Content = new ScrollView
        {
            Padding = 16,
            Content = new LayoutAreaView(workspace, (Address)nodePath, new LayoutAreaReference(area), renderer),
        };
    }
}
