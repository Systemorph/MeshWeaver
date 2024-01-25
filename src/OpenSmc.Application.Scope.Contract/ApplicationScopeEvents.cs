using OpenSmc.Messaging;

namespace OpenSmc.Application.Scope;

public record SubscribeScopeRequest : IRequest<object>
{
    public object Address { get; init; }
    public string ScopeType { get; init; }
    public object Identity { get; init; }
    public string Id { get; init; }

    public object Scope { get; init; }
    public SubscribeScopeRequest(object Scope)
    {
        this.Scope = Scope;
    }

    public SubscribeScopeRequest(string ScopeType, object Identity = null, object Address = null)
    {
        this.ScopeType = ScopeType;
        this.Identity = Identity;
        this.Address = Address;
    }

}

public record UnsubscribeScopeRequest(IMutableScope Scope); 
public record DisposeScopeRequest(IMutableScope Scope);