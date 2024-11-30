using Microsoft.DotNet.Interactive;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.Connection;
using Microsoft.DotNet.Interactive.Events;
using MeshWeaver.Messaging;
using MeshWeaver.Notebooks;
using Microsoft.DotNet.Interactive.Directives;
using Microsoft.CodeAnalysis.Tags;

namespace MeshWeaver.Notebook.Kernel;

public class ConnectMeshWeaverKernel(string connectedKernelName) : ConnectKernelCommand(connectedKernelName)
{
    public string KernelSpecName { get; set; }

    public string InitScript { get; set; }

    public string Url { get; set; }

    public bool Bearer { get; set; }

    public string Token { get; set; }
}

public class ConnectMeshWeaverDirective : ConnectKernelDirective<ConnectMeshWeaverKernel>
{
    private readonly IServiceProvider serviceProvider;

    public ConnectMeshWeaverDirective(IServiceProvider serviceProvider)
        : base("meshweaver", "Connects to MeshWeaver kernel")
    {
        Parameters.Add(UriParameter);
        this.serviceProvider = serviceProvider;
    }

    public KernelDirectiveParameter UriParameter { get; } =
        new("--uri", "The URI to connect to")
        {
            Required = true
        };

    public KernelDirectiveParameter KernelSpecNameParameter { get; } =
        new("--kernel-spec", "The kernel spec to connect to")
        {
            Required = true
        };

    public KernelDirectiveParameter InitScriptParameter { get; } =
        new("--init-script", "Script to run on kernel initialization")
        {
            TypeHint = "file"
        };


    public override async Task<IEnumerable<Microsoft.DotNet.Interactive.Kernel>> ConnectKernelsAsync(
        ConnectMeshWeaverKernel connectCommand,
        KernelInvocationContext context)
    {
        context.DisplayAs(
            "Connecting to MeshWeaver kernel...",
            "text/markdown");

        var kernelSpecName = connectCommand.KernelSpecName;
        var initScript = connectCommand.InitScript;

        var connection = GetMeshWeaverConnection(connectCommand);
        if (connection is null)
        {
            throw new InvalidOperationException("No supported connection options were specified");
        }

        var connector = new MeshWeaverKernelConnector(serviceProvider, connection, kernelSpecName, initScript);

        var localName = connectCommand.ConnectedKernelName;

        var kernel = await connector.CreateKernelAsync(localName);
        if (connection is IDisposable disposableConnection)
        {
            kernel.RegisterForDisposal(disposableConnection);
        }
        return new[] { kernel };
    }

    private IMeshWeaverConnection GetMeshWeaverConnection(ConnectMeshWeaverKernel connectCommand)
    {
        throw new NotImplementedException();
    }
}

