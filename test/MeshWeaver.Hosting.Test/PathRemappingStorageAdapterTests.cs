using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Hosting.Persistence.Http;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Tests for <see cref="PathRemappingStorageAdapter"/> — the
/// <see cref="MirrorOperations"/> uses this to push <c>rbuergi/Story</c>
/// from local to <c>Systemorph/Story</c> on a remote portal. The remapper
/// rewrites paths on every Read/Write/Delete + adjusts the
/// <see cref="MeshNode.Namespace"/> + <see cref="MeshNode.Id"/> +
/// <see cref="MeshNode.MainNode"/> on outgoing nodes.
/// </summary>
public class PathRemappingStorageAdapterTests
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    [Fact]
    public async Task WriteAsync_rewrites_node_path_to_target_prefix()
    {
        var inner = new RecordingStorageAdapter();
        var adapter = new PathRemappingStorageAdapter(inner, "rbuergi", "Systemorph");

        await adapter.WriteAsync(new MeshNode("KernelTour", "rbuergi/Story")
        {
            Name = "tour", NodeType = "Markdown", State = MeshNodeState.Active,
        }, JsonOptions);

        inner.Writes.Should().ContainSingle();
        var written = inner.Writes[0];
        written.Path.Should().Be("Systemorph/Story/KernelTour");
        written.Namespace.Should().Be("Systemorph/Story");
        written.Id.Should().Be("KernelTour");
        // Other fields preserved
        written.Name.Should().Be("tour");
        written.NodeType.Should().Be("Markdown");
    }

    [Fact]
    public async Task WriteAsync_rewrites_MainNode_when_it_pointed_at_the_original_path()
    {
        var inner = new RecordingStorageAdapter();
        var adapter = new PathRemappingStorageAdapter(inner, "rbuergi", "Systemorph");

        // Primary nodes typically have MainNode == Path.
        await adapter.WriteAsync(new MeshNode("McpSmokeTest", "rbuergi")
        {
            Name = "Smoke",
            NodeType = "Code",
            MainNode = "rbuergi/McpSmokeTest",
            State = MeshNodeState.Active,
        }, JsonOptions);

        var written = inner.Writes[0];
        written.MainNode.Should().Be("Systemorph/McpSmokeTest",
            because: "MainNode that pointed at the original primary path must follow the rename");
    }

    [Fact]
    public async Task WriteAsync_leaves_MainNode_untouched_for_satellites()
    {
        var inner = new RecordingStorageAdapter();
        var adapter = new PathRemappingStorageAdapter(inner, "rbuergi", "Systemorph");

        // A satellite node has MainNode pointing at a DIFFERENT path (its parent);
        // we must NOT blindly rewrite it — the parent rename, if any, comes via
        // a separate Write of the parent node itself.
        await adapter.WriteAsync(new MeshNode("act-1", "rbuergi/_Activity")
        {
            Name = "act",
            NodeType = "Activity",
            MainNode = "rbuergi/McpSmokeTest",  // different from Path
            State = MeshNodeState.Active,
        }, JsonOptions);

        var written = inner.Writes[0];
        written.Path.Should().Be("Systemorph/_Activity/act-1");
        written.MainNode.Should().Be("rbuergi/McpSmokeTest",
            because: "MainNode that didn't equal Path is opaque to the remapper");
    }

    [Fact]
    public async Task ReadAsync_rewrites_lookup_path()
    {
        var inner = new RecordingStorageAdapter();
        var adapter = new PathRemappingStorageAdapter(inner, "rbuergi", "Systemorph");

        await adapter.ReadAsync("rbuergi/Story/KernelTour", JsonOptions);

        inner.Reads.Should().ContainSingle().Which.Should().Be("Systemorph/Story/KernelTour");
    }

    [Fact]
    public async Task DeleteAsync_rewrites_path()
    {
        var inner = new RecordingStorageAdapter();
        var adapter = new PathRemappingStorageAdapter(inner, "rbuergi", "Systemorph");

        await adapter.DeleteAsync("rbuergi/Story/KernelTour");

        inner.Deletes.Should().ContainSingle().Which.Should().Be("Systemorph/Story/KernelTour");
    }

    [Fact]
    public async Task ListChildPathsAsync_rewrites_parent_path()
    {
        var inner = new RecordingStorageAdapter();
        var adapter = new PathRemappingStorageAdapter(inner, "rbuergi", "Systemorph");

        await adapter.ListChildPathsAsync("rbuergi/Story");

        inner.ListedParents.Should().ContainSingle().Which.Should().Be("Systemorph/Story");
    }

    [Fact]
    public void Remap_passes_through_paths_outside_the_source_prefix()
    {
        var inner = new RecordingStorageAdapter();
        var adapter = new PathRemappingStorageAdapter(inner, "rbuergi", "Systemorph");

        // ReadAsync on something outside the source prefix lands at the same
        // path on the inner — the remapper only relabels its scoped subtree.
        var task = adapter.ReadAsync("Doc/Architecture/GrantingAccess", JsonOptions);
        task.Wait();
        inner.Reads.Should().ContainSingle().Which.Should().Be("Doc/Architecture/GrantingAccess");
    }

    [Fact]
    public async Task Remap_collapses_root_match_to_target_prefix_directly()
    {
        var inner = new RecordingStorageAdapter();
        var adapter = new PathRemappingStorageAdapter(inner, "rbuergi", "Systemorph");

        // Reading the root of the source ("rbuergi") should turn into reading
        // the root of the target ("Systemorph") — no double prefix.
        await adapter.ReadAsync("rbuergi", JsonOptions);

        inner.Reads.Should().ContainSingle().Which.Should().Be("Systemorph");
    }

    [Fact]
    public void Constructor_throws_on_null_inner()
    {
        Action act = () => new PathRemappingStorageAdapter(null!, "a", "b");
        act.Should().Throw<ArgumentNullException>().WithParameterName("inner");
    }

    /// <summary>Recording stub that captures every call. No-op semantics — returns null/empty.</summary>
    private sealed class RecordingStorageAdapter : IStorageAdapter
    {
        public List<string> Reads { get; } = new();
        public List<MeshNode> Writes { get; } = new();
        public List<string> Deletes { get; } = new();
        public List<string?> ListedParents { get; } = new();

        public Task<MeshNode?> ReadAsync(string path, JsonSerializerOptions options, CancellationToken ct = default)
        {
            Reads.Add(path);
            return Task.FromResult<MeshNode?>(null);
        }

        public Task WriteAsync(MeshNode node, JsonSerializerOptions options, CancellationToken ct = default)
        {
            Writes.Add(node);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string path, CancellationToken ct = default)
        {
            Deletes.Add(path);
            return Task.CompletedTask;
        }

        public Task<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPathsAsync(
            string? parentPath, CancellationToken ct = default)
        {
            ListedParents.Add(parentPath);
            return Task.FromResult(((IEnumerable<string>)Array.Empty<string>(), (IEnumerable<string>)Array.Empty<string>()));
        }

        public Task<bool> ExistsAsync(string path, CancellationToken ct = default) => Task.FromResult(false);

        public IAsyncEnumerable<object> GetPartitionObjectsAsync(
            string nodePath, string? subPath, JsonSerializerOptions options, CancellationToken ct = default)
            => AsyncEnumerable<object>();

        public Task SavePartitionObjectsAsync(
            string nodePath, string? subPath, IReadOnlyCollection<object> objects,
            JsonSerializerOptions options, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeletePartitionObjectsAsync(string nodePath, string? subPath = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<DateTimeOffset?> GetPartitionMaxTimestampAsync(
            string nodePath, string? subPath = null, CancellationToken ct = default)
            => Task.FromResult<DateTimeOffset?>(null);

#pragma warning disable CS1998
        private static async IAsyncEnumerable<T> AsyncEnumerable<T>()
        {
            yield break;
        }
#pragma warning restore CS1998
    }
}
