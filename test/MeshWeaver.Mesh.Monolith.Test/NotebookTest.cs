using FluentAssertions;
using MeshWeaver.Application;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Mesh.SignalR.Client;
using MeshWeaver.Mesh.SignalR.Server;
using MeshWeaver.Mesh.Test;
using MeshWeaver.Messaging;
using MeshWeaver.Notebook.Client;
using MeshWeaver.Notebooks;
using MeshWeaver.Notebooks.Hub;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.AspNetCore.TestHost;
using Microsoft.DotNet.Interactive;
using Microsoft.DotNet.Interactive.CSharp;
using Microsoft.DotNet.Interactive.Events;
using Microsoft.DotNet.Interactive.Formatting;
using Microsoft.DotNet.Interactive.Formatting.Csv;
using Microsoft.DotNet.Interactive.Formatting.TabularData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Mesh.Monolith.Test;

public class NotebookTest(ITestOutputHelper output) : AspNetCoreMeshBase(output)
{


    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureMesh(mesh => mesh.AddNotebooks());



    protected IMessageHub CreateMesh(IServiceProvider serviceProvider)
        => ConfigureMesh(new(c => c.Invoke(Services), new MeshAddress())).BuildHub(ServiceProvider);

    private IHost host;
    private HubConnection hubConnection;


    [Fact]
    public async Task PingPong()
    {
        var kernel = CreateCompositeKernel();

        // Prepend the #r "MeshWeaver.Notebook.Client" command to load the extension
        var result = await kernel.SubmitCodeAsync("#r \"MeshWeaver.Notebook.Client\"");
        result.Events.Last().Should().BeOfType<CommandSucceeded>();

        // Connect to the MeshWeaver instance
        result = await kernel.SubmitCodeAsync(
            $"#!connect-meshweaver --url \"http://localhost/notebook\"");

        result.Events.Last().Should().BeOfType<CommandSucceeded>();

        // Send Ping and receive Pong
        result = await kernel.SubmitCodeAsync(
            "var ping = new Ping();\n" +
            "var pong = await messageHub.AwaitResponse<Pong>(ping);\n" +
            "pong");

        result.Events.Last().Should().BeOfType<CommandSucceeded>();

        //Assert.Contains("Pong", pingResult.KernelEvents.ToString());
    }
    protected CompositeKernel CreateCompositeKernel()
    {
        Formatter.SetPreferredMimeTypesFor(typeof(TabularDataResource), HtmlFormatter.MimeType, CsvFormatter.MimeType);

        var csharpKernel = new CSharpKernel()
            .UseKernelHelpers()
            .UseValueSharing();

        var kernel = new CompositeKernel { csharpKernel };
        kernel.DefaultKernelName = csharpKernel.Name;

        var jupyterKernelCommand = new ConnectMeshWeaverKernelDirective(ServiceProvider);

        kernel.AddConnectDirective(jupyterKernelCommand);
        Disposables.Add(kernel);
        return kernel;
    }


    protected MessageHubConfiguration ConfigureClient(MessageHubConfiguration config)
    {
        return config
            .UseSignalRMesh("http://localhost/connection",
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                })
            .WithTypes(typeof(Ping), typeof(Pong));
    }


}
