using Microsoft.DotNet.Interactive;

namespace MeshWeaver.Notebook.Client;

public static class NotebookClientExtensions
{
    public static void RegisterMeshWeaverCommands(this Kernel kernel, IServiceProvider serviceProvider)
    {
        var commandHandler = new ConnectMeshWeaverCommandHandler(serviceProvider);
        kernel.RegisterCommandHandler<ConnectMeshWeaverCommand>((command, context) => commandHandler.HandleAsync(command, context));
    }
}
