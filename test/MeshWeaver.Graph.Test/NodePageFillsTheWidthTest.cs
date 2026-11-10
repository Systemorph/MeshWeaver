using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The wide-window layout rule (MeshWeaver.Plugins#5317): a node page FILLS the content pane. The
/// shell owns the responsive gutter, reading text keeps its own 100ch measure in the clients, and
/// tables, grids, code and images take the full width. Until this change a node type that declared
/// no width was a centred 1200px column, which left empty bands on both sides of a wide window
/// (Blazor and the React Native app alike). A width stays opt-in, and a declared one keeps the
/// centred column it always had.
/// </summary>
public class NodePageFillsTheWidthTest
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoDeclaredWidth_FillsThePane(string? declared)
    {
        var style = MeshNodeLayoutAreas.ContainerStyle(declared);

        style.Should().Contain("max-width: 100%;", "an undeclared width is no cap at all");
        style.Should().NotContain("1200px", "the centred 1200px column is gone");
        style.Should().Contain("padding: 0 24px;", "the page keeps its inner padding — a phone does not regress");
    }

    [Theory]
    [InlineData("960px")]
    [InlineData(" 1280px ")]
    public void DeclaredWidth_KeepsItsCentredColumn(string declared)
    {
        var style = MeshNodeLayoutAreas.ContainerStyle(declared);

        style.Should().Contain($"max-width: {declared.Trim()};");
        style.Should().Contain("margin: 0 auto;", "a type that asked for a column still gets it centred");
    }

    [Fact]
    public void DefaultWidth_IsTheFullPane()
        => MeshNodeLayoutAreas.FillWidth.Should().Be("100%");
}

/// <summary>
/// The same rule through a real hub: <see cref="MeshNodeLayoutAreas.GetContainerStyle"/> with no
/// <see cref="PageLayoutOptions"/> fills the pane; the hub-level option and the node-type
/// <c>PageMaxWidth</c> still declare a width, with the documented precedence.
/// </summary>
public class NodePageContainerStyleOnTheWireTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string DefaultPage = nameof(DefaultPage);
    private const string TypedPage = nameof(TypedPage);
    private const string OverriddenPage = nameof(OverriddenPage);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .AddLayout(layout => layout
                .WithView(DefaultPage, (host, _) =>
                    Controls.Stack.WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host)))
                .WithView(TypedPage, (host, _) =>
                    Controls.Stack.WithStyle(MeshNodeLayoutAreas.GetContainerStyle(
                        host, new NodeTypeDefinition { PageMaxWidth = "960px" })))
                .WithView(OverriddenPage, (host, _) =>
                    Controls.Stack.WithStyle(MeshNodeLayoutAreas.GetContainerStyle(
                        host, new NodeTypeDefinition { PageMaxWidth = "960px" }, maxWidthOverride: "100%"))));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    private async Task<string> RenderStyleAsync(string area)
    {
        var reference = new LayoutAreaReference(area);
        var stream = GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(CreateHostAddress(), reference);
        var control = await stream.GetControlStream(reference.Area!)
            .Should().Within(10.Seconds()).Match(x => x != null,
                cancellationToken: TestContext.Current.CancellationToken);
        var style = control.Should().BeOfType<StackControl>().Subject.Style?.ToString() ?? "";
        Output.WriteLine($"{area} style on the wire: {style}");
        return style;
    }

    [HubFact]
    public async Task NodeTypeWithoutAWidth_FillsThePane()
    {
        var style = await RenderStyleAsync(DefaultPage);
        style.Should().Contain("max-width: 100%;");
        style.Should().NotContain("1200px");
    }

    [HubFact]
    public async Task NodeTypeWidth_IsHonoured()
        => (await RenderStyleAsync(TypedPage)).Should().Contain("max-width: 960px;");

    [HubFact]
    public async Task CallerOverride_WinsOverTheNodeType()
        => (await RenderStyleAsync(OverriddenPage)).Should().Contain("max-width: 100%;");
}

/// <summary>A hub that declares <see cref="PageLayoutOptions"/> keeps the column it asked for.</summary>
public class NodePageHubOptionWidthTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string OptionPage = nameof(OptionPage);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .Set(new PageLayoutOptions { MaxWidth = "1280px" })
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .AddLayout(layout => layout
                .WithView(OptionPage, (host, _) =>
                    Controls.Stack.WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host))));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    [HubFact]
    public async Task HubOptionWidth_IsHonoured()
    {
        var reference = new LayoutAreaReference(OptionPage);
        var stream = GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(CreateHostAddress(), reference);
        var control = await stream.GetControlStream(reference.Area!)
            .Should().Within(10.Seconds()).Match(x => x != null,
                cancellationToken: TestContext.Current.CancellationToken);
        var style = control.Should().BeOfType<StackControl>().Subject.Style?.ToString() ?? "";
        Output.WriteLine($"style on the wire: {style}");
        style.Should().Contain("max-width: 1280px;");
        style.Should().Contain("margin: 0 auto;");
    }
}
