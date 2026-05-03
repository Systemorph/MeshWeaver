using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Http;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Integration test for <see cref="MirrorRequest"/> — exercises the real mesh
/// hub set up by <see cref="MonolithMeshTestBase"/> (the canonical "portal
/// application" shape) end-to-end:
///
/// <list type="number">
///   <item>Override the <see cref="IRemoteMeshClientFactory"/> registration
///     with a recording stub so we don't need a live remote portal.</item>
///   <item>Seed real nodes in the local persistence (in-memory).</item>
///   <item>Post a <see cref="MirrorRequest"/> at the mesh hub via
///     <c>AwaitResponseAsync</c>.</item>
///   <item>Assert the response shape + that the stub remote received the
///     right calls.</item>
/// </list>
///
/// This mirrors the actual production setup: a portal is just a hub config
/// with persistence + the mirror handler. There's no DI of orchestrator types
/// in the click-action / MCP-tool path — only the MirrorRequest message.
/// </summary>
public class MirrorRequestHandlerTest(ITestOutputHelper output) : MonolithMeshTestBase(output), IDisposable
{
    private static readonly Lazy<RecordingRemoteFactory> SharedRemote = new(() => new RecordingRemoteFactory());

    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        "MeshWeaverMirrorTest-" + Guid.NewGuid().ToString("N")[..8]);

    /// <inheritdoc />
    /// <remarks>
    /// Use file-system persistence (not the default in-memory) so the mesh
    /// hub gets an <see cref="IStorageAdapter"/> registered — the mirror
    /// handler needs it for the LOCAL side of the recursive copy.
    /// Also replaces the production <see cref="McpRemoteMeshClientFactory"/>
    /// with a stub that records every call against the "remote" — no live
    /// HTTP. The stub exposes <see cref="RecordingRemoteClient.CreateCalls"/>
    /// etc. so individual tests can assert what the handler asked the remote
    /// to do.
    /// </remarks>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        Directory.CreateDirectory(_tempDir);
        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(_tempDir)
            .AddRowLevelSecurity()
            .AddGraph()
            .AddMeshNodes(new MeshNode(TestPartition) { Name = "Test Data", NodeType = "Markdown" })
            .AddMeshNodes(TestUsers.PublicAdminAccess())
            .ConfigureHub(c => c.WithQuiesceTimeout(TestQuiesceTimeout))
            .ConfigureHub(c => c.WithServices(s =>
            {
                s.AddSingleton<IRemoteMeshClientFactory>(SharedRemote.Value);
                return s;
            }));
    }

    public new void Dispose()
    {
        base.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task MirrorRequest_DryRun_returns_DryRun_summary_without_calling_remote()
    {
        var remote = SharedRemote.Value;
        remote.Reset();

        // Seed a couple of local nodes the importer can enumerate.
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNode(new MeshNode($"dryrun-root-{Guid.NewGuid():N}", TestPartition)
        {
            Name = "Mirror dry-run root",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = new { Body = "hello" },
        }).FirstAsync().ToTask();

        var response = await AwaitResponseAsync(new MirrorRequest
        {
            RemoteBaseUrl = "https://example.invalid",
            RemoteToken = "mw_test",
            SourcePath = TestPartition,
            Direction = "Push",
            DryRun = true,
        });

        response.Message.Status.Should().Be("DryRun");
        response.Message.Direction.Should().Be("Push");
        response.Message.SourcePath.Should().Be(TestPartition);

        // Dry-run scans the SOURCE (= local) — the recording stub
        // representing the remote should never have been touched for
        // writes (a dry-run push only enumerates what it WOULD send).
        remote.LastClient.CreateCalls.Should().BeEmpty(
            because: "DryRun must not write to the remote");
    }

    [Fact]
    public async Task MirrorRequest_Push_invokes_remote_Create_for_each_local_node()
    {
        var remote = SharedRemote.Value;
        remote.Reset();

        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var nodeId = $"push-target-{Guid.NewGuid():N}";
        await meshService.CreateNode(new MeshNode(nodeId, TestPartition)
        {
            Name = "Push me",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = new { Body = "push body" },
        }).FirstAsync().ToTask();

        // StorageImporter.ImportRecursivelyAsync starts from SourcePath and
        // enumerates its CHILDREN — to copy this leaf node we point at its
        // parent namespace (TestPartition) so the recursive copy walks down.
        var response = await AwaitResponseAsync(new MirrorRequest
        {
            RemoteBaseUrl = "https://example.invalid",
            RemoteToken = "mw_test",
            SourcePath = TestPartition,
            Direction = "Push",
            DryRun = false,
        });

        response.Message.Status.Should().Be("Ok");
        response.Message.Direction.Should().Be("Push");
        response.Message.NodesImported.Should().BeGreaterThan(0,
            because: "the recursive copy must have written at least the seeded node");

        remote.LastClient.CreateCalls.Should().Contain(
            n => n.Path == $"{TestPartition}/{nodeId}",
            because: "the seeded node should have been pushed to the remote");
    }

    [Fact]
    public async Task MirrorRequest_returns_Error_when_required_field_missing()
    {
        var response = await AwaitResponseAsync(new MirrorRequest
        {
            RemoteBaseUrl = "https://example.invalid",
            RemoteToken = "mw_test",
            SourcePath = "",            // ← missing
            Direction = "Push",
        });

        response.Message.Status.Should().Be("Error");
        response.Message.Error.Should().NotBeNullOrEmpty();
        response.Message.Error.Should().Contain("SourcePath",
            because: "the validation message must point at the missing field");
    }

    /// <summary>
    /// Test stub: factory that hands out a single shared
    /// <see cref="RecordingRemoteClient"/> per test (reset between tests).
    /// </summary>
    private sealed class RecordingRemoteFactory : IRemoteMeshClientFactory
    {
        public RecordingRemoteClient LastClient { get; private set; } = new();
        public void Reset() => LastClient = new RecordingRemoteClient();
        public IRemoteMeshClient Create(string remoteBaseUrl, string remoteToken) => LastClient;
    }

    /// <summary>
    /// Stub remote: records every call. Returns null/empty on reads so the
    /// adapter exercises the create branch of upsert; SearchPaths returns
    /// empty so ListChildPathsAsync surfaces no children on the "remote" side
    /// (forces RemoveMissing to be a no-op even when set).
    /// </summary>
    private sealed class RecordingRemoteClient : IRemoteMeshClient
    {
        public List<string> GetCalls { get; } = new();
        public List<MeshNode> CreateCalls { get; } = new();
        public List<MeshNode> UpdateCalls { get; } = new();
        public List<string> DeleteCalls { get; } = new();
        public List<string> SearchCalls { get; } = new();

        public IObservable<MeshNode?> Get(string path)
        {
            GetCalls.Add(path);
            return Observable.Return<MeshNode?>(null);
        }
        public IObservable<Unit> Create(MeshNode node)
        {
            CreateCalls.Add(node);
            return Observable.Return(Unit.Default);
        }
        public IObservable<Unit> Update(MeshNode node)
        {
            UpdateCalls.Add(node);
            return Observable.Return(Unit.Default);
        }
        public IObservable<Unit> Delete(string path)
        {
            DeleteCalls.Add(path);
            return Observable.Return(Unit.Default);
        }
        public IObservable<IReadOnlyList<string>> SearchPaths(string query)
        {
            SearchCalls.Add(query);
            return Observable.Return<IReadOnlyList<string>>(Array.Empty<string>());
        }
    }
}
