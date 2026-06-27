#pragma warning disable CS1591

using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins the render-storm fix in <see cref="ThreadLayoutAreas.BuildThreadViewModel"/>.
///
/// 🚨 The thread node stream alternates between the TYPED <see cref="Thread"/> (the owning hub's
/// own write) and a <see cref="JsonElement"/> (the cache / cross-hub-sync / change-feed form —
/// "content is normally a JsonElement"). The old <c>node.Content as MeshThread</c> was NULL for the
/// JsonElement form → a DEFAULT viewmodel that ALTERNATED with the real one →
/// <c>vmStream.DistinctUntilChanged</c> never deduped → <c>UpdateData</c> fired on every emission →
/// the 931× FullHeader render storm that saturated the Blazor circuit and vanished the chat. The fix
/// uses <c>ContentAs</c> (deserialize), so both representations yield the SAME viewmodel and the
/// dedup fires. This reproduces in a unit test only because in tests the node is normally typed —
/// here we deliberately build the JsonElement form to exercise the production path.
/// </summary>
public class ThreadViewModelJsonElementTest
{
    [Fact]
    public void BuildThreadViewModel_JsonElementContent_EqualsTypedContent()
    {
        var options = new JsonSerializerOptions();
        const string hubPath = "rbuergi/_Thread/hi-3cb8";

        // Only PERSISTED fields — StreamingText/StreamingToolCalls are [JsonIgnore] (transient),
        // so they don't round-trip through the JsonElement form by design.
        var thread = new Thread
        {
            Messages = ImmutableList.Create("m1", "m2"),
            Status = ThreadExecutionStatus.Idle,
        };

        var typedNode = MeshNode.FromPath(hubPath) with { Name = "Hi", Content = thread, MainNode = "rbuergi" };
        // The exact representation the cache / cross-hub sync produces: Content as a JsonElement.
        var jsonNode = typedNode with { Content = JsonSerializer.SerializeToElement(thread, options) };

        var vmTyped = ThreadLayoutAreas.BuildThreadViewModel(typedNode, hubPath, options);
        var vmJson = ThreadLayoutAreas.BuildThreadViewModel(jsonNode, hubPath, options);

        // 🚨 The crux: both must be EQUAL — else DistinctUntilChanged thrashes real↔default → storm.
        Assert.Equal(vmTyped, vmJson);

        // ...and the JsonElement form was actually DESERIALIZED (not dropped to a default vm).
        Assert.Equal(2, vmJson.Messages.Count);
        Assert.Equal("m1", vmJson.Messages[0]);
    }

    [Fact]
    public void BuildThreadViewModel_JsonElementContent_RepeatedlyDedupsToSameValue()
    {
        // Two independent JsonElement parses (as successive cache emissions produce) must be equal,
        // so DistinctUntilChanged drops the no-op re-emission instead of re-rendering.
        var options = new JsonSerializerOptions();
        const string hubPath = "rbuergi/_Thread/hi-3cb8";
        var thread = new Thread { Messages = ImmutableList.Create("m1") };
        var node = MeshNode.FromPath(hubPath) with { Content = thread, MainNode = "rbuergi" };

        var a = ThreadLayoutAreas.BuildThreadViewModel(
            node with { Content = JsonSerializer.SerializeToElement(thread, options) }, hubPath, options);
        var b = ThreadLayoutAreas.BuildThreadViewModel(
            node with { Content = JsonSerializer.SerializeToElement(thread, options) }, hubPath, options);

        Assert.Equal(a, b);
    }
}
