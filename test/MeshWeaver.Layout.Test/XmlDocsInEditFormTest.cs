using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
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
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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

    private const string TwoBuildsArea = "TwoBuilds";

    private readonly HeldReads held = new();

    // Two builds of one documented type, compiled only when the two-builds area renders.
    private readonly Lazy<(Type First, Type Second)> builds = new(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "xmldocs-editform-" + Guid.NewGuid().ToString("N"));
        return (EmitBuild(Path.Combine(root, "first"), "The first build's label."),
                EmitBuild(Path.Combine(root, "second"), "The second build's label."));
    });

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithServices(services => services.AddSingleton<IBlockingReadScheduler>(held))
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .AddLayout(l => l
                .WithView(FormArea,
                    (Func<LayoutAreaHost, RenderingContext, UiControl>)((host, _) => host.Edit(new TodoCategory { Name = "Probe", DisplayName = "Probe", Description = "Probe" }, "probe")))
                .WithView(TwoBuildsArea,
                    (Func<LayoutAreaHost, RenderingContext, UiControl>)((host, _) => Controls.Stack
                        .WithView(host.Hub.Edit(builds.Value.First, "first"))
                        .WithView(host.Hub.Edit(builds.Value.Second, "second")))));

    /// <summary>
    /// Compiles <c>Probe.Subject</c> with its <c>Label</c> documented as <paramref name="labelSummary"/>,
    /// writing <c>{assembly}.xml</c> beside the dll (where the reader looks), into its own collectible
    /// context — the layout of a compiled NodeType build.
    /// </summary>
    private static Type EmitBuild(string directory, string labelSummary)
    {
        Directory.CreateDirectory(directory);
        const string assemblyName = "DynamicNode_EditFormProbe";
        var source = $$"""
            namespace Probe;
            /// <summary>The probe.</summary>
            public sealed class Subject
            {
                /// <summary>{{labelSummary}}</summary>
                public string Label { get; set; } = "";
            }
            """;
        var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var compilation = CSharpCompilation.Create(assemblyName,
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(documentationMode: DocumentationMode.Diagnose))],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDirectory, "System.Runtime.dll")),
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var dllPath = Path.Combine(directory, "EditFormProbe.dll");
        using (var dll = File.Create(dllPath))
        using (var xml = File.Create(Path.Combine(directory, assemblyName + ".xml")))
        {
            var result = compilation.Emit(dll, xmlDocumentationStream: xml);
            result.Success.Should().BeTrue(string.Join("\n", result.Diagnostics));
        }
        var context = new AssemblyLoadContext(assemblyName + "-" + Path.GetFileName(directory), isCollectible: true);
        return context.LoadFromAssemblyPath(dllPath).GetType("Probe.Subject")
            ?? throw new InvalidOperationException("Probe.Subject is not in the emitted assembly");
    }

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

        // The bound entry is SEEDED while the read is still held: a client's first-emission gate waits
        // for the entry to exist, so an unseeded entry would hold the field in its loading shape until
        // the cold read finished — the very wait this change removes.
        await stream.GetDataStream<string>(pointer)
            .Should().Within(10.Seconds()).Match(x => x == string.Empty, "rendering the field seeds its description entry before the edge has read anything");
        docs.TrySummary(Documented, out _).Should().BeFalse("the seed must not come from a read — the edge is still held");

        held.Release();

        var bound = await stream.GetDataStream<string>(pointer)
            .Should().Within(10.Seconds()).Match(x => !string.IsNullOrEmpty(x), "the summary is fed into the bound entry once the edge has read it");
        docs.TrySummary(Documented, out var expected).Should().BeTrue("the edge loaded the snapshot");
        expected.Should().NotBeNullOrWhiteSpace("the probe property is documented");
        bound.Should().Be(expected, "the bound description is the property's XML-doc summary");
    }

    /// <summary>
    /// A documentation ID is unique only within an assembly. Two builds of one type — the shape every
    /// NodeType recompile produces — declare the same <c>P:Probe.Subject.Label</c> with different
    /// summaries; one area editing both must bind each field to its OWN build's summary. Keyed by the
    /// documentation ID alone, both fields shared one <c>/data</c> entry and showed the same text.
    /// </summary>
    [HubFact]
    public async Task TwoBuildsOfOneType_InOneArea_EachFieldBindsItsOwnBuildsSummary()
    {
        var stream = GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(CreateHostAddress(), new LayoutAreaReference(TwoBuildsArea));

        var stack = await stream.GetControlStream(TwoBuildsArea)
            .Should().Within(10.Seconds()).Match(x => x is StackControl, "the area renders both forms at once");
        var pointers = System.Collections.Immutable.ImmutableList<JsonPointerReference>.Empty;
        foreach (var child in stack.Should().BeOfType<StackControl>().Subject.Areas)
        {
            var editor = await stream.GetControlStream(child.Area!.ToString()!)
                .Should().Within(10.Seconds()).Match(x => x is EditorControl, "each build's form renders");
            pointers = pointers.Add(editor.Should().BeOfType<EditorControl>().Subject
                .Areas.SelectMany(a => a.Skins.OfType<PropertySkin>())
                .Single(s => s.Name is string name && name == "label")
                .Description.Should().BeOfType<JsonPointerReference>("both builds are cold while the edge is held").Subject);
        }
        pointers.Should().HaveCount(2);
        pointers[0].Pointer.Should().NotBe(pointers[1].Pointer,
            "two builds documenting the same ID differently must not share one description entry");

        held.Release();
        await stream.GetDataStream<string>(pointers[0])
            .Should().Within(10.Seconds()).Match(x => x == "The first build's label.", "the first form binds the first build's summary");
        await stream.GetDataStream<string>(pointers[1])
            .Should().Within(10.Seconds()).Match(x => x == "The second build's label.", "the second form binds the second build's summary");
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
