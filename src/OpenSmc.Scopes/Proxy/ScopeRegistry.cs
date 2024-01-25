using System.Collections.Concurrent;

namespace OpenSmc.Scopes.Proxy
{
    public class ScopeRegistry : IScopeRegistry
    {
        private readonly ILogger<IScope> logger;
        private readonly ConcurrentDictionary<object, object> identitiesByScope = new();
        private readonly ConcurrentDictionary<object, object> storageByScope = new();
        private readonly ConcurrentDictionary<(Type tScope, object identity, string context), object> scopesByTypeAndIdentity = new();
        private readonly ConcurrentDictionary<object, Type> scopeTypes = new();
        private readonly ConcurrentDictionary<object, string> contextsByScope = new();
        private readonly ConcurrentDictionary<object, Guid> guidsByScope = new();
        private readonly ConcurrentDictionary<Guid, object> scopesByGuid = new();

        public event InstanceCreatedEventHandler InstanceRegistered;

        public ScopeRegistry(ILogger<IScope> logger)
        {
            this.logger = logger;
        }
        public void Register(Type tScope, object scope, object identity, object storage, string context, bool registerMain)
        {
            identitiesByScope[scope] = identity;
            storageByScope[scope] = storage;
            scopeTypes[scope] = tScope;
            if (!string.IsNullOrEmpty(context))
                contextsByScope[scope] = context;
            if (registerMain)
                scopesByTypeAndIdentity[(tScope, identity, context)] = scope;
            InstanceRegistered?.Invoke(this, scope);
        }

        public object GetIdentity(object scope)
        {
            identitiesByScope.TryGetValue(scope, out var ret);
            return ret;
        }

        public object GetStorage(object scope)
        {
            storageByScope.TryGetValue(scope, out var ret);
            return ret;
        }
        public string GetContext(object scope)
        {
            contextsByScope.TryGetValue(scope, out var ret);
            return ret;
        }

        public object GetScope(Guid id)
        {
            scopesByGuid.TryGetValue(id, out var ret);
            return ret;
        }

        public object GetScope(Type tScope, object identity, string context = null)
        {
            var key = (tScope, identity, context);
            if (!scopesByTypeAndIdentity.TryGetValue(key, out var ret))
                return null;
            return ret;
        }

        public void Dispose()
        {
            identitiesByScope.Clear();
            storageByScope.Clear();
            scopesByTypeAndIdentity.Clear();
            scopeTypes.Clear();
            contextsByScope.Clear();
            guidsByScope.Clear();
            scopesByGuid.Clear();
        }

        public Type GetScopeType(object scope)
        {
            return scopeTypes[scope];
        }

        public void Dispose(object scope)
        {
            // Is this registered here?
            if(!identitiesByScope.TryRemove(scope, out var identity))
                return;
            

            var scopeType = GetScopeType(scope);
            contextsByScope.TryRemove(scope, out var context);

            scopesByTypeAndIdentity.TryRemove((scopeType, identity, context), out _);
            storageByScope.TryRemove(scope, out _);
            scopeTypes.TryRemove(scope, out _);
            if(guidsByScope.TryRemove(scope, out var id))
                scopesByGuid.TryRemove(id, out _);
        }

        public Guid GetGuid(object scope)
        {
            return guidsByScope.GetOrAdd(scope, s =>
                                                {
                                                    var newGuid = Guid.NewGuid();
                                                    scopesByGuid[newGuid] = s;
                                                    return newGuid;
                                                });
        }

        public IEnumerable<object> Scopes => scopesByTypeAndIdentity.Values;

    }
}