using System.Reactive.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
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

public class NotebookConnectionTest(ITestOutputHelper output) : AspNetCoreMeshBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureMesh(mesh => mesh.AddNotebooks());


    [Fact]
    public async Task PingPongThroughNotebookHosting()
    {

        var kernel = await ConnectHubAsync();

        // Send Ping and receive Pong
        var pingPong = await kernel.SubmitCodeAsync(
@$"await client.AwaitResponse(
    new Ping(), 
    o => o.WithTarget(new {typeof(ApplicationAddress).FullName}(""Test""))
    , new CancellationTokenSource(10000).Token
)");

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
        var formattedValues = result.FormattedValues.First();

        formattedValues.Value.Should().BeOfType<string>().Which.Should().Contain("iframe");
        var iframeSrc = formattedValues.Value;
        var match = Regex.Match(iframeSrc, @"<iframe src='http://[^/]+/area/(?<addressType>[^/]+)/(?<addressId>[^/]+)/(?<area>[^/]+)'></iframe>");

        match.Success.Should().BeTrue();
        var addressType = match.Groups["addressType"].Value;
        var addressId = match.Groups["addressId"].Value;
        var area = match.Groups["area"].Value;

        // Assert the extracted values
        addressType.Should().Be("nb");
        addressId.Should().NotBeNullOrEmpty();
        area.Should().NotBeNullOrEmpty();

        var uiClient = Server.Services.CreateMessageHub(new UiAddress(), c => c.AddLayoutClient(c => c));
        var stream = uiClient.GetWorkspace()
            .GetRemoteStream(new NotebookAddress() { Id = addressId }, new LayoutAreaReference(area));

        var control = await stream
            .GetControlStream(area)
            .Timeout(3.Seconds())
            .FirstAsync();


        control.Should().BeOfType<MarkdownControl>();
    }

    protected async Task<CompositeKernel> ConnectHubAsync()
    {
        var kernel = await CreateCompositeKernelAsync();

        // Prepend the #r "MeshWeaver.Notebook.Client" command to load the extension
        var loadModule = await kernel.SubmitCodeAsync("#r \"MeshWeaver.Connection.Notebook\"");
        loadModule.Events.Last().Should().BeOfType<CommandSucceeded>();
        var connectMeshWeaver = await kernel.SubmitCodeAsync(
@$"
#r ""MeshWeaver.Connection.Notebook""
#r ""MeshWeaver.Hosting.Test""
using MeshWeaver.Hosting.Test;
using MeshWeaver.Messaging;
using System.Threading;
var client = MeshWeaver.Connection.Notebook.MeshConnection
    .Configure(""{SignalRUrl}"")
    .ConfigureHub(config => config.WithTypes(typeof(Ping), typeof(Pong)))
    .Connect();
"
        );

        connectMeshWeaver.Events.OfType<CommandSucceeded>().SingleOrDefault().Should().NotBeNull();
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
        Disposables.Add(kernel);
        return kernel;
    }

    


}
