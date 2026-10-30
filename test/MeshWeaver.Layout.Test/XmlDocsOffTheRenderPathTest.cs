using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Serialization;
using MeshWeaver.Todo.LayoutAreas;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Systemorph/MeshWeaver#7386: building a view or configuring a hub's layout never reads an XML
/// documentation file. The cold read of a documentation file goes through the mesh's blocking-read
/// edge (<see cref="IBlockingReadScheduler"/>) and the description binds once it has run.
///
/// <para>The instrument is the reader's own memory-only lookup, <see cref="XmlDocsReader.TrySummary"/>:
/// it answers <c>false</c> exactly while an assembly's documentation snapshot has never been loaded.
/// The host's blocking-read edge is HELD — it records the request and runs nothing until the test
/// releases it — so if the snapshot is loaded before the release, something read the file on its
/// own thread. Before the fix both reads below happened that way: the Catalog's description line
/// called <c>Summary</c> while the view was built, and every area registered with a method read its
/// summary while the hub's layout was being configured.</para>
/// </summary>
public class XmlDocsOffTheRenderPathTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string ProbeArea = "Probe";

    // A documented, catalog-visible view method in an assembly whose documentation file nothing else
    // in this mesh reads. Only its summary matters; the area is listed, never rendered.
    private static readonly MethodInfo ProbeMethod =
        typeof(TodoLayoutAreas).GetMethod(nameof(TodoLayoutAreas.Planning))!;

    private readonly HeldReads held = new();

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithServices(services => services.AddSingleton<IBlockingReadScheduler>(held))
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .AddData(data => data.AddSource(ds => ds.WithType<DataRecord>(t => t.WithInitialData(DataRecord.InitialData))))
            .AddLayout(l => l
                .AddDomainLayoutAreas()
                .WithView(ProbeArea, (Func<LayoutAreaHost, RenderingContext, UiControl>)TodoLayoutAreas.Planning));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithRoutes(r => r.RouteAddress(HostType, (_, d) => d.Package()))
            .AddLayoutClient();

    [HubFact]
    public async Task BuildingTheCatalog_ReadsNoDocumentationFile_TheEdgeDoes()
    {
        var host = GetHost();
        var docs = host.XmlDocs();
        var reference = DomainLayoutAreas.GetCatalogReference(nameof(DataRecord));
        var stream = GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(CreateHostAddress(), reference);

        // The view is built and served while the edge is still held: nothing waits for the read.
        var catalog = await stream.GetControlStream(reference.Area!)
            .Should().Within(10.Seconds()).Match(x => x is StackControl, "the Catalog renders before its description is read");
        catalog.Should().BeOfType<StackControl>();

        // The frame above exists only after the hub's layout was configured and the Catalog's
        // generator ran, so any read either of them did on its own thread has happened by now.
        docs.TrySummary(typeof(DataRecord), out _).Should().BeFalse(
            "building the Catalog must not read the documentation file of the type it describes — the edge is held, so a loaded snapshot means the view read it");
        docs.TrySummary(ProbeMethod, out _).Should().BeFalse(
            "configuring the hub's layout must not read the documentation of the areas' view methods");
        await held.Requests.Should().Within(10.Seconds()).Emit("the description's documentation read is routed to the blocking-read edge");

        // Positive control: the edge performs the read, after which the lookup is a memory hit.
        held.Release();
        await host.ObserveXmlDocsSummary(typeof(DataRecord)).Should().Within(10.Seconds()).Emit("the released edge loads the snapshot");
        docs.TrySummary(typeof(DataRecord), out _).Should().BeTrue("the edge loaded the snapshot");
    }

    [HubFact]
    public async Task ListedAreaDescriptions_AreTheViewMethodSummaries_ReadAtTheEdge()
    {
        var host = GetHost();
        var docs = host.XmlDocs();
        var client = GetClient();

        var listing = client.Observe(new GetLayoutAreasRequest(), o => o.WithTarget(CreateHostAddress()));
        var pending = listing.Should().Within(10.Seconds()).Emit("the listing is answered once the edge has run");
        await held.Requests.Should().Within(10.Seconds()).Emit("the area descriptions' documentation read is routed to the blocking-read edge");
        docs.TrySummary(ProbeMethod, out _).Should().BeFalse(
            "neither configuring the layout nor handling the request may read the documentation file — the edge is held");

        held.Release();
        var response = await pending;
        var probe = response.Message.Areas.Single(a => a.Area == ProbeArea);

        docs.TrySummary(ProbeMethod, out var expected).Should().BeTrue("the edge loaded the snapshot");
        expected.Should().NotBeNullOrWhiteSpace("the probe method is documented");
        probe.Description.Should().Be(expected, "an undeclared description is the view method's XML-doc summary");
    }

    /// <summary>
    /// The host's blocking-read edge, held: each request is announced on <see cref="Requests"/> and its
    /// work runs only once <see cref="Release"/> is called (on the releasing thread).
    /// </summary>
    private sealed class HeldReads : IBlockingReadScheduler
    {
        private readonly ReplaySubject<Unit> released = new(1);
        private readonly ReplaySubject<Unit> requests = new(1);

        public IObservable<Unit> Requests => requests;

        public void Release() => released.OnNext(Unit.Default);

        public IObservable<T> InvokeBlocking<T>(Func<CancellationToken, T> work)
            => Observable.Defer(() =>
            {
                requests.OnNext(Unit.Default);
                return released.Take(1).Select(_ => work(CancellationToken.None));
            });
    }
}
