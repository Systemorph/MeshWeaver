using System.Text.Json;
using System.Text.Json.Nodes;
using OpenSmc.Data;
using OpenSmc.Data.Serialization;
using OpenSmc.Layout;
using OpenSmc.Layout.Client;
using OpenSmc.Messaging;

namespace OpenSmc.Blazor;

public interface ILayoutClient
{
    ViewDescriptor GetViewDescriptor(object instance, IChangeStream<JsonElement, LayoutAreaReference> stream, string area);
}

public class LayoutClient(IMessageHub hub) : ILayoutClient
{
    private readonly LayoutClientConfiguration configuration = hub.Configuration
        .GetConfigurationFunctions()
        .Aggregate(new LayoutClientConfiguration(), (c,f) => f(c));


    public ViewDescriptor GetViewDescriptor(object instance, IChangeStream<JsonElement, LayoutAreaReference> stream, string area)
        => configuration.GetViewDescriptor(instance, stream, area);
}
