using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The real tracking handler must create and fold independently of an eventually consistent
/// query index (#1174). The test provider models a stale positive at the backend extension seam;
/// messaging, tracking, storage, permissions and the upsert owner are the real mesh.
/// </summary>
public class ActivityTrackingUpsertTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string User = "activity-upsert-user";
    private const string SourcePath = User + "/page";
    private const string ActivityPath = User + "/_UserActivity/activity-upsert-user_page";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder)
        .ConfigureServices(services => services.AddSingleton<IMeshQueryProvider>(new StaleActivityProvider()));

    /// <summary>A positive index row is neither a live update base nor evidence of a stored node.</summary>
    [Fact(Timeout = 120_000)]
    public async Task StalePositiveQuery_DoesNotPreventCreate_AndLaterTracksFoldTheLiveCount()
    {
        var ct = TestContext.Current.CancellationToken;
        var identity = new AccessContext { ObjectId = User, Name = User };
        Mesh.ServiceProvider.GetRequiredService<AccessService>().SetHostIdentity(identity);
        await NodeFactory.CreateNode(new MeshNode(User)
        {
            Name = User, NodeType = "User", State = MeshNodeState.Active,
        }).Should().Emit(cancellationToken: ct);

        var hub = Mesh.GetActivityTrackingHub();
        var indexed = await hub.ServiceProvider.GetRequiredService<AccessService>()
            .RunAs(identity, () => hub.GetWorkspace().GetQuery($"UserActivity|{ActivityPath}",
                $"path:{ActivityPath} nodeType:UserActivity select:path,id,namespace,name,nodeType,content"))
            .Where(nodes => nodes.Any(n => n.Path == ActivityPath))
            .Take(1).Should().Within(TimeSpan.FromSeconds(15)).Emit(cancellationToken: ct);
        indexed.Should().Contain(n => n.Path == ActivityPath,
            "the control must prove that the query really reports this absent activity");
        var absent = await Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read(ActivityPath, Mesh.JsonSerializerOptions)
            .Should().Emit(cancellationToken: ct);
        absent.Should().BeNull("the index row must be stale, not a real node");

        await Track(hub, identity, SourcePath);
        var first = await ReadActivity();
        first.AccessCount.Should().Be(1, "the create leg must use its seed, not the index's count 999");

        await Track(hub, identity, SourcePath);
        await Track(hub, identity, SourcePath);
        var third = await ReadActivity();
        third.AccessCount.Should().Be(3, "each later track must fold onto the live stored count");
        third.FirstAccessedAt.Should().Be(first.FirstAccessedAt);
        third.LastAccessedAt.Should().BeOnOrAfter(first.LastAccessedAt);
        third.NodePath.Should().Be(SourcePath);
        third.UserId.Should().Be(User);
    }

    /// <summary>Navigation must not create a user's partition ahead of onboarding.</summary>
    [Fact(Timeout = 60_000)]
    public async Task MissingUserRoot_SettlesWithoutCreatingActivity()
    {
        var ct = TestContext.Current.CancellationToken;
        var identity = new AccessContext { ObjectId = User, Name = User };
        Mesh.ServiceProvider.GetRequiredService<AccessService>().SetHostIdentity(identity);
        var hub = Mesh.GetActivityTrackingHub();
        await Track(hub, identity, SourcePath);
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var root = await storage.Read(User, Mesh.JsonSerializerOptions).Should().Emit(cancellationToken: ct);
        var activity = await storage.Read(ActivityPath, Mesh.JsonSerializerOptions).Should().Emit(cancellationToken: ct);
        root.Should().BeNull("tracking must leave onboarding in charge of creating the user");
        activity.Should().BeNull("no activity may create an unonboarded user's partition");
    }

    private static async Task Track(IMessageHub hub, AccessContext identity, string sourcePath)
    {
        var settled = hub.ServiceProvider.GetRequiredService<ActivityWriteTracker>()
            .WhenSettled(ActivityPath).Replay(1);
        using var subscription = settled.Connect();
        hub.Post(new TrackActivityRequest(sourcePath, User, "Activity probe", "Markdown", User),
            options => options.WithAccessContext(identity));
        await settled.Should().Within(TimeSpan.FromSeconds(45)).Emit(
            "the detached write must terminate before storage is inspected",
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task<UserActivityRecord> ReadActivity()
    {
        var node = await Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read(ActivityPath, Mesh.JsonSerializerOptions)
            .Should().Emit(cancellationToken: TestContext.Current.CancellationToken);
        node.Should().NotBeNull("tracking must persist despite the stale positive query result");
        return node!.ContentAs<UserActivityRecord>(Mesh.GetActivityTrackingHub().JsonSerializerOptions)!;
    }

    /// <summary>The actual hub serializer preserves open outcomes and the legacy reason.</summary>
    [Fact]
    public void UpsertFailureKinds_RoundTripThroughTheHubSerializer()
    {
        var options = Mesh.GetActivityTrackingHub().JsonSerializerOptions;
        foreach (var kind in new[]
                 { NodeUpsertFailureKind.Conflict, NodeUpsertFailureKind.HubTeardown, "Module.CustomOutcome" })
        {
            var response = CreateOrUpdateNodeResponse.Fail("owner refusal") with { FailureKind = kind };
            var json = JsonSerializer.Serialize(response, options);
            var restored = JsonSerializer.Deserialize<CreateOrUpdateNodeResponse>(json, options)!;
            restored.FailureKind.Should().Be(kind);
            restored.RejectionReason.Should().Be(NodeUpsertRejectionReason.Unknown,
                "the additive vocabulary must not change what a legacy consumer reads");
        }
    }

    private sealed class StaleActivityProvider : IMeshQueryProvider
    {
        public bool Matches(IReadOnlyList<string> queryNamespaces) => queryNamespaces.Contains(User);

        public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options)
        {
            var stale = MeshNode.FromPath(ActivityPath) with
            {
                NodeType = "UserActivity", Name = "Stale activity", MainNode = User,
                State = MeshNodeState.Active,
                Content = new UserActivityRecord
                {
                    Id = "activity-upsert-user_page", UserId = User, NodePath = SourcePath,
                    AccessCount = 999,
                },
            };
            var matches = request.EffectiveQueries.Any(q => q.Contains(ActivityPath, StringComparison.Ordinal));
            return Observable.Return(new QueryResultChange<T>
            {
                ChangeType = QueryChangeType.Initial,
                Items = matches && stale is T typed ? [typed] : Array.Empty<T>(),
                Timestamp = DateTimeOffset.UtcNow,
            });
        }

        public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
            string basePath, string prefix, JsonSerializerOptions options,
            AutocompleteMode mode = AutocompleteMode.RelevanceFirst, int limit = 10,
            string? contextPath = null, string? context = null)
            => Observable.Return((IReadOnlyCollection<QueryResult>)Array.Empty<QueryResult>());

        public IObservable<T?> Select<T>(string path, string property, JsonSerializerOptions options)
            => Observable.Return<T?>(default);
    }
}
