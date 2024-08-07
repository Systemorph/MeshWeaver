using System.Security.Principal;

namespace MeshWeaver.Session;

public interface ISessionUser
{
    string Name { get; }
    IPrincipal Principal { get; }
    internal void Login(string user);
}

