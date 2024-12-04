using Microsoft.AspNetCore.Http.Connections.Client;

namespace MeshWeaver.Connection.SignalR;

public static class ConnectionContext
{
    public static object Address { get; set; }

    public static Action<HttpConnectionOptions> ConnectionOptions { get; set; } 
}
