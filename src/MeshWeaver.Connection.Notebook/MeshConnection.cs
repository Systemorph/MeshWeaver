using MeshWeaver.Mesh;
using Microsoft.AspNetCore.SignalR.Client;

namespace MeshWeaver.Connection.Notebook;

public static class MeshConnection
{
    public static object Address { get; set; }

    public static Func<IHubConnectionBuilder, IHubConnectionBuilder> ConfigurationOptions { get; set; } 

    //public static Func<MessageHubConfiguration, MessageHubConfiguration> ConfigureHub { get; set; } = config => config;

    public static NotebookMeshClient Configure(string url, object address = null)
    {
        var ret = new NotebookMeshClient(url, address ?? new KernelAddress());
        if (ConfigurationOptions != null)
            ret = ret.ConfigureConnection(ConfigurationOptions);
        return ret;
    }
}
