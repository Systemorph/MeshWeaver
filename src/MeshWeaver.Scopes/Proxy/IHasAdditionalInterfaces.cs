namespace MeshWeaver.Scopes.Proxy
{
    public interface IHasAdditionalInterfaces
    {
        IEnumerable<Type> GetAdditionalInterfaces(Type tScope);
    }
}