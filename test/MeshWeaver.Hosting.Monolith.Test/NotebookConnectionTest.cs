﻿using System.Linq;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
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
        e.FormattedValues.Single().Value.TrimEnd('\n', '\r').Should().Be("Hello World");
    }

    [Fact]
    public async Task LayoutAreas()
    {
        var kernel = await ConnectToRemoteKernelAsync();

        // Send Ping and receive Pong
        var kernelResult = await kernel.SubmitCodeAsync(
            @$"#!mesh
new {typeof(MarkdownControl).FullName}(""Hello World"")"
        );

        kernelResult.Events.Last().Should().BeOfType<CommandSucceeded>();
        var result = kernelResult.Events.OfType<ReturnValueProduced>().Single();
        var formattedValues = result.FormattedValues.First();

        formattedValues.Value.Should().BeOfType<string>().Which.Should().Contain("iframe");
        var iframeSrc = formattedValues.Value;
        var match = Regex.Match(iframeSrc, @"<iframe id='[^']+' src='http://localhost/area/(?<addressType>[^/]+)/(?<addressId>[^/]+)/(?<area>[^/]+)' style='[^']+'></iframe>");
        match.Success.Should().BeTrue();
        var addressType = match.Groups["addressType"].Value;
        var addressId = match.Groups["addressId"].Value;
        var area = match.Groups["area"].Value;

        // Assert the extracted values
        addressType.Should().Be("kernel");
        addressId.Should().NotBeNullOrEmpty();
        area.Should().NotBeNullOrEmpty();

        var uiClient = Server.Services
            .GetRequiredService<IMessageHub>()
            .ServiceProvider
            .CreateMessageHub(new UiAddress(), c => c.AddLayoutClient(x => x));
        var stream = uiClient.GetWorkspace()
            .GetRemoteStream(new KernelAddress() { Id = addressId }, new LayoutAreaReference(area));

        var control = await stream
            .GetControlStream(area)
            .Timeout(5.Seconds())
            .FirstAsync(x => x != null);


        var md = control.Should().BeOfType<MarkdownControl>()
            .Subject;
        md.Markdown.Should().NotBeNull();
        md.Markdown.ToString()!.TrimEnd('\n','\r').Should().Be("Hello World");
    }
    protected async Task<CompositeKernel> ConnectToRemoteKernelAsync()
    {
        var kernel = CreateCompositeKernel();

        // Prepend the #r "MeshWeaver.Notebook.Client" command to load the extension
        var loadModule = await kernel.SubmitCodeAsync($"#!connect mesh --url http://localhost/{KernelEndPoint} --kernel-name mesh");
        loadModule.Events.Last().Should().BeOfType<CommandSucceeded>();
        return kernel;
    }

    protected async Task<CompositeKernel> ConnectHubAsync()
    {
        var kernel = CreateCompositeKernel();

        // Prepend the #r "MeshWeaver.Notebook.Client" command to load the extension
        var loadModule = await kernel.SubmitCodeAsync("#r \"MeshWeaver.Connection.Notebook\"");
        var connect = 
        loadModule.Events.Last().Should().BeOfType<CommandSucceeded>();
        return kernel;
    }



    protected CompositeKernel CreateCompositeKernel()
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
