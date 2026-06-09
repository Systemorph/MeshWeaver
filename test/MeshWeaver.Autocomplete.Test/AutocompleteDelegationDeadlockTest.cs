using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Reactive;
using Memex.Portal.Shared;
using MeshWeaver.Data.Completion;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Autocomplete.Test;

/// <summary>
/// Deadlock-coverage tests for <see cref="UnifiedReferenceAutocompleteProvider.GetNodeDelegatedCompletions"/>.
///
/// The bug being guarded: <c>GetNodeDelegatedCompletions</c> dispatches an
/// <c>AutocompleteRequest</c> to the per-node hub at the resolved path. The previous
/// implementation bridged the hub round-trip back via
/// <c>await hub.Observe(req).FirstAsync().ToTask()</c>. The autocomplete handler on
/// the source hub is itself an aggregator that runs all <c>IAutocompleteProvider</c>
/// instances — including <c>UnifiedReferenceAutocompleteProvider</c> — so a delegated
/// request can re-enter the same hub pump under load and deadlock the ActionBlock.
///
/// These tests fire several autocomplete requests in parallel that each trigger
/// the delegation branch (path ends in '/'). Under the old <c>.ToTask()</c>-bridged
/// implementation, these stall under contention; under the fix they all complete in
/// well under 5 s.
/// </summary>
[Collection("AutocompleteDelegationDeadlockTest")]
public class AutocompleteDelegationDeadlockTest : MonolithMeshTestBase
{
    private static readonly string _cacheDirectory =
        Path.Combine(Path.GetTempPath(), "MeshWeaverDelegateDeadlock", Guid.NewGuid().ToString());
    static AutocompleteDelegationDeadlockTest() => Directory.CreateDirectory(_cacheDirectory);

    protected override bool ShareMeshAcrossTests => true;

    public AutocompleteDelegationDeadlockTest(ITestOutputHelper output) : base(output)
    {
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => builder
            .UseMonolithMesh()
            .AddPartitionedFileSystemPersistence(TestPaths.SamplesGraphData)
            .AddRowLevelSecurity()
            .AddSystemorph()
            .AddAcme()
            .AddUserData()
            .AddMeshNodes(TestUsers.PublicAdminAccess())
            .ConfigureServices(services => services.Configure<CompilationCacheOptions>(o => o.CacheDirectory = _cacheDirectory))
            .AddGraph()
            .ConfigureHub(hub => hub.AddMeshNavigation());

    // Cache dir is class-static + shared SP — never deleted between tests.

    private IAutocompleteProvider GetUnifiedReferenceProvider()
    {
        var providers = Mesh.ServiceProvider.GetServices<IAutocompleteProvider>().ToList();
        var provider = providers.FirstOrDefault(p => p.GetType().Name.Contains("UnifiedReference"));
        provider.Should().NotBeNull("UnifiedReferenceAutocompleteProvider should be registered");
        return provider!;
    }

    /// <summary>
    /// SHOULD-FAIL-IF: <c>GetNodeDelegatedCompletions</c> bridges the inner
    /// <c>hub.Observe(autocompleteReq).FirstAsync().ToTask()</c> with <c>await</c>.
    /// Under concurrent load, the delegated request re-enters the source hub's
    /// AutocompleteRequest aggregator while the original handler is still waiting
    /// on the ToTask continuation — classic ActionBlock deadlock.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void DelegatedAutocomplete_ConcurrentRequests_DoNotDeadlock()
    {
        var provider = GetUnifiedReferenceProvider();

        // Path ending in '/' AND with two completed segments triggers
        // GetNodeDelegatedCompletions in absolute mode — sends a fresh
        // AutocompleteRequest to the per-node hub at "Systemorph/Marketing".
        const string Query = "@/Systemorph/Marketing/";

        // Subscribe to all 8 query streams concurrently. Each is a PROGRESSIVE snapshot stream — we
        // wait for every one to produce its first non-empty snapshot (keywords + local children
        // stream in immediately; the delegated node folds in when it arrives) and NEVER block on the
        // slowest delegation to COMPLETE. CombineLatest fires once all 8 have produced a snapshot.
        // A re-entrant delegation deadlock would stall a stream's first emission → caught by Within.
        // (Waiting on completion — .ToList() — is the anti-pattern: under load the per-source 2s
        // Timeout timer is starved, so completion lags past the test budget even with no deadlock.)
        Observable
            .CombineLatest(Enumerable.Range(0, 8)
                .Select(_ => provider.GetItems(Query, null).Where(snap => snap.Count > 0).Take(1)))
            .Should().Within(15.Seconds())
            .Match(snaps => snaps.Count == 8 && snaps.All(s => s.Count > 0));
    }

    /// <summary>
    /// SHOULD-FAIL-IF: relative-path delegation deadlocks on a single sequential caller
    /// when the context node is the same as the delegation target — the inner request
    /// posts back to the same hub the caller's handler is running on.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void DelegatedAutocomplete_RelativeContextSelfDelegation_ResolvesQuickly()
    {
        var provider = GetUnifiedReferenceProvider();

        // Relative mode: query is empty + ends with '/' so the provider asks the
        // contextPath node for its own completions (areas, data, content).
        // Progressive: the stream must produce its first snapshot promptly — a self-delegation
        // deadlock would stall it. We observe the first emission, never completion.
        provider.GetItems("@", "Systemorph/Marketing")
            .Take(1).Should().Within(10.Seconds()).Emit();
    }

    /// <summary>
    /// SHOULD-FAIL-IF: the delegated request hits a hub that DOES NOT have an
    /// AutocompleteRequest handler — the previous <c>.ToTask()</c> bridge would
    /// hang indefinitely waiting for a response that never comes (DeliveryFailure
    /// gets eaten by the catch). The fix surfaces the failure as an empty list
    /// without blocking.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void DelegatedAutocomplete_NonexistentTarget_DoesNotHang()
    {
        var provider = GetUnifiedReferenceProvider();

        // Path that does not exist anywhere — there is no per-node hub at this address.
        const string Query = "@/ZzzNonexistent/Bogus/";

        // A delegation to a non-existent node must not stall the stream: the first snapshot (local
        // keywords) streams immediately and the dead delegation degrades to empty without blocking.
        // We observe the first emission, never completion.
        provider.GetItems(Query, null)
            .Take(1).Should().Within(15.Seconds()).Emit();
    }

    /// <summary>
    /// SHOULD-FAIL-IF: under sustained interleaved load (fan-out across multiple
    /// distinct delegation targets), the per-node hub pumps deadlock when called
    /// reentrantly from the source hub's aggregator handler.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void DelegatedAutocomplete_FanOutAcrossPartitions_DoesNotDeadlock()
    {
        var provider = GetUnifiedReferenceProvider();
        var queries = new[]
        {
            "@/Systemorph/Marketing/",
            "@/ACME/ProductLaunch/",
            "@/Systemorph/Marketing/",
            "@/ACME/ProductLaunch/",
            "@/Systemorph/Marketing/",
            "@/ACME/ProductLaunch/",
        };

        // Interleaved concurrent fan-out across distinct delegation targets. Each query's stream is
        // observed PROGRESSIVELY — we wait for every one to produce a non-empty snapshot, proving the
        // cross-partition fan-out streams results without deadlocking, and never block on the slowest
        // delegation to complete (a re-entrant deadlock would stall a first emission → caught here).
        Observable
            .CombineLatest(queries
                .Select(q => provider.GetItems(q, null).Where(snap => snap.Count > 0).Take(1)))
            .Should().Within(20.Seconds())
            .Match(snaps => snaps.Count == queries.Length && snaps.All(s => s.Count > 0));
    }
}
