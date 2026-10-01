#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 The Plan form's instance lookup is bounded by the QUERY's own budget, never by a shorter timer
/// of its own (#5894, the inverted ladder of #1198).
///
/// <para><c>InstancePlanService.FindInstancePath</c> carried a 10 s <c>Timeout</c>, written when the
/// fan-in's Initial had no bound at all. The fan-in now faults at
/// <see cref="MeshOperationOptions.QueryInitialBudget"/> (15 s on the default ladder), so the local
/// 10 s was a second, shorter bound on the same wait: a provider answering at 12 s — inside the
/// query's budget — got "TimeoutException" from the form. The two facts pinned here are the two
/// halves of "one bound, the query's": an answer inside the budget is FOUND, and a provider that
/// never answers fails with the QUERY's terminal (<see cref="QueryProviderStalledException"/>, which
/// names the provider), not with an anonymous local timeout.</para>
///
/// <para>The ladder is set explicitly so the margin is the subject, not a coincidence of the test base.</para>
/// </summary>
public class InstanceLookupHasNoBoundOfItsOwnTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string SlowInstance = "slow-instance";
    private const string StalledInstance = "stalled-instance";
    private const string Owner = "slow-owner";

    /// <summary>The production default ladder (its fan-in budget is 15 s today), configured explicitly
    /// so a change of the test base's ladder cannot move the margin under test.</summary>
    private static readonly MeshOperationOptions Ladder = new();

    /// <summary>When the slow provider answers: well past the old local 10 s, still inside the budget.</summary>
    private static TimeSpan SlowAnswer => Ladder.QueryInitialBudget - TimeSpan.FromSeconds(3);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder)
        .AddPluginCatalog()
        .ConfigureServices(services => services.AddSingleton<IMeshQueryProvider>(new SlowInstanceProvider()))
        .ConfigureHub(c => c.WithMeshOperationTimeout(Ladder.Timeout));

    [Fact(Timeout = 300_000)]
    public async Task AnAnswerInsideTheQuerysBudget_IsFound_EvenPastTheOldLocalTenSeconds()
    {
        SlowAnswer.Should().BeGreaterThan(TimeSpan.FromSeconds(10),
            "the margin under test: the answer must land after the bound the form used to carry");

        var path = await Mesh.ServiceProvider.GetRequiredService<InstancePlanService>()
            .FindInstancePath(SlowInstance)
            .Timeout(TestTimeouts.Convergence + Ladder.QueryInitialBudget)
            .Await(TestContext.Current.CancellationToken);

        path.Should().Be($"{Owner}/{MeshWeaverInstanceNodeType.NodeType}/{SlowInstance}",
            "a provider answering inside the query's own budget must not be cut off by a shorter local timer");
    }

    [Fact(Timeout = 300_000)]
    public async Task AProviderThatNeverAnswers_FailsWithTheQuerysOwnTerminal()
    {
        Exception? fault = null;
        try
        {
            await Mesh.ServiceProvider.GetRequiredService<InstancePlanService>()
                .FindInstancePath(StalledInstance)
                .Timeout(TestTimeouts.Convergence + Ladder.QueryInitialBudget)
                .Await(TestContext.Current.CancellationToken);
        }
        catch (Exception ex)
        {
            fault = ex;
        }

        fault.Should().BeOfType<QueryProviderStalledException>(
            "the lookup's only bound is the fan-in's, which names the stalled provider");
    }

    /// <summary>
    /// Answers the lookup of <see cref="SlowInstance"/> after <see cref="SlowAnswer"/>, never answers
    /// the lookup of <see cref="StalledInstance"/>, and answers every other query at once and empty.
    /// A provider on the <see cref="IMeshQueryProvider"/> seam — the same one
    /// <c>InstanceLookupWaitsForEveryProviderTest</c> drives — not a mock of a core service.
    /// </summary>
    private sealed class SlowInstanceProvider : IMeshQueryProvider
    {
        public string Name => nameof(SlowInstanceProvider);

        public bool Matches(IReadOnlyList<string> queryNamespaces) => true;

        public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options)
        {
            bool Asks(string id) => typeof(T) == typeof(MeshNode) && request.EffectiveQueries.Any(q =>
                q.Contains($"nodeType:{MeshWeaverInstanceNodeType.NodeType}", StringComparison.Ordinal)
                && q.Contains($"id:{id}", StringComparison.Ordinal));

            if (Asks(StalledInstance))
                return Observable.Never<QueryResultChange<T>>();
            if (!Asks(SlowInstance))
                return Observable.Return(Initial(Array.Empty<T>()));
            var record = new MeshNode(SlowInstance, $"{Owner}/{MeshWeaverInstanceNodeType.NodeType}")
            {
                NodeType = MeshWeaverInstanceNodeType.NodeType,
                Content = new MeshWeaverInstance { InstanceId = SlowInstance, Plan = "free" },
            };
            return Observable.Timer(SlowAnswer).Select(_ => Initial(new[] { (T)(object)record }));
        }

        private static QueryResultChange<T> Initial<T>(IReadOnlyList<T> items) => new()
        {
            ChangeType = QueryChangeType.Initial,
            Items = items,
            Timestamp = DateTimeOffset.UtcNow,
        };

        public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
            string basePath, string prefix, JsonSerializerOptions options,
            AutocompleteMode mode = AutocompleteMode.RelevanceFirst, int limit = 10,
            string? contextPath = null, string? context = null)
            => Observable.Return((IReadOnlyCollection<QueryResult>)Array.Empty<QueryResult>());

        public IObservable<T?> Select<T>(string path, string property, JsonSerializerOptions options)
            => Observable.Return<T?>(default);
    }
}
