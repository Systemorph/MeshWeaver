using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 A subtree enumeration that COMPLETED WITHOUT AN ANSWER is not an empty subtree
/// (MeshWeaver#7461).
///
/// <para><see cref="IStorageAdapter.ListDescendantPaths"/> "emits one complete snapshot and
/// completes". The production composite (<see cref="PersistenceService"/>) folded every writable
/// provider's answer into a SEEDED union, so a provider that completed empty contributed nothing and
/// the union still emitted — and the recursive-delete planner, its drain verifier, a move's delete
/// leg and a space deletion's install-record read all took "no answer" for "no descendants". The
/// interface's default walk had the same seeded fold one level down: a nested
/// <see cref="IStorageAdapter.ListChildPaths"/> that completed empty dropped its whole subtree.</para>
///
/// <para>Each unanswered case has a positive control beside it: a store that ANSWERS with nothing
/// under the root must still emit an empty collection. A fix that faulted every empty result would
/// pass the unanswered cases and break every leaf delete.</para>
///
/// <para>Deterministic by construction — every store answers (or declines to) synchronously.</para>
/// </summary>
public class AnUnansweredSubtreeEnumerationIsNotAnEmptySubtreeTest
{
    private static readonly JsonSerializerOptions Options = new();

    private static MeshNode Node(string path)
    {
        var slash = path.LastIndexOf('/');
        return new MeshNode(slash < 0 ? path : path[(slash + 1)..], slash < 0 ? null : path[..slash])
        {
            NodeType = "Test",
        };
    }

    private static async Task<InMemoryStorageAdapter> Seeded(params string[] paths)
    {
        var adapter = new InMemoryStorageAdapter();
        foreach (var path in paths)
            await ((IStorageAdapter)adapter).Write(Node(path), Options).FirstAsync()
                .Await(TestContext.Current.CancellationToken);
        return adapter;
    }

    /// <summary>
    /// THE #7461 REPRO. Two writable providers; one answers with the child it holds, the other
    /// completes without emitting. On unfixed code the union emitted <c>{S/A}</c> as if the silent
    /// provider held nothing under <c>S</c>. It must fault, naming the provider that gave no answer.
    /// </summary>
    [Fact]
    public async Task TheComposite_AProviderThatCompletesEmpty_FaultsNamingIt()
    {
        IStorageAdapter composite = new PersistenceService(
        [
            new WritableProvider("answers", await Seeded("S", "S/A")),
            new WritableProvider("silent", new SilentDescendantsAdapter(await Seeded("S", "S/B"))),
        ]);

        var error = await Assert.ThrowsAsync<SubtreeEnumerationUnansweredException>(
            () => composite.ListDescendantPaths("S").FirstAsync().Await(TestContext.Current.CancellationToken));

        Assert.Equal("silent", error.Provider);
        Assert.Equal("S", error.RootPath);
    }

    /// <summary>Positive control: every provider answers, and nothing is under the root — an EMPTY answer.</summary>
    [Fact]
    public async Task TheComposite_EveryProviderAnswersWithNothing_EmitsAnEmptyCollection()
    {
        IStorageAdapter composite = new PersistenceService(
        [
            new WritableProvider("one", await Seeded("S")),
            new WritableProvider("two", await Seeded("Other/X")),
        ]);

        var paths = await composite.ListDescendantPaths("S").FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        Assert.Empty(paths);
    }

    /// <summary>Positive control: the union of the providers that DO answer is unchanged.</summary>
    [Fact]
    public async Task TheComposite_EveryProviderAnswers_EmitsTheUnion()
    {
        IStorageAdapter composite = new PersistenceService(
        [
            new WritableProvider("one", await Seeded("S", "S/A")),
            new WritableProvider("two", await Seeded("S", "S/B", "S/B/c")),
        ]);

        var paths = await composite.ListDescendantPaths("S").FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        Assert.Equal(["S/A", "S/B", "S/B/c"], paths.Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// The interface's default walk: the root level answers, a NESTED level completes empty. On
    /// unfixed code that level's subtree (<c>S/A/x</c>) silently vanished from the enumeration.
    /// </summary>
    [Fact]
    public async Task TheDefaultWalk_ANestedLevelThatCompletesEmpty_Faults()
    {
        IStorageAdapter walking = new SilentLevelAdapter(await Seeded("S", "S/A", "S/A/x"), silentParent: "S/A");

        var error = await Assert.ThrowsAsync<SubtreeEnumerationUnansweredException>(
            () => walking.ListDescendantPaths("S").FirstAsync().Await(TestContext.Current.CancellationToken));

        Assert.Equal("S/A", error.RootPath);
    }

    /// <summary>Positive control for the walk: every level answers, so the whole subtree is enumerated.</summary>
    [Fact]
    public async Task TheDefaultWalk_EveryLevelAnswers_EnumeratesTheSubtree()
    {
        IStorageAdapter walking = new SilentLevelAdapter(await Seeded("S", "S/A", "S/A/x"), silentParent: null);

        var paths = await walking.ListDescendantPaths("S").FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        Assert.Equal(["S/A", "S/A/x"], paths.Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// The outermost decorator every caller resolves: on a single-store mesh there is no composite to
    /// catch the empty completion, so the guard refuses it itself.
    /// </summary>
    [Fact]
    public async Task TheOutermostDecorator_AnInnerThatCompletesEmpty_Faults()
    {
        using var guard = new SubtreeDeletionGuardStorageAdapter(
            new SilentDescendantsAdapter(await Seeded("S", "S/A")), registry: null);

        await Assert.ThrowsAsync<SubtreeEnumerationUnansweredException>(
            () => ((IStorageAdapter)guard).ListDescendantPaths("S").FirstAsync()
                .Await(TestContext.Current.CancellationToken));
    }

    /// <summary>Positive control for the decorator: an inner that answers passes through unchanged.</summary>
    [Fact]
    public async Task TheOutermostDecorator_AnInnerThatAnswers_PassesItThrough()
    {
        using var guard = new SubtreeDeletionGuardStorageAdapter(await Seeded("S", "S/A"), registry: null);

        var paths = await ((IStorageAdapter)guard).ListDescendantPaths("S").FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        Assert.Equal(["S/A"], paths);
    }

    private sealed class WritableProvider(string name, IStorageAdapter adapter) : IPartitionStorageProvider
    {
        public string Name => name;
        public bool IsReadOnly => false;
        public IStorageAdapter Adapter => adapter;
    }

    /// <summary>A real store whose <see cref="IStorageAdapter.ListDescendantPaths"/> completes WITHOUT emitting.</summary>
    private sealed class SilentDescendantsAdapter(IStorageAdapter inner) : ForwardingAdapter(inner), IStorageAdapter
    {
        public IObservable<IReadOnlyCollection<string>> ListDescendantPaths(string rootPath)
            => Observable.Empty<IReadOnlyCollection<string>>();
    }

    /// <summary>
    /// A real store that does NOT override <see cref="IStorageAdapter.ListDescendantPaths"/>, so the
    /// interface's default walk runs over THIS adapter's <see cref="IStorageAdapter.ListChildPaths"/>,
    /// whose listing of <paramref name="silentParent"/> completes WITHOUT emitting.
    /// </summary>
    private sealed class SilentLevelAdapter(IStorageAdapter inner, string? silentParent) : ForwardingAdapter(inner)
    {
        public override IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPaths(string? parentPath)
            => string.Equals(parentPath, silentParent, StringComparison.OrdinalIgnoreCase)
                ? Observable.Empty<(IEnumerable<string>, IEnumerable<string>)>()
                : base.ListChildPaths(parentPath);
    }

    /// <summary>Forwards every abstract member to a real store; <see cref="IStorageAdapter.ListDescendantPaths"/> is left to the interface default.</summary>
    private abstract class ForwardingAdapter(IStorageAdapter inner) : IStorageAdapter
    {
        public IObservable<string> Delete(string path) => inner.Delete(path);

        public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
            => inner.Read(path, options);

        public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
            => inner.Write(node, options);

        public IObservable<DataChangeNotification> Changes => inner.Changes;

        public IObservable<bool> Exists(string path) => inner.Exists(path);

        public virtual IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPaths(string? parentPath)
            => inner.ListChildPaths(parentPath);

        public IObservable<object> GetPartitionObjects(
            string nodePath, string? subPath, JsonSerializerOptions options)
            => inner.GetPartitionObjects(nodePath, subPath, options);

        public IObservable<System.Reactive.Unit> SavePartitionObjects(
            string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
            => inner.SavePartitionObjects(nodePath, subPath, objects, options);

        public IObservable<System.Reactive.Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
            => inner.DeletePartitionObjects(nodePath, subPath);

        public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
            => inner.GetPartitionMaxTimestamp(nodePath, subPath);
    }
}
