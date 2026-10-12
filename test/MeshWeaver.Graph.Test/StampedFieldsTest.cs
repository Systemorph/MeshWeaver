using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph.Security;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A STAMPED FIELD IS WRITTEN BY THE NODE'S OWN HUB AND BY NOTHING ELSE</b>
/// (<see cref="NodeTypeDefinition.StampedFields"/>, Doc/Architecture/StampedFields).
///
/// <para>The probe type stamps <c>confirmedBy</c> from its own hub when the node is confirmed — the
/// shape of an eval case whose confirmer's identity a headless run takes. The platform must then
/// refuse every client write that changes the field (a cross-hub patch, an MCP-style
/// <c>UpdateNode</c>, a create carrying a forged value), naming it, while the owner's stamp lands and
/// an unrelated field on the same node stays writable. The control is the same content under a type
/// that declares nothing: there the very same client write lands, so the refusal is the
/// declaration's doing.</para>
/// </summary>
public class StampedFieldsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Stamp = "stamped-by-owner";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        builder.ConfigureServices(services => services
            .AddSingleton<IStaticNodeProvider, StampedProbeTypeProvider>());
        return base.ConfigureMesh(builder);
    }

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private static string NewId() => "sf" + Guid.NewGuid().ToString("N")[..8];

    private static MeshNode Probe(string id, string type, StampedProbeContent content) => new(id, TestPartition)
    {
        Name = content.Title,
        NodeType = type,
        State = MeshNodeState.Active,
        Content = content,
    };

    private StampedProbeContent? ContentOf(MeshNode node) => node.ContentAs<StampedProbeContent>(Mesh.JsonSerializerOptions);

    private Task<MeshNode> WaitFor(string path, Func<StampedProbeContent, bool> predicate, string because)
        => Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null && ContentOf(n) is { } c && predicate(c))
            .Should().Within(60.Seconds()).Emit(because, cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>Creates a probe, confirms it through a client patch, and waits for the owner's stamp.</summary>
    private async Task<string> CreateConfirmedAndStamped(string type)
    {
        var id = NewId();
        var path = $"{TestPartition}/{id}";
        await MeshService.CreateNode(Probe(id, type, new StampedProbeContent { Title = "case" })).Take(1)
            .Should().Within(60.Seconds()).Emit("the probe must exist first", cancellationToken: TestContext.Current.CancellationToken);

        // An UNRELATED field through a client patch — must land on a node whose type declares a
        // stamped field.
        await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Update(n => n with { Content = ContentOf(n)! with { Confirmed = true } })
            .Should().Within(60.Seconds()).Emit("a client patch of an unstamped field lands",
                cancellationToken: TestContext.Current.CancellationToken);

        await WaitFor(path, c => c.ConfirmedBy == Stamp,
            "the owning hub stamps confirmedBy itself once the node is confirmed");
        return path;
    }

    [Fact(Timeout = 180000)]
    public async Task ClientPatch_ChangingAStampedField_IsRefusedNamingIt()
    {
        var path = await CreateConfirmedAndStamped(StampedProbeTypeProvider.Declaring);

        var failure = await Record.ExceptionAsync(() =>
            Mesh.GetWorkspace().GetMeshNodeStream(path)
                .Update(n => n with { Content = ContentOf(n)! with { ConfirmedBy = "victim" } })
                .Take(1).Timeout(60.Seconds()).Await(TestContext.Current.CancellationToken));

        failure.Should().NotBeNull("a client patch that changes a stamped field must be refused");
        failure!.Message.Should().Contain("confirmedBy", "the refusal names the field it refused");

        var node = await WaitFor(path, c => c.ConfirmedBy == Stamp,
            "the stored value is still the owner's stamp");
        ContentOf(node)!.ConfirmedBy.Should().Be(Stamp);
    }

    [Fact(Timeout = 180000)]
    public async Task UpdateNode_ChangingAStampedField_IsRefused_AndAnUnrelatedFieldStillLands()
    {
        var path = await CreateConfirmedAndStamped(StampedProbeTypeProvider.Declaring);
        var current = await WaitFor(path, c => c.ConfirmedBy == Stamp, "stamped");

        var failure = await Record.ExceptionAsync(() =>
            MeshService.UpdateNode(current with { Content = ContentOf(current)! with { ConfirmedBy = "victim" } })
                .Take(1).Timeout(60.Seconds()).Await(TestContext.Current.CancellationToken));
        failure.Should().NotBeNull("the MCP update surface goes through the same owner-side guard");
        failure!.Message.Should().Contain("confirmedBy");

        await MeshService.UpdateNode(current with { Content = ContentOf(current)! with { Title = "renamed" } })
            .Take(1).Should().Within(60.Seconds()).Emit(
                "a write that leaves the stamped field alone is not affected by it",
                cancellationToken: TestContext.Current.CancellationToken);
        await WaitFor(path, c => c.Title == "renamed" && c.ConfirmedBy == Stamp,
            "the unrelated field landed and the stamp is unchanged");
    }

    /// <summary>
    /// A synced subscriber's write-back arrives as a data change, not a merge patch — the data layer's
    /// half of the guard must refuse it the same way, and the same write-back on a type that declares
    /// nothing must commit (the control: the refusal is the declaration's doing, not the route's).
    /// </summary>
    [Theory(Timeout = 180000)]
    [InlineData(StampedProbeTypeProvider.Declaring, false)]
    [InlineData(StampedProbeTypeProvider.NotDeclaring, true)]
    public async Task DataChange_ChangingAStampedField_IsRefusedOnlyWhereDeclared(string type, bool lands)
    {
        var path = await CreateConfirmedAndStamped(type);
        var current = await WaitFor(path, c => c.ConfirmedBy == Stamp, "stamped");

        var response = await RequestHub
            .Observe<DataChangeResponse>(
                DataChangeRequest.Update([current with { Content = ContentOf(current)! with { ConfirmedBy = "victim" } }]),
                o => o.WithTarget(new Address(path)))
            .FirstAsync()
            .Timeout(60.Seconds())
            .Await(TestContext.Current.CancellationToken);

        if (lands)
        {
            response.Message.Status.Should().Be(DataChangeStatus.Committed,
                "without a declaration the field is an ordinary one");
            await WaitFor(path, c => c.ConfirmedBy == "victim", "the client value landed");
            return;
        }

        response.Message.Status.Should().Be(DataChangeStatus.Failed,
            "a data change that changes a stamped field must be refused");
        string.Join(" | ", response.Message.Log.Messages.Select(m => m.Message))
            .Should().Contain("confirmedBy", "the refusal names the field it refused");
        var node = await WaitFor(path, c => c.ConfirmedBy == Stamp, "the stored value is still the owner's stamp");
        ContentOf(node)!.ConfirmedBy.Should().Be(Stamp);
    }

    [Fact(Timeout = 180000)]
    public async Task Create_CarryingAForgedStampedField_IsRefusedNamingIt()
    {
        var id = NewId();
        var failure = await Record.ExceptionAsync(() =>
            MeshService.CreateNode(Probe(id, StampedProbeTypeProvider.Declaring,
                    new StampedProbeContent { Title = "forged", Confirmed = true, ConfirmedBy = "victim" }))
                .Take(1).Timeout(60.Seconds()).Await(TestContext.Current.CancellationToken));

        failure.Should().NotBeNull("a create may not bring a field only the node's own hub writes");
        failure!.Message.Should().Contain("confirmedBy");
    }

    [Fact(Timeout = 180000)]
    public async Task NegativeControl_TheSameWriteOnATypeThatDeclaresNothing_Lands()
    {
        var path = await CreateConfirmedAndStamped(StampedProbeTypeProvider.NotDeclaring);

        await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Update(n => n with { Content = ContentOf(n)! with { ConfirmedBy = "victim" } })
            .Should().Within(60.Seconds()).Emit(
                "without a declaration the field is an ordinary one — the refusal above is the declaration's doing",
                cancellationToken: TestContext.Current.CancellationToken);
        await WaitFor(path, c => c.ConfirmedBy == "victim", "the client value landed");
    }

    /// <summary>
    /// 🚨 <b>A type declared in mesh CONTENT (not registered in code) must not reorder client
    /// writes.</b> Its declaration is a durable row read; the guard used to start that read on the
    /// first client write, hold the write back, and continue it from the store's thread — outside the
    /// hub's action block, where the writes behind it could overtake it. The declaration is now
    /// settled while the hub initializes, so every write is judged on the hub's own turn.
    ///
    /// <para>Writes are posted back-to-back from one sender to a freshly created node of such a type;
    /// the last one posted is the value that stays (a held-back first write landed last and overwrote
    /// the rest — measured against the lazy resolution, which this fails). The hub's declaration is settled — without a fault and naming the declared
    /// field. The control is a mesh-content
    /// type that declares nothing: the same writes land in order there too, and its settled
    /// declaration is empty.</para>
    /// </summary>
    [Theory(Timeout = 180000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ContentDeclaredType_ClientWritesPostedInOrder_CommitInOrder(bool declares)
    {
        const int Writes = 20;
        var ct = TestContext.Current.CancellationToken;
        var typeId = "SfType" + Guid.NewGuid().ToString("N")[..8];
        var typePath = $"{TestPartition}/{typeId}";
        await MeshService.CreateNode(new MeshNode(typeId, TestPartition)
            {
                Name = typeId,
                NodeType = MeshNode.NodeTypePath,
                Content = new NodeTypeDefinition
                {
                    Description = "Mesh-content type for stamped-field ordering.",
                    StampedFields = declares ? ["confirmedBy"] : null,
                },
            }).Take(1)
            .Should().Within(60.Seconds()).Emit("the content-declared type must exist first", cancellationToken: ct);

        var id = NewId();
        var path = $"{TestPartition}/{id}";
        await MeshService.CreateNode(Probe(id, typePath, new StampedProbeContent { Title = "w0" })).Take(1)
            .Should().Within(60.Seconds()).Emit("the instance must exist first", cancellationToken: ct);

        var acks = await Enumerable.Range(1, Writes)
            .Select(i => RequestHub
                .Observe<PatchDataResponse>(
                    new PatchDataRequest(new MeshNodeReference(), new RawJson("{\"content\":{\"title\":\"w" + i + "\"}}")),
                    o => o.WithTarget(new Address(path)))
                .Take(1)
                .Select(r => r.Message.Success))
            .Merge()
            .ToList()
            .Timeout(60.Seconds())
            .Await(ct);
        acks.Should().HaveCount(Writes).And.OnlyContain(ok => ok, "every write leaves the stamped field alone");

        await WaitFor(path, c => c.Title == $"w{Writes}",
            "the last write posted is the value that stays — a write held back and continued off the hub's "
            + "turn lands after the writes posted behind it and overwrites them");

        var hub = Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never);
        hub.Should().NotBeNull("the node's hub is hosted after the writes it answered");
        var settled = hub!.ServiceProvider.GetRequiredService<StampedFieldDeclarationHolder>().Current;
        settled.Fault.Should().BeNull("the declaration was resolved while the hub initialized");
        settled.Fields.Should().Equal(declares ? ["confirmedBy"] : Array.Empty<string>());
    }

    /// <summary>
    /// Creates a node of a type that declares NOTHING carrying a client-chosen confirmer — legal
    /// there — and retypes it with a client patch.
    /// </summary>
    private async Task<string> CreateUnstampedWithValueAndRetype(string newType, string value)
    {
        var ct = TestContext.Current.CancellationToken;
        var id = NewId();
        var path = $"{TestPartition}/{id}";
        await MeshService.CreateNode(Probe(id, StampedProbeTypeProvider.NotDeclaring,
                new StampedProbeContent { Title = "pre", Confirmed = true, ConfirmedBy = value })).Take(1)
            .Should().Within(60.Seconds()).Emit("a type that declares nothing accepts the value", cancellationToken: ct);
        await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Update(n => n with { NodeType = newType })
            .Should().Within(60.Seconds()).Emit("a client retype lands — the clear never refuses it",
                cancellationToken: ct);
        return path;
    }

    /// <summary>The node's hub, re-activated and bound to <paramref name="nodeType"/>.</summary>
    private Task<IMessageHub> ReboundTo(string path, string nodeType)
        => Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never))
            .Where(h => h is { IsDisposing: false }
                && string.Equals(h.Configuration.Get<NodeTypePathHolder>()?.Path, nodeType, StringComparison.OrdinalIgnoreCase))
            .Select(h => h!)
            .Should().Within(60.Seconds()).Emit($"the node's hub re-activates bound to {nodeType}",
                cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>
    /// 🚨 <b>Clear on retype.</b> A value written while the node's type declared nothing must not
    /// survive retyping the node into a type that declares the field stamped: the activation that
    /// committed the retype clears it before handing the node over, and the new type's own stamp then
    /// lands (the probe stamps only an EMPTY confirmer, so seeing its stamp proves the clear).
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task Retype_IntoAStampingType_ClearsAPreWrittenValue_AndTheOwnersStampLands()
    {
        var path = await CreateUnstampedWithValueAndRetype(StampedProbeTypeProvider.Declaring, "victim");

        var node = await WaitFor(path, c => c.ConfirmedBy == Stamp,
            "the pre-written value is cleared on retype and the new type's own hub stamps the field");
        node.NodeType.Should().Be(StampedProbeTypeProvider.Declaring);

        var hub = await ReboundTo(path, StampedProbeTypeProvider.Declaring);
        hub.ServiceProvider.GetRequiredService<StampedFieldDeclarationHolder>().Current.Fields
            .Should().Equal(["confirmedBy"], "the new activation guards the field from its own start");
        var failure = await Record.ExceptionAsync(() =>
            Mesh.GetWorkspace().GetMeshNodeStream(path)
                .Update(n => n with { Content = ContentOf(n)! with { ConfirmedBy = "victim" } })
                .Take(1).Timeout(60.Seconds()).Await(TestContext.Current.CancellationToken));
        failure.Should().NotBeNull("after the retype the field answers to the new type's declaration");
    }

    /// <summary>
    /// The negative control: retyping into a type that declares NOTHING leaves the same value alone —
    /// the clear above is the new type's declaration at work, not a side effect of the retype.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task NegativeControl_Retype_IntoANonStampingType_LeavesFieldsAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = await CreateUnstampedWithValueAndRetype(StampedProbeTypeProvider.NotDeclaringOther, "victim");
        ct.ThrowIfCancellationRequested();

        // Keep the address subscribed so it re-activates, and wait for the activation bound to the
        // new type: by then the retiring activation's settlement has run.
        using var keepAlive = Mesh.GetWorkspace().GetMeshNodeStream(path).Subscribe(_ => { }, ex => Output.WriteLine($"keep-alive stream for {path} faulted: {ex}"));
        await ReboundTo(path, StampedProbeTypeProvider.NotDeclaringOther);
        var node = await WaitFor(path, c => c.Title == "pre", "the node is readable under its new type");
        node.NodeType.Should().Be(StampedProbeTypeProvider.NotDeclaringOther);
        ContentOf(node)!.ConfirmedBy.Should().Be("victim", "a type that declares nothing clears nothing");
    }

    /// <summary>
    /// An installer retypes many nodes into a newly installed type: every retype lands, and each node
    /// loses its pre-existing value in the stamped field (by design) and gets the type's own stamp.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task InstallerStyleRetype_OfManyNodes_Succeeds_AndClearsEach()
    {
        const int Count = 8;
        var paths = await Task.WhenAll(Enumerable.Range(0, Count)
            .Select(i => CreateUnstampedWithValueAndRetype(StampedProbeTypeProvider.Declaring, $"preexisting-{i}")));
        paths.Should().HaveCount(Count, "every retype lands");
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();

        foreach (var path in paths)
            await WaitFor(path, c => c.ConfirmedBy == Stamp,
                "each retyped node's pre-existing value is cleared and its new type stamps it");
    }

    [Fact]
    public void WithoutValues_RemovesOnlyFieldsThatCarryAValue_AndKeepsTheContentType()
    {
        var options = Mesh.JsonSerializerOptions;
        var node = new MeshNode("x", TestPartition)
        {
            Content = new StampedProbeContent { Title = "t", Confirmed = true, ConfirmedBy = "victim" },
        };
        var cleared = StampedFields.WithoutValues(node, ["confirmedBy"], options);
        cleared.Content.Should().BeOfType<StampedProbeContent>()
            .Which.Should().Be(new StampedProbeContent { Title = "t", Confirmed = true, ConfirmedBy = null });

        var empty = node with { Content = new StampedProbeContent { Title = "t" } };
        StampedFields.WithoutValues(empty, ["confirmedBy"], options).Should().BeSameAs(empty,
            "nothing to clear is no write");
    }

    [Fact]
    public void MergePatch_ChangeDetection_IsPerFieldAndIgnoresNoOps()
    {
        var live = JsonNode.Parse("""{"title":"a","confirmedBy":"owner"}""")!.AsObject();
        IReadOnlyList<string> fields = ["confirmedBy"];

        StampedFields.ChangedByMergePatch(live, JsonNode.Parse("""{"content":{"title":"b"}}""")!.AsObject(), "content", fields)
            .Should().BeEmpty("a patch that does not name the field leaves it alone");
        StampedFields.ChangedByMergePatch(live, JsonNode.Parse("""{"content":{"confirmedBy":"owner"}}""")!.AsObject(), "content", fields)
            .Should().BeEmpty("re-asserting the live value is a no-op");
        StampedFields.ChangedByMergePatch(live, JsonNode.Parse("""{"content":{"confirmedBy":"victim"}}""")!.AsObject(), "content", fields)
            .Should().ContainSingle().Which.Should().Be("confirmedBy");
        StampedFields.ChangedByMergePatch(live, JsonNode.Parse("""{"content":{"confirmedBy":null}}""")!.AsObject(), "content", fields)
            .Should().ContainSingle("clearing a stamped value is a change too").Which.Should().Be("confirmedBy");
        StampedFields.ChangedByMergePatch(live, JsonNode.Parse("""{"content":null}""")!.AsObject(), "content", fields)
            .Should().ContainSingle("removing the whole content removes the stamp").Which.Should().Be("confirmedBy");
        StampedFields.Present(JsonNode.Parse("""{"confirmedBy":""}""")!.AsObject(), fields)
            .Should().BeEmpty("an empty value is no value on a create");
    }
}

/// <summary>Probe content: an unstamped title and confirmation flag, and the stamped confirmer.</summary>
public record StampedProbeContent
{
    /// <summary>Ordinary field.</summary>
    public string? Title { get; init; }

    /// <summary>Ordinary field — a client sets it.</summary>
    public bool Confirmed { get; init; }

    /// <summary>Stamped by the node's own hub when <see cref="Confirmed"/> is set.</summary>
    public string? ConfirmedBy { get; init; }
}

/// <summary>Two static probe types with the same content and the same owner stamp — one declares it.</summary>
internal sealed class StampedProbeTypeProvider : IStaticNodeProvider
{
    public const string Declaring = "StampedProbe";
    public const string NotDeclaring = "UnstampedProbe";
    public const string NotDeclaringOther = "UnstampedProbeB";

    public IEnumerable<MeshNode> GetStaticNodes()
    {
        foreach (var (type, declares) in new[] { (Declaring, true), (NotDeclaring, false), (NotDeclaringOther, false) })
        {
            yield return new MeshNode(type)
            {
                Name = type,
                NodeType = "NodeType",
                HubConfiguration = c => c.WithContentType<StampedProbeContent>().WithInitialization(StampOnConfirm),
                Content = new NodeTypeDefinition
                {
                    Description = "Test NodeType for stamped fields.",
                    StampedFields = declares ? ["confirmedBy"] : null,
                },
            };
            yield return new MeshNode(type, "Admin/Partition")
            {
                NodeType = "Partition",
                Name = $"{type} (static)",
                State = MeshNodeState.Active,
                Content = new PartitionDefinition
                {
                    Namespace = type,
                    DataSource = "static",
                    Description = $"Test NodeType definition partition for '{type}'",
                },
            };
        }
    }

    /// <summary>
    /// The owner's stamp: when its own node is confirmed and not yet stamped, the hub writes the stamp
    /// on its OWN node — the one path the platform lets set a stamped field.
    /// </summary>
    private static IObservable<Unit> StampOnConfirm(IMessageHub hub)
    {
        var access = hub.ServiceProvider.GetService<AccessService>();
        var options = hub.JsonSerializerOptions;
        var sub = hub.GetWorkspace().GetMeshNodeStream()
            .Where(n => n?.ContentAs<StampedProbeContent>(options) is { Confirmed: true, ConfirmedBy: null })
            .Select(_ => access.RunAsSystem(() => hub.GetWorkspace().GetMeshNodeStream()
                .Update(n => n.ContentAs<StampedProbeContent>(options) is { Confirmed: true, ConfirmedBy: null } c
                    ? n with { Content = c with { ConfirmedBy = "stamped-by-owner" } }
                    : n)
                .Take(1)))
            .Concat()
            .Subscribe(
                _ => { },
                ex => hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("StampedProbe")
                    .LogError(ex, "The probe's own stamp failed on {Address}", hub.Address));
        hub.RegisterForDisposal(_ => sub.Dispose());
        return Observable.Return(Unit.Default);
    }
}
