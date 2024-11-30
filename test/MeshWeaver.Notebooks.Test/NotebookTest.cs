using System.Runtime.InteropServices.JavaScript;
using MeshWeaver.Application;
using MeshWeaver.Blazor.Notebooks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Mesh.SignalR;
using MeshWeaver.Notebook.Client;
using MeshWeaver.Notebooks.Hub;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.DotNet.Interactive;
using Microsoft.DotNet.Interactive.CSharp;
using Microsoft.DotNet.Interactive.Formatting;
using Microsoft.DotNet.Interactive.Formatting.Csv;
using Microsoft.DotNet.Interactive.Formatting.TabularData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Notebooks.Test;

public class NotebookTest(ITestOutputHelper output) : MeshTestBase(output)
{
    private IHost host;
    private HubConnection hubConnection;

    protected override MeshBuilder CreateBuilder()
        => base
            .CreateBuilder()
            .AddMonolithMesh()
            .ConfigureMesh(mesh =>
                mesh
                    .AddNotebooks()
                    .WithMeshNodeFactory((addressType, id) =>
                    addressType == typeof(ApplicationAddress).FullName && id == TestApplicationAttribute.Test
                        ? MeshExtensions.GetMeshNode(addressType, id, GetType().Assembly.Location)
                        : null));
    public async Task InitializeAsync()
    {
        host = await new HostBuilder()
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddSignalR();
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapHub<ConnectionHub>("/notebook");
                    });
                });
            })
            .StartAsync();

        var server = host.GetTestServer();
        var client = server.CreateClient();

        hubConnection = new HubConnectionBuilder()
            .WithUrl("http://localhost/notebook", options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
            })
            .Build();

        await hubConnection.StartAsync();
    }


    [Fact]
    public async Task HelloWorld()
    {
        var address = new NotebookAddress();
        var kernel = CreateCompositeKernel();
        var result = await kernel.SubmitCodeAsync(
            $"#!connect meshweaver --kernel-name testKernel --kernel-spec csharp --uri http://localhost/notebook");
    }

    protected CompositeKernel CreateCompositeKernel()
    {
        Formatter.SetPreferredMimeTypesFor(typeof(TabularDataResource), HtmlFormatter.MimeType, CsvFormatter.MimeType);

        var csharpKernel = new CSharpKernel()
            .UseKernelHelpers()
            .UseValueSharing();

        var kernel = new CompositeKernel { csharpKernel };
        kernel.DefaultKernelName = csharpKernel.Name;

        var jupyterKernelCommand = new ConnectMeshWeaverDirective(ServiceProvider);

        kernel.AddConnectDirective(jupyterKernelCommand);
        Disposables.Add(kernel);
        return kernel;
    }

    public override async Task DisposeAsync()
    {
        await hubConnection.DisposeAsync();
        await host.StopAsync();
        host.Dispose();
        foreach (var disposable in Disposables)
            disposable.Dispose();
        await base.DisposeAsync();
    }

    private List<IDisposable> Disposables { get; } = new();

}
