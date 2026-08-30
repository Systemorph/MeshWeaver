using System;
using System.IO;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Persistence.Test;

/// <summary>
/// Pins the issue #613 follow-up (a): a <see cref="FileSystemStorageAdapter"/> created through
/// <see cref="FileSystemStorageAdapterFactory"/> — the path every config-declared FileSystem data
/// source takes, the FutuRe sample among them — runs its file I/O on the REGISTRY's
/// <c>FileSystem</c> pool, never on the ledgerless <c>IoPool.Unbounded</c>.
///
/// <para>🚨 Why the pool identity matters: <c>IoPool.Unbounded</c> reports
/// <c>CurrentInFlight =&gt; 0</c> unconditionally and bridges via a bare
/// <c>Observable.FromAsync</c>, so I/O routed onto it is invisible to
/// <c>IoPoolRegistry.DrainAll()</c> and the silo teardown join — nothing to cancel, dispose or
/// wait for. A straggler file read could then enter hub construction during teardown and fault on
/// an unloaded collectible ALC: the FutuRe.Test exit=139 teardown-SIGSEGV family. The factory used
/// to construct the adapter without the registry, silently falling back to Unbounded; it now
/// resolves the registry from the provider and fails LOUDLY when the provider has none.</para>
/// </summary>
public class FileSystemIoPoolRoutingTest : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mw-fs-iopool-").FullName;

    private static readonly JsonSerializerOptions Options = new();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact(Timeout = 30000)]
    public async Task FactoryCreatedAdapter_RunsItsReadOnTheRegistrysFileSystemPool()
    {
        // Cap = 1 so pool membership is OBSERVABLE: work queued on this pool cannot run while its
        // only slot is held, whereas work on IoPool.Unbounded runs immediately regardless.
        using var registry = new IoPoolRegistry(new IoPoolOptions { FileSystem = 1 });
        using var provider = new ServiceCollection()
            .AddSingleton(registry)
            .BuildServiceProvider();

        var adapter = new FileSystemStorageAdapterFactory()
            .Create(new GraphStorageConfig { BasePath = _dir }, provider);

        // Seed through the adapter itself — the Write leaf shares the same pool, so this also
        // proves the green path end-to-end before the gate goes up.
        await adapter.Write(
                MeshNode.FromPath("probe/alpha") with { NodeType = "Markdown", Name = "IoPool probe" },
                Options)
            .FirstAsync().ToTask();

        var pool = registry.Get(IoPoolNames.FileSystem);
        // Test-only TCS (never in src/): parks the pool's single slot until released. WaitAsync(ct)
        // keeps the leaf observing the pool's cancellation token, as every leaf must.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockerDone = pool.Invoke(async ct =>
        {
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
            return 0;
        }).FirstAsync().ToTask();

        // Positive half of the proof: the LEDGER sees the held slot. This counter is exactly what
        // teardown's DrainAll/WhenDrained join on — and what Unbounded can never report.
        await Observable.Interval(TimeSpan.FromMilliseconds(10)).StartWith(0L)
            .Where(_ => pool.CurrentInFlight == 1)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(5))
            .ToTask();

        // The Read is scheduled onto the adapter's pool at CALL time (IoPoolExtensions.Run is
        // eager), so with the registry's only FileSystem slot held it must queue…
        var readTask = adapter.Read("probe/alpha", Options).FirstAsync().ToTask();

        // …and must NOT complete while the slot is held. Sanctioned negative window ("did not
        // run" has no positive signal to filter for): on the pre-fix Unbounded fallback the read
        // of this tiny file completes in well under the window and this assertion goes red.
        var winner = await Task.WhenAny(readTask, Task.Delay(250));
        winner.Should().NotBeSameAs(readTask,
            "a factory-created adapter's Read must queue on the registry's cap-1 FileSystem " +
            "pool; completing while the pool's only slot is held means it ran on the ledgerless " +
            "unbounded pool — invisible to the teardown drain (issue #613's straggler source)");

        gate.SetResult();
        (await blockerDone).Should().Be(0);
        var node = await readTask.WaitAsync(TimeSpan.FromSeconds(10));
        node.Should().NotBeNull("releasing the slot must let the queued Read run to completion");
        node!.Name.Should().Be("IoPool probe");
    }

    [Fact]
    public void Factory_FailsLoudly_WhenTheProviderHasNoIoPoolRegistry()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        Action act = () => new FileSystemStorageAdapterFactory()
            .Create(new GraphStorageConfig { BasePath = _dir }, provider);

        // Loud, named failure — never a silent IoPool.Unbounded fallback that would resurrect
        // the untracked-I/O straggler source this fix closes.
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*IoPoolRegistry*AddIoPools*");
    }
}
