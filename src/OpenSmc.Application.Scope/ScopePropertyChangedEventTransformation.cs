using OpenSmc.Scopes;
using OpenSmc.Scopes.Proxy;
using OpenSmc.Serialization;
using OpenSmc.ShortGuid;
using OpenSmc.Utils;

namespace OpenSmc.Application.Scope;

public class ScopePropertyChangedEventTransformation 
{
    private readonly IScopeRegistry scopeRegistry;
    private readonly ISerializationService serializationService;

    public ScopePropertyChangedEventTransformation(IScopeRegistry scopeRegistry, ISerializationService serializationService)
    {
        this.scopeRegistry = scopeRegistry;
        this.serializationService = serializationService;
    }

    public Task<object> GetAsync(ScopePropertyChangedEvent @event)
    {
        var s = scopeRegistry.GetScope(@event.ScopeId) as IScope;
        if (s == null)
            return Task.FromResult<object>(null);
        var property = s.GetScopeType().GetScopeProperties().SelectMany(x => x.Properties).First(x => x.Name == @event.Property);
        var serialized = serializationService.SerializeProperty(@event.Value, s, property);
        return Task.FromResult<object>(new ScopePropertyChanged(@event.ScopeId.AsString(), @event.Property.ToCamelCase(), serialized, ConvertEnum(@event.Status)));
    }

    private static PropertyChangeStatus ConvertEnum(ScopeChangedStatus status)
    {
        return Enum.TryParse(typeof(PropertyChangeStatus), status.ToString(), out var item) ? (PropertyChangeStatus)item! : throw new NotSupportedException();
    }
}