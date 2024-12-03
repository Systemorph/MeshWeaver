using FluentAssertions;
using MeshWeaver.Hosting.SignalR.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Test;
using MeshWeaver.Messaging;
using MeshWeaver.Notebooks.Hub;
using Microsoft.DotNet.Interactive;
using Microsoft.DotNet.Interactive.CSharp;
using Microsoft.DotNet.Interactive.Events;
using Microsoft.DotNet.Interactive.Formatting;
using Microsoft.DotNet.Interactive.Formatting.Csv;
using Microsoft.DotNet.Interactive.Formatting.TabularData;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Hosting.Monolith.Test;

public class NotebookTest(ITestOutputHelper output) : AspNetCoreMeshBase(output)
{


    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureMesh(mesh => mesh.AddNotebooks());



    [Fact]
    public async Task PingPong()
    {
        var kernel = CreateCompositeKernel();

        // Prepend the #r "MeshWeaver.Notebook.Client" command to load the extension
        var result = await kernel.SubmitCodeAsync("#r \"MeshWeaver.Notebook.Client\"");
        result.Events.Last().Should().BeOfType<CommandSucceeded>();

        var debug = await kernel.SubmitCodeAsync(
            "var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.FullName).ToList();\nloadedAssemblies"
        );
        
        var r = debug.Events.OfType<ReturnValueProduced>().Single();
        r.Value.Should().BeOfType<List<string>>().Which.Should().NotContain(x => x.Contains("Notebook.Client"));
        var withNotebook = ((IEnumerable<string>)r.Value).Where(x => x.Contains("Notebook")).ToArray();
        // Connect to the MeshWeaver instance
        debug = await kernel.SubmitCodeAsync(
            "using MeshWeaver.Notebook.Client;"
        );
        debug.Events.Last().Should().BeOfType<CommandSucceeded>();
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
