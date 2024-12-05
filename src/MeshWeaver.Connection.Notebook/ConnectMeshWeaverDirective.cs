using MeshWeaver.Connection.SignalR;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.DotNet.Interactive;
using Microsoft.DotNet.Interactive.Connection;
using Microsoft.DotNet.Interactive.CSharp;
using Microsoft.DotNet.Interactive.Directives;

namespace MeshWeaver.Connection.Notebook;

public class ConnectMeshWeaverDirective
    : ConnectKernelDirective<ConnectMeshWeaver>
{

    public static void Install(Kernel kernel)
    {
        if(kernel is CompositeKernel composite)
            composite.AddConnectDirective(new ConnectMeshWeaverDirective());
    }


    public const string Mesh = nameof(Mesh);
    public ConnectMeshWeaverDirective() : base("meshweaver", "Connects to a MeshWeaver instance")
    {
        Parameters.Add(HubUrlParameter);
    }

    public KernelDirectiveParameter HubUrlParameter { get; } =
        new("--url",
            "The URL of the MeshWeaver connection.")
        {
            Required = true
        };

    public override async Task<IEnumerable<Kernel>> ConnectKernelsAsync(
        ConnectMeshWeaver connectCommand,
        KernelInvocationContext context)
    {
        var hubUrl = connectCommand.HubUrl;

        var localName = connectCommand.ConnectedKernelName;
        var address = ConnectionConfiguration.Address ?? new NotebookAddress();

        var innerKernel = CreateInnerKernel();
        var kernel = new ProxyKernel(localName, innerKernel);
        Func<HubConnectionBuilder, IHubConnectionBuilder> connectionConfiguration =
            (builder =>
            {
                if(ConnectionConfiguration.ConnectionOptions is not null)
                    return builder.WithUrl(hubUrl, ConnectionConfiguration.ConnectionOptions);
                return builder.WithUrl(hubUrl);
            });

        var hub = SignalRMeshClient.Configure(address, connectionConfiguration)
            .ConfigureHub(config => ConnectionConfiguration.ConfigureHub(config)
                .AddLayout(layout => 
                    layout.WithView(ctx => 
                            kernel.Cells.ContainsKey(ctx.Area), 
                        (_,ctx) => kernel.Cells.GetValueOrDefault(ctx.Area)
                        )
                    )
            )
            .Connect();

        kernel.Initialize(hub);

        await innerKernel.SetValueAsync(Mesh, new NotebookMeshApi(hub),
            typeof(NotebookMeshApi));
        kernel.RegisterForDisposal(() => hub.Dispose());
        return [kernel];
    }

    private CSharpKernel CreateInnerKernel()
    {
        return new CSharpKernel()
            .UseValueSharing()
            .UseKernelHelpers()
            .UseImportMagicCommand()
            .UseQuitCommand();
    }
}
