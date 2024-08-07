using MeshWeaver.Scopes.Proxy;

namespace MeshWeaver.Scopes.Synchronization;

public delegate void ScopePropertyChangedEventHandler(object sender, ScopePropertyChangedEvent args);
public delegate void ScopeInvalidatedHandler(object sender, IInternalMutableScope invalidated);