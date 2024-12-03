using MeshWeaver.Messaging;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.DotNet.Interactive;
using Microsoft.DotNet.Interactive.Commands;

namespace MeshWeaver.Notebook.Client;

public class ConnectMeshWeaverCommand(string url) : KernelCommand
{
    public string Url { get; } = url;
}

public class ConnectMeshWeaverCommandHandler(IServiceProvider serviceProvider)
    : IKernelCommandHandler<ConnectMeshWeaverCommand>
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private IMessageHub _messageHub;

    public async Task HandleAsync(ConnectMeshWeaverCommand command, KernelInvocationContext context)
    {

        context.DisplayAs("Connected to MeshWeaver instance.", "text/markdown");
    }
}
