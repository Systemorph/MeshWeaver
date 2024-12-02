//using MeshWeaver.Fixture;
//using MeshWeaver.Hosting.Monolith;
//using MeshWeaver.Mesh.Contract;
//using MeshWeaver.Notebook.Client;
//using MeshWeaver.Notebooks.Hub;
//using Microsoft.AspNetCore.SignalR.Client;
//using Microsoft.DotNet.Interactive;
//using Microsoft.DotNet.Interactive.CSharp;
//using Microsoft.DotNet.Interactive.Formatting;
//using Microsoft.DotNet.Interactive.Formatting.Csv;
//using Microsoft.DotNet.Interactive.Formatting.TabularData;
//using Microsoft.Extensions.Hosting;
//using Xunit;
//using Xunit.Abstractions;

//namespace MeshWeaver.Notebooks.Test;

//public class NotebookTest(ITestOutputHelper output) : MeshTestBase(output)
//{
//    private IHost host;
//    private HubConnection hubConnection;

//    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
//        => base
//            .ConfigureMesh(builder)
//            .AddMonolithMesh()
//            .ConfigureMesh(mesh =>
//                mesh
//                    .AddNotebooks()
//                );

//    [Fact]
//    public async Task HelloWorld()
//    {
//        var address = new NotebookAddress();
//        var kernel = CreateCompositeKernel();
//        var result = await kernel.SubmitCodeAsync(
//            $"#!connect meshweaver --kernel-name testKernel --kernel-spec csharp --uri http://localhost/notebook");
//    }

//    protected CompositeKernel CreateCompositeKernel()
//    {
//        Formatter.SetPreferredMimeTypesFor(typeof(TabularDataResource), HtmlFormatter.MimeType, CsvFormatter.MimeType);

//        var csharpKernel = new CSharpKernel()
//            .UseKernelHelpers()
//            .UseValueSharing();

//        var kernel = new CompositeKernel { csharpKernel };
//        kernel.DefaultKernelName = csharpKernel.Name;

//        var jupyterKernelCommand = new ConnectMeshWeaverDirective(ServiceProvider);

//        kernel.AddConnectDirective(jupyterKernelCommand);
//        Disposables.Add(kernel);
//        return kernel;
//    }

//    public override async Task DisposeAsync()
//    {
//        await hubConnection.DisposeAsync();
//        await host.StopAsync();
//        host.Dispose();
//        foreach (var disposable in Disposables)
//            disposable.Dispose();
//        await base.DisposeAsync();
//    }

//    private List<IDisposable> Disposables { get; } = new();

//}
