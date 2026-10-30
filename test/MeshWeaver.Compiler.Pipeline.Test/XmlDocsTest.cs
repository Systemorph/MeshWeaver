using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Serialization;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The platform's own XML-documentation reader (<see cref="XmlDocs"/>), which replaced the
/// third-party Namotion.Reflection reader. The expected texts in the parity facts were produced by
/// Namotion's <c>GetXmlDocsSummary</c> on these exact members before the package was removed, so a
/// change in rendering (whitespace, <c>&lt;see&gt;</c>, <c>&lt;paramref&gt;</c>, <c>&lt;c&gt;</c>,
/// record <c>&lt;param&gt;</c> fallback, <c>&lt;inheritdoc/&gt;</c>) is caught here.
/// A full-assembly comparison over six platform assemblies (72,373 types and members) agreed on
/// every member where Namotion produced text, except where Namotion's ID was wrong — see
/// <c>Doc/Architecture/XmlDocumentationReader</c>.
/// </summary>
public sealed class XmlDocsTest
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    // The mesh-owned reader as a mesh's root type registry creates it.
    private readonly XmlDocsReader Docs = MessageHubExtensions.CreateTypeRegistry().XmlDocs();

    /// <summary>
    /// The reader is owned by the MESH: every registry in one chain shares the root's instance, and a
    /// second mesh gets its own — so neither its snapshots nor its cached misses outlive the mesh.
    /// <b>Should fail if</b> the reader goes back to a static, or a hub registry creates its own.
    /// </summary>
    [Fact]
    public void TheReader_IsOnePerMesh_SharedDownTheRegistryChain()
    {
        var mesh = MessageHubExtensions.CreateTypeRegistry();
        var hub = MessageHubExtensions.CreateTypeRegistry(mesh);
        var nested = MessageHubExtensions.CreateTypeRegistry(hub);
        hub.XmlDocs().Should().BeSameAs(mesh.XmlDocs());
        nested.XmlDocs().Should().BeSameAs(mesh.XmlDocs());
        MessageHubExtensions.CreateTypeRegistry().XmlDocs().Should().NotBeSameAs(mesh.XmlDocs(), "another mesh owns another reader");
    }

    /// <summary>
    /// Every mesh owns a reader, so a process running many meshes parses the same documentation files
    /// once per mesh — measured on a test host at ~10 MB of rendered text per mesh. The texts of an
    /// assembly that can never unload are interned, so two meshes' readers hold ONE copy of each.
    /// <b>Should fail if</b> the parse stops interning, and every mesh again carries its own copy
    /// of every summary in every assembly it has read.
    /// </summary>
    [Fact]
    public void TwoMeshes_ShareOneCopyOfEachSummaryText()
    {
        var method = typeof(ObjectPolymorphicConverter).GetMethod(nameof(ObjectPolymorphicConverter.CanConvert))!;
        var first = MessageHubExtensions.CreateTypeRegistry().XmlDocs().Summary(method);
        var second = MessageHubExtensions.CreateTypeRegistry().XmlDocs().Summary(method);
        first.Length.Should().BeGreaterThan(0);
        ReferenceEquals(first, second).Should().BeTrue("both meshes' snapshots of a non-collectible assembly point at the interned text");
    }

    /// <summary>
    /// Systemorph/MeshWeaver#7386: the reactive lookup never reads a documentation file on the
    /// subscribing thread. A cold summary is read through the blocking-read edge it is handed, and only
    /// once that edge runs; afterwards the answer is in memory and the lookup emits synchronously
    /// without asking the edge at all. <see cref="XmlDocsReader.TrySummary"/> is the instrument: it
    /// reads nothing, and says whether the snapshot has been loaded.
    /// <b>Should fail if</b> <c>ObserveSummary</c>/<c>WhenLoaded</c> reads on subscribe, or if
    /// <c>TrySummary</c> starts loading files.
    /// </summary>
    [Fact]
    public async Task TheReactiveLookup_ReadsAtTheEdge_ThenAnswersFromMemory()
    {
        var docs = MessageHubExtensions.CreateTypeRegistry().XmlDocs();
        var method = typeof(ObjectPolymorphicConverter).GetMethod(nameof(ObjectPolymorphicConverter.CanConvert))!;
        docs.TrySummary(method, out _).Should().BeFalse("a fresh mesh's reader has loaded nothing, and TrySummary itself reads nothing");

        var edge = new HeldReads();
        var pending = docs.ObserveSummary(method, edge).Should().Within(TimeSpan.FromSeconds(10)).Emit("the released edge reads the summary");
        await edge.Requests.Should().Within(TimeSpan.FromSeconds(10)).Emit("a cold summary is routed to the edge");
        docs.TrySummary(method, out _).Should().BeFalse("subscribing read nothing — the edge is still held");

        edge.Release();
        var summary = await pending;
        summary.Should().StartWith("Determines whether this converter applies.");
        docs.TrySummary(method, out var inMemory).Should().BeTrue("the edge loaded the snapshot");
        inMemory.Should().Be(summary);

        // Warm: answered on subscribe, never asking the edge (which would refuse).
        var refusing = new RefusingReads();
        var emitted = ImmutableList<string>.Empty;
        using (docs.ObserveSummary(method, refusing).Subscribe(s => emitted = emitted.Add(s)))
            emitted.Should().Equal([summary], "a warm lookup emits synchronously on subscribe");
        refusing.Asked.Should().BeFalse("a warm lookup never goes to the edge");
    }

    /// <summary>A blocking-read edge that announces each request and runs it only once released.</summary>
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

    /// <summary>A blocking-read edge that must not be used.</summary>
    private sealed class RefusingReads : IBlockingReadScheduler
    {
        private int asked;

        public bool Asked => Volatile.Read(ref asked) != 0;

        public IObservable<T> InvokeBlocking<T>(Func<CancellationToken, T> work)
        {
            Volatile.Write(ref asked, 1);
            return Observable.Throw<T>(new InvalidOperationException("a warm lookup must not read"));
        }
    }

    [Fact]
    public void AMethodSummary_RendersLikeNamotion()
        => Docs.Summary(typeof(ObjectPolymorphicConverter).GetMethod(nameof(ObjectPolymorphicConverter.CanConvert))!)
            .Should().Be("Determines whether this converter applies. It only handles the open object\ntype; concrete types are handled by their own type info.");

    [Fact]
    public void ASeeCref_RendersAsItsLastNameSegment()
        // <see cref="F:...WellKnownUsers.System"/> → "System"; <c>…</c> renders as its text.
        => Docs.Summary(typeof(AccessService).GetField("SystemObjectId", Any)!)
            .Should().Be("The identity the platform writes as (WellKnownUsers.System; Messaging.Hub sits below\nMesh.Contract, so the value is mirrored here once).");

    [Fact]
    public void APositionalRecordProperty_FallsBackToTheTypesParam()
    {
        // ExceptionCallback has no <summary> of its own: the record documents it as <param>, and a
        // <paramref name="Action"/> inside renders as the name.
        Docs.Summary(typeof(ExecutionRequest).GetProperty(nameof(ExecutionRequest.ExceptionCallback))!)
            .Should().Be("Invoked with the exception when Action faults, giving the poster a chance to handle the failure.");

        // A PRIVATE NESTED record: the nested type's ID joins with '.', and the continuation line
        // keeps its indentation because only the FIRST line's indentation is stripped.
        var teardown = typeof(MessageHub).GetNestedType("TeardownCause", Any)!;
        Docs.Summary(teardown.GetProperty("Reason", Any)!)
            .Should().Be("WHY. For a cascade this is the ORIGINATING cause, passed to children\n            unchanged so the chain names the event that started it however deep the tree is.");
        Docs.Summary(teardown).Should().StartWith("🚨 The ONE claim on this hub's teardown cause — FIRST CAUSE WINS, atomically.\n");
    }

    [Fact]
    public void AConstructedGenericType_ReadsItsDefinitionsDocs()
        // <typeparamref name="T"/> renders as nothing (Namotion did the same: "of type .").
        => Docs.Summary(typeof(MessageDelivery<string>))
            .Should().Be("Strongly-typed delivery envelope carrying a payload of type .\nConcrete implementation of the abstract MessageDelivery base.");

    [Fact]
    public void ANestedGenericType_AndACrefToAGenericMethod()
        => Docs.Summary(typeof(BoundedMergeExtensions).GetNestedType("BoundedMerge`1", Any)!)
            .Should().Be("One subscription of MergeBounded``1.");

    [Fact]
    public void AConstructor()
        => Docs.Summary(typeof(AccessService).GetConstructor([typeof(Microsoft.Extensions.Logging.ILoggerFactory)])!)
            .Should().Be("Creates an access service that logs context transitions and leak\ndiagnostics through the MeshWeaver.AccessContext category.");

    [Fact]
    public void AnInheritDoc_ResolvesFromTheInterface()
    {
        var property = Docs.Summary(typeof(MessageHub).GetProperty(nameof(MessageHub.ShuttingDown))!);
        property.Should().StartWith("IsShuttingDown as a SOURCE: fires Unit exactly once — at the\nFIRST instant this hub becomes part of a shutdown — then completes.");
        property.Should().Be(Docs.Summary(typeof(IMessageHub).GetProperty(nameof(IMessageHub.ShuttingDown))!));

        var method = Docs.Summary(typeof(MeshWeaver.Mesh.Completion.AddressCatalogAutocompleteProvider).GetMethod("GetItems")!);
        method.Should().StartWith("Gets autocomplete suggestions from this provider as a progressive snapshot stream. Each\nemission is the provider's current best IReadOnlyCollection`1, sorted by\nPriority descending;");
    }

    /// <summary>
    /// Where Namotion's documentation ID was wrong it read NOTHING (or a different overload); the
    /// compiler-exact ID reads the member's own docs. <b>Should fail if</b> generic-method arity,
    /// by-ref parameters or an <c>&lt;inheritdoc cref&gt;</c> on a field stop resolving.
    /// </summary>
    [Fact]
    public void WhereNamotionsIdWasWrong_TheMembersOwnDocsAreRead()
    {
        // Generic method: Namotion dropped the ``1 arity and matched the NON-generic overload.
        var generic = typeof(MeshWeaver.Mesh.Services.IMeshService).GetMethods()
            .Single(m => m is { Name: "Query", IsGenericMethodDefinition: true });
        var nonGeneric = typeof(MeshWeaver.Mesh.Services.IMeshService).GetMethods()
            .Single(m => m is { Name: "Query", IsGenericMethodDefinition: false });
        XmlDocs.DocumentationId(generic).Should().Be("M:MeshWeaver.Mesh.Services.IMeshService.Query``1(MeshWeaver.Mesh.Services.MeshQueryRequest)");
        Docs.Summary(generic).Should().NotBeNullOrEmpty().And.NotBe(Docs.Summary(nonGeneric));

        // By-ref parameter: Namotion spelled it "System.Type&" and found nothing.
        var byRef = typeof(MeshWeaver.Mesh.Services.IMeshContentTypeRegistry).GetMethod("TryResolveByDiscriminator")!;
        XmlDocs.DocumentationId(byRef).Should().Be("M:MeshWeaver.Mesh.Services.IMeshContentTypeRegistry.TryResolveByDiscriminator(System.String,System.Type@)");
        Docs.Summary(byRef).Should().NotBeNullOrEmpty();

        // <inheritdoc cref="NodeTypeNotRegisteredKey"/> on a private const.
        var extensions = typeof(MeshWeaver.Mesh.MeshNode).Assembly.GetType("MeshWeaver.Mesh.MeshExtensions")!;
        var inheriting = Docs.Summary(extensions.GetField("ValidationFailedKey", Any)!);
        inheriting.Should().NotBeNullOrEmpty();
        inheriting.Should().Be(Docs.Summary(extensions.GetField("NodeTypeNotRegisteredKey", Any)!));
    }

    [Fact]
    public void AnUndocumentedOrUnaddressableMember_ReadsEmpty()
    {
        Docs.Summary(typeof(XmlDocsTest)).Should().BeEmpty("the test assembly ships no XML documentation");
        Docs.Summary(typeof(List<>).GetGenericArguments()[0]).Should().BeEmpty("a generic parameter has no documentation ID");
        Docs.Summary(typeof(int[])).Should().BeEmpty();
    }

    [Theory]
    [InlineData(nameof(IdSubjects.Plain), "M:MeshWeaver.Graph.Test.IdSubjects.Plain")]
    [InlineData(nameof(IdSubjects.Shapes), "M:MeshWeaver.Graph.Test.IdSubjects.Shapes(System.Int32@,System.String@,System.Int32@,System.String[],System.Int32[0:,0:],System.Nullable{System.Int32},System.Collections.Generic.Dictionary{System.String,System.Collections.Generic.List{System.Int32}})")]
    [InlineData(nameof(IdSubjects.Generic), "M:MeshWeaver.Graph.Test.IdSubjects.Generic``2(``0,System.Collections.Generic.List{``1},``0[])")]
    public void DocumentationIds_FollowTheCompilersEncoding(string method, string expected)
        => XmlDocs.DocumentationId(typeof(IdSubjects).GetMethod(method)!).Should().Be(expected);

    [Fact]
    public void DocumentationIds_ForTypesAndMembersOfEveryKind()
    {
        XmlDocs.DocumentationId(typeof(IdSubjects)).Should().Be("T:MeshWeaver.Graph.Test.IdSubjects");
        XmlDocs.DocumentationId(typeof(IdSubjects.Outer<>.Inner<>)).Should().Be("T:MeshWeaver.Graph.Test.IdSubjects.Outer`1.Inner`1");
        XmlDocs.DocumentationId(typeof(IdSubjects.Outer<int>.Inner<string>)).Should().Be("T:MeshWeaver.Graph.Test.IdSubjects.Outer`1.Inner`1");
        XmlDocs.DocumentationId(typeof(IdSubjects.Outer<int>.Inner<string>).GetMethod("Take")!)
            .Should().Be("M:MeshWeaver.Graph.Test.IdSubjects.Outer`1.Inner`1.Take(`0,`1,MeshWeaver.Graph.Test.IdSubjects.Outer{`0}.Inner{`1})");
        XmlDocs.DocumentationId(typeof(IdSubjects).GetConstructor(Type.EmptyTypes)!).Should().Be("M:MeshWeaver.Graph.Test.IdSubjects.#ctor");
        XmlDocs.DocumentationId(typeof(IdSubjects).GetConstructor([typeof(int)])!).Should().Be("M:MeshWeaver.Graph.Test.IdSubjects.#ctor(System.Int32)");
        XmlDocs.DocumentationId(typeof(IdSubjects).TypeInitializer!).Should().Be("M:MeshWeaver.Graph.Test.IdSubjects.#cctor");
        XmlDocs.DocumentationId(typeof(IdSubjects).GetProperty("Item")!).Should().Be("P:MeshWeaver.Graph.Test.IdSubjects.Item(System.String,System.Int32)");
        XmlDocs.DocumentationId(typeof(IdSubjects).GetProperty(nameof(IdSubjects.Name))!).Should().Be("P:MeshWeaver.Graph.Test.IdSubjects.Name");
        XmlDocs.DocumentationId(typeof(IdSubjects).GetField(nameof(IdSubjects.Counter))!).Should().Be("F:MeshWeaver.Graph.Test.IdSubjects.Counter");
        XmlDocs.DocumentationId(typeof(IdSubjects).GetEvent(nameof(IdSubjects.Changed))!).Should().Be("E:MeshWeaver.Graph.Test.IdSubjects.Changed");
        XmlDocs.DocumentationId(typeof(IdSubjects).GetMethod("op_Implicit")!).Should().Be("M:MeshWeaver.Graph.Test.IdSubjects.op_Implicit(MeshWeaver.Graph.Test.IdSubjects)~System.Int32");
        XmlDocs.DocumentationId(typeof(IdSubjects).GetMethod("System.IDisposable.Dispose", Any)!).Should().Be("M:MeshWeaver.Graph.Test.IdSubjects.System#IDisposable#Dispose");
    }
}

#pragma warning disable CS0067, CS0649 // members exist only to be addressed by reflection
/// <summary>Members of every shape a documentation ID encodes.</summary>
public sealed class IdSubjects : IDisposable
{
    static IdSubjects() { }
    public IdSubjects() { }
    public IdSubjects(int seed) => Counter = seed;

    public int Counter;
    public string Name { get; set; } = "";
    public event EventHandler? Changed;
    public int this[string key, int index] => index;

    public void Plain() { }
    public void Shapes(ref int a, out string b, in int c, string[] d, int[,] e, int? f,
        Dictionary<string, List<int>> g) => b = "";
    public void Generic<TKey, TValue>(TKey key, List<TValue> values, TKey[] keys) { }

    public static implicit operator int(IdSubjects subject) => subject.Counter;
    void IDisposable.Dispose() { }

    public sealed class Outer<T>
    {
        public sealed class Inner<U>
        {
            public void Take(T outer, U inner, Inner<U> self) { }
        }
    }
}
#pragma warning restore CS0067, CS0649
