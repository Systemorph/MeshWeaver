using System.Text.Json;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;

namespace MeshWeaver.Layout.Client;

public interface ILayoutClient
{
    ViewDescriptor GetViewDescriptor(object instance, ISynchronizationStream<JsonElement, LayoutAreaReference> stream, string area);
}

public class LayoutClient(IMessageHub hub) : ILayoutClient
{
    private readonly LayoutClientConfiguration configuration = hub.Configuration
        .GetConfigurationFunctions()
        .Aggregate(new LayoutClientConfiguration(hub), (c,f) => f(c));


    public ViewDescriptor GetViewDescriptor(object instance, ISynchronizationStream<JsonElement, LayoutAreaReference> stream, string area)
        => configuration.GetViewDescriptor(instance, stream, area);
}
