using Microsoft.CodeAnalysis;
using Microsoft.DotNet.Interactive.Commands;

namespace MeshWeaver.Connection.Notebook;

public class ConnectMeshWeaverKernel(string connectedKernelName) : ConnectKernelCommand(connectedKernelName)
{
    public string Url { get; set; } = "";
    public string Language { get; set; } = "";
}
