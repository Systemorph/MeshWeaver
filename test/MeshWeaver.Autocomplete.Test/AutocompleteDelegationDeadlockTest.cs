using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Memex.Portal.Shared;
using MeshWeaver.Data.Completion;
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
    private readonly string _cacheDirectory;

    public AutocompleteDelegationDeadlockTest(ITestOutputHelper output) : base(output)
    {
        _cacheDirectory = Path.Combine(Path.GetTempPath(), "MeshWeaverDelegateDeadlock", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_cacheDirectory);
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

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_cacheDirectory))
        {
            try { Directory.Delete(_cacheDirectory, recursive: true); }
            catch { /* best effort */ }
        }
    }

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
    public async Task DelegatedAutocomplete_ConcurrentRequests_DoNotDeadlock()
    {
        var provider = GetUnifiedReferenceProvider();

        // Path ending in '/' AND with two completed segments triggers
        // GetNodeDelegatedCompletions in absolute mode — sends a fresh
        // AutocompleteRequest to the per-node hub at "Systemorph/Marketing".
        const string Query = "@/Systemorph/Marketing/";

        async Task RunOne(int idx)
        {
            var items = await provider.GetItemsAsync(Query, null,
                    TestContext.Current.CancellationToken)
                .ToArrayAsync(TestContext.Current.CancellationToken);
            // No content assertion — the existence test is "did the call return".
            items.Should().NotBeNull($"call {idx} should have returned a result");
        }

        var all = Task.WhenAll(Enumerable.Range(0, 8).Select(RunOne));
        await all.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// SHOULD-FAIL-IF: relative-path delegation deadlocks on a single sequential caller
    /// when the context node is the same as the delegation target — the inner request
    /// posts back to the same hub the caller's handler is running on.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task DelegatedAutocomplete_RelativeContextSelfDelegation_ResolvesQuickly()
    {
        var provider = GetUnifiedReferenceProvider();

        // Relative mode: query is empty + ends with '/' so the provider asks the
        // contextPath node for its own completions (areas, data, content).
        var items = await provider.GetItemsAsync("@", "Systemorph/Marketing",
                TestContext.Current.CancellationToken)
            .ToArrayAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        items.Should().NotBeNull();
    }

    /// <summary>
    /// SHOULD-FAIL-IF: the delegated request hits a hub that DOES NOT have an
    /// AutocompleteRequest handler — the previous <c>.ToTask()</c> bridge would
    /// hang indefinitely waiting for a response that never comes (DeliveryFailure
    /// gets eaten by the catch). The fix surfaces the failure as an empty list
    /// without blocking.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task DelegatedAutocomplete_NonexistentTarget_DoesNotHang()
    {
        var provider = GetUnifiedReferenceProvider();

        // Path that does not exist anywhere — there is no per-node hub at this address.
        const string Query = "@/ZzzNonexistent/Bogus/";

        var items = await provider.GetItemsAsync(Query, null,
                TestContext.Current.CancellationToken)
            .ToArrayAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        // Whether the result is empty or not is irrelevant — the contract is "must return".
        items.Should().NotBeNull();
    }

    /// <summary>
    /// SHOULD-FAIL-IF: under sustained interleaved load (fan-out across multiple
    /// distinct delegation targets), the per-node hub pumps deadlock when called
    /// reentrantly from the source hub's aggregator handler.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task DelegatedAutocomplete_FanOutAcrossPartitions_DoesNotDeadlock()
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

        var tasks = queries.Select(async q =>
        {
            var items = await provider.GetItemsAsync(q, null,
                    TestContext.Current.CancellationToken)
                .ToArrayAsync(TestContext.Current.CancellationToken);
            items.Should().NotBeNull();
        });

        await Task.WhenAll(tasks)
            .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
    }
}
