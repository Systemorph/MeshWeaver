using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// A real <see cref="IStorageAdapter"/> over a real <see cref="InMemoryStorageAdapter"/> that makes
/// ONE planned descendant vanish in the window a recursive delete cannot see: the enumeration is
/// TAKEN from the backing store, the leaf is then removed from it, and only then is the (now stale)
/// snapshot answered.
///
/// <para>Nothing here is a mock of a core interface — the store of record IS an
/// <c>InMemoryStorageAdapter</c> and every call reaches it. What it reproduces is the one thing a
/// quiet single-writer in-memory suite never does: a CONCURRENT delete landing between a recursive
/// delete's plan and the legs that plan feeds. The removal goes through the inner adapter's own
/// <c>Delete</c>, so it publishes on the change feed exactly as another writer's delete would —
/// the path resolver drops it, and the address stops being routable.</para>
///
/// <para><see cref="PretendStillPresent"/> is what makes the pair of tests below a DISCRIMINATOR
/// rather than a restatement: it changes ONLY the answer the store of record gives about the
/// vanished path, leaving the routing failure and its wording byte-identical. If the fix read the
/// refusal's message instead of the store, both tests would come out the same way.</para>
/// </summary>
internal sealed class VanishingDescendantStorageAdapter(InMemoryStorageAdapter inner) : IStorageAdapter
{
    /// <summary>Backing store of record — tests assert against this, never against a cache.</summary>
    public InMemoryStorageAdapter Inner => inner;

    /// <summary>Only an enumeration of THIS root makes the leaf vanish.</summary>
    public string? WatchedRoot { get; set; }

    /// <summary>
    /// The descendant removed from the backing store the FIRST time <see cref="WatchedRoot"/> is
    /// enumerated — after the snapshot is taken, before it is answered. Later enumerations (the
    /// drain's verification passes) see the truth, which is what a real concurrent delete leaves
    /// behind.
    /// </summary>
    public string? VanishPath { get; set; }

    /// <summary>
    /// Makes <see cref="Exists"/> answer <c>true</c> for <see cref="VanishPath"/> even though the
    /// row is gone: the stand-in for a descendant that IS there and whose per-node hub cannot be
    /// reached — a missing NodeType, one that will not load. Routing refuses it with exactly the
    /// same sentence as a deleted node, so the store of record is the only thing that separates
    /// them, and a delete must still be REFUSED in this case.
    /// </summary>
    public bool PretendStillPresent { get; set; }

    /// <summary>Whether the removal actually happened — a test that did not arm is not a test.</summary>
    public bool Vanished => Volatile.Read(ref _vanished) == 1;

    private int _vanished;

    /// <inheritdoc />
    public IObservable<IReadOnlyCollection<string>> ListDescendantPaths(string rootPath)
    {
        var listing = inner.ListDescendantPaths(rootPath);
        if (WatchedRoot is not { Length: > 0 } watched
            || !string.Equals(rootPath, watched, StringComparison.OrdinalIgnoreCase)
            || VanishPath is not { Length: > 0 } vanish)
            return listing;

        return listing.SelectMany(paths =>
            Interlocked.Exchange(ref _vanished, 1) != 0
                ? Observable.Return(paths)
                // Straight through the inner adapter: a concurrent delete publishes on the change
                // feed, so the resolver forgets the path and the address stops routing. Rx, never a
                // Task — the removal IS part of answering this enumeration.
                : inner.Delete(vanish).Select(_ => paths));
    }

    /// <inheritdoc />
    public IObservable<bool> Exists(string path)
        => PretendStillPresent
           && VanishPath is { Length: > 0 } vanish
           && string.Equals(path, vanish, StringComparison.OrdinalIgnoreCase)
            ? Observable.Return(true)
            : inner.Exists(path);

    /// <inheritdoc />
    public IObservable<string> Delete(string path) => inner.Delete(path);

    /// <inheritdoc />
    public IObservable<bool> DeleteIfExists(string path) => inner.DeleteIfExists(path);

    /// <inheritdoc />
    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        => inner.Read(path, options);

    /// <inheritdoc />
    public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
        => inner.Write(node, options);

    /// <inheritdoc />
    public IObservable<bool?> WriteIfVersion(
        MeshNode node, long expectedVersion, JsonSerializerOptions options)
        => inner.WriteIfVersion(node, expectedVersion, options);

    /// <inheritdoc />
    public IObservable<bool> ExistsInWritableStorage(string path)
        => ((IStorageAdapter)inner).ExistsInWritableStorage(path);

    /// <inheritdoc />
    public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
        string fullPath, JsonSerializerOptions options)
        => inner.FindBestPrefixMatch(fullPath, options);

    /// <inheritdoc />
    public IObservable<(MeshNode? Node, int MatchedSegments)> ResolvePath(
        string fullPath, JsonSerializerOptions options)
        => inner.ResolvePath(fullPath, options);

    /// <inheritdoc />
    public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)>
        ListChildPaths(string? parentPath)
        => inner.ListChildPaths(parentPath);

    /// <inheritdoc />
    public IObservable<IEnumerable<string>> ListPartitionSubPaths(string nodePath)
        => ((IStorageAdapter)inner).ListPartitionSubPaths(nodePath);

    /// <inheritdoc />
    public IObservable<object> GetPartitionObjects(
        string nodePath, string? subPath, JsonSerializerOptions options)
        => inner.GetPartitionObjects(nodePath, subPath, options);

    /// <inheritdoc />
    public IObservable<System.Reactive.Unit> SavePartitionObjects(
        string nodePath, string? subPath, IReadOnlyCollection<object> objects,
        JsonSerializerOptions options)
        => inner.SavePartitionObjects(nodePath, subPath, objects, options);

    /// <inheritdoc />
    public IObservable<System.Reactive.Unit> DeletePartitionObjects(
        string nodePath, string? subPath = null)
        => inner.DeletePartitionObjects(nodePath, subPath);

    /// <inheritdoc />
    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(
        string nodePath, string? subPath = null)
        => inner.GetPartitionMaxTimestamp(nodePath, subPath);

    /// <inheritdoc />
    public IObservable<DataChangeNotification> Changes => inner.Changes;
}

/// <summary>
/// Systemorph/MeshWeaver#4680 — a recursive delete must not be REFUSED by a descendant that is
/// already gone, and must still be refused by one that is merely unreachable.
///
/// <para><b>What happened.</b> A recursive delete plans its subtree by enumerating storage ONCE,
/// then asks every planned descendant, in a bulk-atomic pre-flight, whether it may be deleted, and
/// only then commits bottom-up. A concurrent delete that removes one of those leaves in between
/// left the operation holding a path that really was gone — and the WHOLE subtree delete was
/// refused over a node that was already in exactly the state the caller asked for. That is
/// <see href="https://github.com/Systemorph/MeshWeaver/issues/4668">#4668</see>'s decision one
/// level down: an absent node satisfies the delete's postcondition for ITSELF.</para>
///
/// <para><b>Why the obvious fix did not apply.</b> Relaxing the pre-flight's
/// <c>NodeNotFound</c> verdict is dead code for the dominant shape: with no row at the address and
/// no activated hub to short-circuit on, the post never ROUTES, so no verdict is ever produced —
/// the leg's fall-through reports <c>"No node found at 'X'. Closest ancestor is …"</c>. That
/// sentence names three different facts ("missing, has no NodeType, or has an invalid NodeType"),
/// and the last two are precisely what the bulk-atomic pre-flight exists to refuse before any
/// storage side effect fires. So the refusal is confirmed against the STORE OF RECORD instead, and
/// the second test here is that distinction: identical routing failure, opposite store answer,
/// opposite outcome.</para>
/// </summary>
public class RecursiveDeleteVanishedDescendantTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private readonly VanishingDescendantStorageAdapter storage = new(new InMemoryStorageAdapter());

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder.ConfigureServices(services =>
        {
            // Registered BEFORE the bootstrap's TryAddSingleton, so this IS the store of record and
            // production's version-writing / monotonic-guard decorators wrap it unchanged.
            services.AddSingleton<IStorageAdapter>(storage);
            return services;
        }));

    /// <summary>
    /// THE SUBJECT. Before the fix this failed at the pre-flight with
    /// <c>Cannot delete 'TestData/…/gone-by-preflight': No node found at …</c>; with the pre-flight
    /// alone it failed one stage later, in the commit, as <c>[DeleteNode] not-found …
    /// partial-deleted=0</c>. Both halves address the same snapshot, so both had to learn the same
    /// fact.
    /// </summary>
    [Fact]
    public async Task RecursiveDelete_WhenAPlannedDescendantVanished_Succeeds_AndRemovesTheRest()
    {
        var (rootPath, stays, gone) = await SeedSubtree("vanishing-descendant");
        storage.WatchedRoot = rootPath;
        storage.VanishPath = gone;

        // Producer -> test signal: whichever arm fires completes its own AsyncSubject, so a failure
        // is REPORTED rather than silently spending the positive assertion's whole window.
        var removed = new AsyncSubject<bool>();
        var failure = new AsyncSubject<Exception>();
        using var deleting = NodeFactory.DeleteNode(rootPath).Subscribe(
            r => { removed.OnNext(r); removed.OnCompleted(); },
            ex => { failure.OnNext(ex); failure.OnCompleted(); });
        using var reported = failure.Subscribe(ex => Output.WriteLine("DELETE FAILED: " + ex));

        var ok = await removed.Should().Within(TestTimeouts.Convergence).Emit(
            "a descendant that is already gone satisfies the delete's postcondition for ITSELF, so "
            + "it can no more refuse the subtree than a leaf this operation had removed itself",
            cancellationToken: TestContext.Current.CancellationToken);
        ok.Should().BeTrue("the root was there, so this call really removed something");

        storage.Vanished.Should().BeTrue(
            "the test only discriminates if the leaf really was removed mid-operation");
        (await storage.Inner.Exists(rootPath).Should().Within(TestTimeouts.Quick).Emit(
                cancellationToken: TestContext.Current.CancellationToken))
            .Should().BeFalse("the root must be gone");
        (await storage.Inner.Exists(stays).Should().Within(TestTimeouts.Quick).Emit(
                cancellationToken: TestContext.Current.CancellationToken))
            .Should().BeFalse(
                "the sibling that really was there must be removed — the vanished leaf must not "
                + "take the rest of the subtree down with it, in either direction");
    }

    /// <summary>
    /// NEGATIVE CONTROL, and the reason this change is not "the pre-flight stopped refusing". The
    /// leaf is unreachable in exactly the same way — same routing failure, same sentence — but the
    /// store of record says the node IS there, which is what a missing or unloadable NodeType looks
    /// like. The bulk-atomic pre-flight exists to refuse that BEFORE any storage side effect fires
    /// (a partially destroyed subtree), so the delete must fail and nothing may be removed.
    ///
    /// <para>Without this test, the first one would pass just as well on a pre-flight that had
    /// simply been taught to ignore every routing failure.</para>
    /// </summary>
    [Fact]
    public async Task RecursiveDelete_WhenTheDescendantIsUnreachableButStillStored_IsRefused()
    {
        var (rootPath, stays, gone) = await SeedSubtree("unreachable-descendant");
        storage.WatchedRoot = rootPath;
        storage.VanishPath = gone;
        storage.PretendStillPresent = true;

        var removed = new AsyncSubject<bool>();
        var failure = new AsyncSubject<Exception>();
        using var deleting = NodeFactory.DeleteNode(rootPath).Subscribe(
            r => { removed.OnNext(r); removed.OnCompleted(); },
            ex => { failure.OnNext(ex); failure.OnCompleted(); });

        var refused = await failure.Should().Within(TestTimeouts.Convergence).Emit(
            "a descendant the store still holds is exactly what the bulk-atomic pre-flight exists "
            + "to refuse — 'I cannot reach it' must never be read as 'it is already gone'",
            cancellationToken: TestContext.Current.CancellationToken);

        Output.WriteLine(refused.Message);
        refused.Message.Should().Contain(gone,
            "the caller has to be told WHICH descendant blocked the delete");
        refused.Message.Should().Contain("No node found at",
            "the original refusal survives verbatim — the fix changes which refusals are believed, "
            + "never what a believed one says");

        storage.Vanished.Should().BeTrue(
            "the test only discriminates if the leaf really became unroutable");
        (await storage.Inner.Exists(rootPath).Should().Within(TestTimeouts.Quick).Emit(
                cancellationToken: TestContext.Current.CancellationToken))
            .Should().BeTrue("a refused pre-flight removes NOTHING — the root included");
        (await storage.Inner.Exists(stays).Should().Within(TestTimeouts.Quick).Emit(
                cancellationToken: TestContext.Current.CancellationToken))
            .Should().BeTrue("nor may a sibling be removed ahead of the refusal");
    }

    /// <summary>Root with two children; the second is the one that will vanish.</summary>
    private async Task<(string Root, string Stays, string Gone)> SeedSubtree(string id)
    {
        var rootPath = $"{TestPartition}/{id}";
        await NodeFactory.CreateNode(
                new MeshNode(id, TestPartition) { Name = id, NodeType = "Markdown" })
            .Should().Within(TestTimeouts.Convergence).Emit(
                cancellationToken: TestContext.Current.CancellationToken);
        await NodeFactory.CreateNode(
                new MeshNode("stays", rootPath) { Name = "Stays", NodeType = "Markdown" })
            .Should().Within(TestTimeouts.Convergence).Emit(
                cancellationToken: TestContext.Current.CancellationToken);
        await NodeFactory.CreateNode(
                new MeshNode("gone-by-preflight", rootPath) { Name = "Gone", NodeType = "Markdown" })
            .Should().Within(TestTimeouts.Convergence).Emit(
                cancellationToken: TestContext.Current.CancellationToken);
        return (rootPath, $"{rootPath}/stays", $"{rootPath}/gone-by-preflight");
    }
}
