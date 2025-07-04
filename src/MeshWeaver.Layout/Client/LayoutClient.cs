using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Messaging;

namespace MeshWeaver.Layout.Client;

public interface ILayoutClient
{
    IMessageHub Hub { get; }
    public LayoutClientConfiguration Configuration { get; }
    ViewDescriptor? GetViewDescriptor(object instance, ISynchronizationStream<JsonElement> stream, string area);
}

public class LayoutClient(IMessageHub hub) : ILayoutClient
{
    public IMessageHub Hub => hub;
    public LayoutClientConfiguration Configuration { get; } = hub.Configuration
        .GetConfigurationFunctions()
        .Aggregate(new LayoutClientConfiguration(hub), (c,f) => f(c));

    public ViewDescriptor? GetViewDescriptor(object instance, ISynchronizationStream<JsonElement> stream, string area)
        => Configuration.GetViewDescriptor(instance, stream, area);
}
