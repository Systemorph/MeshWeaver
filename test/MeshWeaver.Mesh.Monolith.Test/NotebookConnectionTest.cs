using System.Reactive.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Connection.Notebook;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Kernel.Hub;
using Microsoft.DotNet.Interactive;
using Microsoft.DotNet.Interactive.CSharp;
using Microsoft.DotNet.Interactive.Events;
using Microsoft.DotNet.Interactive.Formatting;
using Microsoft.DotNet.Interactive.Formatting.Csv;
using Microsoft.DotNet.Interactive.Formatting.TabularData;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Hosting.Monolith.Test;

public class NotebookConnectionTest(ITestOutputHelper output) : AspNetCoreMeshBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .AddKernel()
            .ConfigureMesh(config => config.AddMeshNodes(
                TestHubExtensions.Node
            ));



    [Fact]
    public async Task HelloWorld()
    {

        var kernel = await ConnectToRemoteKernelAsync();
        var helloWorld = await kernel.SubmitCodeAsync(
            @$"
#!mesh
Console.WriteLine(""Hello World"");
"
        );

        helloWorld.Events.OfType<CommandSucceeded>().Should().NotBeEmpty();
        var e = helloWorld.Events.OfType<StandardOutputValueProduced>().Single();
        e.FormattedValues.Single().Value.Should().Be("Hello World\r\n");
    }
    //client.Post(
        //    new SubmitCodeRequest(TestHubExtensions.GetDashboardCommand),
        //    o => o.WithTarget(new KernelAddress()));
        //var kernelEvents = await kernelEventsStream
        //    .Select(e => Microsoft.DotNet.Interactive.Connection.KernelEventEnvelope.Deserialize(e.Event).Event)
        //    .TakeUntil(e => e is CommandSucceeded || e is CommandFailed)
        //    //.TakeUntil(e => e is StandardOutputValueProduced)
        //    .ToArray()
        //    .FirstAsync();

        //kernelEvents.OfType<CommandSucceeded>().Should().NotBeEmpty();

        //await Task.Delay(1000).ConfigureAwait(false);
        //kernelEventsStream.OnCompleted();
        //var kernelEvents2 = await kernelEventsStream
        //    .Select(e => Microsoft.DotNet.Interactive.Connection.KernelEventEnvelope.Deserialize(e.Event).Event)
        //    .TakeUntil(e => e is CommandSucceeded || e is CommandFailed)
        //    .ToArray()
        //    .FirstAsync();
        //var standardOutput = kernelEvents.OfType<ReturnValueProduced>().Single();
        //var value = standardOutput.FormattedValues.Single();
        //value.Value.Should().Contain("iframe");

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

        var uiClient = Server.Services
            .GetRequiredService<IMessageHub>()
            .ServiceProvider
            .CreateMessageHub(new UiAddress(), c => c.AddLayoutClient(c => c));
        var stream = uiClient.GetWorkspace()
            .GetRemoteStream(new KernelAddress() { Id = addressId }, new LayoutAreaReference(area));

        var control = await stream
            .GetControlStream(area)
            .Timeout(5.Seconds())
            .FirstAsync(x => x != null);


        control.Should().BeOfType<MarkdownControl>().Which.Data.Should().Be("Hello World");
    }
    protected async Task<CompositeKernel> ConnectToRemoteKernelAsync()
    {
        var kernel = await CreateCompositeKernelAsync();

        // Prepend the #r "MeshWeaver.Notebook.Client" command to load the extension
        var loadModule = await kernel.SubmitCodeAsync($"#!connect mesh --url http://localhost/{KernelEndPoint} --kernel-name mesh");
        loadModule.Events.Last().Should().BeOfType<CommandSucceeded>();
        return kernel;
    }

    protected async Task<CompositeKernel> ConnectHubAsync()
    {
        var kernel = await CreateCompositeKernelAsync();

        // Prepend the #r "MeshWeaver.Notebook.Client" command to load the extension
        var loadModule = await kernel.SubmitCodeAsync("#r \"MeshWeaver.Connection.Notebook\"");
        loadModule.Events.Last().Should().BeOfType<CommandSucceeded>();

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
        kernel.AddConnectDirective(new ConnectMeshWeaverDirective());
        Disposables.Add(kernel);
        return kernel;
    }

    


}
