#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 A lookup of a registered instance BY ID decides on the answer of EVERY query provider — never
/// on the first frame of the progressive fan-in.
///
/// <para><see cref="IMeshService.Query(MeshQueryRequest)"/> seeds each provider with an empty
/// snapshot so it can emit before the slowest one has answered; its first frame therefore holds
/// only the rows of providers that answered synchronously. <c>InstancePlanService.FindInstancePath</c>
/// and <c>MeshWeaverInstanceService.IsIdAvailable</c> took that first frame. On a registry whose
/// instance records live in Postgres — a provider that answers asynchronously — the plan form at
/// Settings ▸ Instance grants answered "No registered instance carries that ID" for EVERY id
/// (measured on the public registry, platform 3.0.0-ci.9606), and the id check answered "available"
/// for an id that is taken. The in-memory store of a test mesh answers synchronously, which is why
/// the existing registration tests never saw it.</para>
///
/// <para>The provider here is a test PROVIDER on the <see cref="IMeshQueryProvider"/> seam, the same
/// one <c>PluginBundleStalledReadTest</c> drives — not a mock of a core service. It holds ONE
/// instance record and answers the lookup for it only when the test releases it, after the lookup
/// has subscribed: the production shape of a store whose rows arrive after the seeds.</para>
/// </summary>
public class InstanceLookupWaitsForEveryProviderTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string LateInstance = "late-instance";
    private const string LatePath = "late-owner/" + MeshWeaverInstanceNodeType.NodeType + "/" + LateInstance;

    private readonly LateInstanceProvider provider = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder)
        .AddPluginCatalog()
        .ConfigureServices(services => services.AddSingleton<IMeshQueryProvider>(provider));

    /// <summary>
    /// Subscribes <paramref name="lookup"/>, releases the late provider once the lookup's read has
    /// reached it, and returns the lookup's answer. Replay keeps the answer whether it arrived
    /// before or after the release — which is exactly what distinguishes the defect from the fix.
    /// </summary>
    private async Task<T> AnswerAfterRelease<T>(IObservable<T> lookup, CancellationToken cancellationToken)
    {
        var answer = lookup.Take(1).Replay(1);
        using var connection = answer.Connect();
        try
        {
            await provider.LookupReached.Should().Within(TestTimeouts.Convergence)
                .Emit("the lookup must reach the provider that holds the instance, or nothing below is measured",
                    cancellationToken: cancellationToken);
        }
        finally
        {
            provider.Release();
        }
        return await answer.Timeout(TimeSpan.FromSeconds(60)).Await(cancellationToken);
    }

    /// <summary>
    /// THE PLAN FORM'S LOOKUP: it finds an instance whose record arrives after the fan-in's seeds.
    /// Unfixed, the first frame carries no such row and the path is null.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task FindInstancePath_FindsAnInstanceAProviderAnswersLate()
    {
        var plans = Mesh.ServiceProvider.GetRequiredService<InstancePlanService>();

        var path = await AnswerAfterRelease(
            plans.FindInstancePath(LateInstance), TestContext.Current.CancellationToken);

        path.Should().Be(LatePath,
            "the lookup must decide on every provider's answer, not on the fan-in's empty-seeded first frame");
    }

    /// <summary>
    /// THE REGISTRATION CLAIM: an id a late provider holds is TAKEN. Unfixed, the first frame is
    /// empty and the id reads as available.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task IsIdAvailable_IsFalseForAnIdAProviderAnswersLate()
    {
        var instances = new MeshWeaverInstanceService(
            Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
            Mesh,
            Mesh.ServiceProvider.GetRequiredService<ILogger<MeshWeaverInstanceService>>(),
            new ConfigurationBuilder().Build());

        var available = await AnswerAfterRelease(
            instances.IsIdAvailable(LateInstance), TestContext.Current.CancellationToken);

        available.Should().BeFalse("an id whose record a slower provider holds is taken");
    }

    /// <summary>
    /// THE CONTROL: an id no provider holds is still reported absent, so the fix cannot pass by
    /// answering "found" or "taken" unconditionally.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task FindInstancePath_StillAnswersNullForAnUnknownId()
    {
        var plans = Mesh.ServiceProvider.GetRequiredService<InstancePlanService>();

        var path = await plans.FindInstancePath("no-such-instance")
            .Timeout(TimeSpan.FromSeconds(60))
            .Await(TestContext.Current.CancellationToken);

        path.Should().BeNull("no provider holds that id");
    }

    /// <summary>
    /// Claims every query. Answers the lookup of <see cref="LateInstance"/> with its record only once
    /// <see cref="Release"/> is called; every other query gets an empty Initial at once.
    /// </summary>
    private sealed class LateInstanceProvider : IMeshQueryProvider
    {
        private readonly ReplaySubject<bool> released = new(1);
        private readonly AsyncSubject<Unit> lookupReached = new();

        public string Name => nameof(LateInstanceProvider);

        /// <summary>Producer → test: completes when a lookup of <see cref="LateInstance"/> has subscribed.</summary>
        public IObservable<Unit> LookupReached => lookupReached;

        /// <summary>Lets the held lookups answer.</summary>
        public void Release() => released.OnNext(true);

        public bool Matches(IReadOnlyList<string> queryNamespaces) => true;

        public IObservable<QueryResultChange<T>> Query<T>(
            MeshQueryRequest request, JsonSerializerOptions options) =>
            Observable.Defer(() =>
            {
                var lookup = typeof(T) == typeof(MeshNode)
                    && request.EffectiveQueries.Any(q =>
                        q.Contains($"nodeType:{MeshWeaverInstanceNodeType.NodeType}", StringComparison.Ordinal)
                        && q.Contains($"id:{LateInstance}", StringComparison.Ordinal));
                if (!lookup)
                    return Observable.Return(Initial(Array.Empty<T>()));
                lookupReached.OnNext(Unit.Default);
                lookupReached.OnCompleted();
                var record = new MeshNode(LateInstance, "late-owner/" + MeshWeaverInstanceNodeType.NodeType)
                {
                    NodeType = MeshWeaverInstanceNodeType.NodeType,
                    Content = new MeshWeaverInstance { InstanceId = LateInstance, Plan = "free" },
                };
                return released.Take(1).Select(_ => Initial(new[] { (T)(object)record }));
            });

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
