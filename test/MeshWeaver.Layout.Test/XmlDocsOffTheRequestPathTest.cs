using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Serialization;
using MeshWeaver.Todo.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// The request and stream half of Systemorph/MeshWeaver#7386: answering a type listing or a JSON
/// schema never reads an XML documentation file on the answering hub's turn. The listing
/// (<see cref="GetDomainTypesRequest"/>, <see cref="DataModelReference"/>) carries each type's XML-doc
/// summary and the schema (<see cref="SchemaReference"/>) carries every walked property's, so both
/// are produced once the mesh's blocking-read edge has run.
///
/// <para>The instrument is the one <c>XmlDocsOffTheRenderPathTest</c> uses: the host's edge is HELD,
/// so <see cref="XmlDocsReader.TrySummary"/> answering <c>true</c> before the release means the
/// handler or the reducer read the file on its own thread. Before the fix every path below did: the
/// handlers called <c>GetDomainTypes</c>/<c>GenerateJsonSchema</c> inline and the schema and data
/// model were synchronous reducers over the store.</para>
/// </summary>
public class XmlDocsOffTheRequestPathTest(ITestOutputHelper output) : HubTestBase(output)
{
    // The probe type: documented, its documentation file ships beside the Todo assembly, and nothing
    // else in this mesh reads it. (A type declared in this test assembly has no documentation file.)
    private readonly HeldReads held = new();

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithServices(services => services.AddSingleton<IBlockingReadScheduler>(held))
            .AddData(data => data.AddSource(ds => ds.WithType<TodoCategory>(t => t.WithInitialData([new TodoCategory { Name = "Work", DisplayName = "Work", Description = "Work items" }]))));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithRoutes(r => r.RouteAddress(HostType, (_, d) => d.Package()))
            .AddData();

    /// <summary>
    /// A REMOTE subscriber of the schema and data-model streams receives their value. The owner's
    /// subscribe path configures the stream it builds with the subscriber's client id; a stream that
    /// drops that configuration emits under a fresh id the waiting remote stream never matches.
    /// </summary>
    [HubFact]
    public async Task RemoteSchemaAndDataModelStreams_ReachTheSubscriber()
    {
        held.Release();
        var host = GetHost();
        var workspace = GetClient().GetWorkspace();

        var schema = await workspace.GetRemoteStream(CreateHostAddress(), new SchemaReference(nameof(TodoCategory)))
            .Select(c => c.Value)
            .Should().Within(10.Seconds()).Match(v => v is not null, "the remote schema stream carries the owner's schema");
        JsonSerializer.Serialize(schema, host.JsonSerializerOptions).Should().Contain(ExpectedSummary(host));

        var model = await workspace.GetRemoteStream(CreateHostAddress(), new DataModelReference())
            .Select(c => c.Value)
            .Should().Within(10.Seconds()).Match(v => v is not null, "the remote data-model stream carries the owner's types");
        JsonSerializer.Serialize(model, host.JsonSerializerOptions).Should().Contain(nameof(TodoCategory));
    }

    /// <summary>
    /// A GetDomainTypesRequest whose description read is still queued when the owning hub is disposed
    /// is answered with a ShuttingDown failure — never left to the caller's timeout.
    /// </summary>
    [HubFact]
    public async Task GetDomainTypes_OwnerDisposedWhileReading_AnswersShuttingDown()
    {
        var host = GetHost();
        var answer = GetClient().Observe(new GetDomainTypesRequest(), o => o.WithTarget(CreateHostAddress()))
            .Materialize()
            .Should().Within(10.Seconds()).Match(n => n.Kind != NotificationKind.OnCompleted, "the caller is answered when the owner goes away mid-read");

        await held.Requests.Should().Within(10.Seconds()).Emit("the description read is queued on the held edge");
        host.Dispose();

        var notification = await answer;
        notification.Kind.Should().Be(NotificationKind.OnError, "the owner never produced the listing");
        var failure = notification.Exception.Should().BeOfType<DeliveryFailureException>().Subject;
        failure.Failure.ErrorType.Should().Be(ErrorType.ShuttingDown, "a recycle mid-read is a retryable shutdown, not a fault");
    }

    [HubFact]
    public async Task GetDomainTypes_ReadsNoDocumentationFile_TheEdgeDoes()
    {
        var host = GetHost();
        var pending = host.Observe(new GetDomainTypesRequest(), o => o.WithTarget(host.Address))
            .Should().Within(10.Seconds()).Emit("the listing is answered once the edge has run");

        await AssertHeldThenRelease(host, "the GetDomainTypesRequest handler");

        var response = await pending;
        response.Message.Types.Single(t => t.Name == nameof(TodoCategory)).Description
            .Should().Contain(ExpectedSummary(host), "a type's description is its XML-doc summary");
    }

    [HubFact]
    public async Task DataModelReference_ReadsNoDocumentationFile_TheEdgeDoes()
    {
        var host = GetHost();
        var pending = host.Observe(new GetDataRequest(new DataModelReference()), o => o.WithTarget(host.Address))
            .Should().Within(10.Seconds()).Emit("the data model is answered once the edge has run");

        await AssertHeldThenRelease(host, "the DataModelReference GetDataRequest");

        var response = await pending;
        var types = response.Message.Data.As<IReadOnlyList<TypeDescription>>(host.JsonSerializerOptions)!;
        types.Single(t => t.Name == nameof(TodoCategory)).Description.Should().Contain(ExpectedSummary(host));
    }

    [HubFact]
    public async Task SchemaReference_ReadsNoDocumentationFile_TheEdgeDoes()
    {
        var host = GetHost();
        var pending = host.Observe(new GetDataRequest(new SchemaReference(nameof(TodoCategory))), o => o.WithTarget(host.Address))
            .Should().Within(10.Seconds()).Emit("the schema is answered once the edge has run");

        await AssertHeldThenRelease(host, "the SchemaReference GetDataRequest");

        var response = await pending;
        var schema = response.Message.Data.As<SchemaInfo>(host.JsonSerializerOptions)!;
        schema.Type.Should().Be(nameof(TodoCategory));
        schema.Schema.Should().Contain(ExpectedSummary(host), "the schema carries the type's XML-doc summary");
    }

    [HubFact]
    public async Task SchemaStream_ReadsNoDocumentationFile_TheEdgeDoes()
    {
        var host = GetHost();
        var stream = host.GetWorkspace().GetNullableStream(new SchemaReference(nameof(TodoCategory)));
        Assert.NotNull(stream);
        var pending = stream!.Where(c => c.Value is not null)
            .Should().Within(10.Seconds()).Emit("the schema stream emits once the edge has run");

        await AssertHeldThenRelease(host, "subscribing to the schema stream");

        var change = await pending;
        change.Value.As<SchemaInfo>(host.JsonSerializerOptions)!.Schema.Should().Contain(ExpectedSummary(host));
    }

    private async Task AssertHeldThenRelease(IMessageHub host, string path)
    {
        await held.Requests.Should().Within(10.Seconds()).Emit($"{path} routes its documentation read to the blocking-read edge");
        host.XmlDocs().TrySummary(typeof(TodoCategory), out _).Should().BeFalse(
            $"{path} must not read the documentation file on its own turn — the edge is held, so a loaded snapshot means it read it");
        held.Release();
    }

    private static string ExpectedSummary(IMessageHub host)
    {
        host.XmlDocs().TrySummary(typeof(TodoCategory), out var summary).Should().BeTrue("the released edge loaded the snapshot");
        summary.Should().NotBeNullOrWhiteSpace("the probe type is documented");
        // The first line: a schema carries the text JSON-escaped, so a multi-line summary is not a
        // substring of it as written.
        return summary.Split('\n')[0].Trim();
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
