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
    /// 🚨 The impersonation (#6553): the guard admits a patch whose sender is the node's own hub, so
    /// a client that WRITES the node's address as its sender must not be that hub. The posting hub
    /// stamps its own address over the forged one, the guard sees the real poster and refuses — and
    /// the refusal comes back to the real poster, which a delivery that left under the forged address
    /// could never do.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task ClientPatch_ForgingTheOwningHubAsSender_IsStillRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = await CreateConfirmedAndStamped(StampedProbeTypeProvider.Declaring);
        var owner = new Address(path);

        var failure = await Record.ExceptionAsync(() => RequestHub
            .Observe<PatchDataResponse>(
                new PatchDataRequest(new MeshNodeReference(),
                    new RawJson("{\"content\":{\"confirmedBy\":\"victim\"}}")),
                o => o.WithTarget(owner) with { Sender = owner })
            .Take(1)
            .Timeout(60.Seconds())
            .Await(ct));

        failure.Should().NotBeNull("a patch claiming the owner's address is still a client patch");
        failure!.Message.Should().Contain("confirmedBy",
            "the guard judged the real poster and refused naming the field — not a timeout of a reply sent to the forged sender");
        var node = await WaitFor(path, c => c.ConfirmedBy == Stamp, "the stored value is still the owner's stamp");
        ContentOf(node)!.ConfirmedBy.Should().Be(Stamp);
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

    /// <summary>
    /// A <see cref="PatchDataRequest"/> may name ANY workspace reference; a JSON pointer into the
    /// node hub's store reaches the same data source through its reducer's reverse patch, with a
    /// shape the field comparison cannot read. On a declaring type it is refused naming the field;
    /// on a type that declares nothing the guard stays out of it (the control: the refusal is the
    /// declaration's doing, not the reference's).
    /// </summary>
    [Theory(Timeout = 180000)]
    [InlineData(StampedProbeTypeProvider.Declaring, true)]
    [InlineData(StampedProbeTypeProvider.NotDeclaring, false)]
    public async Task PatchThroughAnotherReference_IsRefusedOnlyWhereDeclared(string type, bool refused)
    {
        var ct = TestContext.Current.CancellationToken;
        var path = await CreateConfirmedAndStamped(type);

        var failure = await Record.ExceptionAsync(() => RequestHub
            .Observe<PatchDataResponse>(
                new PatchDataRequest(new JsonPointerReference("/MeshNode"),
                    new RawJson("{\"content\":{\"confirmedBy\":\"victim\"}}")),
                o => o.WithTarget(new Address(path)))
            .Take(1)
            .Timeout(60.Seconds())
            .Await(ct));

        if (!refused)
        {
            (failure?.Message ?? string.Empty).Should().NotContain("stamped field",
                "a type that declares nothing is not the stamped-field guard's to judge");
            return;
        }
        failure.Should().NotBeNull("a patch through a reference the guard cannot compare must be refused");
        failure!.Message.Should().Contain("confirmedBy", "the refusal names the declared field")
            .And.Contain(nameof(JsonPointerReference), "and the reference it could not judge");
        var node = await WaitFor(path, c => c.ConfirmedBy == Stamp, "the stored value is still the owner's stamp");
        ContentOf(node)!.ConfirmedBy.Should().Be(Stamp);
    }

    /// <summary>
    /// A data change's CREATION entry for the hub's own, existing node is applied as a replace — so a
    /// creation that omits the stamped field would clear the owner's stamp past the create validator
    /// (which only refuses a non-empty value). The data-layer half judges it like an update.
    /// </summary>
    [Theory(Timeout = 180000)]
    [InlineData(StampedProbeTypeProvider.Declaring, false)]
    [InlineData(StampedProbeTypeProvider.NotDeclaring, true)]
    public async Task DataChangeCreation_ReplacingTheOwnNode_CannotClearTheStamp(string type, bool lands)
    {
        var ct = TestContext.Current.CancellationToken;
        var path = await CreateConfirmedAndStamped(type);
        var current = await WaitFor(path, c => c.ConfirmedBy == Stamp, "stamped");

        var response = await RequestHub
            .Observe<DataChangeResponse>(
                DataChangeRequest.Create([current with { Content = ContentOf(current)! with { ConfirmedBy = null } }],
                    "stamped-fields-test"),
                o => o.WithTarget(new Address(path)))
            .FirstAsync()
            .Timeout(60.Seconds())
            .Await(ct);

        if (lands)
        {
            response.Message.Status.Should().Be(DataChangeStatus.Committed,
                "without a declaration the replace is an ordinary write");
            return;
        }
        response.Message.Status.Should().Be(DataChangeStatus.Failed,
            "a creation entry that replaces the own node and clears a stamped field must be refused");
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

    /// <summary>
    /// The node's hub, re-activated, bound to <paramref name="nodeType"/> and STARTED. A hosted hub is
    /// visible before its buildup actions ran; until then its declaration holder still reads
    /// "not yet resolved" (no fields, a fault), which is what the Initialize gate keeps every message
    /// from observing — so a direct read must wait for the same gate (<see cref="IMessageHub.Started"/>).
    /// </summary>
    private Task<IMessageHub> ReboundTo(string path, string nodeType)
        => Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never))
            .Where(h => h is { IsDisposing: false }
                && h.Started.IsCompletedSuccessfully
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

    /// <summary>
    /// 🚨 <b>The <c>sync/{id}</c> route</b> (#6527, item 1). A subscriber's per-stream sync hub is
    /// hosted ON the node's hub and handles a <see cref="DataChangeRequest"/> addressed straight to it
    /// — a route the node hub's patch check does not sit in front of. A change of the stamped field sent
    /// that way must not reach the node's data; an unrelated change sent the same way is the positive
    /// signal that the route was measured (it either lands, proving the route reaches the data and is
    /// guarded, or nothing sent that way reaches the data at all).
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task DataChange_SentToTheOwnersSyncHub_CannotChangeAStampedField()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = await CreateConfirmedAndStamped(StampedProbeTypeProvider.Declaring);
        using var keepAlive = Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Subscribe(_ => { }, ex => Output.WriteLine($"keep-alive stream for {path} faulted: {ex}"));
        var current = await WaitFor(path, c => c.ConfirmedBy == Stamp, "stamped");

        var owner = new Address(path);
        var syncHub = await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => Mesh.GetHostedHub(owner, HostedHubCreation.Never)?.ServiceProvider
                .GetRequiredService<HostedHubsCollection>().Hubs
                .FirstOrDefault(h => h.Address.Type == SynchronizationAddress.AddressType && !h.IsDisposing))
            .Where(h => h is not null)
            .Select(h => h!)
            .Should().Within(60.Seconds()).Emit("a live subscriber has a sync hub hosted on the node's hub",
                cancellationToken: ct);
        var route = syncHub.Address with { Host = owner };
        Output.WriteLine($"sync route: {route.ToFullString()}");

        ValueTask<Exception?> SendThroughTheSyncRoute(StampedProbeContent content) => Record.ExceptionAsync(() => RequestHub
            .Observe<DataChangeResponse>(DataChangeRequest.Update([current with { Content = content }]),
                o => o.WithTarget(route))
            .Take(1)
            .Timeout(60.Seconds())
            .Await(ct));

        var forged = await SendThroughTheSyncRoute(ContentOf(current)! with { ConfirmedBy = "victim" });
        var unrelated = await SendThroughTheSyncRoute(ContentOf(current)! with { Title = "via-sync" });
        Output.WriteLine($"forged: {forged?.Message}\nunrelated: {unrelated?.Message}");

        // Measured: the sync hub has no workspace of its own to apply a data change to, so EVERY data
        // change sent to it is answered with a delivery failure — the unrelated one included, which is
        // what shows the route reaches no data at all rather than that the guard happened to fire.
        unrelated.Should().BeOfType<DeliveryFailureException>(
            "a data change sent to a sync hub is answered with a failure: the route applies nothing");
        unrelated!.Message.Should().Contain(nameof(IWorkspace),
            "the sync hub cannot resolve a workspace — the reason the route applies nothing");
        forged.Should().BeOfType<DeliveryFailureException>("the stamped-field change is refused the same way");

        var node = await WaitFor(path, c => c.ConfirmedBy == Stamp,
            "a change of the stamped field through the sync route never lands");
        ContentOf(node)!.ConfirmedBy.Should().Be(Stamp);
        ContentOf(node)!.Title.Should().NotBe("via-sync", "nothing sent through the sync route reaches the node's data");
    }

    /// <summary>
    /// <b>The durable row</b> (#6527, item 3): the refusal holds for a node an activation read back
    /// from persistence, not only for the in-memory node the stamping activation held. The node's hub
    /// is recycled, the fresh activation serves the stored stamp, and a client change of it is refused.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task AfterARecycle_TheNodeReadBackFromStorage_StillRefusesAChangeOfTheStamp()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = await CreateConfirmedAndStamped(StampedProbeTypeProvider.Declaring);
        var before = Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never);

        var reread = await RequestHub
            .RecycleNode(path, reason: "StampedFieldsTest: re-activate the node from its durable row")
            .Take(1)
            .Should().Within(90.Seconds()).Emit("the recycled address answers again", cancellationToken: ct);
        reread.Should().NotBeNull();
        ContentOf(reread!)!.ConfirmedBy.Should().Be(Stamp, "the stamp is durable");

        var after = await ReboundTo(path, StampedProbeTypeProvider.Declaring);
        after.Should().NotBeSameAs(before, "a fresh activation serves the node");
        after.ServiceProvider.GetRequiredService<StampedFieldDeclarationHolder>().Current.Fields
            .Should().Equal(["confirmedBy"], "the fresh activation guards the field from its start");

        var failure = await Record.ExceptionAsync(() =>
            Mesh.GetWorkspace().GetMeshNodeStream(path)
                .Update(n => n with { Content = ContentOf(n)! with { ConfirmedBy = "victim" } })
                .Take(1).Timeout(60.Seconds()).Await(ct));
        failure.Should().NotBeNull("a client change of the stamp is refused on the re-read node");
        failure!.Message.Should().Contain("confirmedBy");
        var node = await WaitFor(path, c => c.ConfirmedBy == Stamp, "the stored value is still the owner's stamp");
        ContentOf(node)!.ConfirmedBy.Should().Be(Stamp);
    }

    /// <summary>
    /// <b>A type declared in mesh content, declaration changed while an instance runs</b> (#6527,
    /// item 2). The instance activates under a type that declares nothing; the type's stored row then
    /// gains <c>stampedFields</c>. After the instance is recycled — the documented step after a
    /// declaration changes (Doc/Architecture/StampedFields, "Recycle after a declaration changes") —
    /// the activation reads the new declaration from the durable type row and refuses a client change
    /// of the field.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task ContentDeclaredType_DeclarationAddedWhileRunning_IsEnforcedAfterTheRecycle()
    {
        var ct = TestContext.Current.CancellationToken;
        var typeId = "SfType" + Guid.NewGuid().ToString("N")[..8];
        var typePath = $"{TestPartition}/{typeId}";
        await MeshService.CreateNode(new MeshNode(typeId, TestPartition)
            {
                Name = typeId,
                NodeType = MeshNode.NodeTypePath,
                Content = new NodeTypeDefinition { Description = "Mesh-content type, declaration added later." },
            }).Take(1)
            .Should().Within(60.Seconds()).Emit("the content-declared type must exist first", cancellationToken: ct);

        var id = NewId();
        var path = $"{TestPartition}/{id}";
        await MeshService.CreateNode(Probe(id, typePath,
                new StampedProbeContent { Title = "t0", Confirmed = true, ConfirmedBy = "pre" })).Take(1)
            .Should().Within(60.Seconds()).Emit("a type that declares nothing accepts the value", cancellationToken: ct);
        await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Update(n => n with { Content = ContentOf(n)! with { Title = "t1" } })
            .Should().Within(60.Seconds()).Emit("the instance is active and writable", cancellationToken: ct);

        await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Update(n => n with
            {
                Content = n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)! with { StampedFields = ["confirmedBy"] },
            })
            .Should().Within(60.Seconds()).Emit("the type row gains the declaration", cancellationToken: ct);

        await RequestHub
            .RecycleNode(path, reason: "StampedFieldsTest: the type's declaration changed")
            .Take(1)
            .Should().Within(90.Seconds()).Emit("the recycled instance answers again", cancellationToken: ct);
        var hub = await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never))
            .Where(h => h is { IsDisposing: false } && h.Started.IsCompletedSuccessfully
                && h.ServiceProvider.GetRequiredService<StampedFieldDeclarationHolder>().Current.Fields.Contains("confirmedBy"))
            .Select(h => h!)
            .Should().Within(60.Seconds()).Emit("the re-activated instance binds the declaration from the stored type row",
                cancellationToken: ct);
        hub.ServiceProvider.GetRequiredService<StampedFieldDeclarationHolder>().Current.Fault.Should().BeNull();

        var failure = await Record.ExceptionAsync(() =>
            Mesh.GetWorkspace().GetMeshNodeStream(path)
                .Update(n => n with { Content = ContentOf(n)! with { ConfirmedBy = "victim" } })
                .Take(1).Timeout(60.Seconds()).Await(ct));
        failure.Should().NotBeNull("the declaration now stamps the field");
        failure!.Message.Should().Contain("confirmedBy");
        var node = await WaitFor(path, c => c.Title == "t1", "the instance is readable");
        ContentOf(node)!.ConfirmedBy.Should().Be("pre", "the refused write changed nothing");
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
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
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
