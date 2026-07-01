using System.Collections.Generic;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Deterministic repro + spec for <see cref="MeshDataSourceExtensions.ReduceToMeshNode"/> on the
/// patch path. When an update is derived from a JSON patch (the cross-hub / mirror path), the
/// <see cref="EntityUpdate.Value"/> is a boxed <see cref="JsonElement"/>, NOT a <see cref="MeshNode"/>.
/// The original <c>change.Value as MeshNode</c> silently produced <c>null</c>, pushing a
/// null-content <see cref="ChangeItem{T}"/> into the mirror (a live update that drops the node).
/// The fix deserializes the JsonElement to MeshNode exactly as the sibling <c>PatchMeshNode</c> does.
/// </summary>
public class ReduceToMeshNodeJsonElementTest
{
    [Fact]
    public void PatchPath_JsonElementValue_DeserializesToMeshNode()
    {
        var options = new JsonSerializerOptions();
        var expected = new MeshNode("k1", "TestSpace") { Name = "Hello" };

        // The cross-hub/mirror shape: the EntityUpdate carries the node as a JsonElement.
        var asJson = JsonSerializer.SerializeToElement(expected, options);
        var update = new EntityUpdate(nameof(MeshNode), expected.Id, asJson)
        {
            OldValue = expected
        };

        var change = new ChangeItem<InstanceCollection>(
            new InstanceCollection(new Dictionary<object, object> { [expected.Id] = expected }),
            ChangedBy: "tester",
            StreamId: "stream-1",
            ChangeType: ChangeType.Patch,
            Version: 1,
            Updates: [update]);

        var result = MeshDataSourceExtensions.ReduceToMeshNode(
            change, new MeshNodeReference(), initial: false, options);

        // Pre-fix: `change.Value as MeshNode` → null → a null-content ChangeItem reaches the mirror.
        Assert.NotNull(result.Value);
        Assert.Equal(expected, result.Value);
    }
}
