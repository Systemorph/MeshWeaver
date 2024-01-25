namespace OpenSmc.Scopes.Proxy
{
    public interface IHasAdditionalInterfaces
    {
        IEnumerable<Type> GetAdditionalInterfaces(Type tScope);
    }
}