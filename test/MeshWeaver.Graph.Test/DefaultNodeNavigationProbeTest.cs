using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The children probes of the default index (<see cref="DefaultNodeNavigation.ObserveFolder"/>)
/// against a REAL mesh — never a mocked <see cref="IMeshService"/>; the wrapper below only forwards
/// and records what was asked, as whom.
///
/// <para>Two review findings on Systemorph/MeshWeaver#7022, each pinned here: a probe opened inside
/// a live emission has no ambient identity, so it must carry the reader explicitly; and a live
/// level that gains a sibling must not re-open the probes of the folders that stayed, or an open
/// folder flickers back to a plain link while its new probe is pending.</para>
/// </summary>
public class DefaultNodeNavigationProbeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Space = "ProbeDocs";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(
                new MeshNode(Space) { Name = "Probe Docs", NodeType = "Markdown" },
                new MeshNode("A", Space) { Name = "A", NodeType = "Markdown", Order = 1 },
                new MeshNode("A1", Space + "/A") { Name = "A1", NodeType = "Markdown" },
                new MeshNode("B", Space) { Name = "B", NodeType = "Markdown", Order = 2 });

    private IMeshService RealMeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>Forwards to the real mesh service; records every query SUBSCRIPTION and its reader.</summary>
    private sealed class RecordingMeshService(IMeshService inner) : IMeshService
    {
        public ConcurrentQueue<MeshQueryRequest> Subscribed { get; } = new();

        public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request) =>
            Observable.Defer(() =>
            {
                Subscribed.Enqueue(request);
                return inner.Query<T>(request);
            });

        public IObservable<MeshNode> CreateNode(MeshNode node) => inner.CreateNode(node);
        public IObservable<CreateNodesResponse> CreateNodes(IReadOnlyCollection<MeshNode> nodes) => inner.CreateNodes(nodes);
        public IObservable<MeshNode> UpdateNode(MeshNode node) => inner.UpdateNode(node);
        public IObservable<MeshNode> CreateOrUpdateNode(MeshNode node) => inner.CreateOrUpdateNode(node);
        public IObservable<bool> DeleteNode(string path) => inner.DeleteNode(path);
        public IObservable<MeshNode> CopyNode(string sourcePath, string targetPath,
            bool includeDescendants = true, bool includeSatellites = false) =>
            inner.CopyNode(sourcePath, targetPath, includeDescendants, includeSatellites);
        public IObservable<IReadOnlyCollection<QueryResult>> Query(MeshQueryRequest request) => inner.Query(request);
        public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
            string basePath, string prefix, AutocompleteMode mode = AutocompleteMode.RelevanceFirst,
            int limit = 10, string? contextPath = null, string? context = null) =>
            inner.Autocomplete(basePath, prefix, mode, limit, contextPath, context);
        public IObservable<T?> Select<T>(string path, string property) => inner.Select<T>(path, property);
        public IObservable<string?> GetPreRenderedHtml(string path) => inner.GetPreRenderedHtml(path);
    }

    private static Task<IReadOnlyList<NodeNavigationEntry>> Next(
        IObservable<IReadOnlyList<NodeNavigationEntry>> folder, Func<IReadOnlyList<NodeNavigationEntry>, bool> until) =>
        folder.Where(until).FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

    [Fact]
    public async Task AFolderLineOffersItsExpanderAndEveryReadNamesTheReader()
    {
        var recording = new RecordingMeshService(RealMeshService);
        var viewer = new AccessContext { ObjectId = WellKnownUsers.System };

        var folder = DefaultNodeNavigation.ObserveFolder(recording, Space, "Elsewhere/Page", "Show all", viewer)
            .Replay(1).RefCount();

        var entries = await Next(folder, e => e.Any(x => x.Path == Space + "/A" && x.HasUnloadedChildren));

        entries.Select(e => e.Path).Should().Equal([Space + "/A", Space + "/B"]);
        entries.Single(e => e.Path == Space + "/B").HasUnloadedChildren.Should().BeFalse("B has no children");
        recording.Subscribed.Should().NotBeEmpty();
        recording.Subscribed.Should().OnlyContain(r => r.UserId == viewer.ObjectId,
            "a read opened inside a live emission has no ambient identity — every read names the reader");
    }

    [Fact]
    public async Task ASiblingJoiningTheLevelKeepsTheProbesThatStayed()
    {
        var recording = new RecordingMeshService(RealMeshService);
        var viewer = new AccessContext { ObjectId = WellKnownUsers.System };
        var seen = new ConcurrentQueue<IReadOnlyList<NodeNavigationEntry>>();

        var folder = DefaultNodeNavigation.ObserveFolder(recording, Space, "Elsewhere/Page", "Show all", viewer)
            .Do(seen.Enqueue)
            .Replay(1).RefCount();
        using var live = folder.Subscribe();

        await Next(folder, e => e.Any(x => x.Path == Space + "/A" && x.HasUnloadedChildren));
        var marked = seen.Count;

        await RealMeshService.CreateNode(new MeshNode("C", Space) { Name = "C", NodeType = "Markdown", Order = 3 })
            .Timeout(TestTimeouts.WriteConvergence).Await(TestContext.Current.CancellationToken);
        await Next(folder, e => e.Any(x => x.Path == Space + "/C"));

        string ProbeOf(string path) => DefaultNodeNavigation.ChildProbeQuery(path);
        recording.Subscribed.Count(r => r.Query == ProbeOf(Space + "/A")).Should().Be(1,
            "A stayed in the level, so its probe stays open — never re-opened for a new sibling");
        recording.Subscribed.Count(r => r.Query == ProbeOf(Space + "/C")).Should().Be(1, "the new sibling is probed once");
        seen.Skip(marked).Should().OnlyContain(e => e.Single(x => x.Path == Space + "/A").HasUnloadedChildren,
            "an open folder is never re-rendered as a link while a sibling's probe is pending");
    }
}
