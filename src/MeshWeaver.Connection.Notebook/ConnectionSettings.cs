using Microsoft.AspNetCore.Http.Connections.Client;

namespace MeshWeaver.Connection.Notebook
{
    public static class ConnectionSettings
    {
        public static Action<HttpConnectionOptions> HttpConnectionOptions = _ => { };
    }
}
