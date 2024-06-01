using OpenSmc.Data;
using OpenSmc.Data.Serialization;
using OpenSmc.Layout;
using OpenSmc.Messaging;

namespace OpenSmc.Blazor;
public record ViewDescriptor(Type Type, IReadOnlyDictionary<string, object> Parameters);

public interface IBlazorClient
{
    ViewDescriptor GetViewDescriptor(object instance, IChangeStream<EntityStore, LayoutAreaReference> stream);
}

public class BlazorClient(IMessageHub hub) : IBlazorClient
{
    private readonly BlazorClientConfiguration configuration = hub.Configuration
        .GetConfigurationFunctions()
        .Aggregate(new BlazorClientConfiguration(), (c,f) => f(c));


    public ViewDescriptor GetViewDescriptor(object instance, IChangeStream<EntityStore, LayoutAreaReference> stream)
        => configuration.GetViewDescriptor(instance, stream);
}
