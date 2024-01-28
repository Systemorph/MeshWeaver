using Newtonsoft.Json;

namespace OpenSmc.Scopes.Proxy;

public record ScopePropertyChangedEvent([property:JsonIgnore]object Scope, Guid ScopeId, string Property, object Value, ScopeChangedStatus Status = ScopeChangedStatus.Requested, string ErrorMessage = null);

public enum ScopeChangedStatus
{
    Requested,
    Committed,
    NotFound,
    Exception
}