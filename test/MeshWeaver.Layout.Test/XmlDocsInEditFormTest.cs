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
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Serialization;
using MeshWeaver.Todo.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Systemorph/MeshWeaver#7386, the editor-form half: building an <c>Edit</c> form never reads an XML
/// documentation file. A field without a declared <c>[Description]</c> is described by its property's
/// XML-doc summary; when that summary is not in memory yet, the field's <see cref="PropertySkin"/>
/// carries a DATA-BOUND description, the cold read runs on the mesh's blocking-read edge, and the
/// summary arrives as data the client binds.
///
/// <para>The instrument is the reader's memory-only lookup, <see cref="XmlDocsReader.TrySummary"/>,
/// with the host's blocking-read edge HELD (it records the request and runs nothing until released):
/// a snapshot loaded before the release was read on the thread that built the form. Before the fix the
/// skin called <c>Summary</c> while the form was built, so the snapshot was loaded and the skin held the
/// text.</para>
/// </summary>
public class XmlDocsInEditFormTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string FormArea = "Form";

    // A documented property with no [Description], in an assembly whose documentation file nothing
    // else in this mesh reads.
    private static readonly PropertyInfo Documented = typeof(TodoCategory).GetProperty(nameof(TodoCategory.DisplayName))!;

    private readonly HeldReads held = new();

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithServices(services => services.AddSingleton<IBlockingReadScheduler>(held))
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .AddLayout(l => l.WithView(FormArea,
                (Func<LayoutAreaHost, RenderingContext, UiControl>)((host, _) => host.Edit(new TodoCategory { Name = "Probe", DisplayName = "Probe", Description = "Probe" }, "probe"))));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithRoutes(r => r.RouteAddress(HostType, (_, d) => d.Package()))
            .AddLayoutClient();

    private ISynchronizationStream<JsonElement> OpenForm()
        => GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(CreateHostAddress(), new LayoutAreaReference(FormArea));

    private static async Task<PropertySkin> DisplayNameSkin(ISynchronizationStream<JsonElement> stream)
    {
        var control = await stream.GetControlStream(FormArea)
            .Should().Within(10.Seconds()).Match(x => x is EditorControl, "the form renders without waiting for any documentation read");
        return control.Should().BeOfType<EditorControl>().Subject
            .Areas.SelectMany(a => a.Skins.OfType<PropertySkin>())
            .Single(s => s.Name is string name && name == "displayName");
    }

    [HubFact]
    public async Task BuildingTheForm_ReadsNoDocumentationFile_TheSummaryArrivesAsData()
    {
        var docs = GetHost().XmlDocs();
        var stream = OpenForm();

        // The form is built and served while the edge is still held.
        var skin = await DisplayNameSkin(stream);
        docs.TrySummary(Documented, out _).Should().BeFalse(
            "building the form must not read the documentation file of the type it edits — the edge is held, so a loaded snapshot means the form builder read it");
        var pointer = skin.Description.Should().BeOfType<JsonPointerReference>(
            "a description whose summary is not in memory is data-bound, never read while the form is built").Subject;

        await held.Requests.Should().Within(10.Seconds()).Emit("the field's documentation read is routed to the blocking-read edge");
        held.Release();

        var bound = await stream.GetDataStream<string>(pointer)
            .Should().Within(10.Seconds()).Match(x => !string.IsNullOrEmpty(x), "the summary is fed into the bound entry once the edge has read it");
        docs.TrySummary(Documented, out var expected).Should().BeTrue("the edge loaded the snapshot");
        expected.Should().NotBeNullOrWhiteSpace("the probe property is documented");
        bound.Should().Be(expected, "the bound description is the property's XML-doc summary");
    }

    /// <summary>
    /// The control: once the summary is in memory, the form carries the text itself, as it always did —
    /// a warm mesh renders exactly as before and binds nothing.
    /// </summary>
    [HubFact]
    public async Task WithTheSummaryInMemory_TheFormCarriesTheTextItself()
    {
        var host = GetHost();
        held.Release();
        var expected = await host.ObserveXmlDocsSummary(Documented)
            .Should().Within(10.Seconds()).Emit("the released edge loads the snapshot");
        expected.Should().NotBeNullOrWhiteSpace("the probe property is documented");

        var skin = await DisplayNameSkin(OpenForm());
        skin.Description.Should().Be(expected, "a summary already in memory is written into the skin directly");
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
