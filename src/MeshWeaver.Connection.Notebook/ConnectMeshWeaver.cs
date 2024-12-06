using Microsoft.DotNet.Interactive.Commands;

namespace MeshWeaver.Connection.Notebook;

public class ConnectMeshWeaver(string connectedKernelName) : ConnectKernelCommand(connectedKernelName)
{
    public string Url { get; set; }
}
