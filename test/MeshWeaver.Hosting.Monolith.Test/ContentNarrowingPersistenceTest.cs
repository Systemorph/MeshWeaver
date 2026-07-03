using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 Regression repro for CONTENT-NARROWING SILENT DATA LOSS: when a persisted node's
/// content deserializes through a REGISTERED-but-NARROWER type (an older compiled shape —
/// properties since removed or not yet added), System.Text.Json silently drops the unknown
/// members (no exception, so the preserve-raw fallback in <c>ObjectPolymorphicConverter</c>
/// never fires) and the per-node hub's persistence sampler then ECHOES the narrowed state
/// back to storage on pure activation — no user edit, no version bump. Live artifacts:
/// prod <c>Systemorph/Event/DAV2026</c> stripped to defaults; ~40
/// <c>samples/Graph/Data/*.json</c> NodeType files losing
/// <c>showChildrenInDetails</c>/<c>detailsChildrenLimit</c> in local trees.
/// The fix is two-sided:
/// <list type="bullet">
///   <item><b>Lossless round-trip</b> — persisted content types carry a
///     <c>[JsonExtensionData]</c> buffer so unknown members survive typed
///     materialization and re-serialization (and record <c>with</c>-copies, so
///     edits through the narrower shape keep them too).</item>
///   <item><b>No initial-load echo</b> — a state that ARRIVED from persistence/routing
///     is by construction already persisted; the sampler must not write it back
///     (a load is a READ). Local writes (create + update) still persist.</item>
/// </list>
/// </summary>
public class ContentNarrowingPersistenceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private string? _tempDir;

    private string GetTempDir()
    {
        if (_tempDir != null) return _tempDir;
        _tempDir = Path.Combine(Path.GetTempPath(), "MeshWeaverNarrowingRepro", $"t_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        return _tempDir;
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddFileSystemPersistence(GetTempDir());

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_tempDir != null && Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Seeds a persisted NodeType node whose content carries members the CURRENT
    /// <see cref="NodeTypeDefinition"/> does not have (they existed in an older build) —
    /// the exact shape of the stripped samples/Graph/Data NodeType files.
    /// </summary>
    private async Task<string> SeedNarrowedNodeFile(string id)
    {
        var dir = Path.Combine(GetTempDir(), "TestData");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{id}.json");
        var seeded =
            $"{{\"$type\":\"MeshNode\",\"id\":\"{id}\",\"namespace\":\"TestData\"," +
            $"\"path\":\"TestData/{id}\",\"mainNode\":\"TestData/{id}\",\"name\":\"{id}\"," +
            "\"nodeType\":\"NodeType\",\"version\":3,\"state\":\"Active\"," +
            "\"content\":{\"$type\":\"NodeTypeDefinition\",\"description\":\"repro\"," +
            "\"showChildrenInDetails\":true,\"detailsChildrenLimit\":10}}";
        await File.WriteAllTextAsync(file, seeded, TestContext.Current.CancellationToken);
        return file;
    }

    /// <summary>
    /// Pure activation (a READ) must never destroy stored content members the current
    /// compiled type doesn't know. Pre-fix, the persistence sampler's initial-load echo
    /// rewrote the file from the narrowed typed materialization, silently dropping them.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ActivationEcho_MustNotDropUnknownContentMembers()
    {
        var file = await SeedNarrowedNodeFile("MyType");

        // Activate the per-node hub (deserializes content into the current, narrower type).
        var node = await ReadNode("TestData/MyType")
            .Should().Within(30.Seconds()).Match(n => n is not null);
        node!.Path.Should().Be("TestData/MyType");

        // Sanctioned fixed wait ("confirm nothing bad happened"): the persistence sampler
        // fires 200 ms after the own-stream emission; give it ample headroom.
        await Task.Delay(1500, TestContext.Current.CancellationToken);

        var persisted = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        persisted.Should().Contain("showChildrenInDetails",
            "loading a node must never destroy stored content members the current type doesn't know");
        persisted.Should().Contain("detailsChildrenLimit",
            "loading a node must never destroy stored content members the current type doesn't know");
    }

    /// <summary>
    /// The serializer-level half of the defect, pinned through the HUB's options (the
    /// exact chain storage writes use: <c>ObjectPolymorphicConverter</c> + the polymorphic
    /// type-info resolver): materializing content into a registered-but-narrower type and
    /// re-serializing must round-trip unknown members instead of silently dropping them.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task HubOptions_RoundTrip_PreservesUnknownContentMembers()
    {
        const string stored =
            "{\"$type\":\"MeshNode\",\"id\":\"RT\",\"namespace\":\"TestData\"," +
            "\"path\":\"TestData/RT\",\"mainNode\":\"TestData/RT\",\"name\":\"RT\"," +
            "\"nodeType\":\"NodeType\",\"version\":3,\"state\":\"Active\"," +
            "\"content\":{\"$type\":\"NodeTypeDefinition\",\"description\":\"repro\"," +
            "\"showChildrenInDetails\":true,\"detailsChildrenLimit\":10}}";

        var node = JsonSerializer.Deserialize<MeshNode>(stored, Mesh.JsonSerializerOptions);

        // The defect only exists when the content MATERIALIZES into the registered narrower
        // type (an untyped JsonElement would round-trip trivially) — assert the premise.
        node!.Content.Should().BeOfType<NodeTypeDefinition>(
            "the $type is registered on the hub, so content materializes typed");
        ((NodeTypeDefinition)node.Content!).Description.Should().Be("repro");

        var roundTripped = JsonSerializer.Serialize(node, Mesh.JsonSerializerOptions);
        roundTripped.Should().Contain("showChildrenInDetails",
            "unknown members must survive typed materialization through the hub's serializer chain");
        roundTripped.Should().Contain("detailsChildrenLimit",
            "unknown members must survive typed materialization through the hub's serializer chain");

        await Task.CompletedTask;
    }

    /// <summary>
    /// A REAL edit made through the narrower compiled shape must persist the edit AND keep
    /// the unknown members (the extension-data buffer rides the record <c>with</c>-copy).
    /// This is the flow that would still lose data if only the echo were suppressed.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task EditThroughNarrowerType_PersistsEditAndKeepsUnknownMembers()
    {
        var file = await SeedNarrowedNodeFile("Edited");

        var node = await ReadNode("TestData/Edited")
            .Should().Within(30.Seconds()).Match(n => n is not null);
        node!.Path.Should().Be("TestData/Edited");

        var options = Mesh.JsonSerializerOptions;
        Mesh.GetWorkspace().GetMeshNodeStream("TestData/Edited")
            .Update(current =>
            {
                var def = current!.ContentAs<NodeTypeDefinition>(options)
                          ?? new NodeTypeDefinition();
                return current with { Content = def with { Description = "edited-through-narrow-shape" } };
            })
            .Subscribe(_ => { }, ex => Output.WriteLine($"Update failed: {ex}"));

        // Wait on the actual condition: the edited description landing in the file.
        var persisted = await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => File.Exists(file) ? File.ReadAllText(file) : null)
            .Should().Within(30.Seconds())
            .Match(txt => txt != null && txt.Contains("edited-through-narrow-shape"));

        persisted.Should().Contain("showChildrenInDetails",
            "an edit through the narrower shape must not clobber members it doesn't know");
        persisted.Should().Contain("detailsChildrenLimit",
            "an edit through the narrower shape must not clobber members it doesn't know");
    }

    /// <summary>
    /// The echo side, pinned hard: pure activation of a persistence-backed node is a READ —
    /// it must not touch storage AT ALL. The state arrived FROM persistence, so writing it
    /// back is at best file/mtime churn on every activation (the perpetually-git-dirty
    /// samples/Graph/Data trees) and was, pre-fix, the write that persisted the
    /// content-narrowing loss. Mirrors #227's StaticNodePersistenceEchoTest for
    /// persistence-backed (not static-served) nodes.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task PureActivation_DoesNotWriteStorageAtAll()
    {
        var file = await SeedNarrowedNodeFile("Untouched");
        var before = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);

        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var echoes = new ConcurrentQueue<DataChangeNotification>();
        using var echoSub = storage.Changes
            .Where(c => string.Equals(c.Path, "TestData/Untouched", StringComparison.OrdinalIgnoreCase))
            .Subscribe(echoes.Enqueue);

        var node = await ReadNode("TestData/Untouched")
            .Should().Within(30.Seconds()).Match(n => n is not null);
        node!.Path.Should().Be("TestData/Untouched");

        // Sanctioned fixed wait (negative test — "confirm nothing happened"): the sampler
        // fires 200 ms after the own-stream emission; give it ample headroom.
        await Task.Delay(1500, TestContext.Current.CancellationToken);

        echoes.Should().BeEmpty(
            "a state loaded FROM persistence is already durable — activating its hub must not echo it back");
        var after = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        after.Should().Be(before, "pure activation must leave the persisted bytes untouched");
    }

    /// <summary>
    /// Guards the initial-load-echo suppression: creates and subsequent updates must
    /// persist WITHOUT relying on the activation echo. The create is insta-written by
    /// <c>MeshNodeTypeSource.UpdateImpl</c>'s add path; updates ride the sampler. If the
    /// suppression ever over-reached into these local-write paths, this pins it red.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task CreateThenUpdate_PersistsWithoutRelyingOnActivationEcho()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var created = MeshNode.FromPath("TestData/Created") with
        {
            Name = "Created",
            NodeType = "NodeType",
            Content = new NodeTypeDefinition { Description = "created-content" }
        };

        await meshService.CreateNode(created).Should().Within(30.Seconds()).Emit();

        var file = Path.Combine(GetTempDir(), "TestData", "Created.json");
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => File.Exists(file) ? File.ReadAllText(file) : null)
            .Should().Within(30.Seconds())
            .Match(txt => txt != null && txt.Contains("created-content"));

        var options = Mesh.JsonSerializerOptions;
        Mesh.GetWorkspace().GetMeshNodeStream("TestData/Created")
            .Update(current =>
            {
                var def = current!.ContentAs<NodeTypeDefinition>(options)
                          ?? new NodeTypeDefinition();
                return current with { Content = def with { Description = "updated-content" } };
            })
            .Subscribe(_ => { }, ex => Output.WriteLine($"Update failed: {ex}"));

        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => File.Exists(file) ? File.ReadAllText(file) : null)
            .Should().Within(30.Seconds())
            .Match(txt => txt != null && txt.Contains("updated-content"));
    }
}
