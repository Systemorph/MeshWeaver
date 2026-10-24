using System;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Testing.FaultInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The owner-teardown STORE FENCE (#6856) against the rows it may meet, in isolation. It must move the
/// writable row of the confirmed node past the confirmed version — and nothing else: not a read-only
/// fallback served under a removed override (expecting its version 0 is the insert-if-absent form,
/// which would recreate the deleted override), not a node that was deleted and created again (a new
/// incarnation at a version at or below the confirmed one), and not after the delete's own verdict
/// arrived.
/// </summary>
public class OwnerTeardownStoreFenceTest
{
    private const string Path = "Fence/Doc";
    private static readonly JsonSerializerOptions Options = new();
    private static readonly DateTimeOffset Born = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static MeshNode Confirmed(long version) =>
        new("Doc", "Fence") { Name = "confirmed", NodeType = "Markdown", Version = version, CreatedDate = Born };

    private static async Task<bool> Fence(FaultInjectingStorageAdapter storage, MeshNode confirmed, Func<bool>? stop = null)
        => await MeshExtensions.FenceTheConfirmedRow(storage, confirmed, Options, NullLogger.Instance, stop ?? (() => false))
            .Should().Within(TestTimeouts.Convergence).Emit("the fence terminates", TestContext.Current.CancellationToken);

    private static async Task Put(InMemoryStorageAdapter inner, MeshNode node)
        => await inner.Write(node, Options).Should().Within(TestTimeouts.Convergence)
            .Emit("the row is stored", TestContext.Current.CancellationToken);

    [Fact(Timeout = 10_000)]
    public async Task TheConfirmedRow_IsMovedPastTheConfirmedVersion()
    {
        var inner = new InMemoryStorageAdapter();
        var storage = new FaultInjectingStorageAdapter(inner);
        await Put(inner, Confirmed(3));

        (await Fence(storage, Confirmed(3))).Should().BeFalse("the row exists; it is fenced, not gone");
        var stored = await inner.Read(Path, Options).Should().Within(TestTimeouts.Convergence)
            .Emit(cancellationToken: TestContext.Current.CancellationToken);
        stored!.Version.Should().Be(4, "the paused delete's compare-and-delete at version 3 is now refused");
        stored.Name.Should().Be("confirmed");
    }

    [Fact(Timeout = 10_000)]
    public async Task ARemovedOverrideAboveAnUnversionedFallback_IsGone_AndIsNotRecreated()
    {
        var inner = new InMemoryStorageAdapter();
        var storage = new FaultInjectingStorageAdapter(inner);
        storage.ServeReadOnlyFallback(Confirmed(0) with { Name = "shipped" });

        (await Fence(storage, Confirmed(3))).Should().BeTrue("the WRITABLE row is gone: the delete won");
        (await inner.Exists(Path).Should().Within(TestTimeouts.Convergence)
                .Emit(cancellationToken: TestContext.Current.CancellationToken))
            .Should().BeFalse("the fence must not insert the deleted override again over the shipped fallback");
    }

    [Fact(Timeout = 10_000)]
    public async Task ANodeDeletedAndCreatedAgain_IsLeftAlone()
    {
        var inner = new InMemoryStorageAdapter();
        var storage = new FaultInjectingStorageAdapter(inner);
        await Put(inner, Confirmed(1) with { Name = "recreated", CreatedDate = Born.AddDays(1) });

        (await Fence(storage, Confirmed(3))).Should().BeFalse();
        var stored = await inner.Read(Path, Options).Should().Within(TestTimeouts.Convergence)
            .Emit(cancellationToken: TestContext.Current.CancellationToken);
        stored!.Name.Should().Be("recreated", "another incarnation is not the confirmed node's to overwrite");
        stored.Version.Should().Be(1);
    }

    [Fact(Timeout = 10_000)]
    public async Task OnceTheDeletesVerdictArrived_TheFenceWritesNothing()
    {
        var inner = new InMemoryStorageAdapter();
        var storage = new FaultInjectingStorageAdapter(inner);
        await Put(inner, Confirmed(3));

        (await Fence(storage, Confirmed(3), stop: () => true)).Should().BeFalse();
        var stored = await inner.Read(Path, Options).Should().Within(TestTimeouts.Convergence)
            .Emit(cancellationToken: TestContext.Current.CancellationToken);
        stored!.Version.Should().Be(3, "the delete's own commit or abort decides; the fence stands down");
    }
}
