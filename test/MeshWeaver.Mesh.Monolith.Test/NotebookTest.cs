using FluentAssertions;
using MeshWeaver.Connection.SignalR;
using MeshWeaver.Hosting.Test;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
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
        var kernel = await CreateCompositeKernelAsync();

        // Prepend the #r "MeshWeaver.Notebook.Client" command to load the extension
        var loadModule = await kernel.SubmitCodeAsync("#r \"MeshWeaver.Connection.SignalR\"");
        loadModule.Events.Last().Should().BeOfType<CommandSucceeded>();

        var createHub = await kernel.SubmitCodeAsync(
            @$"var client = MeshWeaver.Connection.SignalR.MeshClient
   .Configure(""{SignalRUrl}"")
   .ConfigureHub(config => config.WithTypes(typeof(Ping), typeof(Pong)))
   .Connect();"
        );

        createHub.Events.Last().Should().BeOfType<CommandSucceeded>();

        // Send Ping and receive Pong
        var pingPong = await kernel.SubmitCodeAsync(
            "var ping = new Ping();\n" +
            $"var pong = await client.AwaitResponse<Pong>(ping, o => o.WithTarget(new {typeof(ApplicationAddress).FullName}(\"Test\")));\n" +
            "pong");

        pingPong.Events.Last().Should().BeOfType<CommandSucceeded>();
        var result = pingPong.Events.OfType<ReturnValueProduced>().Single();
        result.Value.Should().BeAssignableTo<IMessageDelivery>().Which.Message.Should().BeOfType<Pong>();
    }

    [Fact]
    public async Task PingPongThroughNotebookHosting()
    {

        var kernel = await ConnectHubAsync();

        // Send Ping and receive Pong
        var pingPong = await kernel.SubmitCodeAsync(
            "var ping = new Ping();\n" +
            $"var pong = await client.AwaitResponse<Pong>(ping, o => o.WithTarget(new {typeof(ApplicationAddress).FullName}(\"Test\")));\n" +
            "pong");

        pingPong.Events.Last().Should().BeOfType<CommandSucceeded>();
        var result = pingPong.Events.OfType<ReturnValueProduced>().Single();
        result.Value.Should().BeAssignableTo<IMessageDelivery>().Which.Message.Should().BeOfType<Pong>();
    }
    [Fact]
    public async Task LayoutAreas()
    {

        var kernel = await ConnectHubAsync();

        // Send Ping and receive Pong
        var kernelResult = await kernel.SubmitCodeAsync(
            @$"new {typeof(MarkdownControl).FullName}(""Hello World"")"
        );

        kernelResult.Events.Last().Should().BeOfType<CommandSucceeded>();
        var result = kernelResult.Events.OfType<ReturnValueProduced>().Single();
        await Task.Delay(100);
        result.Value.Should().BeAssignableTo<IMessageDelivery>().Which.Message.Should().BeOfType<Pong>();
    }

    protected async Task<CompositeKernel> ConnectHubAsync()
    {
        var kernel = await CreateCompositeKernelAsync();

        // Prepend the #r "MeshWeaver.Notebook.Client" command to load the extension
        var loadModule = await kernel.SubmitCodeAsync("#r \"MeshWeaver.Connection.Notebook\"");
        loadModule.Events.Last().Should().BeOfType<CommandSucceeded>();
        var createHub = await kernel.SubmitCodeAsync(
            @$"var client = await MeshWeaver.Connection.Notebook.MeshClient
        .Configure(""{SignalRUrl}"")
        .ConfigureHub(config => config.WithTypes(typeof(Ping), typeof(Pong)))
        .ConnectAsync();"
        );

        createHub.Events.Last().Should().BeOfType<CommandSucceeded>();

        return kernel;
    }


    protected async Task<CompositeKernel> CreateCompositeKernelAsync()
    {
        Formatter.SetPreferredMimeTypesFor(typeof(TabularDataResource), HtmlFormatter.MimeType, CsvFormatter.MimeType);

        var csharpKernel = new CSharpKernel()
            .UseKernelHelpers()
            .UseValueSharing();

        var kernel = new CompositeKernel { csharpKernel };
        kernel.DefaultKernelName = csharpKernel.Name;
        await csharpKernel.SubmitCodeAsync($"#r \"{Server.GetType().Assembly.Location}\"");
        await csharpKernel.SubmitCodeAsync($"#r \"{typeof(Ping).Assembly.Location}\"");
        await csharpKernel.SubmitCodeAsync($"using {typeof(Ping).Namespace};");
        await csharpKernel.SubmitCodeAsync($"using {typeof(MessageHubExtensions).Namespace};");

        await csharpKernel.SetValueAsync(nameof(Server), Server, Server.GetType());
        Disposables.Add(kernel);
        return kernel;
    }

    


    protected MessageHubConfiguration ConfigureClient(MessageHubConfiguration config)
    {
        return config
            .UseSignalRClient("http://localhost/connection")
            .WithTypes(typeof(Ping), typeof(Pong));
    }


}
