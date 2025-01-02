using System.Reactive.Subjects;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.CodeAnalysis;
using Microsoft.DotNet.Interactive;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.Connection;
using Microsoft.DotNet.Interactive.Directives;

namespace MeshWeaver.Connection.Notebook;

public class ConnectMeshWeaverDirective : ConnectKernelDirective<ConnectMeshWeaverKernel>
{
    private record LanguageDescriptor(string Name, string DisplayName, string LanguageName, string LanguageVersion);

    private static readonly Dictionary<string, LanguageDescriptor> LanguageDescriptors =
        new LanguageDescriptor[]
            {
                new("csharp", "csharp", "C#", "12.0")
            }
            .ToDictionary(x => x.Name);
    private KernelDirectiveParameter UrlParameter { get; } =
        new("--url", description: "The url of the SignalR end point for the MeshWeaver kernel. Typically https://<mesh-url>/kernel.")
        {
            AllowImplicitName = true,
            Required = true,
        };
    private KernelDirectiveParameter LanguageParameter { get; } =
        new("--language", description: "The language of the MeshWeaver kernel. Default is C#")
        {
            AllowImplicitName = false,
            Required = false,
        };

    public ConnectMeshWeaverDirective() : base("mesh", "Connects to a Mesh Weaver instance")
    {
        Parameters.Add(UrlParameter);
        Parameters.Add(LanguageParameter);
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
            await connection.SendAsync("connect", "schnack");
            await tcs.Task;
        }
        catch(Exception e)
        {
            throw new MeshWeaverKernelException($"Failed to connect to MeshWeaver instance on {connectCommand.Url}:\n{e}", e);
        }


        var subject = new Subject<string>();
        connection.On<string>("kernelEvents", e => subject.OnNext(e));
        var receiver = KernelCommandAndEventReceiver.FromObservable(subject);

        var kernel = new ProxyKernel( 
            connectCommand.ConnectedKernelName, 
            new KernelCommandAndEventSignalRHubConnectionSender(connection), 
            receiver,
            new Uri($"kernel://mesh/csharp")
                );
        var language = connectCommand.Language == null
            ? LanguageDescriptors.Values.First()
            : LanguageDescriptors.GetValueOrDefault(connectCommand.Language) ??
              throw new ArgumentException($"Unknown language: {connectCommand.Language}");

        var kernelInfo = kernel.KernelInfo;
        kernelInfo.DisplayName = $"Mesh - {language.DisplayName}";
        kernelInfo.LanguageName = language.LanguageName;
        kernelInfo.LanguageVersion = language.LanguageVersion;
        kernelInfo.RemoteUri = new Uri($"kernel://mesh/{language.Name}");
        kernelInfo.Description = $"Mesh Weaver connection in {language.DisplayName}";
        return [kernel];
    }

    public static void Install()
    {
        if(Kernel.Current.RootKernel is CompositeKernel composite)
            composite.AddConnectDirective(new ConnectMeshWeaverDirective());
    }
}

public class ConnectMeshWeaverKernel(string connectedKernelName) : ConnectKernelCommand(connectedKernelName)
{
    public string Url { get; set; }
    public string Language { get; set; }
}
