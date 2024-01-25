namespace OpenSmc.Scopes.Proxy
{
    public interface IScopeRegistry : IDisposable
    {
        event InstanceCreatedEventHandler InstanceRegistered;
        static object SingletonIdentity = new object();
        void Register(Type tScope, object scope, object identity, object storage, string context, bool registerMain);
        object GetIdentity(object scope);
        object GetStorage(object scope);
        string GetContext(object scope);
        object GetScope(Guid id);
        object GetScope(Type tScope, object identity, string context = null);
        Type GetScopeType(object scope);
        void Dispose(object scope);
        Guid GetGuid(object scope);

        IEnumerable<object> Scopes { get; }
    }

    public delegate void InstanceCreatedEventHandler(object sender, object scope);

}