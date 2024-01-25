using OpenSmc.Messaging;

namespace OpenSmc.Application.Scope;

public enum PropertyChangeStatus
{
    Requested,
    Committed,
    NotFound,
    Exception
}

public record ScopePropertyChanged(string ScopeId, string Property, RawJson Value, PropertyChangeStatus Status) : IRequest<ScopePropertyChanged>;

