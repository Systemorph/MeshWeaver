using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using MeshWeaver.Testing.Xunit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Plugins#1605 at the FIXTURE level: <see cref="MonolithMeshTestBase"/>'s own teardown — the thing
/// xUnit awaits before it builds the next test's mesh — must not finish while a collectible context
/// its mesh retired is still unloading. <c>RetiredContextCollectedSignalTest</c> pins the signal and
/// the drain in isolation; these pin the WIRING, by driving real fixtures through their real
/// <c>DisposeAsync</c> (and, for a shared mesh, through the collection teardown that disposes it):
/// teardown waits until the context is collected, reports one that something live still roots and
/// returns, and fails on an unload that faulted.
///
/// <para>The verdict is read from the phase line the teardown itself writes to
/// <see cref="TestTraceLog"/> — the same line CI's artifact carries — scoped to this process and to
/// the bytes appended during the teardown under test. "The context is gone afterwards" alone would
/// not do: teardown ends with a forced-GC memory trace that can collect a simple context by itself,
/// so a teardown with no drain at all could still leave it collected.</para>
///
/// <para>🚨 What these do NOT pin, measured: the fixture releasing its disposed service provider before
/// the drain (#4046 per-test, #4048 shared). With the provider disposed cleanly, holding it roots
/// nothing this harness can build: Autofac clears the singletons it built when it is disposed (a
/// container-built holder, resolved from the root provider or from the mesh hub, was collected with
/// the release reverted), and anything REGISTERED — a provided instance, a factory's captures — is
/// rooted by the fixture's own <c>Services</c> collection whether the provider is kept or not. The
/// release stays because an in-drain gcroot of a FutuRe teardown showed the provider on the chain;
/// that chain is not reproduced here.</para>
/// </summary>
public class TeardownWaitsForCollectibleUnloadsTest(ITestOutputHelper output)
{
    private const string Source =
        "namespace Dyn1605 { public sealed class Widget { public int Value { get; set; } = 42; } }";

    [Fact]
    public async Task PerTestTeardown_ReturnsOnlyAfterTheContextItRetired_IsCollected()
    {
        await using var fixture = new Teardown1605CollectedFixture(output);
        await fixture.InitializeAsync();
        var unloads = fixture.Unloads;
        var weakContext = LoadIntoHolderAndRetire(unloads, fixture.Holder, "TeardownCollected1605");

        var aliveWhenReleased = new ReplaySubject<bool>(1);
        using var subscription = unloads.AllCollected
            .Select(_ => weakContext.IsAlive)
            .Subscribe(aliveWhenReleased);
        unloads.Pending.Should().Be(1, "the retired context is tracked until it is collected");
        var offset = TraceLength();

        await fixture.DisposeAsync();

        DisposePhases(offset, nameof(Teardown1605CollectedFixture)).Should().Contain(
            "DISPOSE_UNLOADS_COLLECTED",
            "the fixture's teardown must itself wait until the context its mesh retired has REALLY "
            + "unloaded — a teardown that returns first hands xUnit the overlap every FutuRe crash "
            + "dump shows");
        weakContext.IsAlive.Should().BeFalse(
            "xUnit builds the next test's mesh the moment DisposeAsync returns, so by then the context "
            + "must be GONE — returning while it is still unloading is the overlap every FutuRe crash "
            + "dump shows");
        var alive = await aliveWhenReleased.FirstAsync().Timeout(TimeSpan.FromSeconds(10)).Await();
        alive.Should().BeFalse("a start sequenced on the signal is released only after the collection");
        unloads.Pending.Should().Be(0, "a collected retirement stops counting as pending");
    }

    [Fact]
    public async Task PerTestTeardown_ReportsARetainedContext_AndReturnsInsteadOfWaitingOnIt()
    {
        await using var fixture = new Teardown1605RetainedFixture(output);
        await fixture.InitializeAsync();
        var unloads = fixture.Unloads;
        // Rooted by THIS test, not by the fixture: something live the teardown cannot release.
        var root = new CollectibleInstanceHolder();
        var weakContext = LoadIntoHolderAndRetire(unloads, root, "TeardownRetained1605");
        CollectibleUnloadOutcome released;
        try
        {
            var offset = TraceLength();

            await fixture.DisposeAsync();

            DisposePhases(offset, nameof(Teardown1605RetainedFixture)).Should().Contain(
                "DISPOSE_ALC_RETAINED",
                "a context something live still roots can never unload, so teardown must REPORT it and "
                + "return — waiting on it would hang the suite, and calling it collected would hide it");
            weakContext.IsAlive.Should().BeTrue("the test itself still roots it — that is what makes it retained");
            unloads.PendingContextNames.Should().Contain(
                name => name.EndsWith("TeardownRetained1605", StringComparison.Ordinal),
                "a retained context stays tracked, so the next drain still sees it");
        }
        finally
        {
            // Released and drained on EVERY path, so a failing assertion above never leaves this
            // synthetic context loaded for the rest of the host; that failure still propagates.
            root.Instance = null;
            released = await CollectibleUnloadDrain.WaitUntilCollectedAsync(unloads);
        }

        // Released, the same drain collects it — and this test leaves nothing loaded behind.
        released.Collected.Should().BeTrue($"with its last root gone the context must unload ({released})");
        weakContext.IsAlive.Should().BeFalse("and it must actually be gone");
    }

    [Fact]
    public async Task PerTestTeardown_FailsWhenAnUnloadFaulted_InsteadOfHangingOnIt()
    {
        await using var fixture = new Teardown1605FaultedFixture(output);
        await fixture.InitializeAsync();
        LoadHookAndRetire(fixture.Unloads, "TeardownFault1605");
        var offset = TraceLength();

        var teardown = async () => await fixture.DisposeAsync();

        (await teardown.Should().ThrowAsync<InvalidOperationException>(
                "an unload that faulted will never complete, so teardown must FAIL the class naming it "
                + "— a teardown that waits on it hangs, and one that swallows it hides a context that "
                + "stays loaded"))
            .WithMessage("*unload was abandoned*")
            .And.Which.ToString().Should().Contain("unloading handler fault 1605",
                "the failure must carry the fault that abandoned the unload");
        DisposePhases(offset, nameof(Teardown1605FaultedFixture)).Should().Contain("DISPOSE_UNLOAD_FAULTED");
    }

    [Fact]
    public async Task SharedMeshTeardown_ReturnsOnlyAfterTheContextItsMeshRetired_IsCollected()
    {
        // The scope the runner opens around a collection, opened here so this test owns the moment
        // the collection's shared mesh is torn down. It is Current for this method only.
        // `await using` disposes it on every failure path; the explicit disposal below is the one
        // under test, and TestCollectionScope.DisposeAsync is a no-op the second time.
        await using var scope = TestCollectionScope.Begin("Plugins#1605 shared-mesh teardown");
        var weakContext = await RunOneSharedCaseAsync(output);
        var offset = TraceLength();

        await scope.DisposeAsync();

        DisposePhases(offset, nameof(Teardown1605SharedFixture)).Should().Contain(
            "DISPOSE_SHARED_UNLOADS_COLLECTED",
            "the collection's teardown must wait until the contexts its shared mesh retired have REALLY "
            + "unloaded — the next collection's mesh is built the moment it returns");
        weakContext.IsAlive.Should().BeFalse("the collection's teardown returns only after it is gone");
    }

    /// <summary>
    /// One case of a shared-mesh class: build (or join) the collection's mesh, retire a context into
    /// it, run the per-test teardown — which leaves the shared mesh up. Out of line, so no local of the
    /// caller's frame keeps the fixture, and with it the provider, alive.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> RunOneSharedCaseAsync(ITestOutputHelper output)
    {
        var fixture = new Teardown1605SharedFixture(output);
        WeakReference weakContext;
        try
        {
            await fixture.InitializeAsync();
            weakContext = LoadIntoHolderAndRetire(fixture.Unloads, fixture.Holder, "TeardownShared1605");
        }
        finally
        {
            // On every path, exactly once: the per-test half of a shared class's teardown.
            await fixture.DisposeAsync();
        }
        // xUnit drops a test instance once its teardown returns; so does this.
        fixture = null!;
        return weakContext;
    }

    private static long TraceLength() =>
        File.Exists(TestTraceLog.Path) ? new FileInfo(TestTraceLog.Path).Length : 0;

    /// <summary>
    /// The phases <paramref name="fixtureName"/> wrote in THIS process after <paramref name="offset"/>.
    /// The file is shared by every test host and never truncated locally, so both scopes matter: a
    /// stale line from an earlier run under a recycled pid must not satisfy the assertion.
    /// </summary>
    private static IReadOnlyList<string> DisposePhases(long offset, string fixtureName)
    {
        using var stream = new FileStream(
            TestTraceLog.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        var marker = $"pid={Environment.ProcessId} [{fixtureName}] ";
        return reader.ReadToEnd()
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Contains(marker, StringComparison.Ordinal))
            .Select(line => line[(line.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Split(' ')[0])
            .ToArray();
    }

    // Out-of-line and non-inlined so no local keeps the assembly, type or instance rooted on the
    // caller's frame: the holder is the ONLY root of the context once this returns.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadIntoHolderAndRetire(
        CollectibleContextUnloads unloads, CollectibleInstanceHolder holder, string nodeName)
    {
        using var cache = NewCache(unloads);
        var assembly = cache.LoadAssemblyFromBytes(nodeName, Compile(), pdbBytes: null);
        holder.Instance = Activator.CreateInstance(assembly.GetType("Dyn1605.Widget")!);
        var weak = new WeakReference(cache.GetOrCreateLoadContext(nodeName));
        cache.UnloadContext(nodeName);
        return weak;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LoadHookAndRetire(CollectibleContextUnloads unloads, string nodeName)
    {
        using var cache = NewCache(unloads);
        cache.LoadAssemblyFromBytes(nodeName, Compile(), pdbBytes: null);
        cache.GetOrCreateLoadContext(nodeName).Unloading +=
            _ => throw new InvalidOperationException("unloading handler fault 1605");
        cache.UnloadContext(nodeName);
    }

    private static CompilationCacheService NewCache(CollectibleContextUnloads unloads) =>
        new(Options.Create(new CompilationCacheOptions { EnableDiskCache = false }),
            NullLogger<CompilationCacheService>.Instance, unloads);

    private static byte[] Compile()
    {
        var compilation = CSharpCompilation.Create(
            $"Dyn1605_{Guid.NewGuid():N}",
            [CSharpSyntaxTree.ParseText(Source)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
             MetadataReference.CreateFromFile(Path.Combine(
                 Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll"))],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        result.Success.Should().BeTrue(string.Join("; ", result.Diagnostics));
        return ms.ToArray();
    }

    /// <summary>A singleton of the fixture's mesh that holds one instance of a collectible type.</summary>
    private sealed class CollectibleInstanceHolder
    {
        public object? Instance { get; set; }
    }

    /// <summary>A real fixture, driven by hand so its teardown is the subject rather than the harness.</summary>
    private abstract class TeardownFixture(ITestOutputHelper output) : MonolithMeshTestBase(output)
    {
        // Built by the container, never PROVIDED: a provided instance also sits in the fixture's own
        // service collection, which the fixture keeps for its whole lifetime, so it would be rooted
        // whatever the teardown did with the provider.
        protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
            base.ConfigureMesh(builder)
                .ConfigureServices(services => services.AddSingleton(_ => new CollectibleInstanceHolder()));

        public CollectibleContextUnloads Unloads =>
            Mesh.ServiceProvider.GetRequiredService<CollectibleContextUnloads>();

        // Resolved from the fixture's ROOT provider, so the collectible instance's only root is the
        // mesh's own container, which the teardown disposes.
        public CollectibleInstanceHolder Holder =>
            ServiceProvider.GetRequiredService<CollectibleInstanceHolder>();

        private int disposed;

        // Exactly once. Each test calls it explicitly (that call is the subject) and `await using`
        // calls it again on every path, so a setup step or assertion that fails first never leaves a
        // live mesh — or its provider and contexts — behind in the host.
        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            await base.DisposeAsync();
        }
    }

    // One type per case: the teardown traces under the fixture's type name, so each case reads only
    // its own lines.
    private sealed class Teardown1605CollectedFixture(ITestOutputHelper output) : TeardownFixture(output);

    private sealed class Teardown1605RetainedFixture(ITestOutputHelper output) : TeardownFixture(output);

    private sealed class Teardown1605FaultedFixture(ITestOutputHelper output) : TeardownFixture(output);

    private sealed class Teardown1605SharedFixture(ITestOutputHelper output) : TeardownFixture(output)
    {
        protected override bool ShareMeshAcrossTests => true;
    }
}
