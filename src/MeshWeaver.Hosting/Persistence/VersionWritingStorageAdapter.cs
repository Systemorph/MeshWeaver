using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Decorates an <see cref="IStorageAdapter"/> so every <see cref="IStorageAdapter.Write"/>
/// chains a snapshot through <see cref="IVersionQuery.WriteVersion"/> after the inner
/// save succeeds. Replaces the historical
/// <c>FileSystemPersistenceService.SaveNodeAsync</c> chokepoint that was deleted in the
/// persistence cull (2026-05-12) — without this, every save path
/// (CreateNode / UpdateNode handlers, MeshNodeTypeSource flush, sampler) skipped the
/// version-history write and <c>IVersionQuery.GetVersions</c> returned an empty list.
///
/// <para>Best-effort: version-write failures are swallowed so they cannot mask a
/// successful primary save.</para>
/// </summary>
internal class VersionWritingStorageAdapter(
    IStorageAdapter inner,
    IVersionQuery? versionQuery) : IStorageAdapter
{
    // 🚨 Decorator MUST forward Changes — without this it falls back to the
    // interface default Observable.Empty, and every synced query subscribed
    // to persistence.Changes on this decorator stops receiving notifications.
    // The IDataChangeNotifier removal refactor (929bfe985) moved change-feed
    // delivery to IStorageAdapter.Changes — at which point this decorator's
    // missing override silently became the new bottleneck. Symptom in CI
    // (26408564176): ~25 Security / Auth / NodeOps / Layout failures where
    // the synced AccessAssignment query only emitted its Initial = 0 and
    // never re-emitted after CreateNode runtime writes, so permission grants
    // never reached the AccessControlPipeline before its 45 s deadline.
    public IObservable<DataChangeNotification> Changes => inner.Changes;

    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        => inner.Read(path, options);

    public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
    {
        var write = inner.Write(node, options);
        if (versionQuery is null)
            return write;

        return write.SelectMany(saved => saved is null
            ? Observable.Return<MeshNode?>(null)
            : WriteVersionAndReturn(saved!, options));
    }

    private IObservable<MeshNode?> WriteVersionAndReturn(MeshNode saved, JsonSerializerOptions options) =>
        versionQuery!.WriteVersion(saved, options)
            .Catch<MeshNode, Exception>(_ => Observable.Return(saved))
            .DefaultIfEmpty(saved)
            .LastAsync()
            .Select(_ => (MeshNode?)saved);

    public IObservable<string> Delete(string path) => inner.Delete(path);

    public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPaths(string? parentPath)
        => inner.ListChildPaths(parentPath);

    public IObservable<bool> Exists(string path) => inner.Exists(path);

    public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
        string fullPath, JsonSerializerOptions options)
        => inner.FindBestPrefixMatch(fullPath, options);

    /// <summary>
    /// Explicit forward — without this, the interface default routes through
    /// <c>this.FindBestPrefixMatch</c>, stripping the Postgres satellite-UNION
    /// that <c>MeshWeaver.Hosting.PostgreSql.PostgreSqlPathRoutingAdapter.ResolvePath</c>
    /// produces. FileSystem doesn't need the override (its
    /// <c>FindBestPrefixMatch</c> already walks segments), but preserving the
    /// stronger Postgres contract requires the forward.
    /// </summary>
    public IObservable<(MeshNode? Node, int MatchedSegments)> ResolvePath(
        string fullPath, JsonSerializerOptions options)
        => inner.ResolvePath(fullPath, options);

    public IObservable<IEnumerable<string>> ListPartitionSubPaths(string nodePath)
        => inner.ListPartitionSubPaths(nodePath);

    public IObservable<object> GetPartitionObjects(string nodePath, string? subPath, JsonSerializerOptions options)
        => inner.GetPartitionObjects(nodePath, subPath, options);

    public IObservable<Unit> SavePartitionObjects(
        string nodePath, string? subPath,
        IReadOnlyCollection<object> objects, JsonSerializerOptions options)
        => inner.SavePartitionObjects(nodePath, subPath, objects, options);

    public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
        => inner.DeletePartitionObjects(nodePath, subPath);

    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
        => inner.GetPartitionMaxTimestamp(nodePath, subPath);
}
