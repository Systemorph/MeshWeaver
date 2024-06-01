using OpenSmc.Data;
using OpenSmc.Data.Serialization;
using OpenSmc.Layout;
using OpenSmc.Messaging;

namespace OpenSmc.Blazor;
public record ViewDescriptor(Type Type, IReadOnlyDictionary<string, object> Parameters);

public interface IBlazorServer
{
    ViewDescriptor GetViewDescriptor(object instance, IChangeStream<EntityStore, LayoutAreaReference> stream, string area);
}

public class BlazorServer(IMessageHub hub) : IBlazorServer
{
    private readonly BlazorConfiguration configuration = hub.Configuration
        .GetConfigurationFunctions()
        .Aggregate(new BlazorConfiguration(), (c,f) => f(c));


    public ViewDescriptor GetViewDescriptor(object instance, IChangeStream<EntityStore, LayoutAreaReference> stream, string area)
        => configuration.GetViewDescriptor(instance, stream, area);
}
