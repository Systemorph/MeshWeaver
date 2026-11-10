using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// MeshWeaver#8082 — <b>a late quiescence still unloads</b> (policy <c>retire-late-quiescence-unloads</c>).
/// A live swap retires the old generation of a module with a budget (<c>ModuleLiveUpdater.RetireBudget</c>,
/// 30 s). On memex a Gmail inbox read still held the generation's query-provider lease when the budget ran
/// out; the retire gave up for good, so when the read ended a few seconds later nothing unloaded the
/// generation, and it stayed loaded until the pod restarted.
///
/// <para>Now the budget only REPORTS (a Warning naming the holders) and the wait for the lease's release
/// goes on, owned by the registry. These pin both halves with real Roslyn emits and real load contexts:</para>
/// <list type="bullet">
/// <item>A lease released AFTER the budget still unloads the generation, and it is really collected. On
/// the base the retirement was faulted at the budget and the context was never unloaded.</item>
/// <item>The control: a lease that is NEVER released keeps the generation loaded (it is never unloaded
/// under a lease), is reported at the budget, and is reported KEPT loaded at teardown — the retirement
/// faults with its holder named.</item>
/// </list>
/// <para>The budget is short here because what is under test is what happens AFTER it, whatever its length.</para>
/// </summary>
public sealed class ALateQuiescenceStillUnloadsTest : IDisposable
{
    private const string Lib = "MeshWeaver.Test.LateQuiescence";
    private const string Holder = "a query leg still reading";
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private readonly string root = Path.Combine(Path.GetTempPath(), "late-quiescence-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* a mapped DLL on a context still collecting — the OS reclaims it */ }
        catch (UnauthorizedAccessException) { }
    }

    [Fact(Timeout = 120_000)]
    public async Task ALeaseReleasedAfterTheBudget_StillUnloadsTheRetiredGeneration()
    {
        var ct = TestContext.Current.CancellationToken;
        var log = new RecordingLogger();
        var unloads = new CollectibleContextUnloads();
        using var contexts = new ModuleContexts().Attach(unloads, log);
        var unloading = new AsyncSubject<Unit>();

        var (lease, retire) = InstallSwapAndRetireUnderLease(contexts, unloading);
        try
        {
            (await retire.Timeout(Wait).Await(ct)).Should().BeFalse(
                "the lease is still held when the budget runs out, so the unload has not started yet");
            log.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains(Holder),
                "the budget reports the generation still in use, naming its holder");
            unloads.Faults.Should().BeEmpty("a generation still in use at the budget is not given up");
        }
        finally
        {
            lease.Dispose(); // the query leg ends — after the budget
        }

        await unloading.Should().Within(Wait).Emit(
            "the release of the last lease drives the unload, however late it comes", cancellationToken: ct);
        var outcome = await CollectibleUnloadDrain.WaitUntilCollectedAsync(unloads);
        outcome.Collected.Should().BeTrue($"the late-unloaded generation is really collected ({outcome})");
        log.Entries.Should().Contain(e => e.Level == LogLevel.Information && e.Message.Contains("unloaded LATE"));
    }

    [Fact(Timeout = 120_000)]
    public async Task ALeaseNeverReleased_KeepsTheGenerationLoaded_AndTeardownReportsItKept()
    {
        var ct = TestContext.Current.CancellationToken;
        var log = new RecordingLogger();
        var unloads = new CollectibleContextUnloads();
        var contexts = new ModuleContexts().Attach(unloads, log);
        var unloading = new AsyncSubject<Unit>();
        var unloaded = false;
        unloading.Subscribe(_ => unloaded = true);

        var (lease, retire) = InstallSwapAndRetireUnderLease(contexts, unloading);
        try
        {
            (await retire.Timeout(Wait).Await(ct)).Should().BeFalse();
            log.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains(Holder));
            unloads.PendingContextNames.Should().ContainSingle(name => name.StartsWith(ModuleLoadContext.NamePrefix + Lib + "#"),
                "the retirement stays pending — the retention is named while the lease is held");

            contexts.Dispose(); // mesh teardown, with the lease still held

            unloaded.Should().BeFalse("a generation is never unloaded while a lease on it is held");
            unloads.Faults.Should().ContainSingle(
                f => f.Message.Contains("kept loaded at teardown") && f.Message.Contains(Holder),
                "the lease that was never released is reported KEPT loaded, with its holder");
            log.Entries.Should().Contain(e => e.Level == LogLevel.Error
                                              && e.Message.Contains("NOT unloading module generation")
                                              && e.Message.Contains(Holder));
        }
        finally
        {
            lease.Dispose();
        }
    }

    // ───────────────────────────────────────────── helpers

    // Not inlined, so no frame of the test roots the retired generation's context or assembly.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private (IDisposable Lease, IObservable<bool> Retire) InstallSwapAndRetireUnderLease(
        ModuleContexts contexts, AsyncSubject<Unit> unloading)
    {
        var first = contexts.Load(Write("g1", version: 1));
        contexts.Commit(first);
        var second = contexts.Load(Write("g2", version: 2));
        contexts.Commit(second).Should().BeSameAs(first);
        first.Context.Unloading += _ =>
        {
            unloading.OnNext(Unit.Default);
            unloading.OnCompleted();
        };
        var lease = contexts.Leases.Enter(first.Context, Holder);
        var retire = contexts.Retire(first, Budget).Replay(1);
        retire.Connect();
        return (lease, retire);
    }

    private string Write(string generation, int version)
    {
        var source = $$"""
            [assembly: MeshWeaver.Test.LateQuiescence.Module]
            namespace MeshWeaver.Test.LateQuiescence;
            public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute { }
            public static class Api { public static int Version => {{version}}; }
            """;
        var compilation = CSharpCompilation.Create(Lib, [CSharpSyntaxTree.ParseText(source)], PlatformReferences.Platform(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        result.Success.Should().BeTrue(string.Join(Environment.NewLine,
            result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var directory = Path.Combine(root, "modules", $"{Lib}@{generation}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Lib + ".dll");
        File.WriteAllBytes(path, buffer.ToArray());
        return path;
    }

    /// <summary>Records what the registry logs, formatted — an instance per test.</summary>
    private sealed class RecordingLogger : ILogger
    {
        private ImmutableList<(LogLevel Level, string Message)> entries = ImmutableList<(LogLevel, string)>.Empty;

        public ImmutableList<(LogLevel Level, string Message)> Entries => Volatile.Read(ref entries);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            ImmutableInterlocked.Update(ref entries, list => list.Add((logLevel, formatter(state, exception))));
    }
}
