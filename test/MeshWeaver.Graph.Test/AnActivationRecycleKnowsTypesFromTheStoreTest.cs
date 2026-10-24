using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.FaultInjection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>"Is this a NodeType?" is answered by the STORE the type's activation loads from — never by
/// the query read model</b> (#6826).
///
/// <para><b>The defect.</b> <see cref="ActivationRecycle.Request"/> refused a request naming a type it
/// could not find, and it looked the type up with a QUERY (<c>path:{type}</c>). On a portal pod every
/// Doc NodeType — live, compiled, served — was refused as "unknown NodeType(s)" from the type's OWN
/// recycle cascade, on every replica, so none of their live activations was ever recycled. A query's
/// negative is not the store's: the CQRS rule forbids deciding the existence of one known path from
/// it, and a read model that answers a SHORTER snapshot (a provider that caught a read fault and
/// dropped the row, a derived index that does not hold the row) reads exactly like absence.</para>
///
/// <para><b>The instrument.</b> A real mesh over the real <see cref="InMemoryStorageAdapter"/>, whose
/// READ legs can be made to throw for the one type path under test — the production shape of a store
/// read that does not answer. Armed only around the request, so the mesh, the seeding and the
/// request's own write are untouched. With the fault armed the old check read the provider's shortened
/// snapshot as "the type does not exist" and refused the request as UNKNOWN; the check now reads the
/// store, so the same fault surfaces as a FAULT — the honest answer — and never as a refusal that
/// names a live type unknown.</para>
///
/// <para><b>Controls.</b> With nothing armed, a stored NodeType is accepted (the healthy path), while
/// a path with no node and a stored node that is not a NodeType are both still refused as unknown —
/// the side of the check that must not move. Every faulted case asserts the fault was INJECTED.</para>
/// </summary>
public class AnActivationRecycleKnowsTypesFromTheStoreTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "StoreKnownTypes";
    private const string StoredType = $"{Partition}/StoredType";
    private const string MarkdownPage = $"{Partition}/APage";
    private const string AbsentType = $"{Partition}/NoSuchType";

    /// <summary>The real <see cref="InMemoryStorageAdapter"/> under the shared injector, whose READ
    /// legs are made to throw only while a test arms them.</summary>
    private readonly FaultInjectingStorageAdapter store = new(new InMemoryStorageAdapter());

    /// <inheritdoc />
    protected override IMeshBootstrap Bootstrap => new StoreBootstrap(store);

    private sealed class StoreBootstrap(IStorageAdapter adapter) : IMeshBootstrap
    {
        public string Name => "monolith-over-a-faultable-store";

        public MeshBuilder Bootstrap(MeshBuilder builder)
            => builder
                .UseMonolithMesh()
                .ConfigureServices(services => services.AddInMemoryPersistence(adapter))
                .RegisterMeshQueryCoreOnMeshHub();
    }

    private IObservable<ActivationRecycleTicket> Recycle(string type) =>
        ActivationRecycle.Request(Mesh, new ActivationRecycleRequest
        {
            NodeTypes = ImmutableList.Create(type),
            Reason = "test: #6826 — a live type must never be refused as unknown",
            RequestedBy = nameof(AnActivationRecycleKnowsTypesFromTheStoreTest),
        });

    private async Task Seed(MeshNode node)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await access.RunAsSystem(() => meshService.CreateNode(node))
            .Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit($"{node.Path} must be stored before the request names it", TestContext.Current.CancellationToken);
    }

    private Task SeedType() => Seed(new MeshNode("StoredType", Partition)
    {
        NodeType = MeshNode.NodeTypePath,
        Name = "Stored type",
        State = MeshNodeState.Active,
        Content = new NodeTypeDefinition { Description = "a stored, served NodeType" },
    });

    /// <summary>
    /// 🚨 THE #6826 REPRO. The type is stored; the read that would find it does not answer. The
    /// request must not claim the type is unknown — that sentence is the one that sent every live
    /// activation of a served type on keeping its old build with nobody looking.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AStoredTypeWhoseReadFaults_IsNeverRefusedAsUnknown()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedType();

        // Read and ReadMany of every path containing the type's name throw the driver's timeout.
        using var fault = store.FailOperations(p => p.Contains("StoredType", StringComparison.OrdinalIgnoreCase),
            StorageLegs.Read, description: "paths containing StoredType");

        var outcome = await Recycle(StoredType)
            .Select(ticket => (Ticket: (ActivationRecycleTicket?)ticket, Error: (Exception?)null))
            .Catch((Exception ex) => Observable.Return((Ticket: (ActivationRecycleTicket?)null, Error: (Exception?)ex)))
            .Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the request answers — with a ticket or with the read's fault", ct);

        fault.ArrivalCount.Should().BeGreaterThan(0,
            "the fault must actually have been INJECTED — without it this case exercises the healthy "
            + "path and cannot fail");
        outcome.Ticket?.Refusal.Should().NotContain("unknown NodeType",
            "the type IS stored: a read that did not answer must never be reported as the type's absence");
        outcome.Error.Should().NotBeNull(
            "a store read that faulted is surfaced as the fault — the caller logs that nothing was "
            + "recycled because the type could not be READ, which is what happened");
    }

    /// <summary>The healthy path: a stored NodeType is known, and the request is written.</summary>
    [Fact(Timeout = 120_000)]
    public async Task AStoredType_IsAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedType();

        var ticket = await Recycle(StoredType).Should().Within(TestTimeouts.Convergence)
            .Emit("the request answers", ct);

        ticket.Refusal.Should().BeNull("the type is stored and is a NodeType");
        ticket.Path.Should().StartWith(ActivationRecycleRequest.Namespace + "/");
    }

    /// <summary>
    /// 🚨 THE SIDE THAT MUST NOT MOVE. A path nothing stores, and a stored node that is not a
    /// NodeType, are still refused as unknown — reading the store did not turn the check into
    /// "anything goes".
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AnAbsentPath_AndANodeThatIsNotAType_AreStillRefusedAsUnknown()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(new MeshNode("APage", Partition) { NodeType = "Markdown", Name = "A page", State = MeshNodeState.Active });

        var absent = await Recycle(AbsentType).Should().Within(TestTimeouts.Convergence)
            .Emit("the request answers", ct);
        absent.Path.Should().BeNull("nothing was written for a type that does not exist");
        absent.Refusal.Should().Contain("unknown NodeType").And.Contain(AbsentType);

        var page = await Recycle(MarkdownPage).Should().Within(TestTimeouts.Convergence)
            .Emit("the request answers", ct);
        page.Path.Should().BeNull("a Markdown page is a node, not a NodeType");
        page.Refusal.Should().Contain("unknown NodeType").And.Contain(MarkdownPage);
    }
}
