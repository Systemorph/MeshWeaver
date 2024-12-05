using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Http.Connections.Client;

namespace MeshWeaver.Connection.Notebook;

public static class ConnectionConfiguration
{
    public static object Address { get; set; }

    public static Action<HttpConnectionOptions> ConnectionOptions { get; set; } 

    public static Func<MessageHubConfiguration, MessageHubConfiguration> ConfigureHub { get; set; } = config => config;
}
