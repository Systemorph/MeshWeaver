using System.Security.Principal;

namespace OpenSmc.Session;

public interface ISessionUser
{
    string Name { get; }
    IPrincipal Principal { get; }
    internal void Login(string user);
}

