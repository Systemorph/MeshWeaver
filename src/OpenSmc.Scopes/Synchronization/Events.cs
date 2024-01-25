using OpenSmc.Scopes.Proxy;

namespace OpenSmc.Scopes.Synchronization;

public delegate void ScopePropertyChangedEventHandler(object sender, ScopePropertyChangedEvent args);
public delegate void ScopeInvalidatedHandler(object sender, IInternalMutableScope invalidated);