using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
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
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The owner's post-commit durable flush with a switch: a flush of the parked path is BUILT at once
/// (so its claim on the persistence sampler is taken, exactly as the production flush takes it) and
/// SUBSCRIBED only once the test releases it — a cross-hub patch that has passed its owner's
/// commit-time deletion check and committed, whose storage write has not been issued yet. Parking is
/// an Rx continuation on the release, never a sleep.
/// </summary>
internal sealed class ParkedPostCommitFlush(IPostCommitFlush inner, PostCommitFlushSwitch control) : IPostCommitFlush
{
    /// <inheritdoc />
    public IObservable<bool> Flush(object committed)
    {
        var durable = inner.Flush(committed);
        if (committed is not MeshNode node || !control.IsParked(node.Path))
            return durable;
        control.NoteParked();
        return control.Released.SelectMany(_ => durable);
    }

    /// <inheritdoc />
    public void PublishUpdated(object committed) => inner.PublishUpdated(committed);
}

/// <summary>The test-owned switch of <see cref="ParkedPostCommitFlush"/> — one instance per test.</summary>
internal sealed class PostCommitFlushSwitch
{
    private string? _parkPath;
    private readonly AsyncSubject<Unit> _release = new();
    private readonly AsyncSubject<Unit> _parked = new();

    /// <summary>Parks every flush of <paramref name="path"/> from now until <see cref="Release"/>.</summary>
    public void Park(string path) => Volatile.Write(ref _parkPath, path);

    /// <summary>True while flushes of <paramref name="path"/> are parked.</summary>
    public bool IsParked(string path)
        => Volatile.Read(ref _parkPath) is { } parked
           && string.Equals(parked, path, StringComparison.OrdinalIgnoreCase);

    /// <summary>Completes once a flush of the parked path has been built and is parked.</summary>
    public IObservable<Unit> Parked => _parked;

    /// <summary>Completes once the test released the parked flushes.</summary>
    public IObservable<Unit> Released => _release;

    /// <summary>Records that a flush reached the switch.</summary>
    public void NoteParked()
    {
        _parked.OnNext(Unit.Default);
        _parked.OnCompleted();
    }

    /// <summary>Lets every parked flush run. Idempotent.</summary>
    public void Release()
    {
        Volatile.Write(ref _parkPath, null);
        _release.OnNext(Unit.Default);
        _release.OnCompleted();
    }
}

/// <summary>
/// 🚨 Systemorph/MeshWeaver#7346: a cross-hub write that passed its owner's commit-time deletion check
/// (#6953) must be either refused or made durable BEFORE a delete (or move) that starts after the
/// check — never refused at its flush after it had already committed at the owner.
///
/// <para>The window: the owner checks <c>IActiveDeletionScopes</c> in the merge turn and commits; the
/// durable flush follows on the commit echo. A delete opening its scope between the two did not know
/// the write existed: the storage guard registers a write only when the write reaches it, so the
/// delete's wait for admitted writes found nothing, removed the node, and the flush was then refused —
/// by the open scope, or by the tombstone once the delete had answered. The writer was told its write
/// failed while the owner had committed and served it (and, had the operation aborted, would have
/// stored it later behind the writer's back) — the #6953 disagreement in its narrower form.</para>
///
/// <para>The fault is injected deterministically: the flush is parked ABOVE the storage guard (the
/// owner has committed; nothing has reached storage), the delete starts only then, and the flush is
/// released only once the delete has provably begun.</para>
/// </summary>
public class AWriteAdmittedAtCommitIsWaitedForByTheDeleteTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly PostCommitFlushSwitch _flush = new();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder.ConfigureServices(services =>
        {
            // Registered BEFORE the bootstrap's TryAddSingleton, so this IS the owner's flush; it
            // wraps the production flush, which does the real durable write once released.
            services.AddSingleton<IPostCommitFlush>(sp => new ParkedPostCommitFlush(
                new StoragePostCommitFlush(sp.GetRequiredService<IMessageHub>()), _flush));
            return services;
        }));

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    private RecentlyDeletedRegistry Registry => Mesh.ServiceProvider.GetRequiredService<RecentlyDeletedRegistry>();

    /// <summary>
    /// The window in which a delete that does NOT wait for the committed write would answer — inside
    /// the delete's own stage budget, for the reason <see cref="AWriteAdmittedBeforeADeleteNeverRecreatesItTest"/>
    /// gives.
    /// </summary>
    private TimeSpan WhileParkedWindow
    {
        get
        {
            var budget = (Mesh.ServiceProvider.GetService<MeshOperationOptions>() ?? new MeshOperationOptions()).Timeout;
            var quarter = TimeSpan.FromTicks(budget.Ticks / 4);
            return quarter < TestTimeouts.Quick ? quarter : TestTimeouts.Quick;
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task APatchCommittedBeforeTheDeleteBegan_IsDurableBeforeIt_AndTheDeleteRemovesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = $"{TestPartition}/committed-{Guid.NewGuid().ToString("N")[..8]}";
        await NodeFactory.CreateNode(MeshNode.FromPath(path) with
        {
            Name = "before",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        var stream = Mesh.GetWorkspace().GetMeshNodeStream(path);
        await stream.Where(n => n?.Name == "before").Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit("the owner serves the node", cancellationToken: ct);

        // 1. A cross-hub patch passes the owner's commit check, commits, and its flush parks.
        _flush.Park(path);
        var write = stream.Update(n => n with { Name = "committed before the delete" })
            .Take(1)
            .Materialize()
            .Where(n => n.Kind != NotificationKind.OnCompleted)
            .Replay(1);
        using var writeConnection = write.Connect();

        try
        {
            await _flush.Parked.Should().Within(TestTimeouts.Convergence)
                .Emit("the patch committed at the owner and its durable flush is parked", cancellationToken: ct);

            // 2. The delete starts while the committed write's flush is in flight.
            var delete = ObserveNodeOperation(new DeleteNodeRequest(path))
                .Select(d => d.Message)
                .Replay(1);
            using var deleteConnection = delete.Connect();

            // 3. Release only once the delete has provably begun — its scope is open, or it answered.
            await Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
                .Select(_ => Registry.IsUnderActiveDeletion(path, out var _))
                .Where(open => open)
                .Select(_ => Unit.Default)
                .Merge(delete.Select(_ => Unit.Default))
                .Should().Within(TestTimeouts.Convergence)
                .Emit("the delete has begun", cancellationToken: ct);

            // 4. A delete that does not know about the committed write gets every chance to answer.
            var whileParked = await delete.Select(r => (DeleteNodeResponse?)r)
                .Merge(Observable.Timer(WhileParkedWindow).Select(_ => (DeleteNodeResponse?)null))
                .Should().Within(TestTimeouts.Convergence).Emit("either the answer or the window", cancellationToken: ct);
            Output.WriteLine(whileParked is null
                ? "[delete] waiting for the write committed before it"
                : $"[delete] answered while the committed write's flush was parked: success={whileParked.Success} error={whileParked.Error}");

            // Negative control: the commit-time admission of the parked write opens no door for a NEW
            // write — a patch arriving now, under the open scope, is still refused at commit.
            if (whileParked is null)
            {
                var late = await stream.Update(n => n with { Description = "after the delete began" })
                    .Take(1)
                    .Materialize()
                    .Where(n => n.Kind != NotificationKind.OnCompleted)
                    .Should().Within(TestTimeouts.Convergence).Emit("the late patch is answered", cancellationToken: ct);
                Output.WriteLine($"[late write] {late.Kind} {late.Exception?.Message}");
                late.Kind.Should().Be(NotificationKind.OnError,
                    "a write that reaches the owner after the delete's scope opened is refused at commit");
            }

            // 5. Release the flush.
            _flush.Release();
            var writeOutcome = await write.Should().Within(TestTimeouts.Convergence)
                .Emit("the committed write is answered once its flush runs", cancellationToken: ct);
            Output.WriteLine($"[write] {writeOutcome.Kind} {writeOutcome.Exception?.Message}");
            writeOutcome.Kind.Should().Be(NotificationKind.OnNext,
                "the owner committed this write before the delete began, so it is made durable before "
                + "the delete — never refused at its flush after the owner had already served it");

            var response = await delete.Should().Within(TestTimeouts.Convergence)
                .Emit("the delete answers", cancellationToken: ct);
            Output.WriteLine($"[delete] success={response.Success} error={response.Error}");
            response.Success.Should().BeTrue(response.Error ?? "nothing objects to deleting this node");

            var stored = await Storage.Read(path, Mesh.JsonSerializerOptions)
                .DefaultIfEmpty(null)
                .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
            Output.WriteLine($"[store] {(stored is null ? "absent" : $"PRESENT name='{stored.Name}' v{stored.Version}")}");
            stored.Should().BeNull("the delete answered success after the write — it removed what the write stored");

            whileParked.Should().BeNull(
                "the delete must wait for a write its owner committed before the delete began");
        }
        finally
        {
            _flush.Release();
        }
    }
}
