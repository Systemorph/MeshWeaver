using Microsoft.DotNet.Interactive.Commands;

namespace MeshWeaver.Connection.Notebook
{
    public class ConnectMeshWeaver(string connectedKernelName) : ConnectKernelCommand(connectedKernelName)
    {
        public string HubUrl { get; set; }
        public string AddressType { get; set; }
        public string AddressId { get; set; }
    }
}
