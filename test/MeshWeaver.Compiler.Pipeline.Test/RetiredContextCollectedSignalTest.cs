using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Plugins#1605 — the next instance starts only when the previous one's collectible contexts have
/// REALLY unloaded. <c>Unload()</c> only requests an unload; every readable FutuRe crash dump caught
/// 3–7 contexts still <c>Unloading</c> while the next mesh was being built or run. These pin the
/// signal the next start is sequenced on: it fires strictly after the context has been collected,
/// a faulted unload releases the waiter with the fault instead of hanging it, and a start with
/// nothing retired is not delayed.
/// </summary>
public class RetiredContextCollectedSignalTest
{
    private const string Source =
        "namespace Dyn1605 { public sealed class Widget { public int Value { get; set; } = 42; } }";

    [Fact]
    public async Task NextStart_RunsOnlyAfterTheRetiredContextIsCollected_AndTheContextIsCollected()
    {
        var unloads = new CollectibleContextUnloads();
        using var cache = NewCache(unloads);

        var weakContext = LoadUseAndRetire(cache, "Signal1605");

        // A start sequenced on the signal records whether the retired context still existed at the
        // moment it was released. (Independent subscribers are not ordered against each other —
        // only against the collection — so this waits for ITS release, not for anyone else's.)
        var aliveWhenReleased = new ReplaySubject<bool>(1);
        using var subscription = unloads.AllCollected
            .Select(_ => weakContext.IsAlive)
            .Subscribe(aliveWhenReleased);

        var releasedEarly = false;
        using (aliveWhenReleased.Subscribe(_ => releasedEarly = true)) { }
        releasedEarly.Should().BeFalse(
            "Unload() has only been REQUESTED — the runtime still holds the context and its "
            + "LoaderAllocator — so a start sequenced on the signal must not have run yet; running it "
            + "here is the overlap every FutuRe crash dump shows");
        unloads.Pending.Should().Be(1, "the retired context is tracked until it is collected");

        var outcome = await CollectibleUnloadDrain.WaitUntilCollectedAsync(unloads);

        // The line after the driver IS the next instance's start in a test base.
        outcome.Collected.Should().BeTrue($"nothing roots the context, so it must be collected ({outcome})");
        weakContext.IsAlive.Should().BeFalse(
            "the next start runs after the driver returns, and by then the context must be GONE — the "
            + "fix must not simply keep contexts loaded, which is the #4017 retention failure");
        var alive = await aliveWhenReleased.FirstAsync().Timeout(TimeSpan.FromSeconds(10)).Await();
        alive.Should().BeFalse(
            "a start sequenced on the signal must be released only AFTER the context is collected — a "
            + "signal that fires while the context is alive is the unload-in-progress overlap itself");
        unloads.Pending.Should().Be(0, "a collected retirement stops counting as pending");
    }

    [Fact]
    public async Task AFaultedUnload_ReleasesTheWaiterWithTheFault_NeverHangsIt()
    {
        var unloads = new CollectibleContextUnloads();
        using var cache = NewCache(unloads);

        // Unload() throws out of the context's Unloading handler. CompleteUnload handles it where it
        // happens — logged at Error, the context KEPT, nothing thrown at the caller (a rethrow would
        // escape into whichever scan released the last pin). What this pins is that the one party
        // WAITING for the unload is told, instead of waiting forever.
        LoadHookAndRetire(cache, "Fault1605");

        // Awaited with a budget only so a regression FAILS instead of hanging the suite; the
        // expectation is an immediate fault, and a TimeoutException is the regression.
        var wait = async () => await unloads.AllCollected.Timeout(TimeSpan.FromSeconds(10)).Await();
        (await wait.Should().ThrowAsync<InvalidOperationException>(
                "an abandoned unload must ERROR the signal so the next start is released with the "
                + "fault — a waiter left hanging on a context that will never unload is the failure"))
            .WithMessage("*unloading handler fault 1605*");

        var outcome = await CollectibleUnloadDrain.WaitUntilCollectedAsync(unloads);
        outcome.Fault.Should().NotBeNull($"teardown must report the fault, not wait on it ({outcome})");
    }

    [Fact]
    public void WithNothingRetired_TheNextStartIsNotDelayed()
    {
        var unloads = new CollectibleContextUnloads();

        var started = false;
        using var subscription = unloads.AllCollected.Subscribe(_ => started = true);

        started.Should().BeTrue(
            "with no retired context the signal must emit synchronously on subscribe — an ordinary "
            + "start must not wait for a collection that has nothing to collect");
    }

    [Fact]
    public async Task ACollectedRetirement_LeavesNothingBehind_SoTheNextWaitIsImmediate()
    {
        var unloads = new CollectibleContextUnloads();
        using var cache = NewCache(unloads);
        _ = LoadUseAndRetire(cache, "Cleared1605");
        (await CollectibleUnloadDrain.WaitUntilCollectedAsync(unloads)).Collected.Should().BeTrue();

        unloads.Pending.Should().Be(0,
            "a collected retirement must remove itself — a lingering entry would make every later "
            + "start wait on a generation that is long gone");
        var started = false;
        using var subscription = unloads.AllCollected.Subscribe(_ => started = true);
        started.Should().BeTrue("the next start after a completed unload is not delayed");
    }

    [Fact]
    public async Task ATypeSerializedThroughSystemTextJson_IsCollectedAtTeardown_NotWhenStjsTimerFires()
    {
        // The measured holder (gcroot of a FutuRe teardown, Plugins#1605): STJ's static member-accessor
        // cache keeps a DynamicMethod whose scope holds the collectible type, for ~1 s after last use.
        JsonMemberAccessorCacheEviction.IsAvailable.Should().BeTrue(
            "System.Text.Json must still publish its MetadataUpdateHandler.ClearCache hook — without it "
            + "the eviction silently stops and every serialised NodeType outlives its teardown again");

        var unloads = new CollectibleContextUnloads();
        using var cache = NewCache(unloads);
        var weakContext = LoadSerializeAndRetire(cache, "Json1605");

        var outcome = await CollectibleUnloadDrain.WaitUntilCollectedAsync(unloads);

        outcome.Collected.Should().BeTrue(
            "a context whose types went through System.Text.Json must be collectable the moment its "
            + $"unload is requested — not when STJ's 1 s eviction timer happens to fire ({outcome})");
        weakContext.IsAlive.Should().BeFalse("and it must actually be gone before the next start");
    }

    private static CompilationCacheService NewCache(CollectibleContextUnloads unloads) =>
        new(Options.Create(new CompilationCacheOptions { EnableDiskCache = false }),
            NullLogger<CompilationCacheService>.Instance, unloads);

    // Out-of-line and non-inlined so no local keeps the assembly, type or instance rooted on the
    // caller's frame — otherwise the context could never be collected, fix or no fix.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadUseAndRetire(CompilationCacheService cache, string nodeName)
    {
        var assembly = cache.LoadAssemblyFromBytes(nodeName, Compile(), pdbBytes: null);
        var widget = Activator.CreateInstance(assembly.GetType("Dyn1605.Widget")!);
        widget!.GetType().GetProperty("Value")!.GetValue(widget).Should().Be(42);
        var weak = new WeakReference(cache.GetOrCreateLoadContext(nodeName));
        cache.UnloadContext(nodeName);
        return weak;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadSerializeAndRetire(CompilationCacheService cache, string nodeName)
    {
        var assembly = cache.LoadAssemblyFromBytes(nodeName, Compile(), pdbBytes: null);
        var type = assembly.GetType("Dyn1605.Widget")!;
        var widget = Activator.CreateInstance(type)!;
        var json = System.Text.Json.JsonSerializer.Serialize(widget, type, new System.Text.Json.JsonSerializerOptions());
        json.Should().Contain("42");
        System.Text.Json.JsonSerializer.Deserialize(json, type, new System.Text.Json.JsonSerializerOptions())
            .Should().NotBeNull();
        var weak = new WeakReference(cache.GetOrCreateLoadContext(nodeName));
        cache.UnloadContext(nodeName);
        return weak;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LoadHookAndRetire(CompilationCacheService cache, string nodeName)
    {
        cache.LoadAssemblyFromBytes(nodeName, Compile(), pdbBytes: null);
        cache.GetOrCreateLoadContext(nodeName).Unloading +=
            _ => throw new InvalidOperationException("unloading handler fault 1605");
        cache.UnloadContext(nodeName);
    }

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
}
