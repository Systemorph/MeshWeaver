using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.NodeOperations.Test;

/// <summary>
/// #1433 — pins that <see cref="PersistenceService"/>'s existence CHECK and its delete ACTION
/// answer the same question about the same providers.
///
/// <para><b>The asymmetry.</b> <c>Read</c> consults EVERY provider ("read-only providers and
/// writable providers participate equally", per the class doc) while <c>Delete</c> can only remove
/// through the writable ones — and it used to throw ONE message,
/// <i>"no writable storage provider has this node"</i>, for two states that mean opposite things:
/// a path that is genuinely gone, and a path a read-only provider is serving. The first is a
/// benign race (a parallel prune removed the row between the caller's gate and this commit) whose
/// requested END STATE already holds; reporting it as an ERROR-level unexpected
/// <c>InvalidOperationException</c> sent triage looking at storage routing. The second is a real
/// refusal that never said what was actually wrong.</para>
///
/// <para><b>The bound these tests exist to hold.</b> Classifying must not widen what a delete
/// removes. Delete still fans out over WRITABLE providers only, still gated on that provider's own
/// containment read, so a read-only provider is never asked to delete — see
/// <see cref="OnlyAReadOnlyProviderHasIt_Refuses_NamesTheProvider_AndLeavesItServed"/>, which
/// asserts the node is still there after the refusal. The only case that newly SUCCEEDS is the one
/// in which nothing was removed because there was nothing anywhere to remove.</para>
/// </summary>
public class DeleteProviderAgreementTest
{
    private static readonly JsonSerializerOptions Json = new();

    /// <summary>A read-only provider over a fixed node set — the shape of the static
    /// Agent/Harness/LanguageModel definitions and of embedded documentation.</summary>
    private sealed class ReadOnlyProvider(string name, params MeshNode[] nodes) : IPartitionStorageProvider
    {
        public string Name => name;
        public bool IsReadOnly => true;
        public IStorageAdapter Adapter { get; } = new StaticNodeStorageAdapter(nodes);
        public PartitionDefinition? PartitionDefinition { get; } = new()
        {
            Namespace = name, DataSource = "static", Versioned = false
        };
        public ImmutableHashSet<string> Contexts => [];
    }

    private static (PersistenceService Service, InMemoryPartitionStorageProvider Writable)
        Build(params IPartitionStorageProvider[] readOnly)
    {
        var writable = new InMemoryPartitionStorageProvider(new InMemoryStorageAdapter(null));
        var providers = new List<IPartitionStorageProvider>(readOnly) { writable };
        return (new PersistenceService(providers), writable);
    }

    /// <summary>
    /// The positive direction: what the check says EXISTS in a writable provider, the action
    /// removes. Guards against a classification that starts refusing real deletes.
    /// </summary>
    [Fact]
    public async Task WritableProviderHasIt_DeletesIt()
    {
        var (service, writable) = Build();
        var node = new MeshNode("Doomed", "AgreementSpace") { Name = "Doomed", NodeType = "Markdown" };
        await service.Write(node, Json).Should().Within(10.Seconds()).Emit();

        (await service.FindDeleteBlockingProvider(node.Path).Should().Within(10.Seconds()).Emit())
            .Should().BeNull("a writable provider holds it, so nothing blocks the delete");

        (await service.Delete(node.Path).Should().Within(10.Seconds()).Emit())
            .Should().Be(node.Path);

        (await writable.Adapter.Read(node.Path, Json).Should().Within(10.Seconds()).Emit())
            .Should().BeNull("the row must actually be gone");
    }

    /// <summary>
    /// The benign race: by commit time nothing anywhere has the path, so the delete's requested
    /// end state HOLDS and it completes. <b>Fail-without:</b> this threw
    /// <c>InvalidOperationException("… no writable storage provider has this node.")</c>, which the
    /// delete handler logged as <c>[DeleteNode] unexpected … partial-deleted=0</c> — the #1422
    /// failures, all landing in the same second under a parallel prune.
    /// <para>Whether a delete of a node that never existed is an ERROR is answered by the CALLER's
    /// existence gate (<c>HandleDeleteNodeRequest</c> stage 1 → <c>NodeNotFound</c>), not here —
    /// past that gate, "the row is no longer there" is the outcome that was asked for.</para>
    /// </summary>
    [Fact]
    public async Task NothingAnywhereHasIt_Completes_AndRemovesNothingElse()
    {
        var (service, writable) = Build();
        // A bystander in the same store: whatever the absent-path delete does, it must not touch it.
        var bystander = new MeshNode("Bystander", "AgreementSpace")
        {
            Name = "Bystander", NodeType = "Markdown"
        };
        await service.Write(bystander, Json).Should().Within(10.Seconds()).Emit();

        var absent = "AgreementSpace/never-existed";
        (await service.FindDeleteBlockingProvider(absent).Should().Within(10.Seconds()).Emit())
            .Should().BeNull("nothing serves it, so nothing blocks a delete of it");

        (await service.Delete(absent).Should().Within(10.Seconds()).Emit())
            .Should().Be(absent,
                "the requested end state — the path is gone — already holds, so the commit has "
                + "nothing to refuse. A parallel prune winning the race is not a routing fault");

        (await writable.Adapter.Read(bystander.Path, Json).Should().Within(10.Seconds()).Emit())
            .Should().NotBeNull("a delete that removed nothing must have removed NOTHING");
    }

    /// <summary>
    /// The real refusal — and the bound. A path only a READ-ONLY provider serves is readable and
    /// structurally undeletable: the refusal must name the provider (it used to claim, falsely,
    /// that no provider had the node), and — the part that matters — the node must still be there
    /// afterwards. Widening the delete fan-out to every provider would let this tombstone shipped
    /// documentation and static definitions; that is the change this test forbids.
    /// </summary>
    [Fact]
    public async Task OnlyAReadOnlyProviderHasIt_Refuses_NamesTheProvider_AndLeavesItServed()
    {
        var shipped = new MeshNode("Shipped", "ReadOnlyNs") { Name = "Shipped", NodeType = "Markdown" };
        var readOnly = new ReadOnlyProvider("ReadOnlyNs", shipped);
        var (service, writable) = Build(readOnly);

        (await service.Read(shipped.Path, Json).Should().Within(10.Seconds()).Emit())
            .Should().NotBeNull("the read side sees it — that is the whole asymmetry");

        (await service.FindDeleteBlockingProvider(shipped.Path).Should().Within(10.Seconds()).Emit())
            .Should().Contain("ReadOnlyNs",
                "the pre-flight must name the provider that blocks, so the delete handler can "
                + "refuse BEFORE the bottom-up subtree walk removes any writable descendant");

        Func<Task> act = async () => await service.Delete(shipped.Path).FirstAsync().ToTask();
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*READ-ONLY*ReadOnlyNs*",
                "the refusal must say WHY and WHICH provider serves it — not claim, falsely, that "
                + "no provider has the node");

        (await readOnly.Adapter.Read(shipped.Path, Json).Should().Within(10.Seconds()).Emit())
            .Should().NotBeNull("a refused delete must leave the read-only provider's node intact");
        (await writable.Adapter.Read(shipped.Path, Json).Should().Within(10.Seconds()).Emit())
            .Should().BeNull("and must not have written a tombstone into a writable provider");
    }

    /// <summary>
    /// A path served by BOTH a read-only provider and a writable one — a db-synced override of a
    /// shipped node — stays deletable: the delete removes the writable copy. The block predicate
    /// is "no writable provider has it AND a read-only one does", never "a read-only one does".
    /// </summary>
    [Fact]
    public async Task ReadOnlyAndWritableBothHaveIt_DoesNotBlock_AndDeletesTheWritableCopy()
    {
        var shipped = new MeshNode("Overridden", "ReadOnlyNs")
        {
            Name = "Overridden", NodeType = "Markdown"
        };
        var readOnly = new ReadOnlyProvider("ReadOnlyNs", shipped);
        var (service, writable) = Build(readOnly);

        // The override lives in the writable store under the same path.
        await writable.Adapter.Write(shipped with { Name = "Override" }, Json)
            .Should().Within(10.Seconds()).Emit();

        (await service.FindDeleteBlockingProvider(shipped.Path).Should().Within(10.Seconds()).Emit())
            .Should().BeNull("a writable copy exists, so the delete has something it CAN remove");

        (await service.Delete(shipped.Path).Should().Within(10.Seconds()).Emit())
            .Should().Be(shipped.Path);

        (await writable.Adapter.Read(shipped.Path, Json).Should().Within(10.Seconds()).Emit())
            .Should().BeNull("the writable override is what a delete removes");
        (await readOnly.Adapter.Read(shipped.Path, Json).Should().Within(10.Seconds()).Emit())
            .Should().NotBeNull("the shipped node underneath is untouched — it was never asked to delete");
    }
}
