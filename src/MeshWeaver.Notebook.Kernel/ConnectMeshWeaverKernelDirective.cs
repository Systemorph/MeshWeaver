using MeshWeaver.Mesh.Contract;
using MeshWeaver.Notebooks;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.DotNet.Interactive;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.Connection;
using Microsoft.DotNet.Interactive.Directives;

namespace MeshWeaver.Notebook.Client;

public class ConnectMeshWeaverKernel(string connectedKernelName) : ConnectKernelCommand(connectedKernelName)
{
    public string KernelSpecName { get; set; }

    public string InitScript { get; set; }

    public string Url { get; set; }

    public bool Bearer { get; set; }

    public string Token { get; set; }
}

public class ConnectMeshWeaverKernelDirective : ConnectKernelDirective<ConnectMeshWeaverKernel>
{
    private readonly IServiceProvider serviceProvider;

    public ConnectMeshWeaverKernelDirective(IServiceProvider serviceProvider)
        : base("meshweaver", "Connects to MeshWeaver kernel")
    {
        Parameters.Add(UriParameter);
        this.serviceProvider = serviceProvider;
    }

    public KernelDirectiveParameter UriParameter { get; } =
        new("--url", "The URL to connect to")
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

        var connection = await GetMeshWeaverConnectionAsync(connectCommand);
        if (connection is null)
        {
            throw new InvalidOperationException("No supported connection options were specified");
        }

        var connector = new MeshWeaverKernelConnector(serviceProvider, connection, kernelSpecName, initScript);

        var localName = connectCommand.ConnectedKernelName;

        var kernel = await connector.CreateKernelAsync(localName);
        if (connection is IDisposable disposableConnection)
        {
            kernel.RegisterForDisposal(disposable: disposableConnection);
        }
        return new[] { kernel };
    }

    private async Task<MeshConnection> GetMeshWeaverConnectionAsync(ConnectMeshWeaverKernel connectCommand)
    {
        var hubConnection = new HubConnectionBuilder()
            .WithUrl(connectCommand.Url)
            .Build();

        await hubConnection.StartAsync();

        // Assuming the hub has a method to get kernel information
        var kernelInfo = await hubConnection.InvokeAsync<MeshConnection>(
            "GetKernelConnectionAsync", connectCommand.KernelSpecName);


        return kernelInfo;
    }
}

