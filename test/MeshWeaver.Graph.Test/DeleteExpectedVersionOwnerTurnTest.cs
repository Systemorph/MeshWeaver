using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// A real <see cref="IStorageAdapter"/> over a real <see cref="InMemoryStorageAdapter"/> that can
/// HOLD the next write to one path until the test releases it. It reproduces the one thing a busy
/// mesh does that a fast in-memory suite hides: the owner acknowledges an own write from its
/// in-memory commit, and the store receives that write one persistence-sampler interval LATER.
/// Holding the write turns that window from ~200 ms into "until released", deterministically.
/// </summary>
internal sealed class HeldWriteStorageAdapter(InMemoryStorageAdapter inner) : IStorageAdapter
{
    private string? _holdPath;
    private readonly AsyncSubject<Unit> _release = new();
    private readonly AsyncSubject<Unit> _writeHeld = new();

    /// <summary>
    /// Arms the hold: the next write to <paramref name="path"/> (and every later one) waits until
    /// <see cref="Release"/>. Never a sleep: the held write is an Rx continuation on the release.
    /// </summary>
    public void HoldWritesTo(string path) => Volatile.Write(ref _holdPath, path);

    /// <summary>Completes when a write to the held path has reached the store and is being held.</summary>
    public IObservable<Unit> WriteHeld => _writeHeld;

    /// <summary>Lets every held write through. Idempotent.</summary>
    public void Release()
    {
        Volatile.Write(ref _holdPath, null);
        _release.OnNext(Unit.Default);
        _release.OnCompleted();
    }

    private bool Held(string path)
        => Volatile.Read(ref _holdPath) is { } held
           && string.Equals(held, path, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
        => Observable.Defer(() =>
        {
            if (!Held(node.Path))
                return inner.Write(node, options);
            _writeHeld.OnNext(Unit.Default);
            _writeHeld.OnCompleted();
            return _release.SelectMany(_ => inner.Write(node, options));
        });

    /// <inheritdoc />
    public IObservable<bool?> WriteIfVersion(MeshNode node, long expectedVersion, JsonSerializerOptions options)
        => Observable.Defer(() => Held(node.Path)
            ? _release.SelectMany(_ => inner.WriteIfVersion(node, expectedVersion, options))
            : inner.WriteIfVersion(node, expectedVersion, options));

    private string? _holdDeletePath;
    private readonly AsyncSubject<Unit> _releaseDelete = new();
    private readonly AsyncSubject<Unit> _deleteHeld = new();

    /// <summary>
    /// Arms the delete hold: a delete of <paramref name="path"/> that reaches the store waits until
    /// <see cref="ReleaseDelete"/>. It pauses a delete AFTER every check it makes (the owner's
    /// verdict included) and before its commit lands.
    /// </summary>
    public void HoldDeletesOf(string path) => Volatile.Write(ref _holdDeletePath, path);

    /// <summary>Completes when a delete of the held path has reached the store and is being held.</summary>
    public IObservable<Unit> DeleteHeld => _deleteHeld;

    /// <summary>Lets every held delete through. Idempotent.</summary>
    public void ReleaseDelete()
    {
        Volatile.Write(ref _holdDeletePath, null);
        _releaseDelete.OnNext(Unit.Default);
        _releaseDelete.OnCompleted();
    }

    private IObservable<T> HoldDelete<T>(IEnumerable<string> paths, Func<IObservable<T>> delete)
        => Observable.Defer(() =>
        {
            var held = Volatile.Read(ref _holdDeletePath);
            if (held is null || !paths.Any(p => string.Equals(p, held, StringComparison.OrdinalIgnoreCase)))
                return delete();
            _deleteHeld.OnNext(Unit.Default);
            _deleteHeld.OnCompleted();
            return _releaseDelete.SelectMany(_ => delete());
        });

    /// <inheritdoc />
    public IObservable<string> Delete(string path) => HoldDelete([path], () => inner.Delete(path));

    /// <inheritdoc />
    public IObservable<bool> DeleteIfExists(string path) => HoldDelete([path], () => inner.DeleteIfExists(path));

    /// <inheritdoc />
    /// <remarks>Held like every delete, then decided ATOMICALLY by the inner store once released —
    /// the default would read before the hold and delete after it.</remarks>
    public IObservable<bool> DeleteIfVersionAtMost(string path, long maxVersion, JsonSerializerOptions options)
        => HoldDelete([path], () => inner.DeleteIfVersionAtMost(path, maxVersion, options));

    /// <inheritdoc />
    public IObservable<IReadOnlyList<string>> DeleteMany(IReadOnlyCollection<string> paths)
        => HoldDelete(paths, () => ((IStorageAdapter)inner).DeleteMany(paths));

    /// <inheritdoc />
    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        => Volatile.Read(ref _fallback) is { } fallback
           && string.Equals(fallback.Path, path, StringComparison.OrdinalIgnoreCase)
            ? inner.Read(path, options).Select(stored => stored ?? fallback)
            : inner.Read(path, options);

    private MeshNode? _fallback;

    /// <summary>
    /// Serves <paramref name="shipped"/> on <see cref="Read"/> whenever no writable row exists at its
    /// path — the shape of a READ-ONLY provider (shipped content) under a writable override. Writable
    /// existence (<see cref="ExistsInWritableStorage"/>) is untouched, as a read-only provider's is.
    /// </summary>
    public void ServeReadOnlyFallback(MeshNode shipped) => Volatile.Write(ref _fallback, shipped);

    /// <inheritdoc />
    public IObservable<bool> Exists(string path) => inner.Exists(path);

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
    public IObservable<IReadOnlyCollection<string>> ListDescendantPaths(string rootPath)
        => inner.ListDescendantPaths(rootPath);

    /// <inheritdoc />
    public IObservable<IEnumerable<string>> ListPartitionSubPaths(string nodePath)
        => ((IStorageAdapter)inner).ListPartitionSubPaths(nodePath);

    /// <inheritdoc />
    public IObservable<object> GetPartitionObjects(string nodePath, string? subPath, JsonSerializerOptions options)
        => inner.GetPartitionObjects(nodePath, subPath, options);

    /// <inheritdoc />
    public IObservable<Unit> SavePartitionObjects(
        string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
        => inner.SavePartitionObjects(nodePath, subPath, objects, options);

    /// <inheritdoc />
    public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
        => inner.DeletePartitionObjects(nodePath, subPath);

    /// <inheritdoc />
    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
        => inner.GetPartitionMaxTimestamp(nodePath, subPath);

    /// <inheritdoc />
    public IObservable<DataChangeNotification> Changes => inner.Changes;
}

/// <summary>
/// #6673: a delete guarded by <see cref="DeleteNodeRequest.ExpectedVersion"/> must never remove a
/// change the owner has already ACKNOWLEDGED. An own write is acknowledged from the owner's
/// in-memory commit and stored about one sampler interval later; the version check used to read the
/// STORE, so in that window it matched the old version and the delete removed the acknowledged
/// change. The check now runs inside the owning hub's commit turn against its live version, with the
/// path sealed against own writes first, so check and delete are one step.
/// </summary>
public class DeleteExpectedVersionOwnerTurnTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly HeldWriteStorageAdapter _storage = new(new InMemoryStorageAdapter());

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder.ConfigureServices(services =>
        {
            // Registered BEFORE the bootstrap's TryAddSingleton, so this IS the store of record and
            // the production decorators (subtree guard, version writing) wrap it.
            services.AddSingleton<IStorageAdapter>(_storage);
            return services;
        }));

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<MeshNode> Seed(string id) =>
        await NodeFactory.CreateNode(new MeshNode(id, TestPartition) { Name = id, NodeType = "Markdown" })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: Ct);

    private async Task<MeshNode?> Stored(string path) =>
        await _storage.Read(path, Mesh.JsonSerializerOptions).Should().Within(TestTimeouts.Convergence)
            .Emit(cancellationToken: Ct);

    /// <summary>The node's own hub, activated by reading the node and STARTED.</summary>
    private async Task<IMessageHub> OwnerOf(string path)
    {
        await Mesh.GetMeshNodeStream(path).Where(n => n is not null)
            .Should().Within(TestTimeouts.Convergence).Emit("the node is readable", cancellationToken: Ct);
        return await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never))
            .Where(h => h is { IsDisposing: false } && h.Started.IsCompletedSuccessfully)
            .Select(h => h!)
            .Should().Within(TestTimeouts.Convergence).Emit("the node's own hub is hosted", cancellationToken: Ct);
    }

    /// <summary>
    /// An OWN write on the owner, acknowledged while its store write is held: returns the version the
    /// owner acknowledged, after proving the store still holds the old one.
    /// </summary>
    private async Task<long> AcknowledgedButNotStored(IMessageHub owner, string path, long stored)
    {
        _storage.HoldWritesTo(path);
        var acked = await owner.GetWorkspace().GetMeshNodeStream()
            .Update(n => n with { Name = "acknowledged, not yet stored" })
            .Should().Within(TestTimeouts.Convergence).Emit("the owner acknowledges its own write", cancellationToken: Ct);
        await _storage.WriteHeld.Should().Within(TestTimeouts.Convergence)
            .Emit("the sampler's flush reached the store and is held", cancellationToken: Ct);
        (await Stored(path))!.Version.Should().Be(stored,
            "the premise: the acknowledged write is NOT in the store yet");
        acked.Version.Should().BeGreaterThan(stored, "the owner minted a newer version");
        return acked.Version;
    }

    /// <summary>
    /// #6673's comparison SOURCE, pinned in the state it is about — the owner holds V+1 while the
    /// store still holds V — at the owner-turn ask itself. The end-to-end tests below can no longer
    /// observe that state at the comparison: the delete now waits for the held (already ADMITTED)
    /// store write to settle first (#6719), so by the time it compares, store and owner agree, and a
    /// comparison against the STORED version would pass them too. Asked directly, with the store write
    /// still held, the owner must refuse V naming V+1 and confirm V+1 — a stored-version comparison
    /// answers the opposite of both.
    /// </summary>
    [Fact]
    public async Task TheOwnerTurnCompares_TheOwnersLiveVersion_NotTheStoredOne()
    {
        var node = await Seed("owner-turn-source");
        var owner = await OwnerOf(node.Path);
        var read = (await Stored(node.Path))!.Version;
        try
        {
            var acked = await AcknowledgedButNotStored(owner, node.Path, read);

            var stale = (await RequestHub
                .Observe(new ValidateDeleteRequest(node.Path) { ExpectedVersion = read },
                    o => o.WithTarget(new Address(node.Path)))
                .Should().Within(TestTimeouts.Convergence).Emit("the owner answers", Ct)).Message;
            Output.WriteLine($"[ask V={read}] valid={stale.IsValid} reason={stale.Reason} actual={stale.ActualVersion}");
            stale.IsValid.Should().BeFalse($"the owner holds {acked}, not the stored {read}");
            stale.Reason.Should().Be(NodeDeletionRejectionReason.Conflict);
            stale.ActualVersion.Should().Be(acked);

            // Control: the same ask naming the owner's version is confirmed — so the refusal above is
            // a comparison, not a refusal of every ask while a write is pending.
            var live = (await RequestHub
                .Observe(new ValidateDeleteRequest(node.Path) { ExpectedVersion = acked },
                    o => o.WithTarget(new Address(node.Path)))
                .Should().Within(TestTimeouts.Convergence).Emit("the owner answers", Ct)).Message;
            Output.WriteLine($"[ask V={acked}] valid={live.IsValid} confirmed={live.ConfirmedVersion}");
            live.IsValid.Should().BeTrue(string.Join("; ", live.Errors));
            live.ConfirmedVersion.Should().Be(acked);
            (await Stored(node.Path))!.Version.Should().Be(read,
                "the premise held throughout: the store still had the old version while the owner was asked");
        }
        finally
        {
            _storage.Release();
        }
    }

    /// <summary>
    /// THE FALSIFIER. The deleter read version V. The owner acknowledges V+1, and its store write is
    /// held. The delete naming V arrives: it must be refused, and once the store write is released
    /// the acknowledged change is stored. Before #6673 the check read V from the store, matched, and
    /// the delete removed the acknowledged change.
    /// </summary>
    [Fact]
    public async Task AGuardedDelete_IsRefused_WhenTheOwnerAcknowledgedANewerVersionNotYetStored()
    {
        var node = await Seed("owner-turn-stale");
        var owner = await OwnerOf(node.Path);
        var read = (await Stored(node.Path))!.Version;
        long acked;
        IConnectableObservable<IMessageDelivery<DeleteNodeResponse>> delete;
        IDisposable connection;
        try
        {
            acked = await AcknowledgedButNotStored(owner, node.Path, read);
            // The held store write was ADMITTED before the delete began, so the delete waits for it
            // to settle before it compares (#6719) — it is issued here and answered after the release.
            delete = ObserveNodeOperation(new DeleteNodeRequest(node.Path) { ExpectedVersion = read }).Replay(1);
            connection = delete.Connect();
        }
        finally
        {
            _storage.Release();
        }

        using (connection)
        {
            var response = (await delete.Should().Within(TestTimeouts.Convergence)
                .Emit("a delete always answers", Ct)).Message;
            Output.WriteLine($"[delete] success={response.Success} reason={response.RejectionReason} error={response.Error}");

            response.Success.Should().BeFalse(
                $"the owner acknowledged version {acked} after the deleter read {read}");
            response.RejectionReason.Should().Be(NodeDeletionRejectionReason.Conflict);
        }

        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => _storage.Read(node.Path, Mesh.JsonSerializerOptions).Take(1))
            .Where(n => n?.Name == "acknowledged, not yet stored")
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the acknowledged change reaches the store — nothing removed it", cancellationToken: Ct);
    }

    /// <summary>
    /// CONTROL: the comparison is against the owner's LIVE version, not a refusal of every delete
    /// while a write is pending. A delete naming the version the owner acknowledged deletes. It is
    /// ISSUED while the store write is still held, and answers only once that write is released: the
    /// write was admitted by the store's guard before the delete began, so the delete waits for it to
    /// settle and is ordered after it (#6719) — never a commit that lands after the delete and
    /// recreates the node.
    /// </summary>
    [Fact]
    public async Task AGuardedDelete_NamingTheAcknowledgedVersion_Deletes_EvenBeforeItIsStored()
    {
        var node = await Seed("owner-turn-live");
        var owner = await OwnerOf(node.Path);
        var read = (await Stored(node.Path))!.Version;
        IConnectableObservable<IMessageDelivery<DeleteNodeResponse>> delete;
        IDisposable connection;
        try
        {
            var acked = await AcknowledgedButNotStored(owner, node.Path, read);
            delete = ObserveNodeOperation(new DeleteNodeRequest(node.Path) { ExpectedVersion = acked }).Replay(1);
            connection = delete.Connect();
        }
        finally
        {
            _storage.Release();
        }

        using (connection)
        {
            var response = (await delete.Should().Within(TestTimeouts.Convergence)
                .Emit("a delete always answers", Ct)).Message;
            Output.WriteLine($"[delete] success={response.Success} reason={response.RejectionReason} error={response.Error}");
            response.Success.Should().BeTrue(response.Error ?? "the deleter named the version the owner holds");
            response.AlreadyAbsent.Should().BeFalse();
            (await _storage.Exists(node.Path).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: Ct))
                .Should().BeFalse("the node is gone — the released write committed BEFORE the delete, never after");
        }
    }

    /// <summary>
    /// THE SEAL: while a guarded delete of the path is in flight, the owner DEFERS an own write in its
    /// commit turn instead of acknowledging it (#6856) — acknowledged, the write would be removed by the
    /// delete the comparison already let through. Lifting the seal without a delete (the scope ends,
    /// nothing was tombstoned) re-runs the turn, and the same write is then applied.
    /// </summary>
    [Fact]
    public async Task AnOwnWrite_IsDeferred_WhileAGuardedDeleteOfThePathIsInFlight_AndAppliedWhenTheSealLifts()
    {
        var node = await Seed("owner-turn-seal");
        var owner = await OwnerOf(node.Path);
        var registry = Mesh.ServiceProvider.GetRequiredService<RecentlyDeletedRegistry>();

        IConnectableObservable<System.Reactive.Notification<MeshNode>> write;
        IDisposable connection;
        using (registry.BeginVersionGuardedDeletion(node.Path))
        {
            write = owner.GetWorkspace().GetMeshNodeStream()
                .Update(n => n with { Name = "after the comparison" })
                .Materialize()
                .Where(n => n.Kind != System.Reactive.NotificationKind.OnCompleted)
                .Replay();
            connection = write.Connect();
            await write.Should().NotEmit(TimeSpan.FromSeconds(1),
                "a write the guarded delete would remove is neither acknowledged nor refused while the seal holds",
                Ct);
        }

        using (connection)
        {
            var answer = await write.Should().Within(TestTimeouts.Convergence)
                .Emit("the seal lifted with nothing deleted, so the deferred write runs", Ct);
            answer.Kind.Should().Be(System.Reactive.NotificationKind.OnNext, answer.Exception?.Message ?? "the seal lifted");
            answer.Value!.Name.Should().Be("after the comparison");
        }
    }

    /// <summary>
    /// THE SEAL'S WIRING (#6750, #6856): a REAL guarded <see cref="DeleteNodeRequest"/>, paused at the
    /// store after the owner's verdict, keeps the owner's own writes deferred until it completes, and its
    /// commit then refuses them. Without the seal the write is acknowledged by the owner and removed by
    /// the delete that already passed its check.
    /// </summary>
    [Fact]
    public async Task AGuardedDelete_DefersTheOwnersOwnWrites_FromItsVerdict_AndRefusesThemOnCommit()
    {
        var node = await Seed("owner-turn-wired-seal");
        var owner = await OwnerOf(node.Path);
        var read = (await Stored(node.Path))!.Version;
        _storage.HoldDeletesOf(node.Path);
        var delete = ObserveNodeOperation(new DeleteNodeRequest(node.Path) { ExpectedVersion = read }).Replay(1);
        using var connection = delete.Connect();
        var write = owner.GetWorkspace().GetMeshNodeStream()
            .Update(n => n with { Name = "written after the verdict" })
            .Materialize()
            .Where(n => n.Kind != System.Reactive.NotificationKind.OnCompleted)
            .Replay();
        IDisposable writeConnection;
        try
        {
            await _storage.DeleteHeld.Should().Within(TestTimeouts.Convergence)
                .Emit("the guarded delete passed the owner's verdict and reached the store", cancellationToken: Ct);

            writeConnection = write.Connect();
            await write.Should().NotEmit(TimeSpan.FromSeconds(1),
                "the guarded delete in flight has already decided; a write it would remove is deferred", Ct);
        }
        finally
        {
            _storage.ReleaseDelete();
        }

        using (writeConnection)
        {
            var response = (await delete.Should().Within(TestTimeouts.Convergence).Emit("a delete always answers", Ct)).Message;
            response.Success.Should().BeTrue(response.Error ?? "the deleter named the version the owner holds");

            var refused = await write.Should().Within(TestTimeouts.Convergence)
                .Emit("the deferred write is answered once the delete commits", Ct);
            Output.WriteLine($"[own write] {refused.Kind} {refused.Exception?.GetType().Name}: {refused.Exception?.Message}");
            refused.Kind.Should().Be(System.Reactive.NotificationKind.OnError,
                "the node is deleted: the deferred write is refused, never acknowledged");
            // In one process the delete also disposes the owner it removed, so the refusal is either
            // the seal's (SubtreeDeletionInFlightException) or the dying stream's own
            // (ObjectDisposedException), whichever reaches the write first. Both say "not written".
            (refused.Exception is SubtreeDeletionInFlightException or ObjectDisposedException).Should().BeTrue(
                $"the write is refused, not failed for another reason ({refused.Exception?.GetType().Name})");
            (await _storage.Exists(node.Path).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: Ct))
                .Should().BeFalse("nothing deferred by the seal stored the node again");
        }
    }

    /// <summary>
    /// #6856 review: an own write ACKNOWLEDGED just before the owner took a delete seal reaches the
    /// persistence sampler while the path is sealed, and the owner's row guard refuses that save. When
    /// the delete then ABORTS and nothing else changes the node, the refused save must be re-dispatched
    /// — before, it stayed pending until the next change or the dispose flush, so an acknowledged state
    /// was not durable for as long as the node stayed quiet.
    /// </summary>
    [Fact]
    public async Task AnAcknowledgedSaveRefusedWhileSealed_IsStored_WhenTheDeleteAborts()
    {
        var node = await Seed("owner-seal-resume");
        var owner = await OwnerOf(node.Path);
        var registry = Mesh.ServiceProvider.GetRequiredService<RecentlyDeletedRegistry>();
        var stored = (await Stored(node.Path))!.Version;

        // Sealed IN the acknowledgement's own emission — synchronously, before the test continuation
        // or the sampler's next tick can run — so the sampled save of this write meets the row guard.
        var sealedOnAck = false;
        var acked = await owner.GetWorkspace().GetMeshNodeStream()
            .Update(n => n with { Name = "acknowledged before the seal" })
            .Do(_ => sealedOnAck = registry.TakeOwnerDeleteSeal(
                "resume-seal", node.Path, owner.RegisterForDisposalDetachable))
            .Should().Within(TestTimeouts.Convergence).Emit("an own write is acknowledged from memory", Ct);
        sealedOnAck.Should().BeTrue();
        try
        {
            await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
                .SelectMany(_ => _storage.Read(node.Path, Mesh.JsonSerializerOptions).Take(1))
                .Where(n => n?.Version >= acked.Version)
                .Should().NotEmit(TimeSpan.FromSeconds(1), "the sealed row refuses the sampled save", Ct);
        }
        finally
        {
            registry.ReleaseOwnerDeleteSeal("resume-seal", node.Path, DeleteSealOutcome.Aborted);
        }

        var persisted = await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => _storage.Read(node.Path, Mesh.JsonSerializerOptions).Take(1))
            .Where(n => n?.Name == "acknowledged before the seal")
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the abort re-dispatches the refused save, with no further change to the node", Ct);
        persisted!.Version.Should().BeGreaterThan(stored);
    }

    /// <summary>
    /// Review round 11 (4306961101): the save a delete seal refused is re-dispatched THROUGH the
    /// persistence sampler, never beside it, so own saves keep one poster and one order — an older
    /// refused snapshot can never be written after a newer own write. Here a newer write follows the
    /// abort at once; the store reaches the newer state and never falls back to the refused one.
    /// </summary>
    [Fact]
    public async Task AResumedSave_NeverLandsAfterANewerOwnWrite()
    {
        var node = await Seed("owner-seal-resume-order");
        var owner = await OwnerOf(node.Path);
        var registry = Mesh.ServiceProvider.GetRequiredService<RecentlyDeletedRegistry>();

        var sealedOnAck = false;
        await owner.GetWorkspace().GetMeshNodeStream()
            .Update(n => n with { Name = "refused while sealed" })
            .Do(_ => sealedOnAck = registry.TakeOwnerDeleteSeal(
                "resume-order", node.Path, owner.RegisterForDisposalDetachable))
            .Should().Within(TestTimeouts.Convergence).Emit("an own write is acknowledged from memory", Ct);
        sealedOnAck.Should().BeTrue();
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => _storage.Read(node.Path, Mesh.JsonSerializerOptions).Take(1))
            .Where(n => n?.Name == "refused while sealed")
            .Should().NotEmit(TimeSpan.FromMilliseconds(600), "the sealed row refuses the sampled save", Ct);

        registry.ReleaseOwnerDeleteSeal("resume-order", node.Path, DeleteSealOutcome.Aborted).Should().BeTrue();
        await owner.GetWorkspace().GetMeshNodeStream()
            .Update(n => n with { Name = "newer, written right after the abort" })
            .Should().Within(TestTimeouts.Convergence).Emit("the newer write is acknowledged", Ct);

        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => _storage.Read(node.Path, Mesh.JsonSerializerOptions).Take(1))
            .Where(n => n?.Name == "newer, written right after the abort")
            .Should().Within(TestTimeouts.Convergence).Emit("the newer state is stored", Ct);
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => _storage.Read(node.Path, Mesh.JsonSerializerOptions).Take(1))
            .Where(n => n?.Name != "newer, written right after the abort")
            .Should().NotEmit(TimeSpan.FromSeconds(1), "the refused older snapshot never lands after the newer save", Ct);
    }

    /// <summary>
    /// Review round 10 (4306865426): once a delete COMMITTED (the owner's seal released as committed,
    /// which tombstones the path) a still-live owner the delete's invalidation has not reached yet must
    /// not acknowledge a FRESH own write — its save would be dropped as a resurrection, so the
    /// acknowledgement would be for a write that is never stored. Before, only a deferred write
    /// re-checked the tombstone.
    /// </summary>
    [Fact]
    public async Task AFreshOwnWriteAfterACommittedRelease_IsRefused_NotAcknowledged()
    {
        var node = await Seed("owner-after-commit");
        var owner = await OwnerOf(node.Path);
        var registry = Mesh.ServiceProvider.GetRequiredService<RecentlyDeletedRegistry>();
        registry.TakeOwnerDeleteSeal("commit-then-write", node.Path, owner.RegisterForDisposalDetachable)
            .Should().BeTrue();
        registry.ReleaseOwnerDeleteSeal("commit-then-write", node.Path, DeleteSealOutcome.Committed)
            .Should().BeTrue();
        registry.IsVersionGuardedDeletionInFlight(node.Path).Should().BeFalse("the seal settled");

        var answer = await owner.GetWorkspace().GetMeshNodeStream()
            .Update(n => n with { Name = "written after the commit" })
            .Materialize()
            .Where(n => n.Kind != System.Reactive.NotificationKind.OnCompleted)
            .Should().Within(TestTimeouts.Convergence).Emit("the write answers", cancellationToken: Ct);
        answer.Kind.Should().Be(System.Reactive.NotificationKind.OnError,
            "the path was deleted: the owner refuses the write instead of acknowledging one it would drop");
        answer.Exception.Should().BeOfType<SubtreeDeletionInFlightException>();
    }

    /// <summary>
    /// MIXED-VERSION ROLL (#6750): an owner on an image that predates the version-guarded check skips
    /// the unknown <see cref="ValidateDeleteRequest.ExpectedVersion"/>, runs its validators and answers
    /// a plain Ok. That is no verdict about the version, so it is Unavailable — never a match. Control:
    /// the same Ok carrying the echo IS the match.
    /// </summary>
    [Fact]
    public async Task AnOwnerAnsweringOkWithoutTheVersionEcho_IsNoVerdict_NeverAMatch()
    {
        var unconfirmed = await MeshExtensions.OwnerVersionVerdict(
                () => Observable.Return(ValidateDeleteResponse.Ok()),
                () => Observable.Return<MeshNode?>(null), "p/old-owner", 3)
            .Should().Within(TestTimeouts.Convergence).Emit("a verdict always emits", cancellationToken: Ct);
        unconfirmed.IsValid.Should().BeFalse("an owner that never compared cannot pass for one that did");
        unconfirmed.Reason.Should().Be(NodeDeletionRejectionReason.Unavailable);

        var confirmed = await MeshExtensions.OwnerVersionVerdict(
                () => Observable.Return(ValidateDeleteResponse.Ok() with { ConfirmedVersion = 3 }),
                () => Observable.Return<MeshNode?>(null), "p/new-owner", 3)
            .Should().Within(TestTimeouts.Convergence).Emit("a verdict always emits", cancellationToken: Ct);
        confirmed.IsValid.Should().BeTrue("the owner confirmed the expected version");
    }

    /// <summary>
    /// FALLBACK READ FAULT (#6750): the owner could not be asked, and the storage read that would tell
    /// "gone" from "unreachable" faulted too. That is no verdict — Unavailable — never an escaping
    /// fault the handler files as Unknown or ValidationFailed.
    /// </summary>
    [Fact]
    public async Task AFaultingFallbackRead_IsUnavailable()
    {
        var verdict = await MeshExtensions.OwnerVersionVerdict(
                () => Observable.Throw<ValidateDeleteResponse>(new InvalidOperationException("routing refused the owner")),
                () => Observable.Throw<MeshNode?>(new InvalidOperationException("the store faulted")),
                "p/unreachable", 3)
            .Materialize()
            .Where(n => n.Kind != System.Reactive.NotificationKind.OnCompleted)
            .Should().Within(TestTimeouts.Convergence).Emit("a verdict always emits", cancellationToken: Ct);
        verdict.Kind.Should().Be(System.Reactive.NotificationKind.OnNext, verdict.Exception?.Message ?? "");
        verdict.Value.IsValid.Should().BeFalse();
        verdict.Value.Reason.Should().Be(NodeDeletionRejectionReason.Unavailable);
    }

    /// <summary>
    /// OWNER TEARDOWN (#6750): a live-version check that faults because THIS activation is going away
    /// answers ShuttingDown — the retryable nack the asker re-asks the fresh activation on — exactly as
    /// the validator branch does. Control: an unrelated fault is Unavailable.
    /// </summary>
    [Fact]
    public async Task AnOwnerTurnTeardown_AnswersShuttingDown_AndAnyOtherFault_Unavailable()
    {
        var node = await Seed("owner-turn-teardown");
        var owner = await OwnerOf(node.Path);
        var delivery = new MessageDelivery<ValidateDeleteRequest>(
            Mesh.Address, owner.Address, new ValidateDeleteRequest(node.Path) { ExpectedVersion = 1 },
            Mesh.JsonSerializerOptions);

        var teardown = MeshExtensions.OwnerTurnFaultAnswer(
            owner, delivery, node.Path, new HubDisposingException(owner.Address, "the live-version check"),
            NullLogger.Instance);
        teardown.Should().BeOfType<DeliveryFailure>()
            .Which.ErrorType.Should().Be(ErrorType.ShuttingDown, "a recycle is not a verdict about the delete");

        var other = MeshExtensions.OwnerTurnFaultAnswer(
            owner, delivery, node.Path, new InvalidOperationException("unrelated"), NullLogger.Instance);
        other.Should().BeOfType<ValidateDeleteResponse>()
            .Which.Reason.Should().Be(NodeDeletionRejectionReason.Unavailable);
    }
}
