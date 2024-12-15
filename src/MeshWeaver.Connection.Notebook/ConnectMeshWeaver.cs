using System.Reactive.Subjects;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.DotNet.Interactive;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.Connection;
using Microsoft.DotNet.Interactive.Directives;

namespace MeshWeaver.Connection.Notebook;

public class ConnectMeshWeaverDirective : ConnectKernelDirective<ConnectMeshWeaverKernel>
{
    private KernelDirectiveParameter ConnectionStringParameter { get; } =
        new("--url", description: "The url of the SignalR end point for the MeshWeaver kernel.")
        {
            AllowImplicitName = true,
            Required = true,
        };

    public ConnectMeshWeaverDirective() : base("mesh", "Connects to a Mesh Weaver instance")
    {
        Parameters.Add(ConnectionStringParameter);
    }

    public override async Task<IEnumerable<Kernel>> ConnectKernelsAsync(ConnectMeshWeaverKernel connectCommand, KernelInvocationContext context)
    {
        if (string.IsNullOrWhiteSpace(connectCommand.Url))
        {
            throw new ArgumentException("Provide a valid Connection string");
        }

        var connection = new HubConnectionBuilder()
            .WithUrl(connectCommand.Url, ConnectionSettings.HttpConnectionOptions)
            .Build();
        await connection.StartAsync();
        var tcs = new TaskCompletionSource<bool>(new CancellationTokenSource(10_000).Token);
        connection.On<bool>("connected", x => tcs.SetResult(x));

        try
        {
            await connection.SendAsync("connect");
            await tcs.Task;
        }
        catch(Exception e)
        {
            throw new MeshWeaverKernelException($"Failed to connect to MeshWeaver instance on {connectCommand.Url}:\n{e}", e);
        }


        var subject = new Subject<string>();
        var receiver = KernelCommandAndEventReceiver.FromObservable(subject);

        var kernel = new ProxyKernel( connectCommand.ConnectedKernelName, new KernelCommandAndEventSignalRHubConnectionSender(connection), receiver);
        return [kernel];
    }
}

public class ConnectMeshWeaverKernel(string connectedKernelName) : ConnectKernelCommand(connectedKernelName)
{
    public string Url { get; set; }
}
