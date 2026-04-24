using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Kernel;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Orleans regression guard for the original bug: <see cref="MarkdownControl"/> declares
/// <c>Markdown</c> and <c>CodeSubmissions</c> as <c>object</c>, so when the control
/// round-trips through the layout stream across grain boundaries, the values arrive on the
/// portal client as <see cref="JsonElement"/>. Without the coercion in
/// <see cref="MarkdownViewLogic"/>, markdown rendered literally as raw text (e.g. the
/// reported <c>[text](url)</c>) and code submissions never dispatched.
///
/// This test stands up a real Orleans cluster, uses the <b>portal hub's</b>
/// <see cref="IMessageHub.JsonSerializerOptions"/> — the same options the layout stream
/// client uses — to simulate the wire round-trip, and verifies the coerced list is still
/// typed and dispatchable. Execution-path coverage (submissions actually reaching the
/// kernel and producing output) lives in the monolith test suite where the kernel is
/// co-located and far faster to exercise.
/// </summary>
// TODO: needs custom shared fixture — uses InteractiveMarkdownSiloConfigurator with
// AddInMemoryPersistence and ConfigurePortalMesh only (no Graph/AI/RLS). The SharedOrleansFixture
// adds Graph/AI/RLS which would change the registered services and TypeRegistry.
public class OrleansInteractiveMarkdownTest(ITestOutputHelper output) : TestBase(output)
{
    private TestCluster Cluster { get; set; } = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<InteractiveMarkdownSiloConfigurator>();
        builder.AddClientBuilderConfigurator<TestClientConfigurator>();
        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        if (Cluster is not null)
            await Cluster.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task<IMessageHub> CreatePortalHubAsync()
    {
        var meshHub = Cluster.Client.ServiceProvider.GetRequiredService<IMessageHub>();
        var routingService = Cluster.Client.ServiceProvider.GetRequiredService<IRoutingService>();

        var portalHub = meshHub.GetHostedHub(
            AddressExtensions.CreatePortalAddress(),
            config => config
                .AddLayoutClient()
                .WithInitialization(async (hub, _) =>
                {
                    var registration = await routingService.RegisterStreamAsync(hub);
                    hub.RegisterForDisposal(registration);
                }))!;

        await Task.Delay(500);
        return portalHub;
    }

    /// <summary>
    /// With the portal hub wired through the full Orleans mesh, serialise a submission
    /// list using the portal hub's <see cref="JsonSerializerOptions"/> (exactly what the
    /// layout stream does for the <c>object? CodeSubmissions</c> record field), parse it
    /// back as <see cref="JsonElement"/>, and assert that
    /// <see cref="MarkdownViewLogic.CoerceCodeSubmissions"/> recovers a typed, dispatchable
    /// list. This is the path that was broken before the coercion helper was added.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task CodeSubmissions_SurviveGrainSerializationRoundTrip()
    {
        var portal = await CreatePortalHubAsync();

        const string markdown = """
            ```csharp --render orleans-wire
            MeshWeaver.Layout.Controls.Markdown("Survived the grain boundary")
            ```

            ```csharp --execute
            var x = 123;
            ```
            """;

        var rendered = MarkdownViewLogic.Render(markdown, null, null);
        rendered.CodeSubmissions.Should().NotBeNull();
        rendered.CodeSubmissions!.Should().HaveCount(2);
        rendered.Html.Should().Contain(ExecutableCodeBlockRenderer.KernelAddressPlaceholder,
            "rendered HTML must embed the placeholder for later kernel-address substitution");

        // Serialise as `object` — exactly what the layout stream does for object-typed
        // record fields — using the portal hub's serializer, then reparse as JsonElement.
        var serialized = JsonSerializer.Serialize<object>(
            rendered.CodeSubmissions!, portal.JsonSerializerOptions);
        var asJsonElement = JsonDocument.Parse(serialized).RootElement;
        asJsonElement.ValueKind.Should().Be(JsonValueKind.Array,
            "the wire value the client sees must be a JsonElement array — the bug condition");

        // Coerce through the production path.
        var recovered = MarkdownViewLogic.CoerceCodeSubmissions(
            asJsonElement, portal.JsonSerializerOptions);
        recovered.Should().NotBeNull();
        recovered!.Should().HaveCount(2);
        recovered[0].Id.Should().Be("orleans-wire");
        recovered[0].Code.Should().Contain("Survived the grain boundary");
        recovered[1].Code.Should().Contain("var x = 123");
    }

    /// <summary>
    /// Plain markdown also round-trips as <see cref="JsonElement"/> when the server
    /// passes the string via the <c>object? Markdown</c> field of <see cref="MarkdownControl"/>.
    /// Verifies that the string-coercion helper unwraps a <c>JsonValueKind.String</c>
    /// element correctly — the other half of the reported bug (links rendering as
    /// literal <c>[text](url)</c>).
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task MarkdownString_RoundTripsThroughJsonElement()
    {
        var portal = await CreatePortalHubAsync();

        const string markdown = "See [the docs](https://example.com/docs) for details.";

        var serialized = JsonSerializer.Serialize<object>(markdown, portal.JsonSerializerOptions);
        var asJsonElement = JsonDocument.Parse(serialized).RootElement;
        asJsonElement.ValueKind.Should().Be(JsonValueKind.String);

        var recovered = MarkdownViewLogic.CoerceString(asJsonElement);
        recovered.Should().Be(markdown);

        // And the recovered string renders as proper HTML — the anchor exists,
        // not the literal "[the docs](...)" syntax.
        var html = MarkdownViewLogic.Render(recovered!, null, null).Html;
        html.Should().Contain("<a ");
        html.Should().Contain(">the docs</a>");
    }
}

/// <summary>
/// Silo configurator: portal mesh (includes <c>AddKernel()</c>) over in-memory persistence.
/// We don't execute code against the kernel in these tests — we only need the portal-client
/// serializer options and grain routing wiring to be identical to production.
/// </summary>
public class InteractiveMarkdownSiloConfigurator : ISiloConfigurator, IHostConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.ConfigureMeshWeaverServer()
            .AddMemoryGrainStorageAsDefault();
    }

    public void Configure(IHostBuilder hostBuilder)
    {
        hostBuilder.UseOrleansMeshServer()
            .AddInMemoryPersistence()
            .ConfigurePortalMesh();
    }
}
